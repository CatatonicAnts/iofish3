using System;
using System.Globalization;

namespace CGameMod;

/// <summary>
/// Multi-mode tool weapon mod. Right-click cycles between modes:
///   - Entity Picker: trace + proximity pick, AABB highlight, property panel
///   - Entity Spawner: menu of entity classnames, left-click to spawn at crosshair
/// </summary>
public class EntityPickerMod : ICGameMod
{
    public string Name => "Tool Weapon";

    private const int WP_TOOL = 11;
    private const float TRACE_RANGE = 8192f;
    private const int TRACE_INTERVAL_MS = 100;

    private int _charsetShader;
    private int _whiteShader;

    // ── Tool mode ──
    private enum ToolMode { Picker, Spawner }
    private static readonly string[] ModeNames = { "Entity Picker", "Entity Spawner" };
    private ToolMode _mode = ToolMode.Picker;

    // ── Picker state ──
    private int _lastHitEntity = -1;
    private int _lastTraceTime;
    private int _serverHitEntity = -1;
    private EntityInfo _info;

    // ── Spawner state ──
    private int _spawnSelection;

    // ── Bind management ──
    private bool _spawnerBindsActive;

    // ── Spawn entity catalog ──
    private static readonly (string category, string classname, string label)[] SpawnEntities =
    {
        // Weapons
        ("Weapons",   "weapon_rocketlauncher",  "Rocket Launcher"),
        ("Weapons",   "weapon_railgun",         "Railgun"),
        ("Weapons",   "weapon_lightning",        "Lightning Gun"),
        ("Weapons",   "weapon_plasmagun",        "Plasma Gun"),
        ("Weapons",   "weapon_shotgun",          "Shotgun"),
        ("Weapons",   "weapon_grenadelauncher",  "Grenade Launcher"),
        ("Weapons",   "weapon_bfg",              "BFG"),
        // Health
        ("Health",    "item_health_small",       "Small Health (+5)"),
        ("Health",    "item_health",             "Health (+25)"),
        ("Health",    "item_health_large",       "Large Health (+50)"),
        ("Health",    "item_health_mega",        "Mega Health (+100)"),
        // Armor
        ("Armor",     "item_armor_shard",        "Armor Shard (+5)"),
        ("Armor",     "item_armor_combat",       "Yellow Armor (+50)"),
        ("Armor",     "item_armor_body",         "Red Armor (+100)"),
        // Ammo
        ("Ammo",      "ammo_rockets",            "Rockets"),
        ("Ammo",      "ammo_slugs",              "Slugs"),
        ("Ammo",      "ammo_lightning",           "Lightning"),
        ("Ammo",      "ammo_cells",              "Cells"),
        ("Ammo",      "ammo_shells",             "Shells"),
        ("Ammo",      "ammo_grenades",           "Grenades"),
        ("Ammo",      "ammo_bfg",               "BFG Ammo"),
        ("Ammo",      "ammo_bullets",            "Bullets"),
        // Powerups
        ("Powerups",  "item_quad",               "Quad Damage"),
        ("Powerups",  "item_enviro",             "Battle Suit"),
        ("Powerups",  "item_haste",              "Haste"),
        ("Powerups",  "item_invis",              "Invisibility"),
        ("Powerups",  "item_regen",              "Regeneration"),
        ("Powerups",  "item_flight",             "Flight"),
        // Holdable
        ("Holdable",  "holdable_teleporter",     "Personal Teleporter"),
        ("Holdable",  "holdable_medkit",         "Medkit"),
    };

    private struct EntityInfo
    {
        public string Classname, Targetname, Target, Model, Message, Team;
        public int Spawnflags, Health, Count, Damage, SplashDamage, SplashRadius;
        public float Speed;
        public float OriginX, OriginY, OriginZ;
        public float AbsMinX, AbsMinY, AbsMinZ, AbsMaxX, AbsMaxY, AbsMaxZ;
        public float MoveDirX, MoveDirY, MoveDirZ;
        public int Contents, Clipmask, SvFlags;
        public float Wait, Random;
        public int NextThink;
        public float AngleX, AngleY, AngleZ;
        public float Origin2X, Origin2Y, Origin2Z;
        public int MoverState, Flags, TakeDamage;
        public float PhysicsBounce;
        public int EntityType, Gravity;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public void Init()
    {
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        Syscalls.AddCommand("tool");
        Syscalls.AddCommand("toolcycle");
        Syscalls.AddCommand("toolup");
        Syscalls.AddCommand("tooldown");
        Syscalls.AddCommand("toolfire");
    }

