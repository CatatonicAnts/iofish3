using System.Runtime.InteropServices;
using System.Text;

namespace CGameMod;

/// <summary>
/// C# HUD mod — replaces the C HUD with a managed implementation.
/// Draws health, armor, ammo, weapon select, crosshair name, scores,
/// timer, pickup items, center strings, and vote info.
/// </summary>
public unsafe class HudMod : ICGameMod
{
    public string Name => "HUD Mod";

    // HUD control flag
    private const int HUD_FLAG_DISABLED = 0x0001;

    // Virtual screen dimensions (Q3 standard)
    private const float SCREEN_W = 640f;
    private const float SCREEN_H = 480f;

    // Character/icon sizes
    private const float BIGCHAR_W = 16f;
    private const float BIGCHAR_H = 16f;
    private const float ICON_SIZE = 48f;
    private const float SMALLCHAR_W = 8f;
    private const float SMALLCHAR_H = 16f;

    // Status bar positioning
    private const float STATUS_Y = 432f;

    // Coordinate scaling (640x480 virtual → actual screen pixels)
    private float _xScale = 1f;
    private float _yScale = 1f;
    private bool _scaleInit;

    // Shader handles
    private int _charsetShader;
    private int _whiteShader;
    private int _selectShader;
    private int _crosshairShader;
    private int[] _numberShaders = new int[11]; // 0-9 + minus
    private int[] _weaponIcons = new int[ModPlayerState.WP_NUM_WEAPONS];
    private int[] _ammoIcons = new int[ModPlayerState.WP_NUM_WEAPONS];

    // Weapon icon paths (matching Q3 weapon order)
    private static readonly string[] WeaponIconPaths =
    [
        "", // WP_NONE
        "icons/iconw_gauntlet",
        "icons/iconw_machinegun",
        "icons/iconw_shotgun",
        "icons/iconw_grenade",
        "icons/iconw_rocket",
        "icons/iconw_lightning",
        "icons/iconw_railgun",
        "icons/iconw_plasma",
        "icons/iconw_bfg",
        "icons/iconw_grapple",
    ];

    public void Init()
    {
        Syscalls.Print("[MOD] HUD Mod initializing...\n");

        // Register shaders
        _charsetShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        _selectShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/select");

        // Number shaders (gfx/2d/numbers/...)
        string[] numNames = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        for (int i = 0; i < 10; i++)
            _numberShaders[i] = Syscalls.R_RegisterShaderNoMip($"gfx/2d/numbers/{numNames[i]}_32b");
        _numberShaders[10] = Syscalls.R_RegisterShaderNoMip("gfx/2d/numbers/minus_32b");

        // Weapon icons
        for (int i = 1; i < ModPlayerState.WP_NUM_WEAPONS && i < WeaponIconPaths.Length; i++)
        {
            if (WeaponIconPaths[i].Length > 0)
                _weaponIcons[i] = Syscalls.R_RegisterShaderNoMip(WeaponIconPaths[i]);
        }

        // Crosshair
        _crosshairShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/crosshaira");

        // Tell C to disable its HUD
        CGameApi.SetHudFlags(HUD_FLAG_DISABLED);
    }

    public void Shutdown()
    {
        CGameApi.SetHudFlags(0);
    }

    public void Frame(int serverTime) { }

    public void Draw2D(int screenWidth, int screenHeight)
    {
        // Compute coordinate scaling on first call
        if (!_scaleInit && screenWidth > 0 && screenHeight > 0)
        {
            _xScale = screenWidth / SCREEN_W;
            _yScale = screenHeight / SCREEN_H;
            _scaleInit = true;
        }

        var ps = CGameApi.GetPlayerState();
        var hs = CGameApi.GetHudState();

        // Don't draw HUD during intermission
        if (ps.PmType == ModPlayerState.PM_INTERMISSION) return;

        // Status bar (health, armor, ammo)
        DrawStatusBar(ref ps, ref hs);

        // Weapon select bar
        DrawWeaponSelect(ref ps, ref hs);

        // Crosshair
        DrawCrosshair();

        // Crosshair name
        DrawCrosshairName(ref hs);

        // Upper right (timer, FPS, scores)
        DrawUpperRight(ref ps, ref hs);

        // Lower left (pickup items)
        DrawPickupItem(ref hs);

        // Center print
        DrawCenterString(ref hs);

        // Vote display
        DrawVote(ref hs);
    }

    public bool ConsoleCommand(string cmd) => false;
    public void EntityEvent(int entityNum, int eventType, int eventParm) { }
    public void ServerCommand(string cmd) { }

