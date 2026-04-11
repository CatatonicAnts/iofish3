using System.Runtime.InteropServices;

namespace QGameMod;

/// <summary>
/// Wraps the Game API callbacks passed from the C game module.
/// Provides entity query and manipulation functions.
/// </summary>
public static unsafe class GameApi
{
    // Function pointer types matching the C gameModApi_t struct
    private static delegate* unmanaged[Cdecl]<int> _getEntityCount;
    private static delegate* unmanaged[Cdecl]<int, byte*, int, int> _getEntityInfo;
    private static delegate* unmanaged[Cdecl]<byte*, float, float, float, int> _spawnEntity;
    private static delegate* unmanaged[Cdecl]<byte*, int, int> _fireEntity;
    private static delegate* unmanaged[Cdecl]<int, int> _removeEntity;

    /// <summary>
    /// Initialize from the gameModApi_t struct pointer passed during QgMod_Init.
    /// The struct contains 5 function pointers (8 bytes each on x64).
    /// </summary>
    internal static void Init(nint gameApiPtr)
    {
        nint* api = (nint*)gameApiPtr;
        _getEntityCount = (delegate* unmanaged[Cdecl]<int>)api[0];
        _getEntityInfo = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)api[1];
        _spawnEntity = (delegate* unmanaged[Cdecl]<byte*, float, float, float, int>)api[2];
        _fireEntity = (delegate* unmanaged[Cdecl]<byte*, int, int>)api[3];
        _removeEntity = (delegate* unmanaged[Cdecl]<int, int>)api[4];
    }

    /// <summary>Returns the current number of entity slots in use (includes inactive).</summary>
    public static int GetEntityCount()
    {
        return _getEntityCount();
    }

    /// <summary>
    /// Get info for entity at the given index.
    /// Returns a tab-separated string: classname, targetname, eType, health, x, y, z, inuse
    /// Returns null if index is out of range.
    /// </summary>
    public static string? GetEntityInfo(int index)
    {
        byte* buf = stackalloc byte[1024];
        int result = _getEntityInfo(index, buf, 1024);
        if (result == 0) return null;
        return Marshal.PtrToStringUTF8((nint)buf);
    }

    /// <summary>
    /// Get structured entity info for the given index.
    /// Returns null if entity is out of range or not in use.
    /// </summary>
    public static EntityInfo? GetEntity(int index)
    {
        string? raw = GetEntityInfo(index);
        if (raw == null) return null;

        string[] parts = raw.Split('\t');
        if (parts.Length < 8) return null;

        bool inuse = parts[7] == "1";
        if (!inuse) return null;

        return new EntityInfo
        {
            Index = index,
            ClassName = parts[0],
            TargetName = parts[1],
            EntityType = int.TryParse(parts[2], out int et) ? et : 0,
            Health = int.TryParse(parts[3], out int hp) ? hp : 0,
            OriginX = float.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float x) ? x : 0,
            OriginY = float.TryParse(parts[5], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float y) ? y : 0,
            OriginZ = float.TryParse(parts[6], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float z) ? z : 0,
        };
    }

    /// <summary>
    /// Spawn a new entity with the given classname at the specified origin.
    /// Returns the entity number on success, -1 on failure.
    /// </summary>
    public static int SpawnEntity(string classname, float x, float y, float z)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(classname + '\0'))
            return _spawnEntity(p, x, y, z);
    }

    /// <summary>
    /// Fire (call use function on) all entities matching the given targetname.
    /// Returns the number of entities fired.
    /// </summary>
    public static int FireEntity(string targetname, int activatorNum = 0)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(targetname + '\0'))
            return _fireEntity(p, activatorNum);
    }

    /// <summary>
    /// Remove the entity at the given index.
    /// Returns true if the entity was removed successfully.
    /// </summary>
    public static bool RemoveEntity(int index)
    {
        return _removeEntity(index) != 0;
    }
}

/// <summary>Structured entity information.</summary>
public class EntityInfo
{
    public int Index { get; init; }
    public string ClassName { get; init; } = "";
    public string TargetName { get; init; } = "";
    public int EntityType { get; init; }
    public int Health { get; init; }
    public float OriginX { get; init; }
    public float OriginY { get; init; }
    public float OriginZ { get; init; }

    /// <summary>Human-readable entity type name.</summary>
    public string EntityTypeName => EntityType switch
    {
        0 => "ET_GENERAL",
        1 => "ET_PLAYER",
        2 => "ET_ITEM",
        3 => "ET_MISSILE",
        4 => "ET_MOVER",
        5 => "ET_BEAM",
        6 => "ET_PORTAL",
        7 => "ET_SPEAKER",
        8 => "ET_PUSH_TRIGGER",
        9 => "ET_TELEPORT_TRIGGER",
        10 => "ET_INVISIBLE",
        11 => "ET_GRAPPLE",
        13 => "ET_TEAM",
        _ => $"ET_{EntityType}"
    };
}
