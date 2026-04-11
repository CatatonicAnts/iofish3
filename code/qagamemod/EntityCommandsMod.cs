namespace QGameMod;

/// <summary>
/// Server mod providing entity manipulation console commands:
///   ent_list  — list all active entities with details
///   ent_create — spawn a new entity by classname
///   ent_fire  — trigger an entity's use function by targetname
///   ent_remove — remove an entity by index
///   ent_info  — show detailed info about a specific entity
/// </summary>
public class EntityCommandsMod : IQGameMod
{
    public string Name => "Entity Commands";

    public void Init()
    {
    }

    public void Shutdown()
    {
    }

    public void Frame(int levelTime)
    {
    }

    public bool ConsoleCommand(string cmd)
    {
        return cmd switch
        {
            "ent_list" => DoEntList(),
            "ent_create" => DoEntCreate(),
            "ent_fire" => DoEntFire(),
            "ent_remove" => DoEntRemove(),
            "ent_info" => DoEntInfo(),
            _ => false
        };
    }

    /// <summary>
    /// ent_list [filter]
    /// Lists all active entities. Optional classname filter.
    /// </summary>
    private bool DoEntList()
    {
        string filter = Syscalls.Argc() > 1 ? Syscalls.Argv(1) : "";
        int count = GameApi.GetEntityCount();
        int listed = 0;

        Syscalls.Print("--- Entity List ---\n");
        Syscalls.Print("  #   Type                 Class                Target               Origin               HP\n");

        for (int i = 0; i < count; i++)
        {
            var ent = GameApi.GetEntity(i);
            if (ent == null) continue;

            if (filter.Length > 0 &&
                !ent.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            string targetCol = string.IsNullOrEmpty(ent.TargetName) ? "-" : ent.TargetName;
            string origin = $"({ent.OriginX:F0}, {ent.OriginY:F0}, {ent.OriginZ:F0})";

            Syscalls.Print($"  {ent.Index,-4} {ent.EntityTypeName,-20} {ent.ClassName,-20} {targetCol,-20} {origin,-20} {ent.Health}\n");
            listed++;
        }

        Syscalls.Print($"--- {listed} entities ---\n");
        return true;
    }

    /// <summary>
    /// ent_create classname [x y z]
    /// Spawns a new entity. If no origin is given, spawns at player 0's position.
    /// </summary>
    private bool DoEntCreate()
    {
        int argc = Syscalls.Argc();
        if (argc < 2)
        {
            Syscalls.Print("Usage: ent_create <classname> [x y z]\n");
            return true;
        }

        string classname = Syscalls.Argv(1);
        float x = 0, y = 0, z = 0;

        if (argc >= 5)
        {
            float.TryParse(Syscalls.Argv(2), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out x);
            float.TryParse(Syscalls.Argv(3), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out y);
            float.TryParse(Syscalls.Argv(4), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out z);
        }
        else
        {
            // Try to use player 0's position
            var player = GameApi.GetEntity(0);
            if (player != null)
            {
                x = player.OriginX;
                y = player.OriginY;
                z = player.OriginZ;
            }
        }

        int entNum = GameApi.SpawnEntity(classname, x, y, z);
        if (entNum >= 0)
        {
            Syscalls.Print($"^2Created entity {entNum}: '{classname}' at ({x:F0}, {y:F0}, {z:F0})\n");
        }
        else
        {
            Syscalls.Print($"^1Failed to create entity '{classname}' (no spawn function or out of slots)\n");
        }
        return true;
    }

    /// <summary>
    /// ent_fire targetname [activator_entity_num]
    /// Fires (calls use function on) all entities matching the targetname.
    /// </summary>
    private bool DoEntFire()
    {
        int argc = Syscalls.Argc();
        if (argc < 2)
        {
            Syscalls.Print("Usage: ent_fire <targetname> [activator_num]\n");
            return true;
        }

        string targetname = Syscalls.Argv(1);
        int activator = 0;
        if (argc >= 3)
        {
            int.TryParse(Syscalls.Argv(2), out activator);
        }

        int count = GameApi.FireEntity(targetname, activator);
        if (count > 0)
        {
            Syscalls.Print($"^2Fired {count} entity(ies) with targetname '{targetname}'\n");
        }
        else
        {
            Syscalls.Print($"^3No entities found with targetname '{targetname}' (or no use function)\n");
        }
        return true;
    }

    /// <summary>
    /// ent_remove index
    /// Removes the entity at the given index.
    /// </summary>
    private bool DoEntRemove()
    {
        if (Syscalls.Argc() < 2)
        {
            Syscalls.Print("Usage: ent_remove <entity_number>\n");
            return true;
        }

        if (!int.TryParse(Syscalls.Argv(1), out int index))
        {
            Syscalls.Print("^1Invalid entity number\n");
            return true;
        }

        // Show what we're about to remove
        var ent = GameApi.GetEntity(index);
        if (ent == null)
        {
            Syscalls.Print($"^1Entity {index} not found or not in use\n");
            return true;
        }

        if (GameApi.RemoveEntity(index))
        {
            Syscalls.Print($"^2Removed entity {index}: '{ent.ClassName}'\n");
        }
        else
        {
            Syscalls.Print($"^1Cannot remove entity {index} (client entities cannot be removed)\n");
        }
        return true;
    }

    /// <summary>
    /// ent_info index
    /// Shows detailed information about a specific entity.
    /// </summary>
    private bool DoEntInfo()
    {
        if (Syscalls.Argc() < 2)
        {
            Syscalls.Print("Usage: ent_info <entity_number>\n");
            return true;
        }

        if (!int.TryParse(Syscalls.Argv(1), out int index))
        {
            Syscalls.Print("^1Invalid entity number\n");
            return true;
        }

        var ent = GameApi.GetEntity(index);
        if (ent == null)
        {
            Syscalls.Print($"^1Entity {index} not found or not in use\n");
            return true;
        }

        Syscalls.Print($"--- Entity {index} ---\n");
        Syscalls.Print($"  classname:  {ent.ClassName}\n");
        Syscalls.Print($"  targetname: {(string.IsNullOrEmpty(ent.TargetName) ? "(none)" : ent.TargetName)}\n");
        Syscalls.Print($"  type:       {ent.EntityTypeName} ({ent.EntityType})\n");
        Syscalls.Print($"  health:     {ent.Health}\n");
        Syscalls.Print($"  origin:     ({ent.OriginX:F1}, {ent.OriginY:F1}, {ent.OriginZ:F1})\n");
        return true;
    }
}
