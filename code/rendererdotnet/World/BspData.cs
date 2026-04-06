namespace RendererDotNet.World;

/// <summary>
/// Runtime data structures for a parsed Q3 BSP map.
/// </summary>
public sealed class BspWorld
{
    public string Name { get; set; } = "";

    // Core geometry
    public BspVertex[] Vertices { get; set; } = [];
    public int[] Indices { get; set; } = [];
    public BspSurface[] Surfaces { get; set; } = [];
    public BspShaderEntry[] Shaders { get; set; } = [];

    // BSP tree
    public BspNode[] Nodes { get; set; } = [];
    public BspLeaf[] Leafs { get; set; } = [];
    public BspPlane[] Planes { get; set; } = [];
    public int[] LeafSurfaces { get; set; } = [];

    // Submodels (inline models like doors, platforms)
    public BspModel[] Models { get; set; } = [];

    // Lightmaps
    public BspLightmap[] Lightmaps { get; set; } = [];

    // Visibility (PVS)
    public int NumClusters { get; set; }
    public int ClusterBytes { get; set; }
    public byte[]? VisData { get; set; }

    // Entity string from BSP for GetEntityToken
    public string EntityString { get; set; } = "";

    /// <summary>Check if cluster 'from' can see cluster 'to' via PVS.</summary>
    public bool ClusterVisible(int from, int to)
    {
        if (VisData == null || from < 0 || to < 0)
            return true; // No vis data = everything visible
        int ofs = 8 + from * ClusterBytes;
        if (ofs + (to >> 3) >= VisData.Length)
            return true;
        return (VisData[ofs + (to >> 3)] & (1 << (to & 7))) != 0;
    }

    /// <summary>Find which leaf the given point is in by walking the BSP tree.</summary>
    public int FindLeaf(float x, float y, float z)
    {
        int idx = 0;
        while (idx >= 0 && idx < Nodes.Length)
        {
            ref var node = ref Nodes[idx];
            ref var plane = ref Planes[node.PlaneIndex];
            float dist = plane.NormalX * x + plane.NormalY * y + plane.NormalZ * z - plane.Dist;
            idx = dist >= 0 ? node.Child0 : node.Child1;
        }
        // Negative index: leaf = -(idx + 1)
        return -(idx + 1);
    }
}

public struct BspVertex
{
    public float X, Y, Z;          // Position
    public float U, V;             // Texture coords
    public float LmU, LmV;        // Lightmap coords
    public float NX, NY, NZ;      // Normal
    public byte R, G, B, A;       // Vertex color
}

public struct BspSurface
{
    public int ShaderIndex;
    public int FogIndex;
    public int SurfaceType;       // MST_PLANAR=1, MST_PATCH=2, MST_TRIANGLE_SOUP=3, MST_FLARE=4
    public int FirstVertex;
    public int NumVertices;
    public int FirstIndex;
    public int NumIndices;
    public int LightmapIndex;     // -1 = no lightmap
    public int ShaderHandle;      // Resolved shader handle from ShaderManager

    // For patches
    public int PatchWidth;
    public int PatchHeight;
}

public struct BspShaderEntry
{
    public string Name;
    public int SurfaceFlags;
    public int ContentFlags;
}

public struct BspNode
{
    public int PlaneIndex;
    public int Child0, Child1;    // Negative = -(leaf+1)
    public int MinX, MinY, MinZ;
    public int MaxX, MaxY, MaxZ;
}

public struct BspLeaf
{
    public int Cluster;
    public int Area;
    public int MinX, MinY, MinZ;
    public int MaxX, MaxY, MaxZ;
    public int FirstLeafSurface;
    public int NumLeafSurfaces;
}

public struct BspPlane
{
    public float NormalX, NormalY, NormalZ;
    public float Dist;
}

public struct BspModel
{
    public float MinX, MinY, MinZ;
    public float MaxX, MaxY, MaxZ;
    public int FirstSurface;
    public int NumSurfaces;
}

public struct BspLightmap
{
    public byte[] Data;           // 128*128*3 RGB
}

// Surface type constants matching Q3
public static class SurfaceTypes
{
    public const int MST_BAD = 0;
    public const int MST_PLANAR = 1;
    public const int MST_PATCH = 2;
    public const int MST_TRIANGLE_SOUP = 3;
    public const int MST_FLARE = 4;
}

// Surface flags for culling
public static class SurfaceFlags
{
    public const int SURF_NODRAW = 0x80;
    public const int SURF_SKY = 0x4;
}
