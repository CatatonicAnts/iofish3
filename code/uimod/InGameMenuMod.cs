namespace UiMod;

/// <summary>
/// In-game escape menu (UIMENU_INGAME). Semi-transparent overlay with
/// Resume, Add Bot, Remove Bot, Team, Console, Disconnect, Quit.
/// Pauses the game while open.
/// </summary>
public class InGameMenuMod : IUiMod
{
    public string Name => "In-Game Menu";

    private const int UIMENU_NONE = 0;
    private const int UIMENU_INGAME = 2;
    private const int KEYCATCH_UI = 0x0002;

    private bool _active;
    private int _cursor;
    private float _cursorX = 320f;
    private float _cursorY = 240f;
    private int _realtime;

    private enum ConfirmMode { None, Disconnect, Quit }
    private ConfirmMode _confirm;

    private enum SubMenu { None, AddBot, RemoveBot }
    private SubMenu _subMenu;

    private int _cursorShader;
    private int _sfxMove;
    private int _sfxSelect;
    private int _sfxBack;

    private static readonly string[] Items =
    [
        "RESUME",
        "ADD BOT",
        "REMOVE BOT",
        "RESTART MAP",
        "CONSOLE",
        "DISCONNECT",
        "QUIT",
    ];

    private static readonly string[] BotNames =
        ["sarge", "grunt", "major", "visor", "slash", "razor",
         "keel", "lucy", "tankjr", "bitterman", "xaero", "uriel"];
    private static readonly string[] BotSkills = ["1", "2", "3", "4", "5"];
    private int _botNameIdx;
    private int _botSkillIdx = 2;

    // Layout
    private const float PANEL_W = 300f;
    private const float PANEL_H = 310f;
    private const float PANEL_X = (Drawing.SCREEN_W - PANEL_W) * 0.5f;
    private const float PANEL_Y = (Drawing.SCREEN_H - PANEL_H) * 0.5f;
    private const float TITLE_Y = PANEL_Y + 14f;
    private const float ITEM_START_Y = PANEL_Y + 50f;
    private const float ITEM_SPACING = 30f;
    private const float ITEM_CHAR_W = 11f;
    private const float ITEM_CHAR_H = 11f;
    private const float BAR_W = 240f;

    public void Init()
    {
        _cursorShader = Syscalls.R_RegisterShaderNoMip("menu/art/3_cursor2");
        _sfxMove = Syscalls.S_RegisterSound("sound/misc/menu1.wav");
        _sfxSelect = Syscalls.S_RegisterSound("sound/misc/menu3.wav");
        _sfxBack = Syscalls.S_RegisterSound("sound/misc/menu3.wav");
    }

    public void Shutdown() => _active = false;

    public bool SetActiveMenu(int menu)
    {
        if (menu == UIMENU_INGAME)
        {
            _active = true;
            _cursor = 0;
            _confirm = ConfirmMode.None;
            _subMenu = SubMenu.None;
            _cursorX = 320f;
            _cursorY = 240f;

            Syscalls.CvarSet("cl_paused", "1");
            Syscalls.Key_SetCatcher(Syscalls.Key_GetCatcher() | KEYCATCH_UI);
            return true;
        }

        if (menu == UIMENU_NONE && _active) CloseMenu();
        if (_active) { _active = false; _confirm = ConfirmMode.None; _subMenu = SubMenu.None; }
        return false;
    }

