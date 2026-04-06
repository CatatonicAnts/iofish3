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
}
