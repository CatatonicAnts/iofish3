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
    private const int LUMP_SHADERS = 1;
    private const int LUMP_PLANES = 2;
    private const int LUMP_NODES = 3;
    private const int LUMP_LEAFS = 4;
    private const int LUMP_LEAFSURFACES = 5;
    private const int LUMP_MODELS = 7;
    private const int LUMP_DRAWVERTS = 10;
    private const int LUMP_DRAWINDEXES = 11;
    private const int LUMP_SURFACES = 13;
    private const int LUMP_LIGHTMAPS = 14;
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

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Loaded BSP: {path} ({world.Vertices.Length} verts, {world.Surfaces.Length} surfs, " +
            $"{world.Nodes.Length} nodes, {world.Leafs.Length} leafs, {world.Lightmaps.Length} lightmaps)\n");

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
}