    public bool Refresh(int realtime)
    {
        if (!_active) return false;
        _realtime = realtime;

        // Dark overlay over game
        Drawing.SetColor(0.0f, 0.0f, 0.0f, 0.6f);
        Drawing.FillRect(0, 0, Drawing.SCREEN_W, Drawing.SCREEN_H);

        // Panel background
        Drawing.SetColor(0.04f, 0.04f, 0.08f, 0.9f);
        Drawing.FillRect(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawBorder(PANEL_X, PANEL_Y, PANEL_W, PANEL_H, 0.5f, 0.15f, 0.0f, 0.45f);

        // Title
        Drawing.SetColor(0, 0, 0, 0.6f);
        DrawCentered(TITLE_Y + 1, "GAME MENU", 12f, 12f);
        Drawing.SetColor(0.85f, 0.35f, 0.0f, 1.0f);
        DrawCentered(TITLE_Y, "GAME MENU", 12f, 12f);

        // Separator
        Drawing.SetColor(0.4f, 0.12f, 0.0f, 0.35f);
        Drawing.FillRect(PANEL_X + 20, PANEL_Y + 36, PANEL_W - 40, 1);

        if (_confirm != ConfirmMode.None)
            DrawConfirm();
        else if (_subMenu != SubMenu.None)
            DrawSubMenu();
        else
            DrawItems();

        // Cursor
        Drawing.ClearColor();
        if (_cursorShader != 0)
            Drawing.DrawPic(_cursorX - 16, _cursorY - 16, 32, 32, _cursorShader);

        return true;
    }

    private void DrawItems()
    {
        for (int i = 0; i < Items.Length; i++)
        {
            float y = ITEM_START_Y + i * ITEM_SPACING;
            bool sel = i == _cursor;

            if (sel)
            {
                float barX = (Drawing.SCREEN_W - BAR_W) * 0.5f;
                Drawing.SetColor(0.6f, 0.18f, 0.0f, 0.25f);
                Drawing.FillRect(barX, y - 3, BAR_W, ITEM_CHAR_H + 6);

                float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);
                Drawing.SetColor(1.0f, pulse, pulse * 0.25f, 1.0f);
            }
            else
            {
                Drawing.SetColor(0.6f, 0.18f, 0.0f, 0.7f);
            }

            DrawCentered(y, Items[i], ITEM_CHAR_W, ITEM_CHAR_H);
        }
    }

    private void DrawConfirm()
    {
        string prompt = _confirm == ConfirmMode.Quit ? "EXIT GAME?" : "DISCONNECT?";

        Drawing.SetColor(0, 0, 0, 0.6f);
        DrawCentered(ITEM_START_Y + 21, prompt, 14f, 14f);
        Drawing.SetColor(1.0f, 1.0f, 1.0f, 1.0f);
        DrawCentered(ITEM_START_Y + 20, prompt, 14f, 14f);

        float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);
        float yYes = ITEM_START_Y + 60;
        float yNo = ITEM_START_Y + 88;

        Drawing.SetColor(_cursor == 0 ? 1f : 0.4f, _cursor == 0 ? pulse : 0.4f, 0f, 1f);
        DrawCentered(yYes, "YES", 14f, 14f);

