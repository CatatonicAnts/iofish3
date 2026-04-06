namespace RendererDotNet.Models;

/// <summary>
/// Parsed IQM (Inter-Quake Model) data in runtime-friendly format.
/// Supports skeletal animation with up to 128 bones.
/// </summary>
public sealed class IqmModel
{
    public string Name { get; set; } = "";
    public IqmSurface[] Surfaces { get; set; } = [];
    public IqmJoint[] Joints { get; set; } = [];
    public IqmAnimation[] Animations { get; set; } = [];

    /// <summary>Triangle indices — 3 ints per triangle (shared across surfaces).</summary>
    public int[] Triangles { get; set; } = [];

    /// <summary>Vertex positions — 3 floats per vertex.</summary>
    public float[] Positions { get; set; } = [];

    /// <summary>Texture coordinates — 2 floats per vertex.</summary>
    public float[] TexCoords { get; set; } = [];

    /// <summary>Vertex normals — 3 floats per vertex.</summary>
    public float[] Normals { get; set; } = [];

    /// <summary>Vertex tangents — 4 floats per vertex (xyz + handedness w).</summary>
    public float[] Tangents { get; set; } = [];

    /// <summary>Blend indices — 4 bytes per vertex (bone indices).</summary>
    public byte[] BlendIndexes { get; set; } = [];

    /// <summary>Blend weights — 4 bytes per vertex (normalized weights 0-255).</summary>
    public byte[] BlendWeights { get; set; } = [];

    /// <summary>Vertex colors — 4 bytes per vertex (RGBA).</summary>
    public byte[]? Colors { get; set; }

    /// <summary>Bind-pose joint matrices — 12 floats per joint (3x4 row-major).</summary>
    public float[] BindJoints { get; set; } = [];

    /// <summary>Inverse bind-pose matrices — 12 floats per joint.</summary>
    public float[] InvBindJoints { get; set; } = [];

    /// <summary>Animation frame poses — one IqmTransform per joint per frame.</summary>
    public IqmTransform[] Poses { get; set; } = [];

    /// <summary>Model bounds — 6 floats per frame (minX,Y,Z, maxX,Y,Z).</summary>
    public float[] Bounds { get; set; } = [];

    public int NumVertexes { get; set; }
    public int NumTriangles { get; set; }
    public int NumFrames { get; set; }
    public int NumJoints => Joints.Length;
}

/// <summary>
/// A renderable surface (mesh) within an IQM model.
/// </summary>
public sealed class IqmSurface
{
    public string Name { get; set; } = "";
    public string ShaderName { get; set; } = "";
    public int ShaderHandle { get; set; }
    public int FirstVertex { get; set; }
    public int NumVertexes { get; set; }
    public int FirstTriangle { get; set; }
    public int NumTriangles { get; set; }
}

/// <summary>
/// A skeleton joint (bone).
/// </summary>
public struct IqmJoint
{
    public string Name;
    public int Parent; // -1 for root

    // Bind pose
    public float TranslateX, TranslateY, TranslateZ;
    public float RotateX, RotateY, RotateZ, RotateW;
    public float ScaleX, ScaleY, ScaleZ;
}

/// <summary>
/// Per-joint per-frame animation transform.
/// </summary>
public struct IqmTransform
{
    public float TranslateX, TranslateY, TranslateZ;
    public float RotateX, RotateY, RotateZ, RotateW;
    public float ScaleX, ScaleY, ScaleZ;
}

/// <summary>
/// Animation sequence metadata.
/// </summary>
public sealed class IqmAnimation
{
    public string Name { get; set; } = "";
    public int FirstFrame { get; set; }
    public int NumFrames { get; set; }
    public float Framerate { get; set; }
    public bool Loop { get; set; }
}
