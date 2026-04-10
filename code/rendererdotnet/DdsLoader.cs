using System;
using System.Runtime.InteropServices;
using RendererDotNet.Interop;

namespace RendererDotNet;

/// <summary>
/// DDS texture file loader supporting S3TC (DXT1/3/5), RGTC (BC4/5),
/// BPTC (BC6H/7), and uncompressed RGBA formats. Includes DX10 header support.
/// </summary>
public static unsafe class DdsLoader
{
    // DDS header flags
    private const uint DDSFLAGS_MIPMAPCOUNT = 0x20000;

    // Pixel format flags
    private const uint DDSPF_ALPHAPIXELS = 0x1;
    private const uint DDSPF_FOURCC = 0x4;
    private const uint DDSPF_RGB = 0x40;

    // Caps2
    private const uint DDSCAPS2_CUBEMAP = 0xFE00;

    // OpenGL compressed texture format constants
    public const uint GL_COMPRESSED_RGB_S3TC_DXT1_EXT = 0x83F0;
    public const uint GL_COMPRESSED_RGBA_S3TC_DXT1_EXT = 0x83F1;
    public const uint GL_COMPRESSED_RGBA_S3TC_DXT3_EXT = 0x83F2;
    public const uint GL_COMPRESSED_RGBA_S3TC_DXT5_EXT = 0x83F3;
    public const uint GL_COMPRESSED_RED_RGTC1 = 0x8DBB;
    public const uint GL_COMPRESSED_SIGNED_RED_RGTC1 = 0x8DBC;
    public const uint GL_COMPRESSED_RG_RGTC2 = 0x8DBD;
    public const uint GL_COMPRESSED_SIGNED_RG_RGTC2 = 0x8DBE;
    public const uint GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT_ARB = 0x8E8F;
    public const uint GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT_ARB = 0x8E8E;
    public const uint GL_COMPRESSED_RGBA_BPTC_UNORM_ARB = 0x8E8C;
    public const uint GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM_ARB = 0x8E8D;
    public const uint GL_RGBA8 = 0x8058;

    // DXGI format values for DX10 header
    private const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    private const uint DXGI_FORMAT_R8G8B8A8_SNORM = 31;
    private const uint DXGI_FORMAT_BC1_TYPELESS = 70;
    private const uint DXGI_FORMAT_BC1_UNORM = 71;
    private const uint DXGI_FORMAT_BC2_TYPELESS = 73;
    private const uint DXGI_FORMAT_BC2_UNORM = 74;
    private const uint DXGI_FORMAT_BC3_TYPELESS = 76;
    private const uint DXGI_FORMAT_BC3_UNORM = 77;
    private const uint DXGI_FORMAT_BC4_TYPELESS = 79;
    private const uint DXGI_FORMAT_BC4_UNORM = 80;
    private const uint DXGI_FORMAT_BC4_SNORM = 81;
    private const uint DXGI_FORMAT_BC5_TYPELESS = 82;
    private const uint DXGI_FORMAT_BC5_UNORM = 83;
    private const uint DXGI_FORMAT_BC5_SNORM = 84;
    private const uint DXGI_FORMAT_BC6H_TYPELESS = 94;
    private const uint DXGI_FORMAT_BC6H_UF16 = 95;
    private const uint DXGI_FORMAT_BC6H_SF16 = 96;
    private const uint DXGI_FORMAT_BC7_TYPELESS = 97;
    private const uint DXGI_FORMAT_BC7_UNORM = 98;

    [StructLayout(LayoutKind.Sequential)]
    private struct DdsHeader
    {
        public uint HeaderSize;
        public uint Flags;
        public uint Height;
        public uint Width;
        public uint PitchOrFirstMipSize;
        public uint VolumeDepth;
        public uint NumMips;
        public unsafe fixed uint Reserved1[11];
        public uint Always0x20;
        public uint PixelFormatFlags;
        public uint FourCC;
        public uint RgbBitCount;
        public uint RBitMask;
        public uint GBitMask;
        public uint BBitMask;
        public uint ABitMask;
        public uint Caps;
        public uint Caps2;
        public uint Caps3;
        public uint Caps4;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DdsHeaderDxt10
    {
        public uint DxgiFormat;
        public uint Dimensions;
        public uint MiscFlags;
        public uint ArraySize;
        public uint MiscFlags2;
    }

