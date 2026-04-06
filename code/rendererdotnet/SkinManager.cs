using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RendererDotNet.Interop;

namespace RendererDotNet;

/// <summary>
/// Manages .skin files. A skin maps MD3 surface names to shader handles,
/// allowing the same model to render with different textures.
/// Format: "surfaceName,shaderPath" per line, with tag_ lines skipped.
/// </summary>
public sealed class SkinManager
{
    private readonly List<SkinEntry> _skins = [];
    private readonly Dictionary<string, int> _nameToHandle = new(StringComparer.OrdinalIgnoreCase);
    private ShaderManager? _shaders;

    public SkinManager()
    {
        // Handle 0 = invalid
        _skins.Add(new SkinEntry { Name = "<default>" });
    }

    public void SetShaderManager(ShaderManager shaders)
    {
        _shaders = shaders;
    }

    public unsafe int Register(byte* namePtr)
    {
        if (namePtr == null) return 0;
        string name = Marshal.PtrToStringUTF8((nint)namePtr) ?? "";
        return Register(name);
    }

    public int Register(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        // Check cache
        if (_nameToHandle.TryGetValue(name, out int existing))
        {
            var cached = _skins[existing];
            return cached.Surfaces.Count > 0 ? existing : 0;
        }

        int handle = _skins.Count;
        var entry = new SkinEntry { Name = name };
        _skins.Add(entry);
        _nameToHandle[name] = handle;

        // If not a .skin file, treat as a single shader
        if (!name.EndsWith(".skin", StringComparison.OrdinalIgnoreCase))
        {
            int sh = _shaders?.Register(name) ?? 0;
            if (sh > 0)
                entry.Surfaces["_default"] = sh;
            return sh > 0 ? handle : 0;
        }

        // Load and parse .skin file from engine FS
        if (!LoadSkinFile(name, entry))
        {
            EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                $"[.NET] Could not load skin: {name}\n");
            return 0;
        }

        if (entry.Surfaces.Count == 0)
            return 0;

        return handle;
    }

    /// <summary>
    /// Look up the shader handle for a given surface name within a skin.
    /// Returns 0 if not found.
    /// </summary>
    public int GetSurfaceShader(int skinHandle, string surfaceName)
    {
        if (skinHandle <= 0 || skinHandle >= _skins.Count)
            return 0;

        var skin = _skins[skinHandle];
        if (skin.Surfaces.TryGetValue(surfaceName, out int sh))
            return sh;
        return 0;
    }

    /// <summary>
    /// Parse a .skin file. Format is comma-separated lines:
    ///   surfaceName,shaderPath
    /// Lines with "tag_" surface names are skipped.
    /// </summary>
    private unsafe bool LoadSkinFile(string name, SkinEntry entry)
    {
        int len = EngineImports.FS_ReadFile(name, out byte* buf);
        if (len <= 0 || buf == null)
            return false;

        try
        {
            string text = Marshal.PtrToStringUTF8((nint)buf, len) ?? "";
            ParseSkinText(text, entry);
        }
        finally
        {
            EngineImports.FS_FreeFile(buf);
        }

        return entry.Surfaces.Count > 0;
    }

    private void ParseSkinText(string text, SkinEntry entry)
    {
        foreach (string rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            int comma = line.IndexOf(',');
            if (comma < 0) continue;

            string surfName = line[..comma].Trim().ToLowerInvariant();
            string shaderPath = line[(comma + 1)..].Trim();

            // Skip tag surfaces
            if (surfName.StartsWith("tag_", StringComparison.Ordinal))
                continue;

            if (string.IsNullOrEmpty(shaderPath))
                continue;

            // Register the shader for this surface
            int sh = _shaders?.Register(shaderPath) ?? 0;
            entry.Surfaces[surfName] = sh;
        }
    }

    private class SkinEntry
    {
        public string Name { get; set; } = "";
        public Dictionary<string, int> Surfaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
