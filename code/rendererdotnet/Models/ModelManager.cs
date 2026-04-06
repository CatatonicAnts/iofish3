using System.Collections.Generic;
using RendererDotNet.Interop;

namespace RendererDotNet.Models;

/// <summary>
/// Manages model registration and lookup. Models are loaded from the engine
/// filesystem (pk3 archives) and assigned integer handles, similar to ShaderManager.
/// Supports MD3 models with LOD levels.
/// </summary>
public sealed class ModelManager
{
    private readonly List<ModelEntry> _models = [];
    private readonly Dictionary<string, int> _nameToHandle = new(System.StringComparer.OrdinalIgnoreCase);
    private ShaderManager? _shaders;
    private World.BspWorld? _bspWorld;

    public ModelManager()
    {
        // Handle 0 = invalid/default
        _models.Add(new ModelEntry { Name = "<default>" });
    }

    public void SetShaderManager(ShaderManager shaders)
    {
        _shaders = shaders;
    }

    public void SetBspWorld(World.BspWorld? world)
    {
        _bspWorld = world;
    }

    /// <summary>
    /// Register a model by name. Loads MD3 from the engine filesystem.
    /// Returns a handle > 0 on success, 0 on failure.
    /// Mirrors R_RegisterModel from tr_model.c.
    /// </summary>
    public unsafe int Register(byte* namePtr)
    {
        if (namePtr == null) return 0;
        string name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)namePtr) ?? "";
        if (string.IsNullOrEmpty(name)) return 0;

        return Register(name);
    }

    public int Register(string name)
    {
        if (_nameToHandle.TryGetValue(name, out int existing))
        {
            var cached = _models[existing];
            return (cached.Model != null || cached.BspModelIndex >= 0) ? existing : 0;
        }

        int handle = _models.Count;
        var entry = new ModelEntry { Name = name };
        _models.Add(entry);
        _nameToHandle[name] = handle;

        // Inline BSP models: "*N" references a submodel in the loaded BSP
        if (name.StartsWith('*'))
        {
            if (int.TryParse(name.AsSpan(1), out int bspIndex) && bspIndex >= 0)
            {
                entry.BspModelIndex = bspIndex;
                return handle;
            }
            return 0;
        }

        // Try to load the model
        entry.Model = LoadModel(name);

        if (entry.Model == null)
        {
            EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                $"[.NET] Could not load model: {name}\n");
            return 0;
        }

        // Resolve surface shaders
        if (_shaders != null)
        {
            foreach (var surface in entry.Model.Surfaces)
            {
                if (!string.IsNullOrEmpty(surface.ShaderName))
                    surface.ShaderHandle = _shaders.Register(surface.ShaderName);
            }
        }

        EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
            $"[.NET] Loaded model: {name} ({entry.Model.Surfaces.Length} surfaces, {entry.Model.NumFrames} frames)\n");

        return handle;
    }

    /// <summary>
    /// Get the model data for a handle. Returns null for invalid handles or BSP models.
    /// </summary>
    public Md3Model? GetModel(int handle)
    {
        if (handle <= 0 || handle >= _models.Count)
            return null;
        return _models[handle].Model;
    }

    /// <summary>
    /// Check if the given handle is an inline BSP model (e.g. doors, platforms).
    /// </summary>
    public bool IsBspModel(int handle)
    {
        if (handle <= 0 || handle >= _models.Count)
            return false;
        return _models[handle].BspModelIndex >= 0;
    }

    /// <summary>
    /// Get the BSP submodel index for an inline model handle. Returns -1 if not a BSP model.
    /// </summary>
    public int GetBspModelIndex(int handle)
    {
        if (handle <= 0 || handle >= _models.Count)
            return -1;
        return _models[handle].BspModelIndex;
    }

    /// <summary>
    /// Get model bounding box from frame 0. Pointer-based overload for interop.
    /// </summary>
    public unsafe void GetBounds(int handle, float* mins, float* maxs)
    {
        if (GetBounds(handle, out float minX, out float minY, out float minZ,
                      out float maxX, out float maxY, out float maxZ))
        {
            mins[0] = minX; mins[1] = minY; mins[2] = minZ;
            maxs[0] = maxX; maxs[1] = maxY; maxs[2] = maxZ;
        }
        else
        {
            mins[0] = mins[1] = mins[2] = 0;
            maxs[0] = maxs[1] = maxs[2] = 0;
        }
    }

    /// <summary>
    /// Get model bounding box from frame 0.
    /// </summary>
    public bool GetBounds(int handle, out float minX, out float minY, out float minZ,
                          out float maxX, out float maxY, out float maxZ)
    {
        minX = minY = minZ = maxX = maxY = maxZ = 0;

        // Check for BSP inline model
        int bspIdx = GetBspModelIndex(handle);
        if (bspIdx >= 0 && _bspWorld != null && bspIdx < _bspWorld.Models.Length)
        {
            ref var bmodel = ref _bspWorld.Models[bspIdx];
            minX = bmodel.MinX; minY = bmodel.MinY; minZ = bmodel.MinZ;
            maxX = bmodel.MaxX; maxY = bmodel.MaxY; maxZ = bmodel.MaxZ;
            return true;
        }

        var model = GetModel(handle);
        if (model == null || model.Frames.Length == 0)
            return false;

        ref var frame = ref model.Frames[0];
        minX = frame.MinX; minY = frame.MinY; minZ = frame.MinZ;
        maxX = frame.MaxX; maxY = frame.MaxY; maxZ = frame.MaxZ;
        return true;
    }

    /// <summary>
    /// Interpolate a tag between two frames. Writes to orientation_t* (origin[3] + axis[3][3]).
    /// </summary>
    public unsafe int LerpTag(nint tagPtr, int handle, int startFrame, int endFrame,
                              float frac, byte* tagNamePtr)
    {
        string tagName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)tagNamePtr) ?? "";
        if (!LerpTag(handle, startFrame, endFrame, frac, tagName, out var tag))
            return 0;

        float* p = (float*)tagPtr;
        // origin
        p[0] = tag.OriginX; p[1] = tag.OriginY; p[2] = tag.OriginZ;
        // axis[0]
        p[3] = tag.Ax0; p[4] = tag.Ax1; p[5] = tag.Ax2;
        // axis[1]
        p[6] = tag.Ay0; p[7] = tag.Ay1; p[8] = tag.Ay2;
        // axis[2]
        p[9] = tag.Az0; p[10] = tag.Az1; p[11] = tag.Az2;
        return 1;
    }

    /// <summary>
    /// Interpolate a tag between two frames.
    /// Returns false if the tag or model is not found.
    /// </summary>
    public bool LerpTag(int handle, int startFrame, int endFrame, float frac,
                        string tagName, out Md3Tag result)
    {
        result = default;
        var model = GetModel(handle);
        if (model == null || model.NumTags == 0)
            return false;

        // Find tag index
        int tagIndex = -1;
        for (int i = 0; i < model.TagNames.Length; i++)
        {
            if (string.Equals(model.TagNames[i], tagName, System.StringComparison.OrdinalIgnoreCase))
            {
                tagIndex = i;
                break;
            }
        }
        if (tagIndex < 0)
            return false;

        // Clamp frames
        int numFrames = model.NumFrames;
        if (startFrame >= numFrames) startFrame = numFrames - 1;
        if (endFrame >= numFrames) endFrame = numFrames - 1;
        if (startFrame < 0) startFrame = 0;
        if (endFrame < 0) endFrame = 0;

        int numTags = model.NumTags;
        ref var startTag = ref model.Tags[startFrame * numTags + tagIndex];
        ref var endTag = ref model.Tags[endFrame * numTags + tagIndex];

        float backLerp = 1.0f - frac;

        // Interpolate origin
        result.OriginX = startTag.OriginX * backLerp + endTag.OriginX * frac;
        result.OriginY = startTag.OriginY * backLerp + endTag.OriginY * frac;
        result.OriginZ = startTag.OriginZ * backLerp + endTag.OriginZ * frac;

        // Interpolate axes
        result.Ax0 = startTag.Ax0 * backLerp + endTag.Ax0 * frac;
        result.Ax1 = startTag.Ax1 * backLerp + endTag.Ax1 * frac;
        result.Ax2 = startTag.Ax2 * backLerp + endTag.Ax2 * frac;
        result.Ay0 = startTag.Ay0 * backLerp + endTag.Ay0 * frac;
        result.Ay1 = startTag.Ay1 * backLerp + endTag.Ay1 * frac;
        result.Ay2 = startTag.Ay2 * backLerp + endTag.Ay2 * frac;
        result.Az0 = startTag.Az0 * backLerp + endTag.Az0 * frac;
        result.Az1 = startTag.Az1 * backLerp + endTag.Az1 * frac;
        result.Az2 = startTag.Az2 * backLerp + endTag.Az2 * frac;

        return true;
    }

    public int Count => _models.Count - 1;

    /// <summary>
    /// Load a model, trying LOD variants and file extensions.
    /// Mirrors R_RegisterMD3 from tr_model.c.
    /// </summary>
    private static Md3Model? LoadModel(string name)
    {
        // Strip extension
        string baseName = name;
        string ext = "md3";
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            ext = name[(dotIdx + 1)..];
            baseName = name[..dotIdx];
        }

        // Try LOD 0 first (highest detail)
        string path = $"{baseName}.{ext}";
        var model = Md3Loader.LoadFromEngineFS(path);

        // Also try without LOD suffix if that fails
        if (model == null && ext == "md3")
        {
            // Try .md3 with exact name
            model = Md3Loader.LoadFromEngineFS(name);
        }

        return model;
    }

    private class ModelEntry
    {
        public string Name { get; set; } = "";
        public Md3Model? Model { get; set; }
        public int BspModelIndex { get; set; } = -1;
    }
}
