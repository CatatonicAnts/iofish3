using RendererDotNet.Interop;
using StbImageSharp;

namespace RendererDotNet;

/// <summary>
/// Loads images from the engine's virtual filesystem (pk3 archives).
/// Supports TGA, JPEG, PNG, BMP via StbImageSharp.
/// </summary>
public static unsafe class ImageLoader
{
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
