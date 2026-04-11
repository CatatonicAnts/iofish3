using System;

namespace CGameMod;

/// <summary>
/// Entity picker tool mod. When the player has WP_TOOL equipped,
/// traces from the crosshair and highlights the entity under aim with wireframe.
/// Draws "Active tool: Entity picker" on the HUD.
/// </summary>
public class EntityPickerMod : ICGameMod
{
    public string Name => "Entity Picker";

    // WP_TOOL enum value (without MISSIONPACK: WP_GRAPPLING_HOOK=10, WP_TOOL=11)
    private const int WP_TOOL = 11;

    // MASK_SHOT = CONTENTS_SOLID | CONTENTS_BODY | CONTENTS_CORPSE
    private const int MASK_SHOT = 0x6000001;

    // Trace range for entity picking
    private const float TRACE_RANGE = 8192f;

    private int _charsetShader;
    private int _whiteShader;
    private int _lastHitEntity = -1;

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

        // After giving the tool, wait until STAT_WEAPONS has the bit, then switch
        if (_pendingWeaponSwitch)
        {
            // Send weapon switch command — cgame's CG_Weapon_f checks STAT_WEAPONS
            Syscalls.SendConsoleCommand($"weapon {WP_TOOL}\n");
            _pendingWeaponSwitch = false;
        }

        int weapon = CGameApi.GetPlayerWeapon();
        if (weapon != WP_TOOL)
        {
            // Not holding the tool — clear highlight
            if (_lastHitEntity >= 0)
            {
                CGameApi.SetHighlightEntity(-1);
                _lastHitEntity = -1;
            }
            return;
        }

        // Get view origin and forward direction
        var (ox, oy, oz) = CGameApi.GetViewOrigin();
        var (pitch, yaw, roll) = CGameApi.GetViewAngles();

        // Convert angles to forward vector
        float pitchRad = pitch * MathF.PI / 180f;
        float yawRad = yaw * MathF.PI / 180f;
        float cp = MathF.Cos(pitchRad);
        float sp = MathF.Sin(pitchRad);
        float cy = MathF.Cos(yawRad);
        float sy = MathF.Sin(yawRad);

        float fwdX = cp * cy;
        float fwdY = cp * sy;
        float fwdZ = -sp;

        float endX = ox + fwdX * TRACE_RANGE;
        float endY = oy + fwdY * TRACE_RANGE;
        float endZ = oz + fwdZ * TRACE_RANGE;

        // Trace from view origin along view direction
        var (fraction, hitX, hitY, hitZ, hitEnt) = CGameApi.DoTrace(
            ox, oy, oz, endX, endY, endZ, -1, MASK_SHOT);

        // Only highlight actual game entities (not world = 1023)
        if (fraction < 1.0f && hitEnt >= 0 && hitEnt < 1023)
        {
            if (hitEnt != _lastHitEntity)
            {
                CGameApi.SetHighlightEntity(hitEnt);
                _lastHitEntity = hitEnt;
            }
        }
        else
        {
            if (_lastHitEntity >= 0)
            {
                CGameApi.SetHighlightEntity(-1);
                _lastHitEntity = -1;
            }
        }
    }

    public unsafe void Draw2D(int screenWidth, int screenHeight)
    {
        if (!CGameApi.IsAvailable) return;

        int weapon = CGameApi.GetPlayerWeapon();
        if (weapon != WP_TOOL) return;

        // Draw "Active tool: Entity picker" text at top center
        string toolText = "Active tool: Entity picker";
        int charSize = 12;
        int textWidth = toolText.Length * charSize;
        int textX = (screenWidth - textWidth) / 2;
        int textY = 32;

        // Background bar
        Syscalls.R_SetColor(0f, 0f, 0f, 0.6f);
        Syscalls.R_DrawStretchPic(textX - 4, textY - 2, textWidth + 8, charSize + 4,
            0, 0, 1, 1, _whiteShader);

        // Tool name in cyan
        Syscalls.R_SetColor(0f, 1f, 1f, 1f);
        DrawString(textX, textY, toolText, charSize);

        // If we have a highlighted entity, show info near crosshair
        if (_lastHitEntity >= 0)
        {
            int entType = CGameApi.GetEntityType(_lastHitEntity);
            var (ex, ey, ez) = CGameApi.GetEntityOrigin(_lastHitEntity);
            string info = $"Entity #{_lastHitEntity} type:{entType}";

            int infoWidth = info.Length * 8;
            int infoX = (screenWidth - infoWidth) / 2;
            int infoY = screenHeight / 2 + 24;

            Syscalls.R_SetColor(0f, 0f, 0f, 0.5f);
            Syscalls.R_DrawStretchPic(infoX - 2, infoY - 1, infoWidth + 4, 10,
                0, 0, 1, 1, _whiteShader);

            Syscalls.R_SetColor(1f, 1f, 0.5f, 1f);
            DrawString(infoX, infoY, info, 8);
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
}
