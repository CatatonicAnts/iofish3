using System;

namespace CGameMod;

/// <summary>
/// Entity picker tool mod. When the player has WP_TOOL equipped,
/// sends trace requests to the server which does full trace + proximity picking.
/// Highlights the entity under aim with an AABB wireframe.
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

    // Server trace result
    private int _serverHitEntity = -1;
    private string _serverHitClassname = "";

    public void Init()
    {
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        Syscalls.AddCommand("tool");
    }

    public void Shutdown()
    {
        CGameApi.SetHighlightEntity(-1);
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
                _lastHitEntity = -1;
            }
            return;
        }

        // Use server trace result
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
            _lastHitEntity = -1;
        }

        // Send throttled server-side trace request
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

        // Background bar
        Syscalls.R_SetColor(0f, 0f, 0f, 0.6f);
        Syscalls.R_DrawStretchPic(textX - 6, textY - 4, textWidth + 12, charSize + 8,
            0, 0, 1, 1, _whiteShader);

        // Tool name in cyan
        Syscalls.R_SetColor(0f, 1f, 1f, 1f);
        DrawString(textX, textY, toolText, charSize);

        // If we have a highlighted entity, show multiline info panel near crosshair
        if (_lastHitEntity >= 0)
        {
            int entType = CGameApi.GetEntityType(_lastHitEntity);
            var (ex, ey, ez) = CGameApi.GetEntityOrigin(_lastHitEntity);
            string modelName = CGameApi.GetEntityModelName(_lastHitEntity);
            var (entWeapon, eFlags, frame, eventNum) = CGameApi.GetEntityInfo(_lastHitEntity);

            // Build info lines
            string typeName = GetEntityTypeName(entType);
            string line1 = $"Entity #{_lastHitEntity}  [{typeName}]";
            string line2 = $"Origin: {ex:F0} {ey:F0} {ez:F0}";
            string? lineClass = !string.IsNullOrEmpty(_serverHitClassname)
                ? $"Class: {_serverHitClassname}" : null;
            string? line3 = !string.IsNullOrEmpty(modelName) ? $"Model: {GetShortName(modelName)}" : null;
            string? line4 = entWeapon > 0 ? $"Weapon: {entWeapon}  Frame: {frame}" : (frame > 0 ? $"Frame: {frame}" : null);
            string? line5 = eFlags != 0 ? $"Flags: 0x{eFlags:X}" : null;

            var lines = new System.Collections.Generic.List<string> { line1, line2 };
            if (lineClass != null) lines.Add(lineClass);
            if (line3 != null) lines.Add(line3);
            if (line4 != null) lines.Add(line4);
            if (line5 != null) lines.Add(line5);

            int infoCharSize = 16;
            int lineHeight = infoCharSize + 4;
            int maxWidth = 0;
            foreach (var line in lines)
                if (line.Length * infoCharSize > maxWidth)
                    maxWidth = line.Length * infoCharSize;

            int panelHeight = lines.Count * lineHeight + 8;
            int panelX = (screenWidth - maxWidth) / 2 - 8;
            int panelY = screenHeight / 2 + 32;

            // Background panel
            Syscalls.R_SetColor(0f, 0f, 0f, 0.65f);
            Syscalls.R_DrawStretchPic(panelX, panelY, maxWidth + 16, panelHeight,
                0, 0, 1, 1, _whiteShader);

            // Draw lines
            int lineY = panelY + 4;
            for (int li = 0; li < lines.Count; li++)
            {
                // First line: yellow, rest: white
                if (li == 0)
                    Syscalls.R_SetColor(1f, 1f, 0.3f, 1f);
                else
                    Syscalls.R_SetColor(0.9f, 0.9f, 0.9f, 1f);

                int lineX = panelX + 8;
                DrawString(lineX, lineY, lines[li], infoCharSize);
                lineY += lineHeight;
            }
        }

        // Reset color
        float* reset = stackalloc float[4];
        reset[0] = 1; reset[1] = 1; reset[2] = 1; reset[3] = 1;
        Syscalls.R_SetColor(reset);
    }

    private bool _pendingWeaponSwitch;

    public bool ConsoleCommand(string cmd)
    {
        if (cmd == "tool")
        {
            // Give the player the tool weapon — BG_FindItem searches by pickup_name
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

    public void ServerCommand(string args)
    {
        // Parse "entNum classname ox oy oz" or "-1"
        if (string.IsNullOrEmpty(args))
        {
            _serverHitEntity = -1;
            _serverHitClassname = "";
            return;
        }

        var parts = args.Split(' ', 2);
        if (parts.Length < 1 || !int.TryParse(parts[0], out int entNum))
        {
            _serverHitEntity = -1;
            _serverHitClassname = "";
            return;
        }

        _serverHitEntity = entNum;
        _serverHitClassname = parts.Length > 1 ? parts[1].Split(' ')[0] : "";
    }

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
        // Strip leading path, e.g. "models/weapons2/shotgun/shotgun.md3" -> "shotgun.md3"
        int lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
