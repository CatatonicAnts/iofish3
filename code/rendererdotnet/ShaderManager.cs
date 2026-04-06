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
            return BlendMode.Opaque;

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

    /// <summary>
    /// Get multi-stage rendering data for a shader. Returns null for single-stage shaders.
    /// </summary>
    public RuntimeStage[]? GetStages(int handle)
    {
        handle = ResolveRemap(handle);
        if (handle <= 0 || handle >= _shaders.Count)
            return null;

        var entry = _shaders[handle];
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            TryLoadTexture(entry);
        }

        return entry.Stages;
    }

    /// <summary>
    /// Get the texture ID for a specific stage, handling animation cycling.
    /// </summary>
    public uint GetStageTextureId(RuntimeStage stage, float timeSec)
    {
        if (stage.AnimTextureIds != null && stage.AnimTextureIds.Length > 0 && stage.AnimFrequency > 0)
        {
            int frameIdx = (int)(timeSec * stage.AnimFrequency) % stage.AnimTextureIds.Length;
            if (frameIdx < 0) frameIdx = 0;
            return stage.AnimTextureIds[frameIdx] != 0 ? stage.AnimTextureIds[frameIdx] : WhiteTexture;
        }
        if (stage.IsWhiteImage) return WhiteTexture;
        return stage.TextureId != 0 ? stage.TextureId : WhiteTexture;
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

    public bool GetNoDLight(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return false;
        var entry = _shaders[handle];
        if (!entry.Loaded) { entry.Loaded = true; TryLoadTexture(entry); }
        return entry.NoDLight;
    }

    public bool GetNoMarks(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count)
            return false;
        var entry = _shaders[handle];
        if (!entry.Loaded) { entry.Loaded = true; TryLoadTexture(entry); }
        return entry.NoMarks;
    }

    /// <summary>Get normal map texture ID for a shader handle (0 = none).</summary>
    public uint GetNormalMapTexId(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count) return 0;
        var entry = _shaders[handle];
        if (!entry.Loaded) { entry.Loaded = true; TryLoadTexture(entry); }
        return entry.NormalMapTexId;
    }

    /// <summary>Get specular map texture ID for a shader handle (0 = none).</summary>
    public uint GetSpecularMapTexId(int handle)
    {
        if (handle <= 0 || handle >= _shaders.Count) return 0;
        var entry = _shaders[handle];
        if (!entry.Loaded) { entry.Loaded = true; TryLoadTexture(entry); }
        return entry.SpecularMapTexId;
    }

    private void TryLoadTexture(ShaderEntry entry)
    {
        if (_renderer == null) return;

        // First: try loading the image directly by shader name (DDS → TGA → JPG → PNG)
        var texResult = ImageLoader.LoadTextureFromEngineFS(entry.Name);

        // Look up shader script for metadata (blend, alpha test, transparency)
        ShaderDef? def = _scriptParser?.GetShaderDef(entry.Name);

        // If direct load failed, try the script's image path
        if (texResult == null && def?.ImagePath != null && !def.ImagePath.StartsWith('*'))
        {
            texResult = ImageLoader.LoadTextureFromEngineFS(def.ImagePath);
            if (texResult != null)
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
            entry.NoDLight = def.NoDLight;
            entry.NoMarks = def.NoMarks;

            // Load normal map if specified
            if (def.NormalMapPath != null && _renderer != null)
            {
                entry.NormalMapTexId = UploadTexture(
                    ImageLoader.LoadTextureFromEngineFS(def.NormalMapPath), false, true);
            }

            // Load specular map if specified
            if (def.SpecularMapPath != null && _renderer != null)
            {
                entry.SpecularMapTexId = UploadTexture(
                    ImageLoader.LoadTextureFromEngineFS(def.SpecularMapPath), false, true);
            }

            // Determine mipmap generation policy
            bool useMipmaps = !def.NoMipMaps;

            // Load animated texture frames (single-stage fallback path)
            if (def.AnimFrames != null && def.AnimFrames.Length > 1)
            {
                entry.AnimFrequency = def.AnimFrequency;
                var animIds = new uint[def.AnimFrames.Length];
                for (int i = 0; i < def.AnimFrames.Length; i++)
                {
                    animIds[i] = UploadTexture(
                        ImageLoader.LoadTextureFromEngineFS(def.AnimFrames[i]),
                        entry.Clamp, useMipmaps);
                }
                entry.AnimTextureIds = animIds;
            }

            // Load multi-stage data
            if (def.Stages != null && def.Stages.Length > 0)
            {
                var runtimeStages = new RuntimeStage[def.Stages.Length];
                for (int si = 0; si < def.Stages.Length; si++)
                {
                    var src = def.Stages[si];
                    var rs = new RuntimeStage
                    {
                        IsLightmap = src.IsLightmap,
                        IsWhiteImage = src.IsWhiteImage,
                        Blend = src.Blend,
                        AlphaFunc = src.AlphaFunc,
                        HasEnvMap = src.HasEnvMap,
                        TcMods = src.TcMods,
                        RgbGen = src.RgbGen,
                        AlphaGen = src.AlphaGen,
                        DepthFunc = src.DepthFunc,
                        DepthWrite = src.DepthWrite,
                        AnimFrequency = src.AnimFrequency,
                    };

                    // Load stage texture
                    if (src.AnimFrames != null && src.AnimFrames.Length > 1)
                    {
                        var sAnimIds = new uint[src.AnimFrames.Length];
                        for (int fi = 0; fi < src.AnimFrames.Length; fi++)
                        {
                            sAnimIds[fi] = UploadTexture(
                                ImageLoader.LoadTextureFromEngineFS(src.AnimFrames[fi]),
                                src.Clamp, useMipmaps);
                        }
                        rs.AnimTextureIds = sAnimIds;
                        if (sAnimIds.Length > 0) rs.TextureId = sAnimIds[0];
                    }
                    else if (src.ImagePath != null && !src.IsLightmap && !src.IsWhiteImage)
                    {
                        rs.TextureId = UploadTexture(
                            ImageLoader.LoadTextureFromEngineFS(src.ImagePath),
                            src.Clamp, useMipmaps);
                    }

                    runtimeStages[si] = rs;
                }
                entry.Stages = runtimeStages;
            }
        }

        if (texResult == null)
        {
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_DEVELOPER,
                $"[.NET] Could not load texture: {entry.Name}\n");
            return;
        }

        bool mips2 = def == null || !def.NoMipMaps;
        entry.TextureId = UploadTexture(texResult, entry.Clamp, mips2);

        Interop.EngineImports.Printf(Interop.EngineImports.PRINT_DEVELOPER,
            $"[.NET] Loaded texture: {entry.Name} ({texResult.Width}x{texResult.Height}" +
            $"{(texResult.IsDds ? " DDS" : "")})\n");
    }

    /// <summary>Upload a TextureResult (DDS compressed or standard RGBA) to GPU.</summary>
    private uint UploadTexture(ImageLoader.TextureResult? tex, bool clamp, bool generateMipmaps)
    {
        if (tex == null || _renderer == null) return 0;

        if (tex.IsDds)
        {
            return _renderer.CreateCompressedTexture(tex.Dds!, clamp);
        }
        else
        {
            var img = tex.Image!;
            fixed (byte* data = img.Data)
                return _renderer.CreateTexture(img.Width, img.Height, data,
                    clamp: clamp, generateMipmaps: generateMipmaps);
        }
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
        public BlendMode Blend { get; set; } = BlendMode.Opaque;
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
        /// <summary>Multi-stage rendering data (null = single-stage shader)</summary>
        public RuntimeStage[]? Stages { get; set; }
        /// <summary>Surface ignores dynamic lights</summary>
        public bool NoDLight { get; set; }
        /// <summary>Surface doesn't receive impact marks</summary>
        public bool NoMarks { get; set; }
        /// <summary>Normal map texture ID (0 = none)</summary>
        public uint NormalMapTexId { get; set; }
        /// <summary>Specular map texture ID (0 = none)</summary>
        public uint SpecularMapTexId { get; set; }
    }

    /// <summary>
    /// Per-stage runtime data for multi-pass rendering.
    /// </summary>
    public sealed class RuntimeStage
    {
        /// <summary>GL texture ID for this stage (0 = use lightmap or white)</summary>
        public uint TextureId { get; set; }
        /// <summary>Animated frame texture IDs (null if not animated)</summary>
        public uint[]? AnimTextureIds { get; set; }
        /// <summary>Animation frequency</summary>
        public float AnimFrequency { get; set; }
        /// <summary>True if this stage uses $lightmap</summary>
        public bool IsLightmap { get; set; }
        /// <summary>True if this stage uses $whiteimage</summary>
        public bool IsWhiteImage { get; set; }
        /// <summary>Blend mode for this stage</summary>
        public BlendMode Blend { get; set; }
        /// <summary>0=none, 1=GT0, 2=LT128, 3=GE128</summary>
        public int AlphaFunc { get; set; }
        /// <summary>Whether this stage uses tcGen environment</summary>
        public bool HasEnvMap { get; set; }
        /// <summary>tcMod operations</summary>
        public TcMod[]? TcMods { get; set; }
        /// <summary>0=identity, 1=vertex, 2=entity, 3=wave, 4=identityLighting</summary>
        public int RgbGen { get; set; }
        /// <summary>0=identity, 1=vertex, 2=entity, 3=wave</summary>
        public int AlphaGen { get; set; }
        /// <summary>0=lequal, 1=equal</summary>
        public int DepthFunc { get; set; }
        /// <summary>Whether depthWrite is set</summary>
        public bool DepthWrite { get; set; }
    }
}
