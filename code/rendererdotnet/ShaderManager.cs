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
    private readonly Dictionary<int, int> _remaps = new();
    private Renderer2D? _renderer;
    private ShaderScriptParser? _scriptParser;

    public uint WhiteTexture { get; set; }

    /// <summary>Current renderer time in milliseconds, updated per-frame from refdef_t.time.</summary>
    public int CurrentTimeMs { get; set; }

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

    /// <summary>
    /// Remap one shader to another at runtime (e.g. team colors, powerup effects).
    /// </summary>
    public void RemapShader(string oldShader, string newShader)
    {
        int oldHandle = Register(oldShader);
        int newHandle = Register(newShader);
        if (oldHandle > 0 && newHandle > 0 && oldHandle != newHandle)
            _remaps[oldHandle] = newHandle;
    }

    /// <summary>
    /// Resolve a handle through any active remaps.
    /// </summary>
    private int ResolveRemap(int handle)
    {
        return _remaps.TryGetValue(handle, out int remapped) ? remapped : handle;
    }

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
        handle = ResolveRemap(handle);
        if (handle <= 0 || handle >= _shaders.Count)
            return WhiteTexture;

        var entry = _shaders[handle];

        // Lazy load: try to load the texture on first access
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        // Animated texture: cycle through frames based on time
        if (entry.AnimTextureIds != null && entry.AnimTextureIds.Length > 0 && entry.AnimFrequency > 0)
        {
            float timeSec = CurrentTimeMs / 1000.0f;
            int frameIdx = (int)(timeSec * entry.AnimFrequency) % entry.AnimTextureIds.Length;
            if (frameIdx < 0) frameIdx = 0;
            return entry.AnimTextureIds[frameIdx] != 0 ? entry.AnimTextureIds[frameIdx] : WhiteTexture;
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

    public int GetAlphaFunc(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return 0;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.AlphaFunc;
    }

    public int GetCullMode(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return 0;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.CullMode;
    }

    public bool GetHasEnvMap(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return false;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.HasEnvMap;
    }

    public TcMod[]? GetTcMods(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return null;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.TcMods;
    }

    public int GetRgbGen(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return 0;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.RgbGen;
    }

    public bool GetPolygonOffset(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return false;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.PolygonOffset;
    }

    public int GetDepthFunc(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return 0;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.DepthFunc;
    }

    public int GetSortKey(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return 0;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.SortKey;
    }

    public DeformVertexes[]? GetDeforms(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return null;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.Deforms;
    }

    public bool IsTransparent(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return false;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.IsTransparent;
    }

    private void TryLoadTexture(ShaderEntry entry)
    {
        if (_renderer == null) return;

        // First: try loading the image directly by shader name
        var image = ImageLoader.LoadFromEngineFS(entry.Name);

        // Look up shader script for metadata (blend, alpha test, transparency)
        ShaderDef? def = _scriptParser?.GetShaderDef(entry.Name);

        // If direct load failed, try the script's image path
        if (image == null && def?.ImagePath != null && !def.ImagePath.StartsWith('*'))
        {
            image = ImageLoader.LoadFromEngineFS(def.ImagePath);
            if (image != null)
                entry.Clamp = def.Clamp;
        }

        // Apply shader script metadata regardless of how the image was loaded
        if (def != null)
        {
            entry.Blend = def.Blend;
            entry.IsTransparent = def.IsTransparent;
            entry.AlphaFunc = def.AlphaFunc;
            entry.CullMode = def.CullMode;
            entry.HasEnvMap = def.HasEnvMap;
            entry.TcMods = def.TcMods;
            entry.RgbGen = def.RgbGen;
            entry.AlphaGen = def.AlphaGen;
            entry.PolygonOffset = def.PolygonOffset;
            entry.DepthFunc = def.DepthFunc;
            entry.DepthWrite = def.DepthWrite;
            entry.SortKey = def.SortKey;
            entry.Deforms = def.Deforms;

            // Load animated texture frames
            if (def.AnimFrames != null && def.AnimFrames.Length > 1)
            {
                entry.AnimFrequency = def.AnimFrequency;
                var animIds = new uint[def.AnimFrames.Length];
                for (int i = 0; i < def.AnimFrames.Length; i++)
                {
                    var frameImage = ImageLoader.LoadFromEngineFS(def.AnimFrames[i]);
                    if (frameImage != null && _renderer != null)
                    {
                        fixed (byte* frameData = frameImage.Data)
                        {
                            animIds[i] = _renderer.CreateTexture(
                                frameImage.Width, frameImage.Height, frameData, clamp: entry.Clamp);
                        }
                    }
                }
                entry.AnimTextureIds = animIds;
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
        public bool IsTransparent { get; set; }
        /// <summary>0=none, 1=GT0, 2=LT128, 3=GE128</summary>
        public int AlphaFunc { get; set; }
        /// <summary>0=front (default), 1=back, 2=none/twosided</summary>
        public int CullMode { get; set; }
        /// <summary>Any stage uses tcGen environment</summary>
        public bool HasEnvMap { get; set; }
        /// <summary>Loaded texture IDs for animated frames (null if not animated)</summary>
        public uint[]? AnimTextureIds { get; set; }
        /// <summary>Animation frequency in FPS</summary>
        public float AnimFrequency { get; set; }
        /// <summary>tcMod operations for this shader</summary>
        public TcMod[]? TcMods { get; set; }
        /// <summary>0=identity, 1=vertex, 2=entity, 3=wave, 4=identityLighting</summary>
        public int RgbGen { get; set; }
        /// <summary>0=identity, 1=vertex, 2=entity, 3=wave</summary>
        public int AlphaGen { get; set; }
        public bool PolygonOffset { get; set; }
        /// <summary>0=lequal, 1=equal</summary>
        public int DepthFunc { get; set; }
        public bool DepthWrite { get; set; }
        /// <summary>Sort key: 0=auto, 3=opaque, 4=decal, 5=seeThrough, etc.</summary>
        public int SortKey { get; set; }
        /// <summary>deformVertexes operations</summary>
        public DeformVertexes[]? Deforms { get; set; }
    }
}
