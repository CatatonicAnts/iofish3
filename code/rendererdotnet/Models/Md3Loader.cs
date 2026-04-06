using System;
using System.Runtime.InteropServices;
using System.Text;
using RendererDotNet.Interop;

namespace RendererDotNet.Models;

/// <summary>
/// Loads MD3 model files from the engine's virtual filesystem.
/// Parses the binary format defined in qfiles.h and produces
/// runtime-friendly Md3Model objects.
///
/// MD3 binary layout:
///   md3Header_t (108 bytes)
///   md3Frame_t[numFrames] @ ofsFrames
///   md3Tag_t[numTags * numFrames] @ ofsTags
///   md3Surface_t[numSurfaces] @ ofsSurfaces (each surface is variable-sized)
/// </summary>
public static unsafe class Md3Loader
{
    private const int MD3_IDENT = 0x33504449; // 'IDP3'
    private const int MD3_VERSION = 15;
    private const float MD3_XYZ_SCALE = 1.0f / 64.0f;
    private const int MAX_QPATH = 64;

    // Header field offsets
    private const int HDR_IDENT = 0;
    private const int HDR_VERSION = 4;
    private const int HDR_NAME = 8;
    private const int HDR_FLAGS = 72;
    private const int HDR_NUM_FRAMES = 76;
    private const int HDR_NUM_TAGS = 80;
    private const int HDR_NUM_SURFACES = 84;
    private const int HDR_OFS_FRAMES = 92;
    private const int HDR_OFS_TAGS = 96;
    private const int HDR_OFS_SURFACES = 100;

    // Frame struct: 56 bytes
    private const int FRAME_SIZE = 56;

    // Tag struct: 112 bytes (64 name + 12 origin + 36 axis)
    private const int TAG_SIZE = 112;

    // Surface header offsets (relative to surface start)
    private const int SURF_NAME = 4;
    private const int SURF_FLAGS = 68;
    private const int SURF_NUM_FRAMES = 72;
    private const int SURF_NUM_SHADERS = 76;
    private const int SURF_NUM_VERTS = 80;
    private const int SURF_NUM_TRIS = 84;
    private const int SURF_OFS_TRIS = 88;
    private const int SURF_OFS_SHADERS = 92;
    private const int SURF_OFS_ST = 96;
    private const int SURF_OFS_XYZNORMALS = 100;
    private const int SURF_OFS_END = 104;

