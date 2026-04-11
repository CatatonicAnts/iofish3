using System.Runtime.InteropServices;
using System.Text;

namespace CGameMod;

/// <summary>
/// C# HUD mod — full replacement of the C HUD.
/// Implements all standard Q3 HUD elements: status bar, weapon select, crosshair,
/// FPS, timer, scores, ammo warning, powerup timers, pickup items, spectator,
/// center strings, and vote display.
/// Scoreboard, intermission, follow, and warmup are still drawn by C code.
/// </summary>
public unsafe class HudMod : ICGameMod
{
    public string Name => "HUD Mod";

    private const int HUD_FLAG_DISABLED = 0x0001;

    // Virtual screen dimensions (Q3 standard 640x480)
    private const float SCREEN_W = 640f;
    private const float SCREEN_H = 480f;

    // Character sizes (matching Q3 defines)
    private const float BIGCHAR_W = 16f;
    private const float BIGCHAR_H = 16f;
    private const float SMALLCHAR_W = 8f;
    private const float SMALLCHAR_H = 16f;
    private const float ICON_SIZE = 48f;

    // Status bar number field sizes (CHAR_WIDTH/CHAR_HEIGHT in cg_local.h)
    private const float NUM_W = 32f;
    private const float NUM_H = 48f;
    private const float TEXT_ICON_SPACE = 4f;
    private const float STATUS_Y = 432f;

    private const int SCORE_NOT_PRESENT = -9999;
    private const int POWERUP_BLINKS = 5;
    private const int POWERUP_BLINK_TIME = 1000;
    private const int FPS_FRAMES = 4;

    // Teams
    private const int TEAM_FREE = 0;
    private const int TEAM_RED = 1;
    private const int TEAM_BLUE = 2;
    private const int TEAM_SPECTATOR = 3;

    // PM flags
    private const int PMF_FOLLOW = 4096;

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

    // Powerup icon shader paths
    private static readonly (int pwIndex, string icon)[] PowerupIcons =
    [
        (ModPlayerState.PW_QUAD, "icons/quad"),
        (ModPlayerState.PW_BATTLESUIT, "icons/envirosuit"),
        (ModPlayerState.PW_HASTE, "icons/haste"),
        (ModPlayerState.PW_INVIS, "icons/invis"),
        (ModPlayerState.PW_REGEN, "icons/regen"),
        (ModPlayerState.PW_FLIGHT, "icons/flight"),
    ];
    private int[] _powerupShaders = new int[16]; // indexed by PW_* constant

    // FPS counter state
    private int[] _fpsFrameTimes = new int[FPS_FRAMES];
    private int _fpsIndex;
    private int _fpsPrevTime;
    private int _fpsValue;

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

        _charsetShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        _selectShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/select");

        // Number shaders
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

        // Powerup icons
        foreach (var (pw, icon) in PowerupIcons)
        {
            if (pw >= 0 && pw < _powerupShaders.Length)
                _powerupShaders[pw] = Syscalls.R_RegisterShaderNoMip(icon);
        }

        _crosshairShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/crosshaira");

