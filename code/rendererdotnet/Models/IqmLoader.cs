using System;
using System.Runtime.InteropServices;
using System.Text;
using RendererDotNet.Interop;

namespace RendererDotNet.Models;

/// <summary>
/// Loads IQM (Inter-Quake Model v2) files from the engine filesystem.
/// Parses the binary format defined in iqm.h and produces IqmModel objects.
/// Supports skeletal animation with up to 128 bones.
/// </summary>
public static unsafe class IqmLoader
{
    private const int IQM_VERSION = 2;
    private const int IQM_MAX_JOINTS = 128;

    // Vertex array types
    private const int IQM_POSITION = 0;
    private const int IQM_TEXCOORD = 1;
    private const int IQM_NORMAL = 2;
    private const int IQM_TANGENT = 3;
    private const int IQM_BLENDINDEXES = 4;
    private const int IQM_BLENDWEIGHTS = 5;
    private const int IQM_COLOR = 6;

    // Data types
    private const int IQM_UBYTE = 1;
    private const int IQM_FLOAT = 7;

    public static IqmModel? LoadFromEngineFS(string path)
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
                $"[.NET] Failed to parse IQM '{path}': {ex.Message}\n");
            return null;
        }
        finally
        {
            EngineImports.FS_FreeFile(buf);
        }
    }

    private static IqmModel? Parse(byte* data, int fileSize, string path)
    {
        if (fileSize < 124) return null; // header is 124 bytes

        // Validate magic
        var magic = Encoding.ASCII.GetString(data, 16).TrimEnd('\0');
        if (magic != "INTERQUAKEMODEL")
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] IQM: {path} has wrong magic\n");
            return null;
        }

        uint* hdr = (uint*)(data + 16);
        uint version = hdr[0];
        if (version != IQM_VERSION)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] IQM: {path} has wrong version {version}\n");
            return null;
        }

        // Parse header fields (offsets in uint indices from hdr)
        // uint filesize = hdr[1]; uint flags = hdr[2];
        uint numText = hdr[3]; uint ofsText = hdr[4];
        uint numMeshes = hdr[5]; uint ofsMeshes = hdr[6];
        uint numVertexArrays = hdr[7]; uint numVertexes = hdr[8]; uint ofsVertexArrays = hdr[9];
        uint numTriangles = hdr[10]; uint ofsTriangles = hdr[11]; // uint ofsAdjacency = hdr[12];
        uint numJoints = hdr[13]; uint ofsJoints = hdr[14];
        uint numPoses = hdr[15]; uint ofsPoses = hdr[16];
        uint numAnims = hdr[17]; uint ofsAnims = hdr[18];
        uint numFrames = hdr[19]; /* numFrameChannels = hdr[20]; */ uint ofsFrames = hdr[21];
        uint ofsBounds = hdr[22];

        if (numJoints > IQM_MAX_JOINTS)
        {
            EngineImports.Printf(EngineImports.PRINT_WARNING,
                $"[.NET] IQM: {path} has too many joints ({numJoints} > {IQM_MAX_JOINTS})\n");
            return null;
        }

        var model = new IqmModel
        {
            Name = path,
            NumVertexes = (int)numVertexes,
            NumTriangles = (int)numTriangles,
            NumFrames = (int)numFrames,
        };

        // Parse text block for string lookups
        byte* textBase = data + ofsText;

        // Parse vertex arrays
        if (numVertexArrays > 0 && ofsVertexArrays > 0)
            ParseVertexArrays(data, fileSize, ofsVertexArrays, numVertexArrays, numVertexes, model);

        // Parse triangles
        if (numTriangles > 0 && ofsTriangles > 0)
        {
            model.Triangles = new int[numTriangles * 3];
            uint* tri = (uint*)(data + ofsTriangles);
            for (int i = 0; i < (int)numTriangles * 3; i++)
                model.Triangles[i] = (int)tri[i];
        }

        // Parse meshes
        if (numMeshes > 0 && ofsMeshes > 0)
        {
            model.Surfaces = new IqmSurface[numMeshes];
            byte* meshData = data + ofsMeshes;
            for (int i = 0; i < (int)numMeshes; i++)
            {
                uint* m = (uint*)(meshData + i * 6 * 4); // 6 uint fields per mesh
                string meshName = ReadString(textBase, numText, m[0]);
                string matName = ReadString(textBase, numText, m[1]);

                model.Surfaces[i] = new IqmSurface
                {
                    Name = meshName,
                    ShaderName = matName,
                    FirstVertex = (int)m[2],
                    NumVertexes = (int)m[3],
                    FirstTriangle = (int)m[4],
                    NumTriangles = (int)m[5],
                };
            }
        }
        else
        {
            model.Surfaces = [];
        }

        // Parse joints
        if (numJoints > 0 && ofsJoints > 0)
        {
            model.Joints = new IqmJoint[numJoints];
            byte* jointData = data + ofsJoints;
            // iqmJoint_t: uint name, int parent, float[3] translate, float[4] rotate, float[3] scale = 12 * 4 bytes
            for (int i = 0; i < (int)numJoints; i++)
            {
                byte* j = jointData + i * 48; // 12 fields * 4 bytes
                uint nameOfs = *(uint*)j;
                int parent = *(int*)(j + 4);
                float* vals = (float*)(j + 8);

                model.Joints[i] = new IqmJoint
                {
                    Name = ReadString(textBase, numText, nameOfs),
                    Parent = parent,
                    TranslateX = vals[0], TranslateY = vals[1], TranslateZ = vals[2],
                    RotateX = vals[3], RotateY = vals[4], RotateZ = vals[5], RotateW = vals[6],
                    ScaleX = vals[7], ScaleY = vals[8], ScaleZ = vals[9],
                };
            }

            // Compute bind-pose matrices
            ComputeBindPoseMatrices(model);
        }
        else
        {
            model.Joints = [];
            model.BindJoints = [];
            model.InvBindJoints = [];
        }

        // Parse poses (animation keyframes)
        if (numPoses > 0 && ofsPoses > 0 && numFrames > 0 && ofsFrames > 0)
        {
            ParsePoses(data, numPoses, ofsPoses, numFrames, ofsFrames, model);
        }
        else
        {
            model.Poses = [];
        }

        // Parse animations
        if (numAnims > 0 && ofsAnims > 0)
        {
            model.Animations = new IqmAnimation[numAnims];
            byte* animData = data + ofsAnims;
            for (int i = 0; i < (int)numAnims; i++)
            {
                byte* a = animData + i * 20; // 5 fields * 4 bytes
                uint nameOfs = *(uint*)a;
                uint firstFrame = *(uint*)(a + 4);
                uint nFrames = *(uint*)(a + 8);
                float framerate = *(float*)(a + 12);
                uint flags = *(uint*)(a + 16);

                model.Animations[i] = new IqmAnimation
                {
                    Name = ReadString(textBase, numText, nameOfs),
                    FirstFrame = (int)firstFrame,
                    NumFrames = (int)nFrames,
                    Framerate = framerate,
                    Loop = (flags & 1) != 0, // IQM_LOOP
                };
            }
        }
        else
        {
            model.Animations = [];
        }

        // Parse bounds
        if (numFrames > 0 && ofsBounds > 0)
        {
            model.Bounds = new float[numFrames * 6];
            byte* boundsData = data + ofsBounds;
            for (int i = 0; i < (int)numFrames; i++)
            {
                float* b = (float*)(boundsData + i * 32); // bbmin[3] + bbmax[3] + xyradius + radius
                model.Bounds[i * 6 + 0] = b[0]; // minX
                model.Bounds[i * 6 + 1] = b[1]; // minY
                model.Bounds[i * 6 + 2] = b[2]; // minZ
                model.Bounds[i * 6 + 3] = b[3]; // maxX
                model.Bounds[i * 6 + 4] = b[4]; // maxY
                model.Bounds[i * 6 + 5] = b[5]; // maxZ
            }
        }
        else
        {
            model.Bounds = [];
        }

        return model;
    }

    private static void ParseVertexArrays(byte* data, int fileSize, uint ofsVertexArrays,
        uint numVertexArrays, uint numVertexes, IqmModel model)
    {
        byte* vaData = data + ofsVertexArrays;
        // iqmVertexArray_t: uint type, flags, format, size, offset = 5 * 4 bytes

        for (int i = 0; i < (int)numVertexArrays; i++)
        {
            uint* va = (uint*)(vaData + i * 20);
            uint type = va[0];
            // uint flags = va[1];
            uint format = va[2];
            // uint size = va[3];
            uint offset = va[4];

            byte* src = data + offset;

            switch (type)
            {
                case IQM_POSITION:
                    if (format == IQM_FLOAT)
                    {
                        model.Positions = new float[numVertexes * 3];
                        Marshal.Copy((nint)src, model.Positions, 0, (int)numVertexes * 3);
                    }
                    break;

                case IQM_TEXCOORD:
                    if (format == IQM_FLOAT)
                    {
                        model.TexCoords = new float[numVertexes * 2];
                        Marshal.Copy((nint)src, model.TexCoords, 0, (int)numVertexes * 2);
                    }
                    break;

                case IQM_NORMAL:
                    if (format == IQM_FLOAT)
                    {
                        model.Normals = new float[numVertexes * 3];
                        Marshal.Copy((nint)src, model.Normals, 0, (int)numVertexes * 3);
                    }
                    break;

                case IQM_TANGENT:
                    if (format == IQM_FLOAT)
                    {
                        model.Tangents = new float[numVertexes * 4];
                        Marshal.Copy((nint)src, model.Tangents, 0, (int)numVertexes * 4);
                    }
                    break;

                case IQM_BLENDINDEXES:
                    if (format == IQM_UBYTE)
                    {
                        model.BlendIndexes = new byte[numVertexes * 4];
                        Marshal.Copy((nint)src, model.BlendIndexes, 0, (int)numVertexes * 4);
                    }
                    break;

                case IQM_BLENDWEIGHTS:
                    if (format == IQM_UBYTE)
                    {
                        model.BlendWeights = new byte[numVertexes * 4];
                        Marshal.Copy((nint)src, model.BlendWeights, 0, (int)numVertexes * 4);
                    }
                    break;

                case IQM_COLOR:
                    if (format == IQM_UBYTE)
                    {
                        model.Colors = new byte[numVertexes * 4];
                        Marshal.Copy((nint)src, model.Colors, 0, (int)numVertexes * 4);
                    }
                    break;
            }
        }
    }

    private static void ParsePoses(byte* data, uint numPoses, uint ofsPoses,
        uint numFrames, uint ofsFrames, IqmModel model)
    {
        // iqmPose_t: int parent, uint mask, float channeloffset[10], float channelscale[10] = 88 bytes
        byte* poseData = data + ofsPoses;
        ushort* frameData = (ushort*)(data + ofsFrames);

        model.Poses = new IqmTransform[numFrames * numPoses];

        for (int frame = 0; frame < (int)numFrames; frame++)
        {
            for (int pose = 0; pose < (int)numPoses; pose++)
            {
                byte* p = poseData + pose * 88;
                // int parent = *(int*)p; // not needed here, stored in joints
                uint mask = *(uint*)(p + 4);
                float* channelOffset = (float*)(p + 8);
                float* channelScale = (float*)(p + 48);

                // Decode 10 channels: translate[3], rotate[4], scale[3]
                Span<float> channels = stackalloc float[10];
                for (int ch = 0; ch < 10; ch++)
                {
                    channels[ch] = channelOffset[ch];
                    if ((mask & (1u << ch)) != 0)
                    {
                        channels[ch] += *frameData * channelScale[ch];
                        frameData++;
                    }
                }

                // Normalize quaternion
                float qx = channels[3], qy = channels[4], qz = channels[5], qw = channels[6];
                float qLen = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (qLen > 0.0001f)
                {
                    float inv = 1.0f / qLen;
                    qx *= inv; qy *= inv; qz *= inv; qw *= inv;
                }
                else
                {
                    qx = qy = qz = 0; qw = 1;
                }

                model.Poses[frame * (int)numPoses + pose] = new IqmTransform
                {
                    TranslateX = channels[0], TranslateY = channels[1], TranslateZ = channels[2],
                    RotateX = qx, RotateY = qy, RotateZ = qz, RotateW = qw,
                    ScaleX = channels[7], ScaleY = channels[8], ScaleZ = channels[9],
                };
            }
        }
    }

    private static void ComputeBindPoseMatrices(IqmModel model)
    {
        int n = model.Joints.Length;
        model.BindJoints = new float[n * 12];
        model.InvBindJoints = new float[n * 12];
        Span<float> localMat = stackalloc float[12];
        Span<float> tempMat = stackalloc float[12];

        for (int i = 0; i < n; i++)
        {
            ref var joint = ref model.Joints[i];

            // Convert joint transform (quat+translate+scale) to 3x4 matrix
            QuatScaleToMatrix(
                joint.RotateX, joint.RotateY, joint.RotateZ, joint.RotateW,
                joint.ScaleX, joint.ScaleY, joint.ScaleZ,
                joint.TranslateX, joint.TranslateY, joint.TranslateZ,
                localMat);

            if (joint.Parent >= 0)
            {
                // Multiply parent * local
                var parentSlice = model.BindJoints.AsSpan(joint.Parent * 12, 12);
                Matrix34Multiply(parentSlice, localMat, tempMat);
                tempMat.CopyTo(model.BindJoints.AsSpan(i * 12, 12));
            }
            else
            {
                localMat.CopyTo(model.BindJoints.AsSpan(i * 12, 12));
            }

            // Compute inverse
            InvertMatrix34(model.BindJoints.AsSpan(i * 12, 12), model.InvBindJoints.AsSpan(i * 12, 12));
        }
    }

    /// <summary>
    /// Compute interpolated pose matrices for a given frame pair.
    /// Output is numJoints * 12 floats (3x4 row-major matrices).
    /// Each matrix = bindJoint * lerp(oldPose, newPose) * invBindJoint (for skinning).
    /// </summary>
    public static void ComputePoseMatrices(IqmModel model, int frame, int oldFrame, float backLerp,
        Span<float> outMats)
    {
        int numJoints = model.NumJoints;
        if (numJoints == 0) return;

        int numFrames = model.NumFrames;
        if (numFrames > 0)
        {
            frame = frame % numFrames;
            oldFrame = oldFrame % numFrames;
        }
        else
        {
            frame = oldFrame = 0;
        }

        float lerp = 1.0f - backLerp;
        Span<float> localMat = stackalloc float[12];
        Span<float> tempMat = stackalloc float[12];
        Span<float> tempMat2 = stackalloc float[12];

        for (int i = 0; i < numJoints; i++)
        {
            float tx, ty, tz, qx, qy, qz, qw, sx, sy, sz;

            if (numFrames == 0)
            {
                // Use bind pose
                ref var j = ref model.Joints[i];
                tx = j.TranslateX; ty = j.TranslateY; tz = j.TranslateZ;
                qx = j.RotateX; qy = j.RotateY; qz = j.RotateZ; qw = j.RotateW;
                sx = j.ScaleX; sy = j.ScaleY; sz = j.ScaleZ;
            }
            else if (frame == oldFrame || backLerp <= 0f)
            {
                ref var p = ref model.Poses[frame * numJoints + i];
                tx = p.TranslateX; ty = p.TranslateY; tz = p.TranslateZ;
                qx = p.RotateX; qy = p.RotateY; qz = p.RotateZ; qw = p.RotateW;
                sx = p.ScaleX; sy = p.ScaleY; sz = p.ScaleZ;
            }
            else
            {
                // Interpolate between old and new frame
                ref var pOld = ref model.Poses[oldFrame * numJoints + i];
                ref var pNew = ref model.Poses[frame * numJoints + i];
                tx = pOld.TranslateX * backLerp + pNew.TranslateX * lerp;
                ty = pOld.TranslateY * backLerp + pNew.TranslateY * lerp;
                tz = pOld.TranslateZ * backLerp + pNew.TranslateZ * lerp;
                sx = pOld.ScaleX * backLerp + pNew.ScaleX * lerp;
                sy = pOld.ScaleY * backLerp + pNew.ScaleY * lerp;
                sz = pOld.ScaleZ * backLerp + pNew.ScaleZ * lerp;
                QuatSlerp(
                    pOld.RotateX, pOld.RotateY, pOld.RotateZ, pOld.RotateW,
                    pNew.RotateX, pNew.RotateY, pNew.RotateZ, pNew.RotateW,
                    lerp, out qx, out qy, out qz, out qw);
            }

            // Convert to 3x4 matrix
            QuatScaleToMatrix(qx, qy, qz, qw, sx, sy, sz, tx, ty, tz, localMat);

            // Chain with parent to build world-space transform, then multiply by inverse bind pose
            int parent = model.Joints[i].Parent;
            if (parent >= 0)
            {
                // worldMat = parentWorldMat * localMat
                // We store intermediate world matrices in tempMat, then skinMat = worldMat * invBind
                // But outMats already contains skinMat for parent, so we need to recover parentWorldMat.
                // Instead, store world matrices in outMats first, then convert to skin matrices after.
                // For efficiency, use a two-pass or store world mats separately.
                // Simpler: just chain world matrices directly
                Matrix34Multiply(outMats.Slice(parent * 12, 12), localMat, tempMat);
                tempMat.CopyTo(outMats.Slice(i * 12, 12));
            }
            else
            {
                localMat.CopyTo(outMats.Slice(i * 12, 12));
            }
        }

        // Second pass: multiply each world matrix by its inverse bind pose to get skinning matrix
        for (int i = 0; i < numJoints; i++)
        {
            var worldMat = outMats.Slice(i * 12, 12);
            worldMat.CopyTo(tempMat);
            var invBind = model.InvBindJoints.AsSpan(i * 12, 12);
            Matrix34Multiply(tempMat, invBind, worldMat);
        }
    }

    private static string ReadString(byte* textBase, uint numText, uint offset)
    {
        if (offset >= numText) return "";
        byte* str = textBase + offset;
        int len = 0;
        while (offset + len < numText && str[len] != 0) len++;
        return Encoding.UTF8.GetString(str, len);
    }

    private static void QuatScaleToMatrix(float qx, float qy, float qz, float qw,
        float sx, float sy, float sz, float tx, float ty, float tz, Span<float> m)
    {
        float x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
        float xx = qx * x2, yy = qy * y2, zz = qz * z2;
        float xy = qx * y2, xz = qx * z2, yz = qy * z2;
        float wx = qw * x2, wy = qw * y2, wz = qw * z2;

        // Row 0
        m[0] = (1.0f - (yy + zz)) * sx;
        m[1] = (xy - wz) * sy;
        m[2] = (xz + wy) * sz;
        m[3] = tx;
        // Row 1
        m[4] = (xy + wz) * sx;
        m[5] = (1.0f - (xx + zz)) * sy;
        m[6] = (yz - wx) * sz;
        m[7] = ty;
        // Row 2
        m[8] = (xz - wy) * sx;
        m[9] = (yz + wx) * sy;
        m[10] = (1.0f - (xx + yy)) * sz;
        m[11] = tz;
    }

    private static void Matrix34Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> o)
    {
        o[0] = a[0] * b[0] + a[1] * b[4] + a[2] * b[8];
        o[1] = a[0] * b[1] + a[1] * b[5] + a[2] * b[9];
        o[2] = a[0] * b[2] + a[1] * b[6] + a[2] * b[10];
        o[3] = a[0] * b[3] + a[1] * b[7] + a[2] * b[11] + a[3];
        o[4] = a[4] * b[0] + a[5] * b[4] + a[6] * b[8];
        o[5] = a[4] * b[1] + a[5] * b[5] + a[6] * b[9];
        o[6] = a[4] * b[2] + a[5] * b[6] + a[6] * b[10];
        o[7] = a[4] * b[3] + a[5] * b[7] + a[6] * b[11] + a[7];
        o[8] = a[8] * b[0] + a[9] * b[4] + a[10] * b[8];
        o[9] = a[8] * b[1] + a[9] * b[5] + a[10] * b[9];
        o[10] = a[8] * b[2] + a[9] * b[6] + a[10] * b[10];
        o[11] = a[8] * b[3] + a[9] * b[7] + a[10] * b[11] + a[11];
    }

    private static void InvertMatrix34(ReadOnlySpan<float> m, Span<float> inv)
    {
        // For an affine 3x4 matrix, inverse = transpose(rotation) * -translation
        // Transpose 3x3
        inv[0] = m[0]; inv[1] = m[4]; inv[2] = m[8];
        inv[4] = m[1]; inv[5] = m[5]; inv[6] = m[9];
        inv[8] = m[2]; inv[9] = m[6]; inv[10] = m[10];
        // Translation = -(R^T * t)
        inv[3] = -(inv[0] * m[3] + inv[1] * m[7] + inv[2] * m[11]);
        inv[7] = -(inv[4] * m[3] + inv[5] * m[7] + inv[6] * m[11]);
        inv[11] = -(inv[8] * m[3] + inv[9] * m[7] + inv[10] * m[11]);
    }

    private static void QuatSlerp(float ax, float ay, float az, float aw,
        float bx, float by, float bz, float bw, float t,
        out float rx, out float ry, out float rz, out float rw)
    {
        float cosom = ax * bx + ay * by + az * bz + aw * bw;

        // Flip if dot product is negative (shortest path)
        if (cosom < 0)
        {
            cosom = -cosom;
            bx = -bx; by = -by; bz = -bz; bw = -bw;
        }

        float s0, s1;
        if (1.0f - cosom > 0.0001f)
        {
            float omega = MathF.Acos(MathF.Min(cosom, 1.0f));
            float sinOm = MathF.Sin(omega);
            s0 = MathF.Sin((1.0f - t) * omega) / sinOm;
            s1 = MathF.Sin(t * omega) / sinOm;
        }
        else
        {
            // Very close, use linear interpolation
            s0 = 1.0f - t;
            s1 = t;
        }

        rx = s0 * ax + s1 * bx;
        ry = s0 * ay + s1 * by;
        rz = s0 * az + s1 * bz;
        rw = s0 * aw + s1 * bw;
    }
}