    /// <summary>Result from loading a DDS file.</summary>
    public sealed class DdsResult
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int NumMips { get; init; }
        public uint GlFormat { get; init; }
        public bool IsCompressed { get; init; }
        public bool IsCubemap { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    private static uint EncodeFourCC(string s)
    {
        return (uint)s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);
    }

    /// <summary>
    /// Try to load a DDS file from the engine filesystem.
    /// Returns null if the file doesn't exist or can't be parsed.
    /// </summary>
    public static DdsResult? LoadFromEngineFS(string baseName)
    {
        string path = baseName + ".dds";
        int len = EngineImports.FS_ReadFile(path, out byte* buf);
        if (len <= 0 || buf == null) return null;

        try
        {
            return Parse(buf, len, path);
        }
        catch
        {
            return null;
        }
        finally
        {
            EngineImports.FS_FreeFile(buf);
        }
    }

    private static DdsResult? Parse(byte* buffer, int len, string filename)
    {
        int headerSize = sizeof(DdsHeader);

        // Need at least magic + header
        if (len < 4 + headerSize) return null;

        // Check magic "DDS "
        if (*(uint*)buffer != EncodeFourCC("DDS ")) return null;

        var header = *(DdsHeader*)(buffer + 4);

        byte* data;
        int dataLen;

        // Check for DX10 extended header
        bool hasDx10 = false;
        DdsHeaderDxt10 dx10Header = default;
        if ((header.PixelFormatFlags & DDSPF_FOURCC) != 0 && header.FourCC == EncodeFourCC("DX10"))
        {
            int dx10Size = sizeof(DdsHeaderDxt10);
            if (len < 4 + headerSize + dx10Size) return null;

            dx10Header = *(DdsHeaderDxt10*)(buffer + 4 + headerSize);
            hasDx10 = true;
            data = buffer + 4 + headerSize + dx10Size;
            dataLen = len - 4 - headerSize - dx10Size;
        }
        else
        {
            data = buffer + 4 + headerSize;
            dataLen = len - 4 - headerSize;
        }

        int width = (int)header.Width;
        int height = (int)header.Height;
        int numMips = (header.Flags & DDSFLAGS_MIPMAPCOUNT) != 0 ? (int)header.NumMips : 1;

        // Detect cubemap: all 6 faces must be present
        bool isCubemap = (header.Caps2 & DDSCAPS2_CUBEMAP) == DDSCAPS2_CUBEMAP;
        if (isCubemap && width != height)
            return null; // Cubemap faces must be square

        // Determine GL format
        uint glFormat;
        bool isCompressed;

        if (hasDx10)
        {
            (glFormat, isCompressed) = Dx10FormatToGL(dx10Header.DxgiFormat);
        }
        else if ((header.PixelFormatFlags & DDSPF_FOURCC) != 0)
        {
            (glFormat, isCompressed) = FourCCToGL(header.FourCC);
        }
        else if ((header.PixelFormatFlags & (DDSPF_RGB | DDSPF_ALPHAPIXELS)) == (DDSPF_RGB | DDSPF_ALPHAPIXELS)
                 && header.RgbBitCount == 32
                 && header.RBitMask == 0x000000FF
                 && header.GBitMask == 0x0000FF00
                 && header.BBitMask == 0x00FF0000
                 && header.ABitMask == 0xFF000000)
        {
            glFormat = GL_RGBA8;
            isCompressed = false;
        }
        else
        {
            return null; // Unsupported pixel format
        }

        if (glFormat == 0) return null;
        if (dataLen <= 0) return null;

        var result = new byte[dataLen];
        Marshal.Copy((nint)data, result, 0, dataLen);

        return new DdsResult
        {
            Width = width,
            Height = height,
            NumMips = numMips,
            GlFormat = glFormat,
            IsCompressed = isCompressed,
            IsCubemap = isCubemap,
            Data = result,
        };
    }

