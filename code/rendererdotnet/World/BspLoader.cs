using System;
using System.Runtime.InteropServices;
using RendererDotNet.Interop;

namespace RendererDotNet.World;

/// <summary>
/// Parses a Q3 BSP file from the engine filesystem.
/// BSP version 46 (IBSP), little-endian.
/// </summary>
public static unsafe class BspLoader
{
    private const int BSP_IDENT = 0x50534249; // "IBSP" little-endian
    private const int BSP_VERSION = 46;
    private const int HEADER_LUMPS = 17;

    // Lump indices
    private const int LUMP_ENTITIES = 0;
    private const int LUMP_SHADERS = 1;
    private const int LUMP_PLANES = 2;
    private const int LUMP_NODES = 3;
    private const int LUMP_LEAFS = 4;
    private const int LUMP_LEAFSURFACES = 5;
    private const int LUMP_MODELS = 7;
    private const int LUMP_BRUSHES = 8;
    private const int LUMP_BRUSHSIDES = 9;
    private const int LUMP_DRAWVERTS = 10;
    private const int LUMP_DRAWINDEXES = 11;
    private const int LUMP_FOGS = 12;
    private const int LUMP_SURFACES = 13;
    private const int LUMP_LIGHTMAPS = 14;
    private const int LUMP_LIGHTGRID = 15;
    private const int LUMP_VISIBILITY = 16;

    // Struct sizes on disk
    private const int DSHADER_SIZE = 72;    // char[64] + int + int
    private const int DPLANE_SIZE = 16;     // float[3] + float
    private const int DNODE_SIZE = 36;      // int + int[2] + int[3] + int[3]
    private const int DLEAF_SIZE = 48;      // int + int + int[3] + int[3] + int + int + int + int
    private const int DRAWVERT_SIZE = 44;   // float[3] + float[2] + float[2] + float[3] + byte[4]
    private const int DSURFACE_SIZE = 104;  // many fields
    private const int DMODEL_SIZE = 40;     // float[3] + float[3] + int + int + int + int
    private const int LIGHTMAP_SIZE = 128 * 128 * 3;
    private const int DFOG_SIZE = 72;       // char[64] + int + int
    private const int DBRUSH_SIZE = 12;     // int + int + int
    private const int DBRUSHSIDE_SIZE = 8;  // int + int

