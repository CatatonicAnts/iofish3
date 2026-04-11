using System.Runtime.InteropServices;

namespace CGameMod;

/// <summary>
/// Managed wrappers for the cgameModApi_t function pointers passed from C cgame.
/// Provides trace, view, and entity query functions to mods.
/// </summary>
public static unsafe class CGameApi
{
    // Delegate types matching the C function pointer signatures
    private delegate void DoTraceDelegate(float* results, float* start, float* end, int skipNum, int mask);
    private delegate void GetVec3Delegate(float* outVec);
    private delegate void SetHighlightEntityDelegate(int entityNum);
    private delegate int GetIntDelegate();
    private delegate void GetEntityOriginDelegate(int entityNum, float* outVec);
    private delegate int GetEntityIntDelegate(int entityNum);
    private delegate int GetSnapshotEntityNumDelegate(int index);
    private delegate int GetEntityModelNameDelegate(int entityNum, byte* buf, int bufSize);
    private delegate void GetEntityInfoDelegate(int entityNum, int* weapon, int* eFlags, int* frame, int* evnt);

    // Cached delegate instances (prevent GC of the thunks)
    private static DoTraceDelegate? _doTrace;
    private static GetVec3Delegate? _getViewOrigin;
    private static GetVec3Delegate? _getViewAngles;
    private static SetHighlightEntityDelegate? _setHighlightEntity;
    private static GetIntDelegate? _getPlayerWeapon;
    private static GetEntityOriginDelegate? _getEntityOrigin;
    private static GetEntityIntDelegate? _getEntityModelHandle;
    private static GetEntityIntDelegate? _getEntityType;
    private static GetIntDelegate? _getSnapshotEntityCount;
    private static GetSnapshotEntityNumDelegate? _getSnapshotEntityNum;
    private static GetEntityModelNameDelegate? _getEntityModelName;
    private static GetEntityInfoDelegate? _getEntityInfo;

    private static bool _initialized;

    /// <summary>
    /// Parse the cgameModApi_t struct from C and cache delegate wrappers.
    /// The struct is 12 function pointers (nint each).
    /// </summary>
    public static void Init(nint apiPtr)
    {
        if (apiPtr == 0) return;

        nint* ptrs = (nint*)apiPtr;
        _doTrace = Marshal.GetDelegateForFunctionPointer<DoTraceDelegate>(ptrs[0]);
        _getViewOrigin = Marshal.GetDelegateForFunctionPointer<GetVec3Delegate>(ptrs[1]);
        _getViewAngles = Marshal.GetDelegateForFunctionPointer<GetVec3Delegate>(ptrs[2]);
        _setHighlightEntity = Marshal.GetDelegateForFunctionPointer<SetHighlightEntityDelegate>(ptrs[3]);
        _getPlayerWeapon = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(ptrs[4]);
        _getEntityOrigin = Marshal.GetDelegateForFunctionPointer<GetEntityOriginDelegate>(ptrs[5]);
        _getEntityModelHandle = Marshal.GetDelegateForFunctionPointer<GetEntityIntDelegate>(ptrs[6]);
        _getEntityType = Marshal.GetDelegateForFunctionPointer<GetEntityIntDelegate>(ptrs[7]);
        _getSnapshotEntityCount = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(ptrs[8]);
        _getSnapshotEntityNum = Marshal.GetDelegateForFunctionPointer<GetSnapshotEntityNumDelegate>(ptrs[9]);
        _getEntityModelName = Marshal.GetDelegateForFunctionPointer<GetEntityModelNameDelegate>(ptrs[10]);
        _getEntityInfo = Marshal.GetDelegateForFunctionPointer<GetEntityInfoDelegate>(ptrs[11]);

        _initialized = true;
    }

    public static bool IsAvailable => _initialized;

    /// <summary>
    /// Perform a world trace from start to end. Returns fraction, endpos, and hit entity.
    /// </summary>
    public static (float fraction, float endX, float endY, float endZ, int entityNum) DoTrace(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        int skipEntityNum, int contentMask)
    {
        if (!_initialized) return (1f, endX, endY, endZ, -1);

        float* start = stackalloc float[3] { startX, startY, startZ };
        float* end = stackalloc float[3] { endX, endY, endZ };
        float* results = stackalloc float[5]; // fraction, endpos[3], entityNum(bitcast)

        _doTrace!(results, start, end, skipEntityNum, contentMask);

        int hitEnt = *(int*)&results[4];
        return (results[0], results[1], results[2], results[3], hitEnt);
    }

    /// <summary>Get the current first-person view origin.</summary>
    public static (float x, float y, float z) GetViewOrigin()
    {
        if (!_initialized) return (0, 0, 0);
        float* v = stackalloc float[3];
        _getViewOrigin!(v);
        return (v[0], v[1], v[2]);
    }

    /// <summary>Get the current first-person view angles (pitch, yaw, roll).</summary>
    public static (float pitch, float yaw, float roll) GetViewAngles()
    {
        if (!_initialized) return (0, 0, 0);
        float* v = stackalloc float[3];
        _getViewAngles!(v);
        return (v[0], v[1], v[2]);
    }

    /// <summary>Set which entity should be highlighted with wireframe. -1 to clear.</summary>
    public static void SetHighlightEntity(int entityNum)
    {
        if (_initialized) _setHighlightEntity!(entityNum);
    }

    /// <summary>Get the weapon the player currently has selected.</summary>
    public static int GetPlayerWeapon()
    {
        return _initialized ? _getPlayerWeapon!() : 0;
    }

    /// <summary>Get the interpolated origin of a snapshot entity.</summary>
    public static (float x, float y, float z) GetEntityOrigin(int entityNum)
    {
        if (!_initialized) return (0, 0, 0);
        float* v = stackalloc float[3];
        _getEntityOrigin!(entityNum, v);
        return (v[0], v[1], v[2]);
    }

    /// <summary>Get the model handle of a snapshot entity (0 if none).</summary>
    public static int GetEntityModelHandle(int entityNum) =>
        _initialized ? _getEntityModelHandle!(entityNum) : 0;

    /// <summary>Get the entity type (entityType_t) of a snapshot entity.</summary>
    public static int GetEntityType(int entityNum) =>
        _initialized ? _getEntityType!(entityNum) : 0;

    /// <summary>Get how many entities are in the current snapshot.</summary>
    public static int GetSnapshotEntityCount() =>
        _initialized ? _getSnapshotEntityCount!() : 0;

    /// <summary>Get the entity number at a given index in the snapshot.</summary>
    public static int GetSnapshotEntityNum(int index) =>
        _initialized ? _getSnapshotEntityNum!(index) : -1;

    /// <summary>Get the model name string for an entity (from configstrings).</summary>
    public static string GetEntityModelName(int entityNum)
    {
        if (!_initialized) return "";
        byte* buf = stackalloc byte[256];
        int len = _getEntityModelName!(entityNum, buf, 256);
        if (len <= 0) return "";
        return System.Text.Encoding.ASCII.GetString(buf, len);
    }

    /// <summary>Get detailed entity info: weapon, eFlags, frame, event.</summary>
    public static (int weapon, int eFlags, int frame, int eventNum) GetEntityInfo(int entityNum)
    {
        if (!_initialized) return (0, 0, 0, 0);
        int weapon, eFlags, frame, evnt;
        _getEntityInfo!(entityNum, &weapon, &eFlags, &frame, &evnt);
        return (weapon, eFlags, frame, evnt);
    }
}