    public void Shutdown()
    {
        RestoreDefaultBinds();
        ClearHighlights();
        Syscalls.RemoveCommand("tool");
        Syscalls.RemoveCommand("toolcycle");
        Syscalls.RemoveCommand("toolup");
        Syscalls.RemoveCommand("tooldown");
        Syscalls.RemoveCommand("toolfire");
    }

    private bool _pendingWeaponSwitch;

    public bool ConsoleCommand(string cmd)
    {
        switch (cmd)
        {
            case "tool":
                Syscalls.SendConsoleCommand("give Tool\n");
                _pendingWeaponSwitch = true;
                _mode = ToolMode.Picker;
                ApplyBindsForMode();
                Syscalls.Print("[MOD] ^5Tool weapon equipped. mouse2=cycle, mwheelup/down=scroll\n");
                return true;
            case "toolcycle":
                CycleMode();
                return true;
            case "toolup":
                if (_mode == ToolMode.Spawner)
                    _spawnSelection = Math.Max(0, _spawnSelection - 1);
                return true;
            case "tooldown":
                if (_mode == ToolMode.Spawner)
                    _spawnSelection = Math.Min(SpawnEntities.Length - 1, _spawnSelection + 1);
                return true;
            case "toolfire":
                if (_mode == ToolMode.Spawner)
                    DoSpawn();
                return true;
        }
        return false;
    }

    public void EntityEvent(int entityNum, int eventType, int eventParm) { }

    // ═══════════════════════════════════════════════════════════════
    //  Frame
    // ═══════════════════════════════════════════════════════════════

    public void Frame(int serverTime)
    {
        if (!CGameApi.IsAvailable) return;

        if (_pendingWeaponSwitch)
        {
            Syscalls.SendConsoleCommand($"weapon {WP_TOOL}\n");
            _pendingWeaponSwitch = false;
        }

        int weapon = CGameApi.GetPlayerWeapon();
        if (weapon != WP_TOOL)
        {
            if (_lastHitEntity >= 0)
            {
                ClearHighlights();
                _lastHitEntity = -1;
            }
            RestoreDefaultBinds();
            return;
        }

        if (_mode == ToolMode.Picker)
            FramePicker(serverTime);
    }

    private void FramePicker(int serverTime)
    {
        int picked = _serverHitEntity;
        if (picked >= 0)
        {
            if (picked != _lastHitEntity)
            {
                CGameApi.SetHighlightEntity(picked);
                _lastHitEntity = picked;
            }
        }
        else if (_lastHitEntity >= 0)
        {
            ClearHighlights();
            _lastHitEntity = -1;
        }

        if (serverTime - _lastTraceTime >= TRACE_INTERVAL_MS)
        {
            _lastTraceTime = serverTime;
            SendViewTrace("toolTrace");
        }
    }

    private void DoSpawn()
    {
        var entry = SpawnEntities[_spawnSelection];
        var (ox, oy, oz) = CGameApi.GetViewOrigin();
        var (endX, endY, endZ) = GetViewEnd();
        Syscalls.SendClientCommand(
            $"toolSpawn {entry.classname} {ox:F1} {oy:F1} {oz:F1} {endX:F1} {endY:F1} {endZ:F1}");
    }

    private void ApplyBindsForMode()
    {
        Syscalls.SendConsoleCommand("bind mouse2 toolcycle\n");
        if (_mode == ToolMode.Spawner)
        {
            Syscalls.SendConsoleCommand("bind mouse1 toolfire\n");
            Syscalls.SendConsoleCommand("bind mwheelup toolup\n");
            Syscalls.SendConsoleCommand("bind mwheeldown tooldown\n");
            _spawnerBindsActive = true;
        }
        else
        {
            RestoreDefaultBinds();
        }
    }