    /// <summary>
    /// Load an MD3 model from the engine filesystem.
    /// Returns null if the file can't be found or is invalid.
    /// </summary>
    public static Md3Model? LoadFromEngineFS(string path)
    {
        int len = EngineImports.FS_ReadFile(path, out byte* buf);
        if (len <= 0 || buf == null)
        {
            if (buf != null) EngineImports.FS_FreeFile(buf);
            return null;
        }

        try
        {
            return Parse(buf, len, path);
        }
        catch (Exception ex)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] Failed to parse MD3 '{path}': {ex.Message}\n");
            return null;
        }
        finally
        {
            EngineImports.FS_FreeFile(buf);
        }
    }

    private static Md3Model? Parse(byte* buf, int size, string path)
    {
        if (size < 108) return null;

        int ident = *(int*)(buf + HDR_IDENT);
        int version = *(int*)(buf + HDR_VERSION);

        if (ident != MD3_IDENT)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] R_LoadMD3: {path} has wrong ident\n");
            return null;
        }
        if (version != MD3_VERSION)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] R_LoadMD3: {path} has wrong version ({version} should be {MD3_VERSION})\n");
            return null;
        }

        int numFrames = *(int*)(buf + HDR_NUM_FRAMES);
        int numTags = *(int*)(buf + HDR_NUM_TAGS);
        int numSurfaces = *(int*)(buf + HDR_NUM_SURFACES);
        int ofsFrames = *(int*)(buf + HDR_OFS_FRAMES);
        int ofsTags = *(int*)(buf + HDR_OFS_TAGS);
        int ofsSurfaces = *(int*)(buf + HDR_OFS_SURFACES);

        if (numFrames <= 0)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] R_LoadMD3: {path} has no frames\n");
            return null;
        }

        var model = new Md3Model
        {
            Name = ReadString(buf + HDR_NAME, MAX_QPATH),
        };

        // --- Frames ---
        model.Frames = new Md3Frame[numFrames];
        byte* framePtr = buf + ofsFrames;
        for (int i = 0; i < numFrames; i++)
        {
            float* f = (float*)(framePtr + i * FRAME_SIZE);
            model.Frames[i] = new Md3Frame
            {
                MinX = f[0], MinY = f[1], MinZ = f[2],
                MaxX = f[3], MaxY = f[4], MaxZ = f[5],
                OriginX = f[6], OriginY = f[7], OriginZ = f[8],
                Radius = f[9]
            };
        }

        // --- Tags ---
        model.TagNames = new string[numTags];
        model.Tags = new Md3Tag[numTags * numFrames];
        byte* tagPtr = buf + ofsTags;
        for (int i = 0; i < numTags * numFrames; i++)
        {
            byte* t = tagPtr + i * TAG_SIZE;
            string tagName = ReadString(t, MAX_QPATH);
            float* origin = (float*)(t + MAX_QPATH);
            float* axis = (float*)(t + MAX_QPATH + 12);

            if (i < numTags)
                model.TagNames[i] = tagName;

            model.Tags[i] = new Md3Tag
            {
                OriginX = origin[0], OriginY = origin[1], OriginZ = origin[2],
                Ax0 = axis[0], Ax1 = axis[1], Ax2 = axis[2],
                Ay0 = axis[3], Ay1 = axis[4], Ay2 = axis[5],
                Az0 = axis[6], Az1 = axis[7], Az2 = axis[8]
            };
        }

        // --- Surfaces ---
        model.Surfaces = new Md3Surface[numSurfaces];
        byte* surfPtr = buf + ofsSurfaces;

        for (int s = 0; s < numSurfaces; s++)
        {
            int surfNumFrames = *(int*)(surfPtr + SURF_NUM_FRAMES);
            int surfNumShaders = *(int*)(surfPtr + SURF_NUM_SHADERS);
            int surfNumVerts = *(int*)(surfPtr + SURF_NUM_VERTS);
            int surfNumTris = *(int*)(surfPtr + SURF_NUM_TRIS);
            int ofsTriangles = *(int*)(surfPtr + SURF_OFS_TRIS);
            int ofsShaders = *(int*)(surfPtr + SURF_OFS_SHADERS);
            int ofsSt = *(int*)(surfPtr + SURF_OFS_ST);
            int ofsXyzNormals = *(int*)(surfPtr + SURF_OFS_XYZNORMALS);
            int ofsEnd = *(int*)(surfPtr + SURF_OFS_END);

            var surface = new Md3Surface
            {
                Name = ReadString(surfPtr + SURF_NAME, MAX_QPATH),
                NumVerts = surfNumVerts,
                NumTriangles = surfNumTris,
                NumFrames = surfNumFrames
            };

            // Shader name (first shader entry — 68 bytes: 64 name + 4 index)
            if (surfNumShaders > 0)
            {
                surface.ShaderName = ReadString(surfPtr + ofsShaders, MAX_QPATH);
            }

            // Triangles: 3 ints per triangle
            surface.Indices = new int[surfNumTris * 3];
            int* triPtr = (int*)(surfPtr + ofsTriangles);
            for (int i = 0; i < surfNumTris * 3; i++)
                surface.Indices[i] = triPtr[i];

            // Texture coordinates: 2 floats per vertex
            surface.TexCoords = new float[surfNumVerts * 2];
            float* stPtr = (float*)(surfPtr + ofsSt);
            for (int i = 0; i < surfNumVerts * 2; i++)
                surface.TexCoords[i] = stPtr[i];

            // Vertex positions and normals: numVerts * numFrames
            int totalVerts = surfNumVerts * surfNumFrames;
            surface.Positions = new float[totalVerts * 3];
            surface.Normals = new float[totalVerts * 3];

            // md3XyzNormal_t: 8 bytes per vertex (3 shorts xyz + 1 short normal)
            byte* xyzPtr = surfPtr + ofsXyzNormals;
            for (int i = 0; i < totalVerts; i++)
            {
                short* xyz = (short*)(xyzPtr + i * 8);
                surface.Positions[i * 3] = xyz[0] * MD3_XYZ_SCALE;
                surface.Positions[i * 3 + 1] = xyz[1] * MD3_XYZ_SCALE;
                surface.Positions[i * 3 + 2] = xyz[2] * MD3_XYZ_SCALE;

                // Decode encoded normal (lat/lng packed into short)
                short encodedNormal = xyz[3];
                DecodeNormal(encodedNormal, out float nx, out float ny, out float nz);
                surface.Normals[i * 3] = nx;
                surface.Normals[i * 3 + 1] = ny;
                surface.Normals[i * 3 + 2] = nz;
            }

            model.Surfaces[s] = surface;

            // Advance to next surface
            surfPtr += ofsEnd;
        }

        return model;
    }

    /// <summary>
    /// Decode MD3 packed normal (latitude/longitude encoded in 16 bits).
    /// </summary>
    private static void DecodeNormal(short encoded, out float nx, out float ny, out float nz)
    {
        int lat = (encoded >> 8) & 0xFF;
        int lng = encoded & 0xFF;

        float latRad = lat * (2.0f * MathF.PI / 256.0f);
        float lngRad = lng * (2.0f * MathF.PI / 256.0f);

        nx = MathF.Cos(latRad) * MathF.Sin(lngRad);
        ny = MathF.Sin(latRad) * MathF.Sin(lngRad);
        nz = MathF.Cos(lngRad);
    }

    private static string ReadString(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0)
            len++;
        return Encoding.UTF8.GetString(ptr, len);
    }
}