    #region Status Bar

    // Q3 layout: CHAR_WIDTH=32, CHAR_HEIGHT=48, TEXT_ICON_SPACE=4
    private const float NUM_W = 32f;
    private const float NUM_H = 48f;
    private const float TEXT_ICON_SPACE = 4f;

    private void DrawStatusBar(ref ModPlayerState ps, ref ModHudState hs)
    {
        int health = ps.Health;
        int armor = ps.Armor;
        int ammo = ps.GetAmmo(ps.Weapon);

        // Ammo (x=0, y=432) — matches Q3's CG_DrawField(0, 432, 3, value)
        if (ps.Weapon > ModPlayerState.WP_GAUNTLET)
        {
            if (hs.LowAmmoWarning == 2)
                SetColor(1f, 0f, 0f, 1f);
            else if (hs.LowAmmoWarning == 1)
                SetColor(1f, 1f, 0f, 1f);
            else
                SetColor(1f, 0.7f, 0f, 1f);
            DrawField(0, STATUS_Y, 3, ammo);

            // Ammo icon after digits
            if (ps.Weapon > 0 && ps.Weapon < ModPlayerState.WP_NUM_WEAPONS && _weaponIcons[ps.Weapon] != 0)
            {
                SetColor(1f, 1f, 1f, 1f);
                DrawPic(NUM_W * 3 + TEXT_ICON_SPACE, STATUS_Y, ICON_SIZE, ICON_SIZE, _weaponIcons[ps.Weapon]);
            }
        }

        // Health (x=185, y=432)
        GetHealthColor(health, hs.Time, out float hr, out float hg, out float hb);
        SetColor(hr, hg, hb, 1f);
        DrawField(185, STATUS_Y, 3, health);

        // Armor (x=370, y=432)
        if (armor > 0)
        {
            SetColor(0f, 1f, 0f, 1f);
            DrawField(370, STATUS_Y, 3, armor);
        }

        ResetColor();
    }

    private void GetHealthColor(int health, int time, out float r, out float g, out float b)
    {
        if (health > 100) { r = 1f; g = 1f; b = 1f; }
        else if (health > 25) { r = 1f; g = 0.7f; b = 0f; }
        else if (health > 0)
        {
            // Flash red
            bool flash = ((time >> 8) & 1) != 0;
            r = 1f; g = flash ? 0f : 0.2f; b = 0f;
        }
        else { r = 1f; g = 0f; b = 0f; }
    }

    #endregion

    #region Weapon Select

    private void DrawWeaponSelect(ref ModPlayerState ps, ref ModHudState hs)
    {
        if (hs.WeaponSelectTime == 0) return;
        int elapsed = hs.Time - hs.WeaponSelectTime;
        if (elapsed < 0 || elapsed > ModHudState.WEAPON_SELECT_TIME) return;

        // Fade out
        float alpha = 1f - (float)elapsed / ModHudState.WEAPON_SELECT_TIME;
        if (alpha <= 0) return;

        // Count owned weapons
        int count = 0;
        int bits = ps.GetStat(ModPlayerState.STAT_WEAPONS);
        for (int i = 1; i < ModPlayerState.WP_NUM_WEAPONS; i++)
        {
            if ((bits & (1 << i)) != 0) count++;
        }
        if (count == 0) return;

        float iconW = 32f;
        float iconH = 32f;
        float gap = 4f;
        float totalW = count * (iconW + gap) - gap;
        float x = (SCREEN_W - totalW) * 0.5f;
        float y = STATUS_Y - iconH - 8;

        for (int i = 1; i < ModPlayerState.WP_NUM_WEAPONS; i++)
        {
            if ((bits & (1 << i)) == 0) continue;

            bool selected = (i == hs.WeaponSelect);
            if (selected && _selectShader != 0)
            {
                SetColor(1f, 1f, 1f, alpha);
                DrawPic(x - 4, y - 4, iconW + 8, iconH + 8, _selectShader);
            }

            if (_weaponIcons[i] != 0)
            {
                SetColor(selected ? 1f : 0.5f, selected ? 1f : 0.5f, selected ? 1f : 0.5f, alpha);
                DrawPic(x, y, iconW, iconH, _weaponIcons[i]);
            }

            x += iconW + gap;
        }
        ResetColor();
    }

    #endregion

    #region Crosshair

    private void DrawCrosshair()
    {
        if (_crosshairShader == 0) return;
        float size = 24f;
        float x = (SCREEN_W - size) * 0.5f;
        float y = (SCREEN_H - size) * 0.5f;
        SetColor(1f, 1f, 1f, 1f);
        DrawPic(x, y, size, size, _crosshairShader);
        ResetColor();
    }

