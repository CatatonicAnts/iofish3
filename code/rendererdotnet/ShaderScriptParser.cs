using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RendererDotNet.Interop;

namespace RendererDotNet;

/// <summary>
/// Parses Q3 .shader script files to resolve shader names to image paths.
/// Mirrors the approach in renderergl2/tr_shader.c ScanAndLoadShaderFiles().
///
/// Q3 shader scripts define multi-stage materials. Each shader has a name
/// and one or more stages containing "map" directives that reference texture files.
/// This parser extracts the first usable image path from each shader definition.
/// </summary>
public sealed unsafe class ShaderScriptParser
{
    private readonly Dictionary<string, ShaderDef> _shaders = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _shaders.Count;

    /// <summary>
    /// Load and parse all .shader files from the engine's scripts/ directory.
    /// Uses FS_ListFiles("scripts", ".shader") just like the C renderer does.
    /// </summary>
    public void LoadAllShaders()
    {
        byte** fileList = EngineImports.FS_ListFiles("scripts", ".shader", out int numFiles);
        if (fileList == null || numFiles == 0)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                "[.NET] WARNING: no shader files found\n");
            return;
        }

        int loaded = 0;
        for (int i = 0; i < numFiles; i++)
        {
            string fileName = Marshal.PtrToStringUTF8((nint)fileList[i]) ?? "";
            if (string.IsNullOrEmpty(fileName))
                continue;

            string path = $"scripts/{fileName}";
            int len = EngineImports.FS_ReadFile(path, out byte* buf);
            if (len <= 0 || buf == null)
            {
                if (buf != null) EngineImports.FS_FreeFile(buf);
                continue;
            }

            string text = Encoding.UTF8.GetString(buf, (int)len);
            EngineImports.FS_FreeFile(buf);

            ParseShaderFile(text, path);
            loaded++;
        }

        EngineImports.FS_FreeFileList(fileList);
        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Parsed {loaded} shader files, {_shaders.Count} shader definitions\n");
    }

    /// <summary>
    /// Look up the primary image path for a shader name.
    /// Returns null if the shader isn't defined in any script file.
    /// </summary>
    public ShaderDef? GetShaderDef(string name)
    {
        _shaders.TryGetValue(name, out var def);
        return def;
    }

    /// <summary>
    /// Parse all shader definitions from a single .shader file's text content.
    /// Format:
    ///   shaderName
    ///   {
    ///       // global directives (cull, surfaceparm, etc.)
    ///       {
    ///           map texturePath.tga
    ///           blendFunc ...
    ///       }
    ///   }
    /// </summary>
    private void ParseShaderFile(string text, string filename)
    {
        var tokenizer = new ShaderTokenizer(text);

        while (tokenizer.HasMore())
        {
            string? shaderName = tokenizer.NextToken();
            if (shaderName == null)
                break;

            // Expect opening brace for shader body
            string? openBrace = tokenizer.NextToken();
            if (openBrace != "{")
            {
                EngineImports.Printf(EngineImports.PRINT_WARNING,
                    $"[.NET] WARNING: expected '{{' after shader '{shaderName}' in {filename}\n");
                break;
            }

            var def = ParseSingleShader(shaderName, ref tokenizer);
            if (def != null && !_shaders.ContainsKey(shaderName))
            {
                _shaders[shaderName] = def;
            }
        }
    }

    /// <summary>
    /// Parse a single shader definition (everything between the outer { }).
    /// Extracts the first usable "map" image path from the shader's stages.
    /// </summary>
    private ShaderDef? ParseSingleShader(string name, ref ShaderTokenizer tokenizer)
    {
        int depth = 1;
        string? imagePath = null;
        bool clamp = false;
        bool foundImage = false;

        while (depth > 0 && tokenizer.HasMore())
        {
            string? token = tokenizer.NextToken();
            if (token == null)
                break;

            if (token == "{")
            {
                depth++;
                continue;
            }

            if (token == "}")
            {
                depth--;
                continue;
            }

            // Only look for map directives inside stages (depth == 2)
            if (depth == 2 && !foundImage)
            {
                if (string.Equals(token, "map", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken))
                    {
                        imagePath = mapToken;
                        foundImage = true;
                    }
                }
                else if (string.Equals(token, "clampMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken))
                    {
                        imagePath = mapToken;
                        clamp = true;
                        foundImage = true;
                    }
                }
                else if (string.Equals(token, "animMap", StringComparison.OrdinalIgnoreCase))
                {
                    // animMap <frequency> <image1> <image2> ...
                    tokenizer.NextToken(); // skip frequency
                    string? firstFrame = tokenizer.NextToken();
                    if (firstFrame != null && !IsSpecialMap(firstFrame))
                    {
                        imagePath = firstFrame;
                        foundImage = true;
                    }
                }
            }
        }

        return new ShaderDef
        {
            Name = name,
            ImagePath = imagePath,
            Clamp = clamp
        };
    }

    private static bool IsSpecialMap(string token)
    {
        return token.StartsWith('$') ||
               string.Equals(token, "$lightmap", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "$whiteimage", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "$deluxemap", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Simple tokenizer that handles Q3 shader script syntax:
    /// - Skips C-style comments (// and /* */)
    /// - Handles quoted strings
    /// - Returns individual tokens delimited by whitespace
    /// - Treats { and } as standalone tokens
    /// </summary>
    private ref struct ShaderTokenizer
    {
        private ReadOnlySpan<char> _text;
        private int _pos;

        public ShaderTokenizer(string text)
        {
            _text = text.AsSpan();
            _pos = 0;
        }

        public bool HasMore()
        {
            SkipWhitespaceAndComments();
            return _pos < _text.Length;
        }

        public string? NextToken()
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
                return null;

            // Quoted string
            if (_text[_pos] == '"')
            {
                _pos++;
                int start = _pos;
                while (_pos < _text.Length && _text[_pos] != '"' && _text[_pos] != '\n')
                    _pos++;
                string result = _text[start.._pos].ToString();
                if (_pos < _text.Length && _text[_pos] == '"')
                    _pos++;
                return result;
            }

            // Braces are standalone tokens
            if (_text[_pos] == '{' || _text[_pos] == '}')
            {
                return _text[_pos++..(_pos)].ToString();
            }

            // Regular token
            {
                int start = _pos;
                while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos])
                       && _text[_pos] != '{' && _text[_pos] != '}')
                    _pos++;
                return _text[start.._pos].ToString();
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _text.Length)
            {
                // Skip whitespace
                if (char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                    continue;
                }

                // Skip // line comments
                if (_pos + 1 < _text.Length && _text[_pos] == '/' && _text[_pos + 1] == '/')
                {
                    _pos += 2;
                    while (_pos < _text.Length && _text[_pos] != '\n')
                        _pos++;
                    continue;
                }

                // Skip /* block comments */
                if (_pos + 1 < _text.Length && _text[_pos] == '/' && _text[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < _text.Length && !(_text[_pos] == '*' && _text[_pos + 1] == '/'))
                        _pos++;
                    if (_pos + 1 < _text.Length)
                        _pos += 2;
                    continue;
                }

                break;
            }
        }
    }
}

/// <summary>
/// Represents a parsed shader definition from a .shader script file.
/// </summary>
public sealed class ShaderDef
{
    /// <summary>Shader name as defined in the script.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Path to the primary image/texture (from the first "map" or "clampMap" directive).
    /// Null if the shader has no usable image stage (e.g. $lightmap only).
    /// </summary>
    public string? ImagePath { get; init; }

    /// <summary>Whether the texture should use clamp-to-edge wrapping.</summary>
    public bool Clamp { get; init; }
}
