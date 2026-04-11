namespace UiMod;

/// <summary>
/// Main menu (UIMENU_MAIN). Draws a fullscreen menu with Play, Setup, Quit.
/// Intercepts SetActiveMenu(UIMENU_MAIN) and handles all drawing/input.
/// </summary>
public class MainMenuMod : IUiMod
{
    public string Name => "Main Menu";

    private const int UIMENU_NONE = 0;
    private const int UIMENU_MAIN = 1;
    private const int KEYCATCH_UI = 0x0002;
    private const int CHAN_LOCAL_SOUND = 1;

    // Key codes (from keycodes.h enum values)
    private const int K_ENTER = 13;
    private const int K_ESCAPE = 27;
    private const int K_UPARROW = 132;
    private const int K_DOWNARROW = 133;
    private const int K_KP_UPARROW = 161;
    private const int K_KP_DOWNARROW = 167;
    private const int K_KP_ENTER = 169;
    private const int K_MOUSE1 = 178;
    private const int K_MOUSE2 = 179;

    // State
    private bool _active;
    private int _cursor;
    private float _cursorX = 320f;
    private float _cursorY = 240f;
    private int _realtime;
    private bool _confirmQuit;

    // Shaders & sounds
    private int _cursorShader;
    private int _sfxMove;
    private int _sfxSelect;
    private int _sfxBack;

    // Menu items
    private static readonly MenuItem[] Items =
    {
        new("PLAY",    "devmap q3dm17\n"),
        new("SETUP",   "toggleconsole\n"),
        new("QUIT",    null),
    };

    // Layout
    private const float TITLE_Y = 100f;
    private const float TITLE_CHAR = 24f;
    private const float SUBTITLE_Y = 135f;
    private const float ITEM_START_Y = 220f;
    private const float ITEM_SPACING = 42f;
    private const float ITEM_CHAR_W = 16f;
    private const float ITEM_CHAR_H = 16f;
    private const float BAR_W = 260f;
    private const float FOOTER_Y = 456f;

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
        if (menu == UIMENU_MAIN)
        {
            _active = true;
            _cursor = 0;
            _confirmQuit = false;
            _cursorX = 320f;
            _cursorY = 240f;

            Syscalls.CvarSet("sv_killserver", "1");
            Syscalls.Key_SetCatcher(Syscalls.Key_GetCatcher() | KEYCATCH_UI);
            return true;
        }

