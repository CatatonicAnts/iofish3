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
            return (cached.Model != null || cached.IqmModel != null || cached.BspModelIndex >= 0) ? existing : 0;
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

        // Try IQM first if extension is .iqm, or as fallback after MD3
        string ext = "";
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0) ext = name[(dotIdx + 1)..];

        if (ext.Equals("iqm", System.StringComparison.OrdinalIgnoreCase))
        {
            entry.IqmModel = LoadIqmModel(name);
        }
        else
        {
            entry.Model = LoadModel(name);
            // Try IQM as fallback
            if (entry.Model == null)
                entry.IqmModel = LoadIqmModel(name);
        }

        if (entry.Model == null && entry.IqmModel == null)
        {
            EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                $"[.NET] Could not load model: {name}\n");
            return 0;
        }

        // Resolve surface shaders
        if (_shaders != null)
        {
            if (entry.Model != null)
            {
                foreach (var surface in entry.Model.Surfaces)
                {
                    if (!string.IsNullOrEmpty(surface.ShaderName))
                        surface.ShaderHandle = _shaders.Register(surface.ShaderName);
                }
            }
            else if (entry.IqmModel != null)
            {
                foreach (var surface in entry.IqmModel.Surfaces)
                {
                    if (!string.IsNullOrEmpty(surface.ShaderName))
                        surface.ShaderHandle = _shaders.Register(surface.ShaderName);
                }
            }
        }

        if (entry.Model != null)
        {
            EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                $"[.NET] Loaded model: {name} ({entry.Model.Surfaces.Length} surfaces, {entry.Model.NumFrames} frames)\n");
        }
        else if (entry.IqmModel != null)
        {
            EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                $"[.NET] Loaded IQM: {name} ({entry.IqmModel.Surfaces.Length} surfaces, {entry.IqmModel.NumJoints} joints, {entry.IqmModel.NumFrames} frames)\n");
        }

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
    /// Get the IQM model data for a handle. Returns null if not an IQM model.
    /// </summary>
    public IqmModel? GetIqmModel(int handle)
    {
        if (handle <= 0 || handle >= _models.Count)
            return null;
        return _models[handle].IqmModel;
    }

    /// <summary>
    /// Check if the given handle is an IQM model.
    /// </summary>
    public bool IsIqmModel(int handle)
    {
        if (handle <= 0 || handle >= _models.Count)
            return false;
        return _models[handle].IqmModel != null;
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
        if (model != null && model.Frames.Length > 0)
        {
            ref var frame = ref model.Frames[0];
            minX = frame.MinX; minY = frame.MinY; minZ = frame.MinZ;
            maxX = frame.MaxX; maxY = frame.MaxY; maxZ = frame.MaxZ;
            return true;
        }

        // Check IQM model bounds
        var iqm = GetIqmModel(handle);
        if (iqm != null && iqm.Bounds.Length >= 6)
        {
            minX = iqm.Bounds[0]; minY = iqm.Bounds[1]; minZ = iqm.Bounds[2];
            maxX = iqm.Bounds[3]; maxY = iqm.Bounds[4]; maxZ = iqm.Bounds[5];
            return true;
        }

        return false;
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

        // Try IQM model (joints as tags)
        var iqm = GetIqmModel(handle);
        if (iqm != null)
            return LerpIqmTag(iqm, startFrame, endFrame, frac, tagName, out result);

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

    private static bool LerpIqmTag(IqmModel model, int startFrame, int endFrame, float frac,
                                    string tagName, out Md3Tag result)
    {
        result = default;
        int numJoints = model.NumJoints;
        if (numJoints == 0) return false;

        // Find joint by name
        int jointIdx = -1;
        for (int i = 0; i < numJoints; i++)
        {
            if (string.Equals(model.Joints[i].Name, tagName, System.StringComparison.OrdinalIgnoreCase))
            {
                jointIdx = i;
                break;
            }
        }
        if (jointIdx < 0) return false;

        // Compute pose matrices for the requested frame interpolation
        Span<float> poseMats = stackalloc float[numJoints * 12];
        float backLerp = 1.0f - frac;
        IqmLoader.ComputePoseMatrices(model, endFrame, startFrame, backLerp, poseMats);

        // Extract the joint's world-space 3x4 matrix
        // But we need to multiply by the bind pose (not inverse bind) to get world pos
        // Actually, ComputePoseMatrices outputs matrices that include invBindPose multiply,
        // so we need a different approach: compute the raw joint transform chain.
        // For tags, we actually want the joint's world-space transform without inverse bind.
        // Let's compute it directly from the pose data.

        // Simpler approach: compute the joint's world-space matrix using bind pose * poseMatrix
        // poseMatrix = parentPose * localPose * invBind
        // jointWorld = poseMatrix * bindPose
        var pm = poseMats.Slice(jointIdx * 12, 12);
        var bp = model.BindJoints.AsSpan(jointIdx * 12, 12);

        // Multiply: result = poseMatrix * bindPose
        Span<float> world = stackalloc float[12];
        // Row-major 3x4 multiply
        world[0] = pm[0] * bp[0] + pm[1] * bp[4] + pm[2] * bp[8];
        world[1] = pm[0] * bp[1] + pm[1] * bp[5] + pm[2] * bp[9];
        world[2] = pm[0] * bp[2] + pm[1] * bp[6] + pm[2] * bp[10];
        world[3] = pm[0] * bp[3] + pm[1] * bp[7] + pm[2] * bp[11] + pm[3];

        world[4] = pm[4] * bp[0] + pm[5] * bp[4] + pm[6] * bp[8];
        world[5] = pm[4] * bp[1] + pm[5] * bp[5] + pm[6] * bp[9];
        world[6] = pm[4] * bp[2] + pm[5] * bp[6] + pm[6] * bp[10];
        world[7] = pm[4] * bp[3] + pm[5] * bp[7] + pm[6] * bp[11] + pm[7];

        world[8] = pm[8] * bp[0] + pm[9] * bp[4] + pm[10] * bp[8];
        world[9] = pm[8] * bp[1] + pm[9] * bp[5] + pm[10] * bp[9];
        world[10] = pm[8] * bp[2] + pm[9] * bp[6] + pm[10] * bp[10];
        world[11] = pm[8] * bp[3] + pm[9] * bp[7] + pm[10] * bp[11] + pm[11];

        // Extract origin (column 3 of 3x4)
        result.OriginX = world[3];
        result.OriginY = world[7];
        result.OriginZ = world[11];

        // Extract axes (rows 0-2 of 3x3 portion)
        result.Ax0 = world[0]; result.Ax1 = world[1]; result.Ax2 = world[2];
        result.Ay0 = world[4]; result.Ay1 = world[5]; result.Ay2 = world[6];
        result.Az0 = world[8]; result.Az1 = world[9]; result.Az2 = world[10];

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

        // If explicitly requesting IQM, try that first
        if (ext.Equals("iqm", System.StringComparison.OrdinalIgnoreCase))
            return null; // IQM handled separately

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

    private static IqmModel? LoadIqmModel(string name)
    {
        // Strip extension and try .iqm
        string baseName = name;
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0)
            baseName = name[..dotIdx];

        string path = $"{baseName}.iqm";
        return IqmLoader.LoadFromEngineFS(path);
    }

    private class ModelEntry
    {
        public string Name { get; set; } = "";
        public Md3Model? Model { get; set; }
        public IqmModel? IqmModel { get; set; }
        public int BspModelIndex { get; set; } = -1;
    }
}
