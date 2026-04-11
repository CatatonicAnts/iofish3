using System;
using System.Globalization;

namespace CGameMod;

/// <summary>
/// Entity picker tool mod. When the player has WP_TOOL equipped,
/// sends trace requests to the server which does full trace + proximity picking.
/// Highlights the entity under aim with an AABB wireframe and shows a property panel.
/// </summary>
public class EntityPickerMod : ICGameMod
{
    public string Name => "Entity Picker";

    private const int WP_TOOL = 11;
    private const float TRACE_RANGE = 8192f;
    private const int TRACE_INTERVAL_MS = 100;

    private int _charsetShader;
    private int _whiteShader;
    private int _lastHitEntity = -1;
    private int _lastTraceTime;

    // Server trace result — all fields from gentity_t
    private int _serverHitEntity = -1;
    private EntityInfo _info;

    private struct EntityInfo
    {
        public string Classname;
        public string Targetname;
        public string Target;
        public string Model;
        public int Spawnflags;
        public int Health;
        public float Speed;
        public int Count;
        public int Damage;
        public int SplashDamage;
        public int SplashRadius;
        public float OriginX, OriginY, OriginZ;
        public float AbsMinX, AbsMinY, AbsMinZ;
        public float AbsMaxX, AbsMaxY, AbsMaxZ;
        public float MoveDirX, MoveDirY, MoveDirZ;
        public int Contents;
        public int Clipmask;
        public int SvFlags;
        public string Message;
        public string Team;
    }

    public void Init()
    {
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        Syscalls.AddCommand("tool");
    }