    public static BspWorld? LoadFromEngineFS(string path)
    {
        int len = EngineImports.FS_ReadFile(path, out byte* buf);
        if (len <= 0 || buf == null)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] Could not load BSP: {path}\n");
            return null;
        }

        try
        {
            return Parse(buf, len, path);
        }
        finally
        {
            EngineImports.FS_FreeFile(buf);
        }
    }

    private static BspWorld? Parse(byte* buf, int size, string path)
    {
        if (size < 144) return null; // Header too small

        int ident = *(int*)(buf + 0);
        int version = *(int*)(buf + 4);

        if (ident != BSP_IDENT)
        {
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] Invalid BSP ident in {path}\n");
            return null;
        }
        if (version != BSP_VERSION)
        {
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] Unsupported BSP version {version} in {path}\n");
            return null;
        }

        var world = new BspWorld { Name = path };

        // Read lump directory (starts at offset 8, each lump is 8 bytes)
        int* lumps = (int*)(buf + 8);

        world.Shaders = LoadShaders(buf, lumps[LUMP_SHADERS * 2], lumps[LUMP_SHADERS * 2 + 1]);
        world.Planes = LoadPlanes(buf, lumps[LUMP_PLANES * 2], lumps[LUMP_PLANES * 2 + 1]);
        world.Nodes = LoadNodes(buf, lumps[LUMP_NODES * 2], lumps[LUMP_NODES * 2 + 1]);
        world.Leafs = LoadLeafs(buf, lumps[LUMP_LEAFS * 2], lumps[LUMP_LEAFS * 2 + 1]);
        world.LeafSurfaces = LoadLeafSurfaces(buf, lumps[LUMP_LEAFSURFACES * 2], lumps[LUMP_LEAFSURFACES * 2 + 1]);
        world.Models = LoadModels(buf, lumps[LUMP_MODELS * 2], lumps[LUMP_MODELS * 2 + 1]);
        world.Vertices = LoadVertices(buf, lumps[LUMP_DRAWVERTS * 2], lumps[LUMP_DRAWVERTS * 2 + 1]);
        world.Indices = LoadIndices(buf, lumps[LUMP_DRAWINDEXES * 2], lumps[LUMP_DRAWINDEXES * 2 + 1]);
        world.Surfaces = LoadSurfaces(buf, lumps[LUMP_SURFACES * 2], lumps[LUMP_SURFACES * 2 + 1]);
        world.Lightmaps = LoadLightmaps(buf, lumps[LUMP_LIGHTMAPS * 2], lumps[LUMP_LIGHTMAPS * 2 + 1]);
        LoadVisibility(world, buf, lumps[LUMP_VISIBILITY * 2], lumps[LUMP_VISIBILITY * 2 + 1]);
        LoadLightGrid(world, buf, lumps[LUMP_LIGHTGRID * 2], lumps[LUMP_LIGHTGRID * 2 + 1]);
        LoadFogs(world, buf, lumps, lumps[LUMP_FOGS * 2], lumps[LUMP_FOGS * 2 + 1]);

        // Load entity string for GetEntityToken
        int entOfs = lumps[LUMP_ENTITIES * 2];
        int entLen = lumps[LUMP_ENTITIES * 2 + 1];
        if (entLen > 0)
            world.EntityString = System.Text.Encoding.UTF8.GetString(buf + entOfs, entLen).TrimEnd('\0');

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Loaded BSP: {path} ({world.Vertices.Length} verts, {world.Surfaces.Length} surfs, " +
            $"{world.Nodes.Length} nodes, {world.Leafs.Length} leafs, {world.Lightmaps.Length} lightmaps)\n");

        TessellatePatches(world);

        return world;
    }

    private static BspShaderEntry[] LoadShaders(byte* buf, int ofs, int len)
    {
        int count = len / DSHADER_SIZE;
        var result = new BspShaderEntry[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            byte* entry = p + i * DSHADER_SIZE;
            result[i].Name = Marshal.PtrToStringUTF8((nint)entry, 64)?.TrimEnd('\0') ?? "";
            result[i].SurfaceFlags = *(int*)(entry + 64);
            result[i].ContentFlags = *(int*)(entry + 68);
        }
        return result;
    }

    private static BspPlane[] LoadPlanes(byte* buf, int ofs, int len)
    {
        int count = len / DPLANE_SIZE;
        var result = new BspPlane[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            float* f = (float*)(p + i * DPLANE_SIZE);
            result[i].NormalX = f[0];
            result[i].NormalY = f[1];
            result[i].NormalZ = f[2];
            result[i].Dist = f[3];
        }
        return result;
    }

    private static BspNode[] LoadNodes(byte* buf, int ofs, int len)
    {
        int count = len / DNODE_SIZE;
        var result = new BspNode[count];
        int* p = (int*)(buf + ofs);

        for (int i = 0; i < count; i++)
        {
            int b = i * 9; // 36 bytes / 4 = 9 ints
            result[i].PlaneIndex = p[b];
            result[i].Child0 = p[b + 1];
            result[i].Child1 = p[b + 2];
            result[i].MinX = p[b + 3];
            result[i].MinY = p[b + 4];
            result[i].MinZ = p[b + 5];
            result[i].MaxX = p[b + 6];
            result[i].MaxY = p[b + 7];
            result[i].MaxZ = p[b + 8];
        }
        return result;
    }

    private static BspLeaf[] LoadLeafs(byte* buf, int ofs, int len)
    {
        int count = len / DLEAF_SIZE;
        var result = new BspLeaf[count];
        int* p = (int*)(buf + ofs);

        for (int i = 0; i < count; i++)
        {
            int b = i * 12; // 48 bytes / 4 = 12 ints
            result[i].Cluster = p[b];
            result[i].Area = p[b + 1];
            result[i].MinX = p[b + 2];
            result[i].MinY = p[b + 3];
            result[i].MinZ = p[b + 4];
            result[i].MaxX = p[b + 5];
            result[i].MaxY = p[b + 6];
            result[i].MaxZ = p[b + 7];
            result[i].FirstLeafSurface = p[b + 8];
            result[i].NumLeafSurfaces = p[b + 9];
            // b+10, b+11 are firstLeafBrush, numLeafBrushes (collision only)
        }
        return result;
    }

    private static int[] LoadLeafSurfaces(byte* buf, int ofs, int len)
    {
        int count = len / 4;
        var result = new int[count];
        int* p = (int*)(buf + ofs);
        for (int i = 0; i < count; i++)
            result[i] = p[i];
        return result;
    }

    private static BspModel[] LoadModels(byte* buf, int ofs, int len)
    {
        int count = len / DMODEL_SIZE;
        var result = new BspModel[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            float* f = (float*)(p + i * DMODEL_SIZE);
            result[i].MinX = f[0]; result[i].MinY = f[1]; result[i].MinZ = f[2];
            result[i].MaxX = f[3]; result[i].MaxY = f[4]; result[i].MaxZ = f[5];
            int* ii = (int*)(f + 6);
            result[i].FirstSurface = ii[0];
            result[i].NumSurfaces = ii[1];
        }
        return result;
    }

    private static BspVertex[] LoadVertices(byte* buf, int ofs, int len)
    {
        int count = len / DRAWVERT_SIZE;
        var result = new BspVertex[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            byte* v = p + i * DRAWVERT_SIZE;
            float* f = (float*)v;
            result[i].X = f[0];
            result[i].Y = f[1];
            result[i].Z = f[2];
            result[i].U = f[3];
            result[i].V = f[4];
            result[i].LmU = f[5];
            result[i].LmV = f[6];
            result[i].NX = f[7];
            result[i].NY = f[8];
            result[i].NZ = f[9];
            result[i].R = v[40];
            result[i].G = v[41];
            result[i].B = v[42];
            result[i].A = v[43];
        }
        return result;
    }

    private static int[] LoadIndices(byte* buf, int ofs, int len)
    {
        int count = len / 4;
        var result = new int[count];
        int* p = (int*)(buf + ofs);
        for (int i = 0; i < count; i++)
            result[i] = p[i];
        return result;
    }

    private static BspSurface[] LoadSurfaces(byte* buf, int ofs, int len)
    {
        int count = len / DSURFACE_SIZE;
        var result = new BspSurface[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            int* s = (int*)(p + i * DSURFACE_SIZE);
            result[i].ShaderIndex = s[0];
            result[i].FogIndex = s[1];
            result[i].SurfaceType = s[2];
            result[i].FirstVertex = s[3];
            result[i].NumVertices = s[4];
            result[i].FirstIndex = s[5];
            result[i].NumIndices = s[6];
            result[i].LightmapIndex = s[7];
            result[i].PatchWidth = s[24];   // offset 96
            result[i].PatchHeight = s[25];  // offset 100
        }
        return result;
    }

    private static BspLightmap[] LoadLightmaps(byte* buf, int ofs, int len)
    {
        int count = len / LIGHTMAP_SIZE;
        var result = new BspLightmap[count];
        byte* p = buf + ofs;

        for (int i = 0; i < count; i++)
        {
            result[i].Data = new byte[LIGHTMAP_SIZE];
            fixed (byte* dst = result[i].Data)
            {
                Buffer.MemoryCopy(p + i * LIGHTMAP_SIZE, dst, LIGHTMAP_SIZE, LIGHTMAP_SIZE);
            }
        }
        return result;
    }

    private static void LoadVisibility(BspWorld world, byte* buf, int ofs, int len)
    {
        if (len <= 8)
        {
            world.NumClusters = 0;
            world.ClusterBytes = 0;
            world.VisData = null;
            return;
        }

        world.VisData = new byte[len];
        fixed (byte* dst = world.VisData)
        {
            Buffer.MemoryCopy(buf + ofs, dst, len, len);
        }

        int* header = (int*)(buf + ofs);
        world.NumClusters = header[0];
        world.ClusterBytes = header[1];
    }

    private static void LoadLightGrid(BspWorld world, byte* buf, int ofs, int len)
    {
        if (world.Models.Length == 0 || len < 8)
        {
            world.LightGridData = null;
            return;
        }

        // Copy raw grid data
        world.LightGridData = new byte[len];
        fixed (byte* dst = world.LightGridData)
        {
            Buffer.MemoryCopy(buf + ofs, dst, len, len);
        }

        // Compute grid origin and bounds from world model (Models[0])
        ref var wm = ref world.Models[0];
        float[] gridSize = world.LightGridSize;

        for (int i = 0; i < 3; i++)
        {
            float wMin = i == 0 ? wm.MinX : i == 1 ? wm.MinY : wm.MinZ;
            float wMax = i == 0 ? wm.MaxX : i == 1 ? wm.MaxY : wm.MaxZ;
            world.LightGridOrigin[i] = gridSize[i] * MathF.Ceiling(wMin / gridSize[i]);
            float maxGrid = gridSize[i] * MathF.Floor(wMax / gridSize[i]);
            world.LightGridBounds[i] = (int)((maxGrid - world.LightGridOrigin[i]) / gridSize[i]) + 1;
            world.LightGridInverseSize[i] = 1.0f / gridSize[i];
        }

        int numGridPoints = world.LightGridBounds[0] * world.LightGridBounds[1] * world.LightGridBounds[2];
        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Light grid: {numGridPoints} points ({world.LightGridBounds[0]}x{world.LightGridBounds[1]}x{world.LightGridBounds[2]}), {len} bytes\n");
    }

    // --- Bezier patch tessellation ---

    private const int TESS_LEVEL = 8;

    /// <summary>
    /// Tessellate all MST_PATCH surfaces into triangle meshes.
    /// Appends new vertices/indices and updates surface references.
    /// </summary>
    private static void TessellatePatches(BspWorld world)
    {
        var newVerts = new System.Collections.Generic.List<BspVertex>(world.Vertices);
        var newIndices = new System.Collections.Generic.List<int>(world.Indices);
        int patchCount = 0;

        for (int si = 0; si < world.Surfaces.Length; si++)
        {
            ref var surf = ref world.Surfaces[si];
            if (surf.SurfaceType != SurfaceTypes.MST_PATCH)
                continue;
            if (surf.PatchWidth < 3 || surf.PatchHeight < 3)
                continue;

            int cpW = surf.PatchWidth;
            int cpH = surf.PatchHeight;
            int numPatchesX = (cpW - 1) / 2;
            int numPatchesY = (cpH - 1) / 2;

            int tessVerts = (numPatchesX * TESS_LEVEL + 1) * (numPatchesY * TESS_LEVEL + 1);
            int tessQuads = numPatchesX * TESS_LEVEL * numPatchesY * TESS_LEVEL;
            int tessIdxCount = tessQuads * 6;

            int firstVert = newVerts.Count;
            int firstIdx = newIndices.Count;

            int totalW = numPatchesX * TESS_LEVEL + 1;
            int totalH = numPatchesY * TESS_LEVEL + 1;

            // Tessellate: generate vertices in grid order
            for (int gy = 0; gy < totalH; gy++)
            {
                for (int gx = 0; gx < totalW; gx++)
                {
                    // Which sub-patch does this grid point belong to?
                    int px = Math.Min(gx / TESS_LEVEL, numPatchesX - 1);
                    int py = Math.Min(gy / TESS_LEVEL, numPatchesY - 1);

                    // Local parametric coordinates within the sub-patch [0..1]
                    float s = (gx - px * TESS_LEVEL) / (float)TESS_LEVEL;
                    float t = (gy - py * TESS_LEVEL) / (float)TESS_LEVEL;

                    // Get 3x3 control points for this sub-patch
                    var cp = new BspVertex[3, 3];
                    for (int cy = 0; cy < 3; cy++)
                    {
                        for (int cx = 0; cx < 3; cx++)
                        {
                            int vi = surf.FirstVertex + (py * 2 + cy) * cpW + (px * 2 + cx);
                            if (vi < world.Vertices.Length)
                                cp[cy, cx] = world.Vertices[vi];
                        }
                    }

                    newVerts.Add(EvalBezier(cp, s, t));
                }
            }

            // Generate indices for the tessellated grid (relative to firstVert)
            for (int gy = 0; gy < totalH - 1; gy++)
            {
                for (int gx = 0; gx < totalW - 1; gx++)
                {
                    int i0 = gy * totalW + gx;
                    int i1 = i0 + 1;
                    int i2 = i0 + totalW;
                    int i3 = i2 + 1;

                    newIndices.Add(i0);
                    newIndices.Add(i2);
                    newIndices.Add(i1);

                    newIndices.Add(i1);
                    newIndices.Add(i2);
                    newIndices.Add(i3);
                }
            }

            surf.FirstVertex = firstVert;
            surf.NumVertices = totalW * totalH;
            surf.FirstIndex = firstIdx;
            surf.NumIndices = (totalH - 1) * (totalW - 1) * 6;
            patchCount++;
        }

        if (patchCount > 0)
        {
            world.Vertices = newVerts.ToArray();
            world.Indices = newIndices.ToArray();
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] Tessellated {patchCount} patches ({world.Vertices.Length} total verts)\n");
        }
    }

    /// <summary>
    /// Evaluate a biquadratic Bezier patch at (s, t) given 3x3 control points.
    /// B(s,t) = sum_i sum_j B_i(s) * B_j(t) * CP[j,i]
    /// </summary>
    private static BspVertex EvalBezier(BspVertex[,] cp, float s, float t)
    {
        // Bernstein basis: B0=(1-x)², B1=2x(1-x), B2=x²
        float s0 = (1 - s) * (1 - s), s1 = 2 * s * (1 - s), s2 = s * s;
        float t0 = (1 - t) * (1 - t), t1 = 2 * t * (1 - t), t2 = t * t;

        var result = new BspVertex();
        for (int j = 0; j < 3; j++)
        {
            float bj = j == 0 ? t0 : j == 1 ? t1 : t2;
            for (int i = 0; i < 3; i++)
            {
                float bi = i == 0 ? s0 : i == 1 ? s1 : s2;
                float w = bi * bj;
                ref var c = ref cp[j, i];
                result.X += c.X * w; result.Y += c.Y * w; result.Z += c.Z * w;
                result.NX += c.NX * w; result.NY += c.NY * w; result.NZ += c.NZ * w;
                result.U += c.U * w; result.V += c.V * w;
                result.LmU += c.LmU * w; result.LmV += c.LmV * w;
            }
        }

        // Blend vertex colors (use weighted average, clamp to byte)
        float r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j < 3; j++)
        {
            float bj = j == 0 ? t0 : j == 1 ? t1 : t2;
            for (int i = 0; i < 3; i++)
            {
                float bi = i == 0 ? s0 : i == 1 ? s1 : s2;
                float w = bi * bj;
                ref var c = ref cp[j, i];
                r += c.R * w; g += c.G * w; b += c.B * w; a += c.A * w;
            }
        }
        result.R = (byte)Math.Clamp(r, 0, 255);
        result.G = (byte)Math.Clamp(g, 0, 255);
        result.B = (byte)Math.Clamp(b, 0, 255);
        result.A = (byte)Math.Clamp(a, 0, 255);

        // Normalize the normal
        float nl = MathF.Sqrt(result.NX * result.NX + result.NY * result.NY + result.NZ * result.NZ);
        if (nl > 0.0001f)
        {
            result.NX /= nl; result.NY /= nl; result.NZ /= nl;
        }

        return result;
    }

    /// <summary>
    /// Load fog volumes from BSP fogs lump. Requires brushes and brush sides
    /// lumps to extract the AABB bounds, and planes for the visible surface.
    /// </summary>
    private static void LoadFogs(BspWorld world, byte* buf, int* lumps, int ofs, int len)
    {
        int fogCount = len / DFOG_SIZE;
        if (fogCount == 0)
        {
            world.Fogs = [];
            return;
        }

        int brushOfs = lumps[LUMP_BRUSHES * 2];
        int brushLen = lumps[LUMP_BRUSHES * 2 + 1];
        int brushCount = brushLen / DBRUSH_SIZE;

        int sideOfs = lumps[LUMP_BRUSHSIDES * 2];
        int sideLen = lumps[LUMP_BRUSHSIDES * 2 + 1];
        int sideCount = sideLen / DBRUSHSIDE_SIZE;

        byte* fogData = buf + ofs;
        byte* brushData = buf + brushOfs;
        byte* sideData = buf + sideOfs;

        // Fogs array: index 0 is reserved as "no fog" (surfaces reference fogIndex+1)
        var fogs = new BspFog[fogCount];

        for (int i = 0; i < fogCount; i++)
        {
            byte* f = fogData + i * DFOG_SIZE;
            string shaderName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)f, 64)?.TrimEnd('\0') ?? "";
            int brushNum = *(int*)(f + 64);
            int visibleSide = *(int*)(f + 68);

            ref var fog = ref fogs[i];
            fog.ShaderName = shaderName;

            // Extract AABB from brush sides (first 6 axial sides)
            if (brushNum >= 0 && brushNum < brushCount)
            {
                int firstSide = *(int*)(brushData + brushNum * DBRUSH_SIZE);

                if (firstSide >= 0 && firstSide + 5 < sideCount)
                {
                    // Axial sides: -X, +X, -Y, +Y, -Z, +Z
                    int p0 = *(int*)(sideData + (firstSide + 0) * DBRUSHSIDE_SIZE);
                    if (p0 >= 0 && p0 < world.Planes.Length)
                        fog.MinX = -world.Planes[p0].Dist;

                    int p1 = *(int*)(sideData + (firstSide + 1) * DBRUSHSIDE_SIZE);
                    if (p1 >= 0 && p1 < world.Planes.Length)
                        fog.MaxX = world.Planes[p1].Dist;

                    int p2 = *(int*)(sideData + (firstSide + 2) * DBRUSHSIDE_SIZE);
                    if (p2 >= 0 && p2 < world.Planes.Length)
                        fog.MinY = -world.Planes[p2].Dist;

                    int p3 = *(int*)(sideData + (firstSide + 3) * DBRUSHSIDE_SIZE);
                    if (p3 >= 0 && p3 < world.Planes.Length)
                        fog.MaxY = world.Planes[p3].Dist;

                    int p4 = *(int*)(sideData + (firstSide + 4) * DBRUSHSIDE_SIZE);
                    if (p4 >= 0 && p4 < world.Planes.Length)
                        fog.MinZ = -world.Planes[p4].Dist;

                    int p5 = *(int*)(sideData + (firstSide + 5) * DBRUSHSIDE_SIZE);
                    if (p5 >= 0 && p5 < world.Planes.Length)
                        fog.MaxZ = world.Planes[p5].Dist;

                    // Visible surface plane for gradient fog
                    if (visibleSide >= 0 && firstSide + visibleSide < sideCount)
                    {
                        int planeNum = *(int*)(sideData + (firstSide + visibleSide) * DBRUSHSIDE_SIZE);
                        if (planeNum >= 0 && planeNum < world.Planes.Length)
                        {
                            fog.HasSurface = true;
                            // Q3 negates the normal for fog surface
                            fog.SurfNX = -world.Planes[planeNum].NormalX;
                            fog.SurfNY = -world.Planes[planeNum].NormalY;
                            fog.SurfNZ = -world.Planes[planeNum].NormalZ;
                            fog.SurfD = -world.Planes[planeNum].Dist;
                        }
                    }
                }
            }

            // Default fog parameters (will be resolved from shader later)
            fog.ColorR = 0.5f;
            fog.ColorG = 0.5f;
            fog.ColorB = 0.5f;
            fog.DepthForOpaque = 300f;
            fog.TcScale = 1f / (300f * 8f);
        }

        world.Fogs = fogs;
    }
}
