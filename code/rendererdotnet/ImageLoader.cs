using RendererDotNet.Interop;
using StbImageSharp;

namespace RendererDotNet;

/// <summary>
/// Loads images from the engine's virtual filesystem (pk3 archives).
/// Supports DDS (compressed), TGA, JPEG, PNG, BMP via StbImageSharp.
/// </summary>
public static unsafe class ImageLoader
{
    /// <summary>
    /// Unified texture load result: either decoded RGBA pixels or DDS compressed data.
    /// </summary>
    public sealed class TextureResult
    {
        /// <summary>Decoded RGBA pixel data (null for DDS compressed textures).</summary>
        public ImageResult? Image { get; init; }
        /// <summary>DDS data with compressed format info (null for standard images).</summary>
        public DdsLoader.DdsResult? Dds { get; init; }

        public int Width => Dds?.Width ?? Image?.Width ?? 0;
        public int Height => Dds?.Height ?? Image?.Height ?? 0;
        public bool IsDds => Dds != null;
    }

    /// <summary>
    /// Try to load an image by shader name from the engine filesystem.
    /// Tries DDS first (for compressed textures), then TGA/JPG/PNG/BMP.
    /// Returns a TextureResult, or null if not found.
    /// </summary>
    public static TextureResult? LoadTextureFromEngineFS(string name)
    {
        string baseName = name;
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0)
            baseName = name[..dotIdx];

        // Try DDS first (compressed textures are preferred for memory/performance)
        var dds = DdsLoader.LoadFromEngineFS(baseName);
        if (dds != null)
            return new TextureResult { Dds = dds };

        // Fall back to standard image formats
        var img = LoadFromEngineFS(name);
        if (img != null)
            return new TextureResult { Image = img };

        return null;
    }

    /// <summary>
    /// Try to load an image by shader name from the engine filesystem.
    /// Q3 shaders can resolve to .tga, .jpg, or .png files.
    /// Returns pixel data in RGBA format, or null if not found.
    /// </summary>
    public static ImageResult? LoadFromEngineFS(string name)
    {
        // Strip any extension and try common image formats
        string baseName = name;
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0)
            baseName = name[..dotIdx];

        // Try loading with each extension the engine supports
        string[] extensions = [".tga", ".jpg", ".jpeg", ".png", ".bmp"];

        foreach (var ext in extensions)
        {
            string path = baseName + ext;
            int len = EngineImports.FS_ReadFile(path, out byte* buf);
            if (len > 0 && buf != null)
            {
                try
                {
                    using var stream = new UnmanagedMemoryStream(buf, len);
                    var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                    return result;
                }
                catch
                {
                    // Image decode failed, try next extension
                }
                finally
                {
                    EngineImports.FS_FreeFile(buf);
                }
            }
        }

        // Also try the exact name as-is (might already have extension)
        if (dotIdx >= 0)
        {
            int len = EngineImports.FS_ReadFile(name, out byte* buf);
            if (len > 0 && buf != null)
            {
                try
                {
                    using var stream = new UnmanagedMemoryStream(buf, len);
                    return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                }
                catch { }
                finally
                {
                    EngineImports.FS_FreeFile(buf);
                }
            }
        }

        return null;
    }
}
