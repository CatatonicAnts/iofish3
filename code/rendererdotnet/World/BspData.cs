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

    // Deluxe maps (per-pixel light direction, interleaved with lightmaps in BSP)
    public BspLightmap[] DeluxeMaps { get; set; } = [];
    public bool HasDeluxeMapping { get; set; }

    // Visibility (PVS)
    public int NumClusters { get; set; }
    public int ClusterBytes { get; set; }
    public byte[]? VisData { get; set; }

    // Light grid for entity lighting
    public byte[]? LightGridData { get; set; }
    public float[] LightGridOrigin { get; set; } = new float[3];
    public float[] LightGridSize { get; set; } = [64f, 64f, 128f];
    public float[] LightGridInverseSize { get; set; } = new float[3];
    public int[] LightGridBounds { get; set; } = new int[3];

    // Entity string from BSP for GetEntityToken
    public string EntityString { get; set; } = "";

    // Fog volumes from BSP
    public BspFog[] Fogs { get; set; } = [];

    /// <summary>
    /// Sample the light grid at a world position using trilinear interpolation.
    /// Returns ambient RGB (0-1), directed RGB (0-1), and light direction vector.
    /// </summary>
    public void SampleLightGrid(float x, float y, float z,
        out float ambR, out float ambG, out float ambB,
        out float dirR, out float dirG, out float dirB,
        out float dirX, out float dirY, out float dirZ)
    {
        ambR = ambG = ambB = 0.5f;
        dirR = dirG = dirB = 0.5f;
        dirX = 0.57735f; dirY = 0.57735f; dirZ = 0.57735f;

        if (LightGridData == null || LightGridData.Length == 0)
            return;

        // Transform position to grid-local coordinates
        float lx = x - LightGridOrigin[0];
        float ly = y - LightGridOrigin[1];
        float lz = z - LightGridOrigin[2];

        // Grid coordinates (fractional)
        float vx = lx * LightGridInverseSize[0];
        float vy = ly * LightGridInverseSize[1];
        float vz = lz * LightGridInverseSize[2];

        int px = (int)MathF.Floor(vx);
        int py = (int)MathF.Floor(vy);
        int pz = (int)MathF.Floor(vz);

        float fracX = vx - px;
        float fracY = vy - py;
        float fracZ = vz - pz;

        // Clamp
        if (px < 0) { px = 0; fracX = 0; }
        else if (px > LightGridBounds[0] - 1) { px = LightGridBounds[0] - 1; fracX = 0; }
        if (py < 0) { py = 0; fracY = 0; }
        else if (py > LightGridBounds[1] - 1) { py = LightGridBounds[1] - 1; fracY = 0; }
        if (pz < 0) { pz = 0; fracZ = 0; }
        else if (pz > LightGridBounds[2] - 1) { pz = LightGridBounds[2] - 1; fracZ = 0; }

        // Grid step sizes (in bytes, 8 bytes per sample)
        int stepX = 8;
        int stepY = 8 * LightGridBounds[0];
        int stepZ = 8 * LightGridBounds[0] * LightGridBounds[1];
        int baseOfs = px * stepX + py * stepY + pz * stepZ;

        // Trilinear interpolation over 8 corners
        float totalAmbR = 0, totalAmbG = 0, totalAmbB = 0;
        float totalDirR = 0, totalDirG = 0, totalDirB = 0;
        float totalDX = 0, totalDY = 0, totalDZ = 0;
        float totalFactor = 0;

        for (int i = 0; i < 8; i++)
        {
            float factor = 1.0f;
            int ofs = baseOfs;
            bool skip = false;

            for (int j = 0; j < 3; j++)
            {
                if ((i & (1 << j)) != 0)
                {
                    int pos = j == 0 ? px : j == 1 ? py : pz;
                    int bound = LightGridBounds[j];
                    if (pos + 1 > bound - 1)
                    {
                        skip = true;
                        break;
                    }
                    float frac = j == 0 ? fracX : j == 1 ? fracY : fracZ;
                    factor *= frac;
                    ofs += j == 0 ? stepX : j == 1 ? stepY : stepZ;
                }
                else
                {
                    float frac = j == 0 ? fracX : j == 1 ? fracY : fracZ;
                    factor *= 1.0f - frac;
                }
            }

            if (skip) continue;
            if (ofs + 7 >= LightGridData.Length) continue;

            byte a0 = LightGridData[ofs];
            byte a1 = LightGridData[ofs + 1];
            byte a2 = LightGridData[ofs + 2];

            // Skip samples in walls (all ambient zero)
            if (a0 + a1 + a2 == 0) continue;

            totalFactor += factor;
            totalAmbR += factor * a0;
            totalAmbG += factor * a1;
            totalAmbB += factor * a2;
            totalDirR += factor * LightGridData[ofs + 3];
            totalDirG += factor * LightGridData[ofs + 4];
            totalDirB += factor * LightGridData[ofs + 5];

            // Decode lat/long direction
            byte lng = LightGridData[ofs + 6];
            byte lat = LightGridData[ofs + 7];
            float latRad = lat * (MathF.PI * 2f / 256f);
            float lngRad = lng * (MathF.PI * 2f / 256f);
            float nx = MathF.Cos(latRad) * MathF.Sin(lngRad);
            float ny = MathF.Sin(latRad) * MathF.Sin(lngRad);
            float nz = MathF.Cos(lngRad);
            totalDX += factor * nx;
            totalDY += factor * ny;
            totalDZ += factor * nz;
        }

        if (totalFactor > 0 && totalFactor < 0.99f)
        {
            float inv = 1.0f / totalFactor;
            totalAmbR *= inv; totalAmbG *= inv; totalAmbB *= inv;
            totalDirR *= inv; totalDirG *= inv; totalDirB *= inv;
        }

        // Add minimum ambient (32/255 ~ 0.125)
        const float minAmb = 32f;
        totalAmbR += minAmb;
        totalAmbG += minAmb;
        totalAmbB += minAmb;

        // Clamp to 255 and normalize to 0-1
        ambR = MathF.Min(totalAmbR, 255f) / 255f;
        ambG = MathF.Min(totalAmbG, 255f) / 255f;
        ambB = MathF.Min(totalAmbB, 255f) / 255f;
        dirR = MathF.Min(totalDirR, 255f) / 255f;
        dirG = MathF.Min(totalDirG, 255f) / 255f;
        dirB = MathF.Min(totalDirB, 255f) / 255f;

        // Normalize direction
        float dl = MathF.Sqrt(totalDX * totalDX + totalDY * totalDY + totalDZ * totalDZ);
        if (dl > 0.001f)
        {
            dirX = totalDX / dl;
            dirY = totalDY / dl;
            dirZ = totalDZ / dl;
        }
        else
        {
            dirX = 0.57735f; dirY = 0.57735f; dirZ = 0.57735f;
        }
    }

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
    public float TX, TY, TZ, TS;  // Tangent + sign (for normal mapping)
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

    // For flares (MST_FLARE): origin, color, normal from lightmap fields
    public float FlareOriginX, FlareOriginY, FlareOriginZ;
    public float FlareColorR, FlareColorG, FlareColorB;
    public float FlareNormalX, FlareNormalY, FlareNormalZ;
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

/// <summary>
/// A fog volume defined by a brush in the BSP.
/// Surfaces with FogIndex pointing to this volume get fogged.
/// </summary>
public struct BspFog
{
    public string ShaderName;           // Shader that defines fogParms
    public float ColorR, ColorG, ColorB; // Fog color (0-1)
    public float DepthForOpaque;         // Distance at which fog is fully opaque
    public float TcScale;                // 1.0 / (depthForOpaque * 8)

    // Bounding box of the fog brush
    public float MinX, MinY, MinZ;
    public float MaxX, MaxY, MaxZ;

    // Visible surface plane (for gradient fog)
    public bool HasSurface;
    public float SurfNX, SurfNY, SurfNZ, SurfD; // Plane equation
}
