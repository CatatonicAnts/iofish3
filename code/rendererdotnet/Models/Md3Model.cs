namespace RendererDotNet.Models;

/// <summary>
/// Parsed MD3 model data in runtime-friendly format.
/// Mirrors the mdvModel_t structure from tr_local.h.
/// </summary>
public sealed class Md3Model
{
    public string Name { get; set; } = "";
    public Md3Frame[] Frames { get; set; } = [];
    public Md3Tag[] Tags { get; set; } = [];       // numTags * numFrames
    public string[] TagNames { get; set; } = [];    // numTags
    public Md3Surface[] Surfaces { get; set; } = [];
    public int NumFrames => Frames.Length;
    public int NumTags => TagNames.Length;
}

/// <summary>
/// Per-frame bounding box and origin.
/// </summary>
public struct Md3Frame
{
    public float MinX, MinY, MinZ;
    public float MaxX, MaxY, MaxZ;
    public float OriginX, OriginY, OriginZ;
    public float Radius;
}

/// <summary>
/// Tag: attachment point with position and orientation.
/// Used for weapon/head attachment. numTags * numFrames stored linearly.
/// </summary>
public struct Md3Tag
{
    public float OriginX, OriginY, OriginZ;
    // 3x3 rotation matrix stored as 3 column vectors
    public float Ax0, Ax1, Ax2;
    public float Ay0, Ay1, Ay2;
    public float Az0, Az1, Az2;
}

/// <summary>
/// A renderable surface (sub-mesh) within an MD3 model.
/// </summary>
public sealed class Md3Surface
{
    public string Name { get; set; } = "";
    public int ShaderHandle { get; set; }
    public string ShaderName { get; set; } = "";

    /// <summary>Triangle indices — 3 ints per triangle.</summary>
    public int[] Indices { get; set; } = [];

    /// <summary>Texture coordinates — 2 floats (u,v) per vertex, shared across all frames.</summary>
    public float[] TexCoords { get; set; } = [];

    /// <summary>
    /// Vertex positions — 3 floats (x,y,z) per vertex, numVerts * numFrames entries.
    /// Frame N vertices start at index (N * numVerts * 3).
    /// </summary>
    public float[] Positions { get; set; } = [];

    /// <summary>
    /// Vertex normals — 3 floats (nx,ny,nz) per vertex, numVerts * numFrames entries.
    /// </summary>
    public float[] Normals { get; set; } = [];

    public int NumVerts { get; set; }
    public int NumTriangles { get; set; }
    public int NumFrames { get; set; }
}