    private void RestoreDefaultBinds()
    {
        if (!_spawnerBindsActive) return;
        Syscalls.SendConsoleCommand("bind mouse1 +attack\n");
        Syscalls.SendConsoleCommand("bind mwheelup weapprev\n");
        Syscalls.SendConsoleCommand("bind mwheeldown weapnext\n");
        _spawnerBindsActive = false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Draw2D
    // ═══════════════════════════════════════════════════════════════

    public unsafe void Draw2D(int screenWidth, int screenHeight)
    {
        if (!CGameApi.IsAvailable) return;
        int weapon = CGameApi.GetPlayerWeapon();
        if (weapon != WP_TOOL) return;

        // Header bar
        string toolText = $"Active tool: {ModeNames[(int)_mode]}  [mouse2: cycle]";
        int charSize = 20;
        int textWidth = toolText.Length * charSize;
        int textX = (screenWidth - textWidth) / 2;
        int textY = 32;

        Syscalls.R_SetColor(0f, 0f, 0f, 0.6f);
        Syscalls.R_DrawStretchPic(textX - 6, textY - 4, textWidth + 12, charSize + 8,
            0, 0, 1, 1, _whiteShader);
        Syscalls.R_SetColor(0f, 1f, 1f, 1f);
        DrawString(textX, textY, toolText, charSize);

        if (_mode == ToolMode.Picker)
            DrawPickerPanel(screenWidth, screenHeight);
        else
            DrawSpawnMenu(screenWidth, screenHeight);

        float* reset = stackalloc float[4] { 1, 1, 1, 1 };
        Syscalls.R_SetColor(reset);
    }

    private void DrawPickerPanel(int screenWidth, int screenHeight)
    {
        if (_lastHitEntity < 0) return;

        ref var info = ref _info;
        int entType = CGameApi.GetEntityType(_lastHitEntity);
        string clientModelName = CGameApi.GetEntityModelName(_lastHitEntity);

        var lines = new System.Collections.Generic.List<(string text, uint color)>();

        string typeName = GetEntityTypeName(entType);
        lines.Add(($"Entity #{_lastHitEntity}  [{typeName}]", 0xFFFF4D));

        if (!string.IsNullOrEmpty(info.Classname))
            lines.Add(($"Class: {info.Classname}", 0x66FFFF));
        if (!string.IsNullOrEmpty(info.Targetname))
            lines.Add(($"Targetname: {info.Targetname}", 0x66FF66));
        if (!string.IsNullOrEmpty(info.Target))
            lines.Add(($"Target: {info.Target}", 0x66FF66));
        if (!string.IsNullOrEmpty(info.Team))
            lines.Add(($"Team: {info.Team}", 0x66FF66));
        if (!string.IsNullOrEmpty(info.Message))
            lines.Add(($"Message: {info.Message}", 0xCCCCCC));
        if (!string.IsNullOrEmpty(info.Model))
            lines.Add(($"Brush model: {info.Model}", 0xCCCCCC));
        if (!string.IsNullOrEmpty(clientModelName))
            lines.Add(($"Model: {GetShortName(clientModelName)}", 0xCCCCCC));

        lines.Add(($"Origin: {info.OriginX:F0} {info.OriginY:F0} {info.OriginZ:F0}", 0xE6E6E6));

        if (info.MoveDirX != 0 || info.MoveDirY != 0 || info.MoveDirZ != 0)
            lines.Add(($"MoveDir: {info.MoveDirX:F0} {info.MoveDirY:F0} {info.MoveDirZ:F0}", 0xFFCC66));
        if (info.Speed != 0) lines.Add(($"Speed: {info.Speed:F0}", 0xFFCC66));
        if (info.Health != 0) lines.Add(($"Health: {info.Health}", 0xFF6666));
        if (info.Damage != 0) lines.Add(($"Damage: {info.Damage}", 0xFF6666));
        if (info.SplashDamage != 0)
            lines.Add(($"Splash: {info.SplashDamage} r={info.SplashRadius}", 0xFF6666));
        if (info.Count != 0) lines.Add(($"Count: {info.Count}", 0xCCCCCC));
        if (info.Spawnflags != 0) lines.Add(($"Spawnflags: 0x{info.Spawnflags:X}", 0x999999));
        if (info.Flags != 0) lines.Add(($"Flags: 0x{info.Flags:X}", 0x999999));
        if (info.Contents != 0) lines.Add(($"Contents: 0x{info.Contents:X}", 0x999999));
        if (info.Clipmask != 0) lines.Add(($"Clipmask: 0x{info.Clipmask:X}", 0x999999));
        if (info.SvFlags != 0) lines.Add(($"SvFlags: 0x{info.SvFlags:X}", 0x999999));
        if (info.TakeDamage != 0)
            lines.Add(($"TakeDamage: {(info.TakeDamage == 1 ? "yes" : "no_knockback")}", 0xFF9999));
        if (info.MoverState != 0)
            lines.Add(($"MoverState: {GetMoverStateName(info.MoverState)}", 0xFFCC66));
        if (info.Wait != 0) lines.Add(($"Wait: {info.Wait:F1}s", 0xCCCCCC));
        if (info.Random != 0) lines.Add(($"Random: {info.Random:F1}s", 0xCCCCCC));
        if (info.NextThink >= 0) lines.Add(($"NextThink: {info.NextThink}ms", 0xCCCCCC));
        if (info.PhysicsBounce != 0) lines.Add(($"Bounce: {info.PhysicsBounce:F2}", 0xCCCCCC));
        if (info.AngleX != 0 || info.AngleY != 0 || info.AngleZ != 0)
            lines.Add(($"Angles: {info.AngleX:F1} {info.AngleY:F1} {info.AngleZ:F1}", 0xE6E6E6));
        if (info.Origin2X != 0 || info.Origin2Y != 0 || info.Origin2Z != 0)
            lines.Add(($"PushVel: {info.Origin2X:F0} {info.Origin2Y:F0} {info.Origin2Z:F0}", 0xFF66FF));

        DrawPanel(lines, screenWidth, screenHeight);
    }

    private void DrawSpawnMenu(int screenWidth, int screenHeight)
    {
        const int CHAR_SIZE = 14;
        const int LINE_H = CHAR_SIZE + 4;
        const int VISIBLE = 20;

        // Compute scroll window around selection
        int total = SpawnEntities.Length;
        int halfVis = VISIBLE / 2;
        int scrollTop = Math.Clamp(_spawnSelection - halfVis, 0, Math.Max(0, total - VISIBLE));
        int scrollBot = Math.Min(scrollTop + VISIBLE, total);

        // Count category headers in visible range
        var visLines = new System.Collections.Generic.List<(string text, uint color, bool isHeader)>();
        visLines.Add(("[fire to spawn at crosshair]", 0x888888, false));
        string lastCat = "";
        for (int i = scrollTop; i < scrollBot; i++)
        {
            var (cat, cls, label) = SpawnEntities[i];
            if (cat != lastCat)
            {
                visLines.Add(($"-- {cat} --", 0xFFCC00, true));
                lastCat = cat;
            }
            string prefix = i == _spawnSelection ? "> " : "  ";
            uint color = i == _spawnSelection ? 0xFFFFFFu : 0xAAAAAAu;
            visLines.Add(($"{prefix}{label}  ({cls})", color, false));
        }
        if (scrollTop > 0)
            visLines.Insert(1, ("  ...", 0x666666, false));
        if (scrollBot < total)
            visLines.Add(("  ...", 0x666666, false));

        // Measure panel width
        int maxW = 0;
        foreach (var (text, _, _) in visLines)
        {
            int w = text.Length * CHAR_SIZE;
            if (w > maxW) maxW = w;
        }

        int panelH = visLines.Count * LINE_H + 8;
        int panelX = (screenWidth - maxW) / 2 - 8;
        int panelY = screenHeight / 2 - panelH / 2;

        Syscalls.R_SetColor(0f, 0f, 0.1f, 0.8f);
        Syscalls.R_DrawStretchPic(panelX, panelY, maxW + 16, panelH,
            0, 0, 1, 1, _whiteShader);

        int lineY = panelY + 4;
        foreach (var (text, color, _) in visLines)
        {
            float r = ((color >> 16) & 0xFF) / 255f;
            float g = ((color >> 8) & 0xFF) / 255f;
            float b = (color & 0xFF) / 255f;
            Syscalls.R_SetColor(r, g, b, 1f);
            DrawString(panelX + 8, lineY, text, CHAR_SIZE);
            lineY += LINE_H;
        }
    }

    private void DrawPanel(System.Collections.Generic.List<(string text, uint color)> lines,
        int screenWidth, int screenHeight)
    {
        int infoCharSize = 14;
        int lineHeight = infoCharSize + 3;
        int maxWidth = 0;
        foreach (var (text, _) in lines)
            if (text.Length * infoCharSize > maxWidth)
                maxWidth = text.Length * infoCharSize;

        int panelHeight = lines.Count * lineHeight + 8;
        int panelX = (screenWidth - maxWidth) / 2 - 8;
        int panelY = screenHeight / 2 + 32;

        Syscalls.R_SetColor(0f, 0f, 0f, 0.7f);
        Syscalls.R_DrawStretchPic(panelX, panelY, maxWidth + 16, panelHeight,
            0, 0, 1, 1, _whiteShader);

        int lineY = panelY + 4;
        foreach (var (text, color) in lines)
        {
            float r = ((color >> 16) & 0xFF) / 255f;
            float g = ((color >> 8) & 0xFF) / 255f;
            float b = (color & 0xFF) / 255f;
            Syscalls.R_SetColor(r, g, b, 1f);
            DrawString(panelX + 8, lineY, text, infoCharSize);
            lineY += lineHeight;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Server command (toolHit response)
    // ═══════════════════════════════════════════════════════════════

    public unsafe void ServerCommand(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            _serverHitEntity = -1;
            CGameApi.ClearHighlightAABB();
            CGameApi.ClearHighlightTrajectory();
            return;
        }

        var p = args.Split(' ');
        if (p.Length < 1 || !int.TryParse(p[0], out int entNum) || entNum < 0)
        {
            _serverHitEntity = -1;
            CGameApi.ClearHighlightAABB();
            CGameApi.ClearHighlightTrajectory();
            return;
        }

        _serverHitEntity = entNum;
        _info = new EntityInfo
        {
            Classname = Str(p, 1), Targetname = Str(p, 2), Target = Str(p, 3),
            Model = Str(p, 4), Spawnflags = Int(p, 5), Health = Int(p, 6),
            Speed = Float(p, 7), Count = Int(p, 8), Damage = Int(p, 9),
            SplashDamage = Int(p, 10), SplashRadius = Int(p, 11),
            OriginX = Float(p, 12), OriginY = Float(p, 13), OriginZ = Float(p, 14),
            AbsMinX = Float(p, 15), AbsMinY = Float(p, 16), AbsMinZ = Float(p, 17),
            AbsMaxX = Float(p, 18), AbsMaxY = Float(p, 19), AbsMaxZ = Float(p, 20),
            MoveDirX = Float(p, 21), MoveDirY = Float(p, 22), MoveDirZ = Float(p, 23),
            Contents = Int(p, 24), Clipmask = Int(p, 25), SvFlags = Int(p, 26),
            Message = Str(p, 27), Team = Str(p, 28),
            Wait = Float(p, 29), Random = Float(p, 30), NextThink = Int(p, 31),
            AngleX = Float(p, 32), AngleY = Float(p, 33), AngleZ = Float(p, 34),
            Origin2X = Float(p, 35), Origin2Y = Float(p, 36), Origin2Z = Float(p, 37),
            MoverState = Int(p, 38), Flags = Int(p, 39), TakeDamage = Int(p, 40),
            PhysicsBounce = Float(p, 41), EntityType = Int(p, 42), Gravity = Int(p, 43),
        };

        float* mins = stackalloc float[3] { _info.AbsMinX, _info.AbsMinY, _info.AbsMinZ };
        float* maxs = stackalloc float[3] { _info.AbsMaxX, _info.AbsMaxY, _info.AbsMaxZ };
        CGameApi.SetHighlightAABB(mins, maxs);

        const int ET_PUSH_TRIGGER = 8;
        if (_info.EntityType == ET_PUSH_TRIGGER &&
            (_info.Origin2X != 0 || _info.Origin2Y != 0 || _info.Origin2Z != 0))
            ComputeAndSetTrajectory();
        else
            CGameApi.ClearHighlightTrajectory();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void CycleMode()
    {
        _mode = _mode == ToolMode.Picker ? ToolMode.Spawner : ToolMode.Picker;
        ClearHighlights();
        _lastHitEntity = -1;
        _serverHitEntity = -1;
        ApplyBindsForMode();
        Syscalls.Print($"[MOD] ^5Tool mode: {ModeNames[(int)_mode]}\n");
    }

    private void ClearHighlights()
    {
        CGameApi.SetHighlightEntity(-1);
        CGameApi.ClearHighlightAABB();
        CGameApi.ClearHighlightTrajectory();
    }

    private void SendViewTrace(string cmd)
    {
        var (ox, oy, oz) = CGameApi.GetViewOrigin();
        var (endX, endY, endZ) = GetViewEnd();
        Syscalls.SendClientCommand($"{cmd} {ox:F1} {oy:F1} {oz:F1} {endX:F1} {endY:F1} {endZ:F1}");
    }

    private (float x, float y, float z) GetViewEnd()
    {
        var (ox, oy, oz) = CGameApi.GetViewOrigin();
        var (pitch, yaw, _) = CGameApi.GetViewAngles();
        float pr = pitch * MathF.PI / 180f, yr = yaw * MathF.PI / 180f;
        float cp = MathF.Cos(pr), sp = MathF.Sin(pr);
        float cy = MathF.Cos(yr), sy = MathF.Sin(yr);
        return (ox + cp * cy * TRACE_RANGE, oy + cp * sy * TRACE_RANGE, oz - sp * TRACE_RANGE);
    }

    private unsafe void ComputeAndSetTrajectory()
    {
        float gravity = _info.Gravity > 0 ? _info.Gravity : 800f;
        const int MAX_POINTS = 64;
        const float TIME_STEP = 0.02f;

        float startX = (_info.AbsMinX + _info.AbsMaxX) * 0.5f;
        float startY = (_info.AbsMinY + _info.AbsMaxY) * 0.5f;
        float startZ = _info.AbsMaxZ;
        float vx = _info.Origin2X, vy = _info.Origin2Y, vz = _info.Origin2Z;

        float* points = stackalloc float[MAX_POINTS * 3];
        int numPoints = 0;
        float x = startX, y = startY, z = startZ;

        for (int i = 0; i < MAX_POINTS; i++)
        {
            points[numPoints * 3] = x;
            points[numPoints * 3 + 1] = y;
            points[numPoints * 3 + 2] = z;
            numPoints++;

            float t = (i + 1) * TIME_STEP;
            float newZ = startZ + vz * t - 0.5f * gravity * t * t;
            if (i > 5 && newZ < startZ) break;
            x = startX + vx * t;
            y = startY + vy * t;
            z = newZ;
        }

        if (numPoints < MAX_POINTS)
        {
            points[numPoints * 3] = x;
            points[numPoints * 3 + 1] = y;
            points[numPoints * 3 + 2] = z;
            numPoints++;
        }

        CGameApi.SetHighlightTrajectory(points, numPoints);
    }

    private static string Str(string[] p, int i) =>
        i < p.Length && p[i] != "-" ? p[i] : "";
    private static int Int(string[] p, int i) =>
        i < p.Length && int.TryParse(p[i], out int v) ? v : 0;
    private static float Float(string[] p, int i) =>
        i < p.Length && float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    private void DrawString(int x, int y, string text, int charSize)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int ch = text[i];
            if (ch == ' ') { x += charSize; continue; }
            float row = (ch >> 4) / 16.0f;
            float col = (ch & 15) / 16.0f;
            float size = 1.0f / 16.0f;
            Syscalls.R_DrawStretchPic(x, y, charSize, charSize,
                col, row, col + size, row + size, _charsetShader);
            x += charSize;
        }
    }

    private static string GetEntityTypeName(int et) => et switch
    {
        0 => "General", 1 => "Player", 2 => "Item", 3 => "Missile",
        4 => "Mover", 5 => "Beam", 6 => "Portal", 7 => "Speaker",
        8 => "PushTrigger", 9 => "TeleportTrigger", 10 => "Invisible",
        11 => "Grapple", 12 => "Team",
        _ when et >= 13 => $"Event({et - 13})", _ => $"Unknown({et})"
    };

    private static string GetMoverStateName(int s) => s switch
    {
        0 => "POS1", 1 => "POS2", 2 => "1TO2", 3 => "2TO1", _ => $"Unknown({s})"
    };

    private static string GetShortName(string path)
    {
        int i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }
}
