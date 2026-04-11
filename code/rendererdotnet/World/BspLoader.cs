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
        DetectAndSplitDeluxeMaps(world);
        LoadVisibility(world, buf, lumps[LUMP_VISIBILITY * 2], lumps[LUMP_VISIBILITY * 2 + 1]);
        LoadLightGrid(world, buf, lumps[LUMP_LIGHTGRID * 2], lumps[LUMP_LIGHTGRID * 2 + 1]);
        LoadFogs(world, buf, lumps, lumps[LUMP_FOGS * 2], lumps[LUMP_FOGS * 2 + 1]);

        // Load entity string for GetEntityToken
        int entOfs = lumps[LUMP_ENTITIES * 2];
        int entLen = lumps[LUMP_ENTITIES * 2 + 1];
        if (entLen > 0)
            world.EntityString = System.Text.Encoding.UTF8.GetString(buf + entOfs, entLen).TrimEnd('\0');

        // Parse cubemap entities from entity string
        ParseCubemapEntities(world);

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Loaded BSP: {path} ({world.Vertices.Length} verts, {world.Surfaces.Length} surfs, " +
            $"{world.Nodes.Length} nodes, {world.Leafs.Length} leafs, {world.Lightmaps.Length} lightmaps)\n");

        TessellatePatches(world);
        ComputeTangents(world);

        // Apply Q3 overbright compensation to lightmap, vertex color, and light grid data.
        // Q3 maps bake lighting at 1/(2^mapOverBrightBits) intensity; we must shift it back.
        // r_mapOverBrightBits defaults to 2 in Q3. Since this renderer has no hardware gamma
        // ramp, we use the full shift (equivalent to windowed mode: overbrightBits = 0).
        int mapOverBrightBits = EngineImports.Cvar_VariableIntegerValue("r_mapOverBrightBits");
        if (mapOverBrightBits <= 0) mapOverBrightBits = 2; // default; cvar returns 0 if unset
        int shift = mapOverBrightBits; // overbrightBits = 0 for this renderer

        if (shift > 0)
        {
            ColorShiftLightmaps(world, shift);
            ColorShiftVertexColors(world, shift);
            ColorShiftLightGrid(world, shift);
        }

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
            result[i].CubemapIndex = -1;     // No cubemap by default
            result[i].PatchWidth = s[24];   // offset 96
            result[i].PatchHeight = s[25];  // offset 100

            // For MST_FLARE: extract origin/color/normal from lightmap fields
            if (result[i].SurfaceType == SurfaceTypes.MST_FLARE)
            {
                float* f = (float*)(p + i * DSURFACE_SIZE);
                result[i].FlareOriginX = f[12]; // lightmapOrigin[0]
                result[i].FlareOriginY = f[13]; // lightmapOrigin[1]
                result[i].FlareOriginZ = f[14]; // lightmapOrigin[2]
                result[i].FlareColorR = f[15];  // lightmapVecs[0][0]
                result[i].FlareColorG = f[16];  // lightmapVecs[0][1]
                result[i].FlareColorB = f[17];  // lightmapVecs[0][2]
                result[i].FlareNormalX = f[21]; // lightmapVecs[2][0]
                result[i].FlareNormalY = f[22]; // lightmapVecs[2][1]
                result[i].FlareNormalZ = f[23]; // lightmapVecs[2][2]
            }

            // For MST_PLANAR: extract face normal from lightmapVecs[2] (BSP compiler-stored)
            if (result[i].SurfaceType == SurfaceTypes.MST_PLANAR)
            {
                float* f = (float*)(p + i * DSURFACE_SIZE);
                result[i].FaceNormalX = f[21]; // lightmapVecs[2][0]
                result[i].FaceNormalY = f[22]; // lightmapVecs[2][1]
                result[i].FaceNormalZ = f[23]; // lightmapVecs[2][2]
            }
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

    /// <summary>
    /// Detect interleaved deluxe maps in lightmap data.
    /// If numLightmaps > 1 and all surface lightmap indices are even,
    /// the lightmaps are interleaved as (lightmap, deluxe, lightmap, deluxe, ...).
    /// Split them into separate arrays and remap surface lightmap indices.
    /// </summary>
    private static void DetectAndSplitDeluxeMaps(BspWorld world)
    {
        int numLm = world.Lightmaps.Length;
        if (numLm <= 1)
        {
            world.HasDeluxeMapping = false;
            world.DeluxeMaps = [];
            // HACK: same as GL1 — maps with only one lightmap render as fullbright otherwise.
            // Duplicate the single lightmap so the array has 2 entries.
            if (numLm == 1)
            {
                world.Lightmaps = [world.Lightmaps[0], world.Lightmaps[0]];
            }
            return;
        }

        // Check if all surface lightmap indices are even
        bool allEven = true;
        for (int i = 0; i < world.Surfaces.Length; i++)
        {
            int lmIdx = world.Surfaces[i].LightmapIndex;
            if (lmIdx >= 0 && (lmIdx & 1) != 0)
            {
                allEven = false;
                break;
            }
        }

        if (!allEven || numLm < 2)
        {
            world.HasDeluxeMapping = false;
            world.DeluxeMaps = [];
            return;
        }

        // Split interleaved lightmaps: even = lightmap, odd = deluxe
        int halfCount = numLm / 2;
        var lightmaps = new BspLightmap[halfCount];
        var deluxeMaps = new BspLightmap[halfCount];

        for (int i = 0; i < halfCount; i++)
        {
            lightmaps[i] = world.Lightmaps[i * 2];
            deluxeMaps[i] = world.Lightmaps[i * 2 + 1];

            // Remap 0,0,0 to 127,127,127 in deluxe maps (no direction → neutral)
            var data = deluxeMaps[i].Data;
            for (int j = 0; j < data.Length; j += 3)
            {
                if (data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0)
                {
                    data[j] = 127; data[j + 1] = 127; data[j + 2] = 127;
                }
            }
        }

        // Remap surface lightmap indices from even to sequential
        for (int i = 0; i < world.Surfaces.Length; i++)
        {
            int lmIdx = world.Surfaces[i].LightmapIndex;
            if (lmIdx >= 0)
                world.Surfaces[i].LightmapIndex = lmIdx / 2;
        }

        world.Lightmaps = lightmaps;
        world.DeluxeMaps = deluxeMaps;
        world.HasDeluxeMapping = true;

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Detected deluxe mapping: {halfCount} lightmap/deluxe pairs\n");
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

    // --- R_ColorShiftLightingBytes (Q3 overbright compensation) ---

    /// <summary>
    /// Shift RGB bytes left by 'shift' bits, normalizing by color (not saturating to white)
    /// to preserve hue. Matches Q3's R_ColorShiftLightingBytes exactly.
    /// </summary>
    private static void ColorShiftBytes(ref byte r, ref byte g, ref byte b, int shift)
    {
        int ri = r << shift;
        int gi = g << shift;
        int bi = b << shift;

        if ((ri | gi | bi) > 255)
        {
            int max = ri;
            if (gi > max) max = gi;
            if (bi > max) max = bi;
            ri = ri * 255 / max;
            gi = gi * 255 / max;
            bi = bi * 255 / max;
        }

        r = (byte)ri;
        g = (byte)gi;
        b = (byte)bi;
    }

    /// <summary>Apply overbright shift to all lightmap texels.</summary>
    private static void ColorShiftLightmaps(BspWorld world, int shift)
    {
        foreach (var lm in world.Lightmaps)
        {
            byte[] data = lm.Data;
            for (int i = 0; i < data.Length - 2; i += 3)
                ColorShiftBytes(ref data[i], ref data[i + 1], ref data[i + 2], shift);
        }
    }

    /// <summary>Apply overbright shift to BSP vertex colors (R, G, B).</summary>
    private static void ColorShiftVertexColors(BspWorld world, int shift)
    {
        for (int i = 0; i < world.Vertices.Length; i++)
            ColorShiftBytes(ref world.Vertices[i].R, ref world.Vertices[i].G,
                            ref world.Vertices[i].B, shift);
    }

    /// <summary>Apply overbright shift to light grid ambient and directed RGB bytes.</summary>
    private static void ColorShiftLightGrid(BspWorld world, int shift)
    {
        byte[]? grid = world.LightGridData;
        if (grid == null) return;

        // Grid format: 8 bytes per point: ambient RGB (3), directed RGB (3), lat (1), lng (1)
        for (int i = 0; i + 7 < grid.Length; i += 8)
        {
            ColorShiftBytes(ref grid[i], ref grid[i + 1], ref grid[i + 2], shift);
            ColorShiftBytes(ref grid[i + 3], ref grid[i + 4], ref grid[i + 5], shift);
        }
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

    /// <summary>
    /// Compute per-vertex tangent vectors from triangle UV deltas.
    /// Uses MikkTSpace-style averaging: accumulate tangent per triangle vertex,
    /// then normalize and Gram-Schmidt orthogonalize against the normal.
    /// </summary>
    private static void ComputeTangents(BspWorld world)
    {
        var verts = world.Vertices;
        int numVerts = verts.Length;

        // Accumulate tangent + bitangent per vertex
        var tan1 = new float[numVerts * 3]; // tangent accumulator
        var tan2 = new float[numVerts * 3]; // bitangent accumulator

        // Process each surface's triangles
        for (int si = 0; si < world.Surfaces.Length; si++)
        {
            ref var surf = ref world.Surfaces[si];
            if (surf.SurfaceType != SurfaceTypes.MST_PLANAR &&
                surf.SurfaceType != SurfaceTypes.MST_TRIANGLE_SOUP &&
                surf.SurfaceType != SurfaceTypes.MST_PATCH)
                continue;

            for (int j = 0; j < surf.NumIndices; j += 3)
            {
                int idx0 = surf.FirstIndex + j;
                int idx1 = surf.FirstIndex + j + 1;
                int idx2 = surf.FirstIndex + j + 2;
                if (idx2 >= world.Indices.Length) break;

                int i0 = world.Indices[idx0] + surf.FirstVertex;
                int i1 = world.Indices[idx1] + surf.FirstVertex;
                int i2 = world.Indices[idx2] + surf.FirstVertex;
                if (i0 >= numVerts || i1 >= numVerts || i2 >= numVerts) continue;

                ref var v0 = ref verts[i0];
                ref var v1 = ref verts[i1];
                ref var v2 = ref verts[i2];

                float dx1 = v1.X - v0.X, dy1 = v1.Y - v0.Y, dz1 = v1.Z - v0.Z;
                float dx2 = v2.X - v0.X, dy2 = v2.Y - v0.Y, dz2 = v2.Z - v0.Z;

                float du1 = v1.U - v0.U, dv1 = v1.V - v0.V;
                float du2 = v2.U - v0.U, dv2 = v2.V - v0.V;

                float r = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(r) < 1e-12f) continue;
                r = 1f / r;

                float tx = (dv2 * dx1 - dv1 * dx2) * r;
                float ty = (dv2 * dy1 - dv1 * dy2) * r;
                float tz = (dv2 * dz1 - dv1 * dz2) * r;

                float bx = (du1 * dx2 - du2 * dx1) * r;
                float by = (du1 * dy2 - du2 * dy1) * r;
                float bz = (du1 * dz2 - du2 * dz1) * r;

                // Accumulate for all 3 vertices
                foreach (int vi in new[] { i0, i1, i2 })
                {
                    tan1[vi * 3] += tx; tan1[vi * 3 + 1] += ty; tan1[vi * 3 + 2] += tz;
                    tan2[vi * 3] += bx; tan2[vi * 3 + 1] += by; tan2[vi * 3 + 2] += bz;
                }
            }
        }

        // Gram-Schmidt orthogonalize and compute handedness
        for (int i = 0; i < numVerts; i++)
        {
            float nx = verts[i].NX, ny = verts[i].NY, nz = verts[i].NZ;
            float tx = tan1[i * 3], ty = tan1[i * 3 + 1], tz = tan1[i * 3 + 2];

            // t = normalize(t - n * dot(n, t))
            float dot = nx * tx + ny * ty + nz * tz;
            tx -= nx * dot; ty -= ny * dot; tz -= nz * dot;

            float len = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            if (len > 1e-6f)
            {
                tx /= len; ty /= len; tz /= len;
            }
            else
            {
                // Fallback: generate arbitrary tangent perpendicular to normal
                if (MathF.Abs(nx) < 0.9f)
                { tx = 0; ty = -nz; tz = ny; }
                else
                { tx = nz; ty = 0; tz = -nx; }
                len = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (len > 0) { tx /= len; ty /= len; tz /= len; }
            }

            // Handedness: sign = dot(cross(n, t), bitangent)
            float cx = ny * tz - nz * ty;
            float cy = nz * tx - nx * tz;
            float cz = nx * ty - ny * tx;
            float bx = tan2[i * 3], by = tan2[i * 3 + 1], bz = tan2[i * 3 + 2];
            float sign = (cx * bx + cy * by + cz * bz) < 0f ? -1f : 1f;

            verts[i].TX = tx;
            verts[i].TY = ty;
            verts[i].TZ = tz;
            verts[i].TS = sign;
        }
    }

    /// <summary>
    /// Parse cubemap entities from the BSP entity string.
    /// Looks for misc_cubemap first, falls back to info_player_deathmatch spawn points.
    /// </summary>
    private static void ParseCubemapEntities(BspWorld world)
    {
        if (string.IsNullOrEmpty(world.EntityString)) return;

        var cubemaps = ParseEntitiesByClassname(world.EntityString, "misc_cubemap");
        if (cubemaps.Count == 0)
            cubemaps = ParseEntitiesByClassname(world.EntityString, "info_player_deathmatch");
        if (cubemaps.Count == 0) return;

        world.Cubemaps = cubemaps.ToArray();
    }

    private static List<BspCubemap> ParseEntitiesByClassname(string entityString, string classname)
    {
        var result = new List<BspCubemap>();
        int pos = 0;

        while (pos < entityString.Length)
        {
            int braceStart = entityString.IndexOf('{', pos);
            if (braceStart < 0) break;
            int braceEnd = entityString.IndexOf('}', braceStart);
            if (braceEnd < 0) break;

            string block = entityString.Substring(braceStart + 1, braceEnd - braceStart - 1);
            pos = braceEnd + 1;

            string? foundClass = null;
            float ox = 0, oy = 0, oz = 0;
            float radius = 1000f;
            bool originSet = false;

            // Parse key-value pairs from the entity block
            int linePos = 0;
            while (linePos < block.Length)
            {
                // Find key
                int q1 = block.IndexOf('"', linePos);
                if (q1 < 0) break;
                int q2 = block.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string key = block.Substring(q1 + 1, q2 - q1 - 1);

                // Find value
                int q3 = block.IndexOf('"', q2 + 1);
                if (q3 < 0) break;
                int q4 = block.IndexOf('"', q3 + 1);
                if (q4 < 0) break;
                string value = block.Substring(q3 + 1, q4 - q3 - 1);

                linePos = q4 + 1;

                if (key == "classname")
                    foundClass = value;
                else if (key == "origin")
                {
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out ox) &&
                        float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out oy) &&
                        float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out oz))
                        originSet = true;
                }
                else if (key == "radius" || key == "parallaxRadius")
                {
                    float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out radius);
                }
            }

            if (foundClass == classname && originSet)
            {
                result.Add(new BspCubemap
                {
                    Name = classname,
                    OriginX = ox,
                    OriginY = oy,
                    OriginZ = oz,
                    ParallaxRadius = radius > 0 ? radius : 1000f,
                });
            }
        }

        return result;
    }
}