    private static (uint glFormat, bool isCompressed) FourCCToGL(uint fourCC)
    {
        if (fourCC == EncodeFourCC("DXT1")) return (GL_COMPRESSED_RGB_S3TC_DXT1_EXT, true);
        if (fourCC == EncodeFourCC("DXT2")) return (GL_COMPRESSED_RGBA_S3TC_DXT3_EXT, true);
        if (fourCC == EncodeFourCC("DXT3")) return (GL_COMPRESSED_RGBA_S3TC_DXT3_EXT, true);
        if (fourCC == EncodeFourCC("DXT4")) return (GL_COMPRESSED_RGBA_S3TC_DXT5_EXT, true);
        if (fourCC == EncodeFourCC("DXT5")) return (GL_COMPRESSED_RGBA_S3TC_DXT5_EXT, true);
        if (fourCC == EncodeFourCC("ATI1")) return (GL_COMPRESSED_RED_RGTC1, true);
        if (fourCC == EncodeFourCC("BC4U")) return (GL_COMPRESSED_RED_RGTC1, true);
        if (fourCC == EncodeFourCC("BC4S")) return (GL_COMPRESSED_SIGNED_RED_RGTC1, true);
        if (fourCC == EncodeFourCC("ATI2")) return (GL_COMPRESSED_RG_RGTC2, true);
        if (fourCC == EncodeFourCC("BC5U")) return (GL_COMPRESSED_RG_RGTC2, true);
        if (fourCC == EncodeFourCC("BC5S")) return (GL_COMPRESSED_SIGNED_RG_RGTC2, true);
        return (0, false);
    }

    private static (uint glFormat, bool isCompressed) Dx10FormatToGL(uint dxgiFormat)
    {
        return dxgiFormat switch
        {
            DXGI_FORMAT_BC1_TYPELESS or DXGI_FORMAT_BC1_UNORM
                => (GL_COMPRESSED_RGB_S3TC_DXT1_EXT, true),
            DXGI_FORMAT_BC2_TYPELESS or DXGI_FORMAT_BC2_UNORM
                => (GL_COMPRESSED_RGBA_S3TC_DXT3_EXT, true),
            DXGI_FORMAT_BC3_TYPELESS or DXGI_FORMAT_BC3_UNORM
                => (GL_COMPRESSED_RGBA_S3TC_DXT5_EXT, true),
            DXGI_FORMAT_BC4_TYPELESS or DXGI_FORMAT_BC4_UNORM
                => (GL_COMPRESSED_RED_RGTC1, true),
            DXGI_FORMAT_BC4_SNORM
                => (GL_COMPRESSED_SIGNED_RED_RGTC1, true),
            DXGI_FORMAT_BC5_TYPELESS or DXGI_FORMAT_BC5_UNORM
                => (GL_COMPRESSED_RG_RGTC2, true),
            DXGI_FORMAT_BC5_SNORM
                => (GL_COMPRESSED_SIGNED_RG_RGTC2, true),
            DXGI_FORMAT_BC6H_TYPELESS or DXGI_FORMAT_BC6H_UF16
                => (GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT_ARB, true),
            DXGI_FORMAT_BC6H_SF16
                => (GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT_ARB, true),
            DXGI_FORMAT_BC7_TYPELESS or DXGI_FORMAT_BC7_UNORM
                => (GL_COMPRESSED_RGBA_BPTC_UNORM_ARB, true),
            DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT_R8G8B8A8_SNORM
                => (GL_RGBA8, false),
            _ => (0, false),
        };
    }

    /// <summary>
    /// Compute the size in bytes of one mip level for a compressed format.
    /// </summary>
    public static int CompressedMipSize(uint glFormat, int width, int height)
    {
        int blockW = Math.Max(1, (width + 3) / 4);
        int blockH = Math.Max(1, (height + 3) / 4);
        int blockSize = glFormat switch
        {
            GL_COMPRESSED_RGB_S3TC_DXT1_EXT or GL_COMPRESSED_RGBA_S3TC_DXT1_EXT
                or GL_COMPRESSED_RED_RGTC1 or GL_COMPRESSED_SIGNED_RED_RGTC1 => 8,
            _ => 16,
        };
        return blockW * blockH * blockSize;
    }
}