        CGameApi.SetHudFlags(HUD_FLAG_DISABLED);
    }

    public void Shutdown()
    {
        CGameApi.SetHudFlags(0);
    }

    public void Frame(int serverTime) { }

    public void Draw2D(int screenWidth, int screenHeight)
    {
        if (!_scaleInit && screenWidth > 0 && screenHeight > 0)
        {
            _xScale = screenWidth / SCREEN_W;
            _yScale = screenHeight / SCREEN_H;
            _scaleInit = true;
        }

        var ps = CGameApi.GetPlayerState();
        var hs = CGameApi.GetHudState();

        // Don't draw HUD during intermission (C handles scoreboard)
        if (ps.PmType == ModPlayerState.PM_INTERMISSION) return;

        bool isSpectator = ps.GetPersistant(ModPlayerState.PERS_TEAM) == TEAM_SPECTATOR;
        bool isAlive = ps.Health > 0;
        bool isDead = ps.PmType == ModPlayerState.PM_DEAD;

        if (isSpectator)
        {
            DrawSpectator(ref hs);
            DrawCrosshairName(ref hs);
        }
        else if (isAlive && !isDead && hs.ShowScores == 0)
        {
            // Status bar (health, armor, ammo)
            DrawStatusBar(ref ps, ref hs);

            // Ammo warning text
            DrawAmmoWarning(ref hs);

            // Crosshair name
            DrawCrosshairName(ref hs);

            // Weapon select bar
            DrawWeaponSelect(ref ps, ref hs);
        }

        // Crosshair (always, even spectator)
        DrawCrosshair();

        // Vote display
        DrawVote(ref hs);

        // Upper right (FPS, timer)
        DrawUpperRight(ref ps, ref hs);

        // Lower right (scores, powerup timers)
        DrawLowerRight(ref ps, ref hs);

        // Lower left (pickup items)
        DrawLowerLeft(ref ps, ref hs);

        // Center print
        DrawCenterString(ref hs);
    }

    public bool ConsoleCommand(string cmd) => false;
    public void EntityEvent(int entityNum, int eventType, int eventParm) { }
    public void ServerCommand(string cmd) { }

    #region Status Bar

    private void DrawStatusBar(ref ModPlayerState ps, ref ModHudState hs)
    {
        int health = ps.Health;
        int armor = ps.Armor;
        int ammo = ps.GetAmmo(ps.Weapon);

        // Ammo (x=0, y=432) — matches Q3's CG_DrawField(0, 432, 3, value)
        if (ps.Weapon > ModPlayerState.WP_GAUNTLET)
        {
            GetAmmoColor(ref hs, out float ar, out float ag, out float ab);
            SetColor(ar, ag, ab, 1f);
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

    private static void GetHealthColor(int health, int time, out float r, out float g, out float b)
    {
        // Matches Q3's colors[] array in CG_DrawStatusBar
        if (health > 100) { r = 1f; g = 1f; b = 1f; }         // white
        else if (health > 25) { r = 0f; g = 1f; b = 0f; }     // green
        else if (health > 0)
        {
            // Flash between green and red
            bool flash = ((time >> 8) & 1) != 0;
            if (flash) { r = 1f; g = 0f; b = 0f; }            // red
            else { r = 0f; g = 1f; b = 0f; }                   // green
        }
        else { r = 1f; g = 0f; b = 0f; }                       // red (dead)
    }

    private static void GetAmmoColor(ref ModHudState hs, out float r, out float g, out float b)
    {
        // Q3 colors: 0=green, 1=red, 2=dark grey
        if (hs.LowAmmoWarning == 2) { r = 1f; g = 0f; b = 0f; }        // out of ammo = red
        else if (hs.LowAmmoWarning == 1) { r = 1f; g = 1f; b = 0f; }   // low ammo = yellow
        else { r = 0f; g = 1f; b = 0f; }                                 // normal = green
    }

    #endregion

    #region Ammo Warning

    private void DrawAmmoWarning(ref ModHudState hs)
    {
        if (hs.LowAmmoWarning == 0) return;

        string s = hs.LowAmmoWarning == 2 ? "OUT OF AMMO" : "LOW AMMO WARNING";
        float w = StripColorCodes(s).Length * BIGCHAR_W;
        SetColor(1f, 1f, 1f, 1f);
        DrawString(320 - w / 2, 64, s, BIGCHAR_W, BIGCHAR_H);
        ResetColor();
    }

    #endregion

    #region Spectator

    private void DrawSpectator(ref ModHudState hs)
    {
        string text = "SPECTATOR";
        float w = text.Length * BIGCHAR_W;
        SetColor(1f, 1f, 1f, 1f);
        DrawString(320 - w / 2, 440, text, BIGCHAR_W, BIGCHAR_H);

        if (hs.Gametype == ModHudState.GT_TOURNAMENT)
        {
            text = "waiting to play";
            w = text.Length * BIGCHAR_W;
            DrawString(320 - w / 2, 460, text, BIGCHAR_W, BIGCHAR_H);
        }
        else if (hs.Gametype >= ModHudState.GT_TEAM)
        {
            text = "press ESC and use the JOIN menu to play";
            w = text.Length * BIGCHAR_W;
            DrawString(320 - w / 2, 460, text, BIGCHAR_W, BIGCHAR_H);
        }

        ResetColor();
    }

    #endregion

    #region Weapon Select

    private void DrawWeaponSelect(ref ModPlayerState ps, ref ModHudState hs)
    {
        if (hs.WeaponSelectTime == 0) return;
        int elapsed = hs.Time - hs.WeaponSelectTime;
        if (elapsed < 0 || elapsed > ModHudState.WEAPON_SELECT_TIME) return;

        float alpha = 1f - (float)elapsed / ModHudState.WEAPON_SELECT_TIME;
        if (alpha <= 0) return;

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
                float bright = selected ? 1f : 0.5f;
                SetColor(bright, bright, bright, alpha);
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

        string name;
        fixed (byte* p = hs.CrosshairClientName)
            name = GetFixedString(p, 64);
        if (name.Length == 0) return;

        string stripped = StripColorCodes(name);
        float textW = stripped.Length * SMALLCHAR_W;
        float x = (SCREEN_W - textW) * 0.5f;
        float y = SCREEN_H * 0.5f + 24f;

        SetColor(1f, 1f, 1f, alpha);
        DrawString(x, y, name, SMALLCHAR_W, SMALLCHAR_H);
        ResetColor();
    }

    #endregion

    #region Upper Right (FPS, Timer)

    private void DrawUpperRight(ref ModPlayerState ps, ref ModHudState hs)
    {
        float y = 0f;

        // FPS counter
        y = DrawFPS(y, hs.RealTime);

        // Timer
        y = DrawTimer(y, ref hs);
    }

    private float DrawFPS(float y, int realTime)
    {
        int frameTime = realTime - _fpsPrevTime;
        _fpsPrevTime = realTime;

        _fpsFrameTimes[_fpsIndex % FPS_FRAMES] = frameTime;
        _fpsIndex++;

        if (_fpsIndex > FPS_FRAMES)
        {
            int total = 0;
            for (int i = 0; i < FPS_FRAMES; i++)
                total += _fpsFrameTimes[i];
            if (total == 0) total = 1;
            _fpsValue = 1000 * FPS_FRAMES / total;

            string s = $"{_fpsValue}fps";
            float w = s.Length * BIGCHAR_W;
            SetColor(1f, 1f, 1f, 1f);
            DrawString(635 - w, y + 2, s, BIGCHAR_W, BIGCHAR_H);
        }

        return y + BIGCHAR_H + 4;
    }

    private float DrawTimer(float y, ref ModHudState hs)
    {
        int msec = hs.Time - hs.LevelStartTime;
        if (msec < 0) msec = 0;

        int seconds = msec / 1000;
        int mins = seconds / 60;
        seconds -= mins * 60;
        int tens = seconds / 10;
        seconds -= tens * 10;

        string s = $"{mins}:{tens}{seconds}";
        float w = s.Length * BIGCHAR_W;

        SetColor(1f, 1f, 1f, 1f);
        DrawString(635 - w, y + 2, s, BIGCHAR_W, BIGCHAR_H);
        ResetColor();

        return y + BIGCHAR_H + 4;
    }

    #endregion

    #region Lower Right (Scores, Powerups)

    private void DrawLowerRight(ref ModPlayerState ps, ref ModHudState hs)
    {
        float y = SCREEN_H - ICON_SIZE;

        y = DrawScores(y, ref ps, ref hs);
        DrawPowerups(y, ref ps, ref hs);
    }

    private float DrawScores(float y, ref ModPlayerState ps, ref ModHudState hs)
    {
        int s1 = hs.Scores1;
        int s2 = hs.Scores2;

        y -= BIGCHAR_H + 8;
        float y1 = y;

        if (hs.Gametype >= ModHudState.GT_TEAM)
        {
            // Team mode: red and blue score boxes
            float x = SCREEN_W;

            // Blue score
            string bs = $"{s2,2}";
            float bw = StripColorCodes(bs).Length * BIGCHAR_W + 8;
            x -= bw;
            SetColor(0f, 0f, 1f, 0.33f);
            FillRect(x, y - 4, bw, BIGCHAR_H + 8);
            if (ps.GetPersistant(ModPlayerState.PERS_TEAM) == TEAM_BLUE && _selectShader != 0)
            {
                SetColor(1f, 1f, 1f, 1f);
                DrawPic(x, y - 4, bw, BIGCHAR_H + 8, _selectShader);
            }
            SetColor(1f, 1f, 1f, 1f);
            DrawString(x + 4, y, bs, BIGCHAR_W, BIGCHAR_H);

            // Red score
            string rs = $"{s1,2}";
            float rw = StripColorCodes(rs).Length * BIGCHAR_W + 8;
            x -= rw;
            SetColor(1f, 0f, 0f, 0.33f);
            FillRect(x, y - 4, rw, BIGCHAR_H + 8);
            if (ps.GetPersistant(ModPlayerState.PERS_TEAM) == TEAM_RED && _selectShader != 0)
            {
                SetColor(1f, 1f, 1f, 1f);
                DrawPic(x, y - 4, rw, BIGCHAR_H + 8, _selectShader);
            }
            SetColor(1f, 1f, 1f, 1f);
            DrawString(x + 4, y, rs, BIGCHAR_W, BIGCHAR_H);

            // Limit
            int limit = hs.Gametype >= ModHudState.GT_CTF ? hs.Capturelimit : hs.Fraglimit;
            if (limit > 0)
            {
                string ls = $"{limit,2}";
                float lw = StripColorCodes(ls).Length * BIGCHAR_W + 8;
                x -= lw;
                SetColor(1f, 1f, 1f, 1f);
                DrawString(x + 4, y, ls, BIGCHAR_W, BIGCHAR_H);
            }
        }
        else
        {
            // FFA mode
            int score = ps.GetPersistant(ModPlayerState.PERS_SCORE);
            bool spectator = ps.GetPersistant(ModPlayerState.PERS_TEAM) == TEAM_SPECTATOR;

            // Always show your score in second box if not in first place
            if (s1 != score) s2 = score;

            float x = SCREEN_W;

            // Second place / your score
            if (s2 != SCORE_NOT_PRESENT)
            {
                string ss = $"{s2,2}";
                float sw = StripColorCodes(ss).Length * BIGCHAR_W + 8;
                x -= sw;
                if (!spectator && score == s2 && score != s1)
                {
                    SetColor(1f, 0f, 0f, 0.33f);
                    FillRect(x, y - 4, sw, BIGCHAR_H + 8);
                    if (_selectShader != 0)
                    {
                        SetColor(1f, 1f, 1f, 1f);
                        DrawPic(x, y - 4, sw, BIGCHAR_H + 8, _selectShader);
                    }
                }
                else
                {
                    SetColor(0.5f, 0.5f, 0.5f, 0.33f);
                    FillRect(x, y - 4, sw, BIGCHAR_H + 8);
                }
                SetColor(1f, 1f, 1f, 1f);
                DrawString(x + 4, y, ss, BIGCHAR_W, BIGCHAR_H);
            }

            // First place
            if (s1 != SCORE_NOT_PRESENT)
            {
                string fs = $"{s1,2}";
                float fw = StripColorCodes(fs).Length * BIGCHAR_W + 8;
                x -= fw;
                if (!spectator && score == s1)
                {
                    SetColor(0f, 0f, 1f, 0.33f);
                    FillRect(x, y - 4, fw, BIGCHAR_H + 8);
                    if (_selectShader != 0)
                    {
                        SetColor(1f, 1f, 1f, 1f);
                        DrawPic(x, y - 4, fw, BIGCHAR_H + 8, _selectShader);
                    }
                }
                else
                {
                    SetColor(0.5f, 0.5f, 0.5f, 0.33f);
                    FillRect(x, y - 4, fw, BIGCHAR_H + 8);
                }
                SetColor(1f, 1f, 1f, 1f);
                DrawString(x + 4, y, fs, BIGCHAR_W, BIGCHAR_H);
            }

            // Fraglimit
            if (hs.Fraglimit > 0)
            {
                string ls = $"{hs.Fraglimit,2}";
                float lw = StripColorCodes(ls).Length * BIGCHAR_W + 8;
                x -= lw;
                SetColor(1f, 1f, 1f, 1f);
                DrawString(x + 4, y, ls, BIGCHAR_W, BIGCHAR_H);
            }
        }

        ResetColor();
        return y1 - 8;
    }

    private void DrawPowerups(float y, ref ModPlayerState ps, ref ModHudState hs)
    {
        if (ps.Health <= 0) return;

        // Sort active powerups by time remaining
        Span<(int pw, int timeLeft)> active = stackalloc (int, int)[16];
        int count = 0;

        for (int i = 0; i < 16; i++)
        {
            int pwTime = ps.GetPowerup(i);
            if (pwTime == 0) continue;
            if (pwTime == int.MaxValue) continue; // infinite (CTF flags)

            int t = pwTime - hs.Time;
            if (t <= 0) continue;

            // Insertion sort by time remaining (ascending)
            int j = count;
            while (j > 0 && active[j - 1].timeLeft > t)
            {
                active[j] = active[j - 1];
                j--;
            }
            active[j] = (i, t);
            count++;
        }

        float x = SCREEN_W - ICON_SIZE - NUM_W * 2;
        for (int i = 0; i < count; i++)
        {
            int pw = active[i].pw;
            int tLeft = active[i].timeLeft;

            y -= ICON_SIZE;

            // Timer digits
            SetColor(1f, 0.2f, 0.2f, 1f);
            DrawField(x, y, 2, tLeft / 1000);

            // Icon - blink when about to expire
            float pwEndTime = ps.GetPowerup(pw);
            if (pwEndTime - hs.Time >= POWERUP_BLINKS * POWERUP_BLINK_TIME)
            {
                SetColor(1f, 1f, 1f, 1f);
            }
            else
            {
                float f = (float)(pwEndTime - hs.Time) / POWERUP_BLINK_TIME;
                f -= (int)f;
                SetColor(f, f, f, f);
            }

            if (pw < _powerupShaders.Length && _powerupShaders[pw] != 0)
                DrawPic(SCREEN_W - ICON_SIZE, y + ICON_SIZE / 2 - ICON_SIZE / 2, ICON_SIZE, ICON_SIZE, _powerupShaders[pw]);
        }

        ResetColor();
    }

    #endregion

    #region Lower Left (Pickup Item)

    private void DrawLowerLeft(ref ModPlayerState ps, ref ModHudState hs)
    {
        float y = SCREEN_H - ICON_SIZE;
        DrawPickupItem(y, ref ps, ref hs);
    }

    private void DrawPickupItem(float y, ref ModPlayerState ps, ref ModHudState hs)
    {
        if (ps.Health <= 0) return;
        if (hs.ItemPickupTime == 0) return;

        int elapsed = hs.Time - hs.ItemPickupTime;
        if (elapsed < 0 || elapsed > 3000) return;

        float alpha;
        if (elapsed > 2000)
            alpha = 1f - (float)(elapsed - 2000) / 1000f;
        else
            alpha = 1f;
        if (alpha <= 0) return;

        y -= ICON_SIZE;

        // Get item name from the new itemPickupName field
        string itemName;
        fixed (byte* p = hs.ItemPickupName)
            itemName = GetFixedString(p, 64);
        if (itemName.Length == 0) return;

        SetColor(1f, 1f, 1f, alpha);
        DrawString(ICON_SIZE + 16, y + (ICON_SIZE / 2 - BIGCHAR_H / 2), itemName, BIGCHAR_W, BIGCHAR_H);
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
        string[] lines = text.Split('\n');
        float y = SCREEN_H * 0.3f;
        float cw = hs.CenterPrintCharWidth > 0 ? hs.CenterPrintCharWidth : SMALLCHAR_W;
        foreach (string line in lines)
        {
            string stripped = StripColorCodes(line);
            float textW = stripped.Length * cw;
            float x = (SCREEN_W - textW) * 0.5f;
            DrawString(x, y, line, cw, BIGCHAR_H);
            y += cw + 4;
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
        string text = $"VOTE({sec}):{vote} yes:{hs.VoteYes} no:{hs.VoteNo}";
        SetColor(1f, 1f, 0f, 1f);
        DrawString(0, 58, text, SMALLCHAR_W, SMALLCHAR_H);
        ResetColor();
    }

    #endregion

    #region Drawing Helpers

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

    private void DrawString(float x, float y, string text, float charW, float charH)
    {
        float cx = x;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
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

    private void DrawField(float x, float y, int width, int value)
    {
        if (width < 1) return;
        if (width > 5) width = 5;
        int maxVal = width switch { 1 => 9, 2 => 99, 3 => 999, 4 => 9999, _ => 99999 };
        int minVal = width switch { 1 => 0, 2 => -9, 3 => -99, 4 => -999, _ => -9999 };
        if (value > maxVal) value = maxVal;
        if (value < minVal) value = minVal;

        string numStr = value.ToString();
        int l = numStr.Length;
        if (l > width) l = width;

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
            case '0': SetColor(0, 0, 0, 1); break;
            case '1': SetColor(1, 0, 0, 1); break;
            case '2': SetColor(0, 1, 0, 1); break;
            case '3': SetColor(1, 1, 0, 1); break;
            case '4': SetColor(0, 0, 1, 1); break;
            case '5': SetColor(0, 1, 1, 1); break;
            case '6': SetColor(1, 0, 1, 1); break;
            default: SetColor(1, 1, 1, 1); break;
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