    private void DrawCrosshairName(ref ModHudState hs)
    {
        if (hs.CrosshairClientNum < 0 || hs.CrosshairClientTime == 0) return;
        int elapsed = hs.Time - hs.CrosshairClientTime;
        if (elapsed < 0 || elapsed > 1000) return;

        float alpha = 1f - (float)elapsed / 1000f;
        if (alpha <= 0) return;

        // Read the name - need fixed because CrosshairClientName is a fixed buffer
        string name;
        fixed (byte* p = hs.CrosshairClientName)
            name = GetFixedString(p, 64);
        if (name.Length == 0) return;

        // Strip Q3 color codes for width calculation
        string stripped = StripColorCodes(name);
        float textW = stripped.Length * SMALLCHAR_W;
        float x = (SCREEN_W - textW) * 0.5f;
        float y = SCREEN_H * 0.5f + 24f;

        SetColor(1f, 1f, 1f, alpha);
        DrawString(x, y, name, SMALLCHAR_W, SMALLCHAR_H);
        ResetColor();
    }

    #endregion

    #region Upper Right (timer, scores, FPS)

    private void DrawUpperRight(ref ModPlayerState ps, ref ModHudState hs)
    {
        float y = 0f;
        float x = SCREEN_W - 4f;

        // Timer
        int elapsed = hs.Time - hs.LevelStartTime;
        if (elapsed < 0) elapsed = 0;
        int minutes = elapsed / 60000;
        int seconds = (elapsed / 1000) % 60;
        string timer = $"{minutes}:{seconds:D2}";
        x = SCREEN_W - timer.Length * SMALLCHAR_W - 4;
        SetColor(1f, 1f, 1f, 1f);
        DrawString(x, y, timer, SMALLCHAR_W, SMALLCHAR_H);
        y += SMALLCHAR_H + 2;

        // Score
        int score = ps.GetPersistant(ModPlayerState.PERS_SCORE);
        string scoreStr = $"Score: {score}";
        x = SCREEN_W - scoreStr.Length * SMALLCHAR_W - 4;
        DrawString(x, y, scoreStr, SMALLCHAR_W, SMALLCHAR_H);
        y += SMALLCHAR_H + 2;

        ResetColor();
    }

    #endregion

    #region Pickup Item

    private void DrawPickupItem(ref ModHudState hs)
    {
        if (hs.ItemPickupTime == 0) return;
        int elapsed = hs.Time - hs.ItemPickupTime;
        if (elapsed < 0 || elapsed > 3000) return;

        float alpha = elapsed > 2000 ? 1f - (float)(elapsed - 2000) / 1000f : 1f;
        if (alpha <= 0) return;

        // Get item name from config string (CS_ITEMS=27 is the items string)
        // The item index is hs.ItemPickup — use config string to get the name
        // For now just show a generic "Item picked up" text
        SetColor(1f, 1f, 1f, alpha);
        DrawString(4, STATUS_Y - 20, "Item picked up", SMALLCHAR_W, SMALLCHAR_H);
        ResetColor();
    }

    #endregion

    #region Center String

    private void DrawCenterString(ref ModHudState hs)
    {
        if (hs.CenterPrintTime == 0) return;
        int elapsed = hs.Time - hs.CenterPrintTime;
        if (elapsed < 0 || elapsed > 3000) return;

        float alpha = elapsed > 2000 ? 1f - (float)(elapsed - 2000) / 1000f : 1f;
        if (alpha <= 0) return;

        string text;
        fixed (byte* p = hs.CenterPrint)
            text = GetFixedString(p, 1024);
        if (text.Length == 0) return;

        SetColor(1f, 1f, 1f, alpha);
        // Split by newlines, center each line
        string[] lines = text.Split('\n');
        float y = SCREEN_H * 0.3f;
        foreach (string line in lines)
        {
            string stripped = StripColorCodes(line);
            float textW = stripped.Length * hs.CenterPrintCharWidth;
            float x = (SCREEN_W - textW) * 0.5f;
            DrawString(x, y, line, hs.CenterPrintCharWidth, BIGCHAR_H);
            y += hs.CenterPrintCharWidth + 4;
        }
        ResetColor();
    }

    #endregion

    #region Vote

