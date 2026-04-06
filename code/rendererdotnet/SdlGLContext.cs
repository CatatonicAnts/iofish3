using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;

namespace RendererDotNet;

/// <summary>
/// Bridges Silk.NET's GL loader to SDL2's GL proc address resolution.
/// </summary>
internal sealed unsafe class SdlGLContext(Sdl sdl) : INativeContext
{
    public nint GetProcAddress(string proc, int? slot = null)
    {
        var ptr = Marshal.StringToHGlobalAnsi(proc);
        try
        {
            return (nint)sdl.GLGetProcAddress((byte*)ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        addr = GetProcAddress(proc, slot);
        return addr != 0;
    }

    public void Dispose() { }
}
