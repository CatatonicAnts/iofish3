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
    /// Find the first shader with a skyparms outerbox definition.
    /// Returns the sky box texture base name, or null if none found.
    /// </summary>
    public string? FindSkyBoxName()
    {
        foreach (var def in _shaders.Values)
        {
            if (def.SkyBox != null)
                return def.SkyBox;
        }
        return null;
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
    /// Extracts the first usable "map" image path and blendFunc from the shader's stages.
    /// </summary>
    private ShaderDef? ParseSingleShader(string name, ref ShaderTokenizer tokenizer)
    {
        int depth = 1;
        string? imagePath = null;
        bool clamp = false;
        BlendMode blend = BlendMode.Opaque;
        bool foundUsableStage = false;
        string? skyBox = null;
        string? editorImage = null;
        bool isTransparent = false;
        string? envMapImage = null; // fallback: first env-mapped stage's image

        // Per-stage tracking
        string? stageImage = null;
        bool stageClamp = false;
        BlendMode stageBlend = BlendMode.Opaque;
        bool stageHasEnvMap = false;

        while (depth > 0 && tokenizer.HasMore())
        {
            string? token = tokenizer.NextToken();
            if (token == null)
                break;

            if (token == "{")
            {
                depth++;
                if (depth == 2)
                {
                    stageImage = null;
                    stageClamp = false;
                    stageBlend = BlendMode.Opaque;
                    stageHasEnvMap = false;
                }
                continue;
            }

            if (token == "}")
            {
                // When leaving a stage, adopt the first stage that has a real image
                // and doesn't use environment mapping
                if (depth == 2 && !foundUsableStage && stageImage != null && !stageHasEnvMap)
                {
                    imagePath = stageImage;
                    clamp = stageClamp;
                    blend = stageBlend;
                    foundUsableStage = true;
                }
                // Track env-mapped images as a last-resort fallback
                if (depth == 2 && stageHasEnvMap && stageImage != null && envMapImage == null)
                    envMapImage = stageImage;

                depth--;
                continue;
            }

            // Parse global shader directives (depth == 1)
            if (depth == 1)
            {
                if (string.Equals(token, "skyparms", StringComparison.OrdinalIgnoreCase))
                {
                    string? outerBox = tokenizer.NextToken();
                    tokenizer.NextToken(); // cloudheight
                    tokenizer.NextToken(); // innerbox
                    if (outerBox != null && outerBox != "-")
                        skyBox = outerBox;
                }
                else if (string.Equals(token, "qer_editorimage", StringComparison.OrdinalIgnoreCase))
                {
                    string? edImg = tokenizer.NextToken();
                    if (edImg != null && !IsSpecialMap(edImg))
                        editorImage = edImg;
                }
                else if (string.Equals(token, "surfaceparm", StringComparison.OrdinalIgnoreCase))
                {
                    string? parm = tokenizer.NextToken();
                    if (parm != null && string.Equals(parm, "trans", StringComparison.OrdinalIgnoreCase))
                        isTransparent = true;
                }
            }

            // Parse directives inside stages (depth == 2)
            if (depth == 2)
            {
                if (string.Equals(token, "map", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken) && stageImage == null)
                        stageImage = mapToken;
                }
                else if (string.Equals(token, "clampMap", StringComparison.OrdinalIgnoreCase))
                {
                    string? mapToken = tokenizer.NextToken();
                    if (mapToken != null && !IsSpecialMap(mapToken) && stageImage == null)
                    {
                        stageImage = mapToken;
                        stageClamp = true;
                    }
                }
                else if (string.Equals(token, "animMap", StringComparison.OrdinalIgnoreCase))
                {
                    tokenizer.NextToken(); // skip frequency
                    string? firstFrame = tokenizer.NextToken();
                    if (firstFrame != null && !IsSpecialMap(firstFrame) && stageImage == null)
                        stageImage = firstFrame;
                }
                else if (string.Equals(token, "blendFunc", StringComparison.OrdinalIgnoreCase))
                {
                    stageBlend = ParseBlendFunc(ref tokenizer);
                }
                else if (string.Equals(token, "tcGen", StringComparison.OrdinalIgnoreCase))
                {
                    string? tcGenToken = tokenizer.NextToken();
                    if (tcGenToken != null && string.Equals(tcGenToken, "environment", StringComparison.OrdinalIgnoreCase))
                        stageHasEnvMap = true;
                }
            }
        }

        // Fallback chain: qer_editorimage → env-mapped stage image
        imagePath ??= editorImage ?? envMapImage;

        return new ShaderDef
        {
            Name = name,
            ImagePath = imagePath,
            Clamp = clamp,
            Blend = blend,
            SkyBox = skyBox,
            IsTransparent = isTransparent
        };
    }

    /// <summary>
    /// Parse a blendFunc directive: either shorthand (add/filter/blend)
    /// or long form (GL_ONE GL_ONE, etc.)
    /// </summary>
    private static BlendMode ParseBlendFunc(ref ShaderTokenizer tokenizer)
    {
        string? src = tokenizer.NextToken();
        if (src == null || src == "{" || src == "}") return BlendMode.Alpha;

        // Shorthand forms
        if (string.Equals(src, "add", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Add;
        if (string.Equals(src, "filter", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Filter;
        if (string.Equals(src, "blend", StringComparison.OrdinalIgnoreCase))
            return BlendMode.Alpha;

        // Long form: blendFunc <srcFactor> <dstFactor>
        string? dst = tokenizer.NextToken();
        if (dst == null || dst == "{" || dst == "}") return BlendMode.Alpha;

        return new BlendMode
        {
            SrcFactor = ParseGLFactor(src),
            DstFactor = ParseGLFactor(dst)
        };
    }

    private static int ParseGLFactor(string factor)
    {
        if (factor.Equals("GL_ZERO", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ZERO;
        if (factor.Equals("GL_ONE", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE;
        if (factor.Equals("GL_SRC_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_COLOR;
        if (factor.Equals("GL_ONE_MINUS_SRC_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_SRC_COLOR;
        if (factor.Equals("GL_SRC_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_ALPHA;
        if (factor.Equals("GL_ONE_MINUS_SRC_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_SRC_ALPHA;
        if (factor.Equals("GL_DST_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_DST_ALPHA;
        if (factor.Equals("GL_ONE_MINUS_DST_ALPHA", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_DST_ALPHA;
        if (factor.Equals("GL_DST_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_DST_COLOR;
        if (factor.Equals("GL_ONE_MINUS_DST_COLOR", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_ONE_MINUS_DST_COLOR;
        if (factor.Equals("GL_SRC_ALPHA_SATURATE", StringComparison.OrdinalIgnoreCase)) return BlendMode.GL_SRC_ALPHA_SATURATE;
        return BlendMode.GL_ONE; // Default
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

    /// <summary>Blend mode from the first stage's blendFunc directive.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Skybox outer box texture base name from skyparms directive (e.g. "env/tim_dm14/dm14").</summary>
    public string? SkyBox { get; init; }

    /// <summary>Whether the shader has surfaceparm trans (truly transparent).</summary>
    public bool IsTransparent { get; init; }
}

/// <summary>
/// Q3 blend modes storing actual GL blend factors for correct rendering.
/// </summary>
public struct BlendMode : System.IEquatable<BlendMode>
{
    public int SrcFactor;
    public int DstFactor;

    // GL blend factor constants (matching OpenGL)
    public const int GL_ZERO = 0;
    public const int GL_ONE = 1;
    public const int GL_SRC_COLOR = 0x0300;
    public const int GL_ONE_MINUS_SRC_COLOR = 0x0301;
    public const int GL_SRC_ALPHA = 0x0302;
    public const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int GL_DST_ALPHA = 0x0304;
    public const int GL_ONE_MINUS_DST_ALPHA = 0x0305;
    public const int GL_DST_COLOR = 0x0306;
    public const int GL_ONE_MINUS_DST_COLOR = 0x0307;
    public const int GL_SRC_ALPHA_SATURATE = 0x0308;

    public static readonly BlendMode Opaque = new() { SrcFactor = GL_ONE, DstFactor = GL_ZERO };
    public static readonly BlendMode Alpha = new() { SrcFactor = GL_SRC_ALPHA, DstFactor = GL_ONE_MINUS_SRC_ALPHA };
    public static readonly BlendMode Add = new() { SrcFactor = GL_ONE, DstFactor = GL_ONE };
    public static readonly BlendMode Filter = new() { SrcFactor = GL_DST_COLOR, DstFactor = GL_ZERO };

    public bool IsOpaque => SrcFactor == GL_ONE && DstFactor == GL_ZERO;
    public bool NeedsBlending => !IsOpaque;

    public bool Equals(BlendMode other) => SrcFactor == other.SrcFactor && DstFactor == other.DstFactor;
    public override bool Equals(object? obj) => obj is BlendMode other && Equals(other);
    public override int GetHashCode() => System.HashCode.Combine(SrcFactor, DstFactor);
    public static bool operator ==(BlendMode a, BlendMode b) => a.Equals(b);
    public static bool operator !=(BlendMode a, BlendMode b) => !a.Equals(b);
}