    private void DrawVote(ref ModHudState hs)
    {
        if (hs.VoteTime == 0) return;
        int elapsed = hs.Time - hs.VoteTime;
        if (elapsed < 0 || elapsed > 30000) return;

        string vote;
        fixed (byte* p = hs.VoteString)
            vote = GetFixedString(p, 256);
        if (vote.Length == 0) return;

        int sec = (30000 - elapsed) / 1000;
        string text = $"VOTE({sec}): {vote}  Yes:{hs.VoteYes}  No:{hs.VoteNo}";
        SetColor(1f, 1f, 0f, 1f);
        DrawString(4, 58, text, SMALLCHAR_W, SMALLCHAR_H);
        ResetColor();
    }

    #endregion

    #region Drawing Helpers

    /// <summary>Convert 640x480 virtual coordinates to actual screen pixels.</summary>
    private void AdjustFrom640(ref float x, ref float y, ref float w, ref float h)
    {
        x *= _xScale;
        y *= _yScale;
        w *= _xScale;
        h *= _yScale;
    }

    private void SetColor(float r, float g, float b, float a) =>
        Syscalls.R_SetColor(r, g, b, a);

    private void ResetColor() =>
        Syscalls.R_SetColor(1f, 1f, 1f, 1f);

    private void DrawPic(float x, float y, float w, float h, int shader)
    {
        AdjustFrom640(ref x, ref y, ref w, ref h);
        Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 1, 1, shader);
    }

    private void FillRect(float x, float y, float w, float h)
    {
        AdjustFrom640(ref x, ref y, ref w, ref h);
        if (_whiteShader != 0)
            Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 0, 0, _whiteShader);
    }

    /// <summary>Draw a character from the bigchars charset.</summary>
    private void DrawChar(float x, float y, float w, float h, int ch)
    {
        if (ch <= ' ') return;
        if (_charsetShader == 0) return;
        AdjustFrom640(ref x, ref y, ref w, ref h);
        int row = ch >> 4;
        int col = ch & 15;
        float s = col * 0.0625f;
        float t = row * 0.0625f;
        Syscalls.R_DrawStretchPic(x, y, w, h, s, t, s + 0.0625f, t + 0.0625f, _charsetShader);
    }

    /// <summary>Draw a string using the bigchars charset.</summary>
    private void DrawString(float x, float y, string text, float charW, float charH)
    {
        float cx = x;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            // Q3 color code
            if (c == '^' && i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9')
            {
                ApplyQ3Color(text[i + 1]);
                i++;
                continue;
            }
            DrawChar(cx, y, charW, charH, c);
            cx += charW;
        }
    }

    /// <summary>Draw a number field, right-aligned within 'width' digit columns (matches Q3's CG_DrawField).</summary>
    private void DrawField(float x, float y, int width, int value)
    {
        // Clamp value to fit in width digits
        if (width < 1) return;
        if (width > 5) width = 5;
        int maxVal = width switch { 1 => 9, 2 => 99, 3 => 999, 4 => 9999, _ => 99999 };
        int minVal = width switch { 1 => 0, 2 => -9, 3 => -99, 4 => -999, _ => -9999 };
        if (value > maxVal) value = maxVal;
        if (value < minVal) value = minVal;

        string numStr = value.ToString();
        int l = numStr.Length;
        if (l > width) l = width;

        // Right-align: x += 2 + CHAR_WIDTH * (width - l)
        float cx = x + 2 + NUM_W * (width - l);

        for (int i = 0; i < l; i++)
        {
            char c = numStr[i];
            int idx;
            if (c == '-')
                idx = 10;
            else if (c >= '0' && c <= '9')
                idx = c - '0';
            else
                continue;

            if (_numberShaders[idx] != 0)
                DrawPic(cx, y, NUM_W, NUM_H, _numberShaders[idx]);
            cx += NUM_W;
        }
    }

    private void ApplyQ3Color(char code)
    {
        switch (code)
        {
            case '0': SetColor(0, 0, 0, 1); break;             // black
            case '1': SetColor(1, 0, 0, 1); break;             // red
            case '2': SetColor(0, 1, 0, 1); break;             // green
            case '3': SetColor(1, 1, 0, 1); break;             // yellow
            case '4': SetColor(0, 0, 1, 1); break;             // blue
            case '5': SetColor(0, 1, 1, 1); break;             // cyan
            case '6': SetColor(1, 0, 1, 1); break;             // magenta
            default: SetColor(1, 1, 1, 1); break;              // white
        }
    }

    private static string GetFixedString(byte* buf, int maxLen)
    {
        int len = 0;
        while (len < maxLen && buf[len] != 0) len++;
        if (len == 0) return "";
        return Encoding.ASCII.GetString(buf, len);
    }

    private static string StripColorCodes(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9')
            {
                i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    #endregion
}
