using System.Collections.Generic;

namespace RendererDotNet;

/// <summary>
/// Maps engine shader handles (int) to GL texture IDs (uint).
/// Handle 0 is reserved (invalid), handle 1+ are valid.
/// Lazily loads textures from the engine filesystem on first use.
///
/// When a direct image file lookup fails, falls back to the shader script
/// parser to resolve shader names to their actual image paths (as defined
/// in scripts/*.shader files).
/// </summary>
public unsafe class ShaderManager
{
    private readonly List<ShaderEntry> _shaders = [];
    private readonly Dictionary<string, int> _nameToHandle = new(System.StringComparer.OrdinalIgnoreCase);
    private Renderer2D? _renderer;
    private ShaderScriptParser? _scriptParser;

    public uint WhiteTexture { get; set; }

    public ShaderManager()
    {
        // Handle 0 = invalid/default, occupies index 0
        _shaders.Add(new ShaderEntry { Name = "<default>", TextureId = 0 });
    }

    public void SetRenderer(Renderer2D renderer)
    {
        _renderer = renderer;
    }

    /// <summary>
    /// Load and parse all Q3 shader script files. Must be called after
    /// the engine filesystem is initialized (after BeginRegistration).
    /// </summary>
    public void LoadShaderScripts()
    {
        _scriptParser = new ShaderScriptParser();
        _scriptParser.LoadAllShaders();
    }

    public ShaderScriptParser? GetScriptParser() => _scriptParser;

    public int Register(byte* namePtr)
    {
        if (namePtr == null) return 0;

        string name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)namePtr) ?? "";
        if (string.IsNullOrEmpty(name)) return 0;

        return Register(name);
    }

    public int Register(string name)
    {
        if (_nameToHandle.TryGetValue(name, out int existing))
            return existing;

        int handle = _shaders.Count;
        _shaders.Add(new ShaderEntry { Name = name, TextureId = 0, Loaded = false });
        _nameToHandle[name] = handle;

        return handle;
    }

    public uint GetTextureId(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return WhiteTexture;

        var entry = _shaders[handle];

        // Lazy load: try to load the texture on first access
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.TextureId != 0 ? entry.TextureId : WhiteTexture;
    }

    public BlendMode GetBlendMode(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return BlendMode.Alpha;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.Blend;
    }

    private void TryLoadTexture(ShaderEntry entry)
    {
        if (_renderer == null) return;

        // First: try loading the image directly by shader name
        var image = ImageLoader.LoadFromEngineFS(entry.Name);

        // Fallback: look up the shader script to find the actual image path
        if (image == null && _scriptParser != null)
        {
            var def = _scriptParser.GetShaderDef(entry.Name);
            if (def?.ImagePath != null && !def.ImagePath.StartsWith('*'))
            {
                image = ImageLoader.LoadFromEngineFS(def.ImagePath);
                if (image != null)
                {
                    entry.Clamp = def.Clamp;
                    entry.Blend = def.Blend;
                }
            }
            else if (def != null)
            {
                // No image but shader def exists — still apply blend mode
                entry.Blend = def.Blend;
            }
        }

        if (image == null)
        {
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_DEVELOPER,
                $"[.NET] Could not load texture: {entry.Name}\n");
            return;
        }

        fixed (byte* data = image.Data)
        {
            entry.TextureId = _renderer.CreateTexture(
                image.Width, image.Height, data, clamp: entry.Clamp);
        }

        Interop.EngineImports.Printf(Interop.EngineImports.PRINT_DEVELOPER,
            $"[.NET] Loaded texture: {entry.Name} ({image.Width}x{image.Height})\n");
    }

    public string? GetName(int handle)
    {
        if (handle >= 0 && handle < _shaders.Count)
            return _shaders[handle].Name;
        return null;
    }

    public int Count => _shaders.Count;

    public void Clear()
    {
        _shaders.Clear();
        _nameToHandle.Clear();
        _shaders.Add(new ShaderEntry { Name = "<default>", TextureId = 0 });
    }

    private class ShaderEntry
    {
        public string Name { get; set; } = "";
        public uint TextureId { get; set; }
        public bool Loaded { get; set; }
        public bool Clamp { get; set; }
        public BlendMode Blend { get; set; } = BlendMode.Alpha;
    }
}
