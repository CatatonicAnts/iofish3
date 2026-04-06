using System.Runtime.InteropServices;
using System.Text;

namespace RendererDotNet.Interop;

/// <summary>
/// Wraps the raw function pointers from RefImport into callable C# methods.
/// The RefImport struct is COPIED (not stored by pointer) because the engine
/// passes a local variable that goes out of scope after GetRefAPI returns.
/// </summary>
public static unsafe class EngineImports
{
    private static RefImport _ri;
    private static bool _initialized;

    // Quake 3 print levels (from q_shared.h)
    public const int PRINT_ALL = 0;
    public const int PRINT_DEVELOPER = 1;
    public const int PRINT_WARNING = 2;
    public const int PRINT_ERROR = 3;

    public static void Init(RefImport* ri)
    {
        _ri = *ri; // Copy the struct — the pointer is to a local variable in the engine
        _initialized = true;
    }

    /// <summary>
    /// Print a message to the engine console.
    /// Uses the engine's Printf with a simple "%s" format to avoid
    /// variadic interop issues.
    /// </summary>
    public static void Printf(int level, string message)
    {
        if (!_initialized || _ri.Printf == 0)
            return;

        var fmt = "%s\0"u8;
        byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\0");

        fixed (byte* fmtPtr = fmt)
        fixed (byte* msgPtr = msgBytes)
        {
            ((delegate* unmanaged[Cdecl]<int, byte*, byte*, void>)_ri.Printf)(level, fmtPtr, msgPtr);
        }
    }

    public static int Milliseconds()
    {
        if (!_initialized || _ri.Milliseconds == 0)
            return 0;
        return ((delegate* unmanaged[Cdecl]<int>)_ri.Milliseconds)();
    }

    /// <summary>
    /// Initialize engine input system with the SDL window handle.
    /// </summary>
    public static void IN_Init(nint windowHandle)
    {
        if (!_initialized || _ri.IN_Init == 0)
            return;
        ((delegate* unmanaged[Cdecl]<nint, void>)_ri.IN_Init)(windowHandle);
    }

    /// <summary>
    /// Read a file from the engine's virtual filesystem (pk3 archives + loose files).
    /// Returns file length, or -1 if not found. Caller must free with FS_FreeFile.
    /// Note: C "long" is 4 bytes on Windows MSVC x64, matching C# "int".
    /// </summary>
    public static int FS_ReadFile(string path, out byte* buffer)
    {
        buffer = null;
        if (!_initialized || _ri.FS_ReadFile == 0)
            return -1;

        byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* pathPtr = pathBytes)
        {
            byte* buf = null;
            int len = ((delegate* unmanaged[Cdecl]<byte*, byte**, int>)_ri.FS_ReadFile)(pathPtr, &buf);
            buffer = buf;
            return len;
        }
    }

    /// <summary>
    /// Free a buffer allocated by FS_ReadFile.
    /// </summary>
    public static void FS_FreeFile(byte* buffer)
    {
        if (!_initialized || _ri.FS_FreeFile == 0 || buffer == null)
            return;
        ((delegate* unmanaged[Cdecl]<byte*, void>)_ri.FS_FreeFile)(buffer);
    }

    /// <summary>
    /// List files in a directory matching an extension.
    /// Returns a native array of C strings; caller must free with FS_FreeFileList.
    /// </summary>
    public static byte** FS_ListFiles(string directory, string extension, out int numFiles)
    {
        numFiles = 0;
        if (!_initialized || _ri.FS_ListFiles == 0)
            return null;

        byte[] dirBytes = Encoding.UTF8.GetBytes(directory + "\0");
        byte[] extBytes = Encoding.UTF8.GetBytes(extension + "\0");
        int count = 0;

        fixed (byte* dirPtr = dirBytes)
        fixed (byte* extPtr = extBytes)
        {
            byte** result = ((delegate* unmanaged[Cdecl]<byte*, byte*, int*, byte**>)_ri.FS_ListFiles)(
                dirPtr, extPtr, &count);
            numFiles = count;
            return result;
        }
    }

    /// <summary>
    /// Free a file list returned by FS_ListFiles.
    /// </summary>
    public static void FS_FreeFileList(byte** fileList)
    {
        if (!_initialized || _ri.FS_FreeFileList == 0 || fileList == null)
            return;
        ((delegate* unmanaged[Cdecl]<byte**, void>)_ri.FS_FreeFileList)(fileList);
    }
}