        if (_active) { _active = false; _confirmQuit = false; }
        return false;
    }

    public bool Refresh(int realtime)
    {
        if (!_active) return false;
        _realtime = realtime;

        // Solid dark background
        Drawing.SetColor(0.02f, 0.02f, 0.04f, 1.0f);
        Drawing.FillRect(0, 0, Drawing.SCREEN_W, Drawing.SCREEN_H);

        // Subtle top accent bar
        Drawing.SetColor(0.6f, 0.15f, 0.0f, 0.4f);
        Drawing.FillRect(0, 0, Drawing.SCREEN_W, 3);

        // Title
        Drawing.SetColor(0, 0, 0, 0.6f);
        DrawCenteredText(TITLE_Y + 2, "QUAKE III ARENA", TITLE_CHAR, TITLE_CHAR);
        Drawing.SetColor(0.85f, 0.25f, 0.0f, 1.0f);
        DrawCenteredText(TITLE_Y, "QUAKE III ARENA", TITLE_CHAR, TITLE_CHAR);

        // Subtitle
        Drawing.SetColor(0.45f, 0.45f, 0.45f, 0.6f);
        DrawCenteredText(SUBTITLE_Y, "iofish3", 8f, 12f);

        if (_confirmQuit)
            DrawQuitConfirm();
        else
            DrawItems();

        // Footer
        Drawing.SetColor(0.25f, 0.25f, 0.25f, 0.4f);
        DrawCenteredText(FOOTER_Y, "(c) 1999-2000 id Software", 6f, 10f);

        // Cursor
        Drawing.ClearColor();
        if (_cursorShader != 0)
            Drawing.DrawPic(_cursorX, _cursorY, 32, 32, _cursorShader);

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
                Drawing.SetColor(0.7f, 0.2f, 0.0f, 0.2f);
                Drawing.FillRect(barX, y - 4, BAR_W, ITEM_CHAR_H + 8);

                float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);
                Drawing.SetColor(1.0f, pulse, pulse * 0.25f, 1.0f);
            }
            else
            {
                Drawing.SetColor(0.65f, 0.15f, 0.0f, 0.75f);
            }

            DrawCenteredText(y, Items[i].Label, ITEM_CHAR_W, ITEM_CHAR_H);
        }
    }

    private void DrawQuitConfirm()
    {
        // Dimmed panel
        Drawing.SetColor(0.0f, 0.0f, 0.0f, 0.4f);
        Drawing.FillRect(170, 200, 300, 100);

        Drawing.SetColor(0, 0, 0, 0.7f);
        DrawCenteredText(217, "EXIT GAME?", 16f, 16f);
        Drawing.SetColor(1.0f, 1.0f, 1.0f, 1.0f);
        DrawCenteredText(215, "EXIT GAME?", 16f, 16f);

        float pulse = 0.7f + 0.3f * MathF.Sin(_realtime * 0.004f);

        Drawing.SetColor(_cursor == 0 ? 1.0f : 0.4f, _cursor == 0 ? pulse : 0.4f, 0f, 1f);
        DrawCenteredText(255, "YES", 16f, 16f);

        Drawing.SetColor(_cursor == 1 ? 1.0f : 0.4f, _cursor == 1 ? pulse : 0.4f, 0f, 1f);
        DrawCenteredText(280, "NO", 16f, 16f);
    }

    public bool KeyEvent(int key, int down)
    {
        if (!_active) return false;
        if (down == 0) return true;

        if (_confirmQuit) return HandleConfirmKey(key);

        switch (key)
        {
            case K_UPARROW or K_KP_UPARROW:
                _cursor = (_cursor - 1 + Items.Length) % Items.Length;
                PlaySound(_sfxMove);
                return true;

            case K_DOWNARROW or K_KP_DOWNARROW:
                _cursor = (_cursor + 1) % Items.Length;
                PlaySound(_sfxMove);
                return true;

            case K_ENTER or K_KP_ENTER or K_MOUSE1:
                ActivateItem(_cursor);
                return true;

            case K_ESCAPE or K_MOUSE2:
                _confirmQuit = true;
                _cursor = 1; // default NO
                PlaySound(_sfxBack);
                return true;
        }

        return true;
    }

    private bool HandleConfirmKey(int key)
    {
        switch (key)
        {
            case K_UPARROW or K_KP_UPARROW or K_DOWNARROW or K_KP_DOWNARROW:
                _cursor = _cursor == 0 ? 1 : 0;
                PlaySound(_sfxMove);
                return true;

            case K_ENTER or K_KP_ENTER or K_MOUSE1:
                if (_cursor == 0) Syscalls.ExecuteCommand("quit\n");
                else { _confirmQuit = false; _cursor = 0; PlaySound(_sfxBack); }
                return true;

            case K_ESCAPE or K_MOUSE2:
                _confirmQuit = false;
                _cursor = 0;
                PlaySound(_sfxBack);
                return true;
        }
        return true;
    }

    private void ActivateItem(int index)
    {
        PlaySound(_sfxSelect);
        var item = Items[index];

        if (item.Command != null)
            Syscalls.ExecuteCommand(item.Command);
        else if (item.Label == "QUIT")
        {
            _confirmQuit = true;
            _cursor = 1;
        }
    }

    public bool MouseEvent(int dx, int dy)
    {
        if (!_active) return false;

        _cursorX = Math.Clamp(_cursorX + dx, 0, Drawing.SCREEN_W);
        _cursorY = Math.Clamp(_cursorY + dy, 0, Drawing.SCREEN_H);

        if (_confirmQuit)
        {
            HitTestItem(255f, "YES", 16f, 0);
            HitTestItem(280f, "NO", 16f, 1);
        }
        else
        {
            for (int i = 0; i < Items.Length; i++)
            {
                float y = ITEM_START_Y + i * ITEM_SPACING;
                HitTestItem(y, Items[i].Label, ITEM_CHAR_W, i);
            }
        }

        return true;
    }

    public int IsFullscreen() => _active ? 1 : -1;
    public bool ConsoleCommand(int realtime) => false;
    public bool DrawConnectScreen(int overlay) => false;

    // --- helpers ---

    private void HitTestItem(float y, string label, float charW, int index)
    {
        float textW = Drawing.MeasureString(label, charW);
        float x = (Drawing.SCREEN_W - textW) * 0.5f;
        if (_cursorX >= x - 12 && _cursorX <= x + textW + 12 &&
            _cursorY >= y - 6 && _cursorY <= y + charW + 6)
        {
            if (_cursor != index) { _cursor = index; PlaySound(_sfxMove); }
        }
    }

    private static void DrawCenteredText(float y, string text, float charW, float charH)
    {
        string stripped = Drawing.StripColors(text);
        float x = (Drawing.SCREEN_W - stripped.Length * charW) * 0.5f;
        Drawing.DrawString(x, y, text, charW, charH);
    }

    private void PlaySound(int sfx)
    {
        if (sfx > 0) Syscalls.S_StartLocalSound(sfx, CHAN_LOCAL_SOUND);
    }

    private record struct MenuItem(string Label, string? Command);
}