        Drawing.SetColor(_cursor == 1 ? 1f : 0.4f, _cursor == 1 ? pulse : 0.4f, 0f, 1f);
        DrawCentered(yNo, "NO", 14f, 14f);
    }

    private void DrawSubMenu()
    {
        if (_subMenu == SubMenu.AddBot)
        {
            Drawing.SetColor(1f, 1f, 1f, 1f);
            DrawCentered(ITEM_START_Y, "ADD BOT", 12f, 12f);

            float y = ITEM_START_Y + 30;
            Drawing.SetColor(_cursor == 0 ? 1f : 0.5f, _cursor == 0 ? 0.8f : 0.3f, 0f, 1f);
            DrawCentered(y, $"Bot: {BotNames[_botNameIdx]}  < >", 10f, 11f);

            y += 26;
            Drawing.SetColor(_cursor == 1 ? 1f : 0.5f, _cursor == 1 ? 0.8f : 0.3f, 0f, 1f);
            DrawCentered(y, $"Skill: {BotSkills[_botSkillIdx]}  < >", 10f, 11f);

            y += 34;
            float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);
            Drawing.SetColor(_cursor == 2 ? 1f : 0.5f, _cursor == 2 ? pulse : 0.3f, 0f, 1f);
            DrawCentered(y, "ADD", 12f, 12f);

            y += 26;
            Drawing.SetColor(_cursor == 3 ? 1f : 0.5f, _cursor == 3 ? pulse : 0.3f, 0f, 1f);
            DrawCentered(y, "BACK", 10f, 11f);
        }
        else if (_subMenu == SubMenu.RemoveBot)
        {
            Drawing.SetColor(1f, 1f, 1f, 1f);
            DrawCentered(ITEM_START_Y, "REMOVE BOT", 12f, 12f);

            float y = ITEM_START_Y + 30;
            Drawing.SetColor(_cursor == 0 ? 1f : 0.5f, _cursor == 0 ? 0.8f : 0.3f, 0f, 1f);
            DrawCentered(y, $"Bot: {BotNames[_botNameIdx]}  < >", 10f, 11f);

            y += 34;
            float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);
            Drawing.SetColor(_cursor == 1 ? 1f : 0.5f, _cursor == 1 ? pulse : 0.3f, 0f, 1f);
            DrawCentered(y, "REMOVE", 12f, 12f);

            y += 26;
            Drawing.SetColor(_cursor == 2 ? 1f : 0.5f, _cursor == 2 ? pulse : 0.3f, 0f, 1f);
            DrawCentered(y, "BACK", 10f, 11f);
        }
    }

    public bool KeyEvent(int key, int down)
    {
        if (!_active) return false;
        if (down == 0) return true;

        if (_confirm != ConfirmMode.None) return HandleConfirmKey(key);
        if (_subMenu != SubMenu.None) return HandleSubMenuKey(key);

        switch (key)
        {
            case Keys.K_ESCAPE or Keys.K_MOUSE2:
                CloseMenu();
                return true;

            case Keys.K_UPARROW or Keys.K_KP_UPARROW:
                _cursor = (_cursor - 1 + Items.Length) % Items.Length;
                PlaySound(_sfxMove);
                return true;

            case Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                _cursor = (_cursor + 1) % Items.Length;
                PlaySound(_sfxMove);
                return true;

            case Keys.K_ENTER or Keys.K_KP_ENTER or Keys.K_MOUSE1:
                ActivateItem(_cursor);
                return true;
        }

        return true;
    }

    private bool HandleConfirmKey(int key)
    {
        switch (key)
        {
            case Keys.K_UPARROW or Keys.K_KP_UPARROW or Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                _cursor = _cursor == 0 ? 1 : 0;
                PlaySound(_sfxMove);
                return true;

            case Keys.K_ENTER or Keys.K_KP_ENTER or Keys.K_MOUSE1:
                if (_cursor == 0)
                {
                    if (_confirm == ConfirmMode.Quit)
                        Syscalls.ExecuteCommand("quit\n");
                    else
                    {
                        Syscalls.ExecuteCommand("disconnect\n");
                        _active = false;
                        _confirm = ConfirmMode.None;
                    }
                }
                else
                {
                    _confirm = ConfirmMode.None;
                    _cursor = 0;
                    PlaySound(_sfxBack);
                }
                return true;

            case Keys.K_ESCAPE or Keys.K_MOUSE2:
                _confirm = ConfirmMode.None;
                _cursor = 0;
                PlaySound(_sfxBack);
                return true;
        }
        return true;
    }

    private bool HandleSubMenuKey(int key)
    {
        if (_subMenu == SubMenu.AddBot)
        {
            int maxItems = 4;
            switch (key)
            {
                case Keys.K_UPARROW or Keys.K_KP_UPARROW:
                    _cursor = (_cursor - 1 + maxItems) % maxItems;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                    _cursor = (_cursor + 1) % maxItems;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_LEFTARROW or Keys.K_KP_LEFTARROW:
                    if (_cursor == 0) _botNameIdx = (_botNameIdx - 1 + BotNames.Length) % BotNames.Length;
                    else if (_cursor == 1) _botSkillIdx = (_botSkillIdx - 1 + BotSkills.Length) % BotSkills.Length;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_RIGHTARROW or Keys.K_KP_RIGHTARROW:
                    if (_cursor == 0) _botNameIdx = (_botNameIdx + 1) % BotNames.Length;
                    else if (_cursor == 1) _botSkillIdx = (_botSkillIdx + 1) % BotSkills.Length;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_ENTER or Keys.K_KP_ENTER or Keys.K_MOUSE1:
                    if (_cursor == 2) // ADD
                    {
                        Syscalls.ExecuteCommand($"addbot {BotNames[_botNameIdx]} {BotSkills[_botSkillIdx]}\n");
                        PlaySound(_sfxSelect);
                    }
                    else if (_cursor == 3) // BACK
                    {
                        _subMenu = SubMenu.None;
                        _cursor = 1;
                        PlaySound(_sfxBack);
                    }
                    return true;
                case Keys.K_ESCAPE or Keys.K_MOUSE2:
                    _subMenu = SubMenu.None;
                    _cursor = 1;
                    PlaySound(_sfxBack);
                    return true;
            }
        }
        else if (_subMenu == SubMenu.RemoveBot)
        {
            int maxItems = 3;
            switch (key)
            {
                case Keys.K_UPARROW or Keys.K_KP_UPARROW:
                    _cursor = (_cursor - 1 + maxItems) % maxItems;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                    _cursor = (_cursor + 1) % maxItems;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_LEFTARROW or Keys.K_KP_LEFTARROW:
                    if (_cursor == 0) _botNameIdx = (_botNameIdx - 1 + BotNames.Length) % BotNames.Length;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_RIGHTARROW or Keys.K_KP_RIGHTARROW:
                    if (_cursor == 0) _botNameIdx = (_botNameIdx + 1) % BotNames.Length;
                    PlaySound(_sfxMove);
                    return true;
                case Keys.K_ENTER or Keys.K_KP_ENTER or Keys.K_MOUSE1:
                    if (_cursor == 1) // REMOVE
                    {
                        Syscalls.ExecuteCommand($"kick {BotNames[_botNameIdx]}\n");
                        PlaySound(_sfxSelect);
                    }
                    else if (_cursor == 2) // BACK
                    {
                        _subMenu = SubMenu.None;
                        _cursor = 2;
                        PlaySound(_sfxBack);
                    }
                    return true;
                case Keys.K_ESCAPE or Keys.K_MOUSE2:
                    _subMenu = SubMenu.None;
                    _cursor = 2;
                    PlaySound(_sfxBack);
                    return true;
            }
        }
        return true;
    }

    private void ActivateItem(int index)
    {
        PlaySound(_sfxSelect);

        switch (index)
        {
            case 0: // RESUME
                CloseMenu();
                break;
            case 1: // ADD BOT
                _subMenu = SubMenu.AddBot;
                _cursor = 0;
                break;
            case 2: // REMOVE BOT
                _subMenu = SubMenu.RemoveBot;
                _cursor = 0;
                break;
            case 3: // RESTART MAP
                Syscalls.ExecuteCommand("map_restart 0\n");
                CloseMenu();
                break;
            case 4: // CONSOLE
                CloseMenu();
                Syscalls.ExecuteCommand("toggleconsole\n");
                break;
            case 5: // DISCONNECT
                _confirm = ConfirmMode.Disconnect;
                _cursor = 1;
                break;
            case 6: // QUIT
                _confirm = ConfirmMode.Quit;
                _cursor = 1;
                break;
        }
    }

    private void CloseMenu()
    {
        _active = false;
        _confirm = ConfirmMode.None;
        _subMenu = SubMenu.None;
        Syscalls.CvarSet("cl_paused", "0");
        Syscalls.Key_SetCatcher(Syscalls.Key_GetCatcher() & ~KEYCATCH_UI);
    }

    public bool MouseEvent(int dx, int dy)
    {
        if (!_active) return false;

        _cursorX = Math.Clamp(_cursorX + dx, 0, Drawing.SCREEN_W);
        _cursorY = Math.Clamp(_cursorY + dy, 0, Drawing.SCREEN_H);

        if (_confirm != ConfirmMode.None)
        {
            float yYes = ITEM_START_Y + 60;
            float yNo = ITEM_START_Y + 88;
            HitTest(yYes, "YES", 14f, 0);
            HitTest(yNo, "NO", 14f, 1);
        }
        else if (_subMenu == SubMenu.None)
        {
            for (int i = 0; i < Items.Length; i++)
            {
                float y = ITEM_START_Y + i * ITEM_SPACING;
                HitTest(y, Items[i], ITEM_CHAR_W, i);
            }
        }

        return true;
    }

    public int IsFullscreen() => _active ? 0 : -1;
    public bool ConsoleCommand(int realtime) => false;
    public bool DrawConnectScreen(int overlay) => false;

    // --- helpers ---

    private void HitTest(float y, string label, float charW, int index)
    {
        float textW = Drawing.MeasureString(label, charW);
        float x = (Drawing.SCREEN_W - textW) * 0.5f;
        if (_cursorX >= x - 12 && _cursorX <= x + textW + 12 &&
            _cursorY >= y - 6 && _cursorY <= y + charW + 6)
        {
            if (_cursor != index) { _cursor = index; PlaySound(_sfxMove); }
        }
    }

    private static void DrawCentered(float y, string text, float charW, float charH)
    {
        string stripped = Drawing.StripColors(text);
        float x = (Drawing.SCREEN_W - stripped.Length * charW) * 0.5f;
        Drawing.DrawString(x, y, text, charW, charH);
    }

    private static void DrawBorder(float x, float y, float w, float h,
        float r, float g, float b, float a)
    {
        Drawing.SetColor(r, g, b, a);
        Drawing.FillRect(x, y, w, 2);
        Drawing.FillRect(x, y + h - 2, w, 2);
        Drawing.FillRect(x, y, 2, h);
        Drawing.FillRect(x + w - 2, y, 2, h);
    }

    private void PlaySound(int sfx)
    {
        if (sfx > 0) Syscalls.S_StartLocalSound(sfx, 1);
    }
}