    public void Shutdown()
    {
        CGameApi.SetHighlightEntity(-1);
        CGameApi.ClearHighlightAABB();
        Syscalls.RemoveCommand("tool");
    }

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
                CGameApi.SetHighlightEntity(-1);
                CGameApi.ClearHighlightAABB();
                _lastHitEntity = -1;
            }
            return;
        }

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
            CGameApi.SetHighlightEntity(-1);
            CGameApi.ClearHighlightAABB();
            _lastHitEntity = -1;
        }

        if (serverTime - _lastTraceTime >= TRACE_INTERVAL_MS)
        {
            _lastTraceTime = serverTime;

            var (ox, oy, oz) = CGameApi.GetViewOrigin();
            var (pitch, yaw, roll) = CGameApi.GetViewAngles();

            float pitchRad = pitch * MathF.PI / 180f;
            float yawRad = yaw * MathF.PI / 180f;
            float cp = MathF.Cos(pitchRad);
            float sp = MathF.Sin(pitchRad);
            float cy = MathF.Cos(yawRad);
            float sy = MathF.Sin(yawRad);

            float endX = ox + cp * cy * TRACE_RANGE;
            float endY = oy + cp * sy * TRACE_RANGE;
            float endZ = oz + (-sp) * TRACE_RANGE;

            Syscalls.SendClientCommand(
                $"toolTrace {ox:F1} {oy:F1} {oz:F1} {endX:F1} {endY:F1} {endZ:F1}");
        }
    }

    public unsafe void Draw2D(int screenWidth, int screenHeight)
    {
        if (!CGameApi.IsAvailable) return;

        int weapon = CGameApi.GetPlayerWeapon();
        if (weapon != WP_TOOL) return;

        // Draw "Active tool: Entity picker" text at top center
        string toolText = "Active tool: Entity picker";
        int charSize = 24;
        int textWidth = toolText.Length * charSize;
        int textX = (screenWidth - textWidth) / 2;
        int textY = 32;

        Syscalls.R_SetColor(0f, 0f, 0f, 0.6f);
        Syscalls.R_DrawStretchPic(textX - 6, textY - 4, textWidth + 12, charSize + 8,
            0, 0, 1, 1, _whiteShader);

        Syscalls.R_SetColor(0f, 1f, 1f, 1f);
        DrawString(textX, textY, toolText, charSize);

        if (_lastHitEntity >= 0)
        {
            ref var info = ref _info;
            int entType = CGameApi.GetEntityType(_lastHitEntity);
            string clientModelName = CGameApi.GetEntityModelName(_lastHitEntity);

            var lines = new System.Collections.Generic.List<(string text, uint color)>();

            // Header line (yellow)
            string typeName = GetEntityTypeName(entType);
            lines.Add(($"Entity #{_lastHitEntity}  [{typeName}]", 0xFFFF4D));

            // Core identity
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

            // Model
            if (!string.IsNullOrEmpty(info.Model))
                lines.Add(($"Brush model: {info.Model}", 0xCCCCCC));
            if (!string.IsNullOrEmpty(clientModelName))
                lines.Add(($"Model: {GetShortName(clientModelName)}", 0xCCCCCC));

            // Spatial
            lines.Add(($"Origin: {info.OriginX:F0} {info.OriginY:F0} {info.OriginZ:F0}", 0xE6E6E6));
            lines.Add(($"AABB min: {info.AbsMinX:F0} {info.AbsMinY:F0} {info.AbsMinZ:F0}", 0xBBBBBB));
            lines.Add(($"AABB max: {info.AbsMaxX:F0} {info.AbsMaxY:F0} {info.AbsMaxZ:F0}", 0xBBBBBB));

            // Movement direction (for jump pads, movers)
            if (info.MoveDirX != 0 || info.MoveDirY != 0 || info.MoveDirZ != 0)
                lines.Add(($"MoveDir: {info.MoveDirX:F0} {info.MoveDirY:F0} {info.MoveDirZ:F0}", 0xFFCC66));

            if (info.Speed != 0)
                lines.Add(($"Speed: {info.Speed:F0}", 0xFFCC66));

            // Gameplay
            if (info.Health != 0)
                lines.Add(($"Health: {info.Health}", 0xFF6666));
            if (info.Damage != 0)
                lines.Add(($"Damage: {info.Damage}", 0xFF6666));
            if (info.SplashDamage != 0)
                lines.Add(($"Splash: {info.SplashDamage} r={info.SplashRadius}", 0xFF6666));
            if (info.Count != 0)
                lines.Add(($"Count: {info.Count}", 0xCCCCCC));

            // Flags
            if (info.Spawnflags != 0)
                lines.Add(($"Spawnflags: 0x{info.Spawnflags:X}", 0x999999));
            if (info.Contents != 0)
                lines.Add(($"Contents: 0x{info.Contents:X}", 0x999999));
            if (info.Clipmask != 0)
                lines.Add(($"Clipmask: 0x{info.Clipmask:X}", 0x999999));
            if (info.SvFlags != 0)
                lines.Add(($"SvFlags: 0x{info.SvFlags:X}", 0x999999));

            // Render panel
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

        float* reset = stackalloc float[4];
        reset[0] = 1; reset[1] = 1; reset[2] = 1; reset[3] = 1;
        Syscalls.R_SetColor(reset);
    }

    private bool _pendingWeaponSwitch;

    public bool ConsoleCommand(string cmd)
    {
        if (cmd == "tool")
        {
            Syscalls.SendConsoleCommand("give Tool\n");
            _pendingWeaponSwitch = true;
            Syscalls.Print("[MOD] ^5Tool weapon equipped\n");
            return true;
        }
        return false;
    }

    public void EntityEvent(int entityNum, int eventType, int eventParm)
    {
    }

    public unsafe void ServerCommand(string args)
    {
        // Format: entNum classname targetname target model spawnflags health speed
        //         count damage splashDamage splashRadius
        //         ox oy oz  mnx mny mnz  mxx mxy mxz  mdx mdy mdz
        //         contents clipmask svFlags message team
        if (string.IsNullOrEmpty(args))
        {
            _serverHitEntity = -1;
            CGameApi.ClearHighlightAABB();
            return;
        }

        var p = args.Split(' ');
        if (p.Length < 1 || !int.TryParse(p[0], out int entNum) || entNum < 0)
        {
            _serverHitEntity = -1;
            CGameApi.ClearHighlightAABB();
            return;
        }

        _serverHitEntity = entNum;
        _info = new EntityInfo
        {
            Classname    = Str(p, 1),
            Targetname   = Str(p, 2),
            Target       = Str(p, 3),
            Model        = Str(p, 4),
            Spawnflags   = Int(p, 5),
            Health       = Int(p, 6),
            Speed        = Float(p, 7),
            Count        = Int(p, 8),
            Damage       = Int(p, 9),
            SplashDamage = Int(p, 10),
            SplashRadius = Int(p, 11),
            OriginX      = Float(p, 12),
            OriginY      = Float(p, 13),
            OriginZ      = Float(p, 14),
            AbsMinX      = Float(p, 15),
            AbsMinY      = Float(p, 16),
            AbsMinZ      = Float(p, 17),
            AbsMaxX      = Float(p, 18),
            AbsMaxY      = Float(p, 19),
            AbsMaxZ      = Float(p, 20),
            MoveDirX     = Float(p, 21),
            MoveDirY     = Float(p, 22),
            MoveDirZ     = Float(p, 23),
            Contents     = Int(p, 24),
            Clipmask     = Int(p, 25),
            SvFlags      = Int(p, 26),
            Message      = Str(p, 27),
            Team         = Str(p, 28),
        };

        // Set the AABB highlight from server data
        float* mins = stackalloc float[3] { _info.AbsMinX, _info.AbsMinY, _info.AbsMinZ };
        float* maxs = stackalloc float[3] { _info.AbsMaxX, _info.AbsMaxY, _info.AbsMaxZ };
        CGameApi.SetHighlightAABB(mins, maxs);
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

    private static string GetEntityTypeName(int entType) => entType switch
    {
        0 => "General",
        1 => "Player",
        2 => "Item",
        3 => "Missile",
        4 => "Mover",
        5 => "Beam",
        6 => "Portal",
        7 => "Speaker",
        8 => "PushTrigger",
        9 => "TeleportTrigger",
        10 => "Invisible",
        11 => "Grapple",
        12 => "Team",
        _ when entType >= 13 => $"Event({entType - 13})",
        _ => $"Unknown({entType})"
    };

    private static string GetShortName(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
