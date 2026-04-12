namespace UiMod;

/// <summary>
/// Main menu (UIMENU_MAIN). Uses a MenuSystem screen stack to provide
/// the main menu and all submenus (Multiplayer, Start Server, Setup, etc.).
/// </summary>
public class MainMenuMod : IUiMod
{
    public string Name => "Main Menu";

    private const int UIMENU_MAIN = 1;
    private const int KEYCATCH_UI = 0x0002;

    private bool _active;
    private float _cursorX = 320f;
    private float _cursorY = 240f;
    private int _realtime;

    private int _cursorShader;
    private readonly MenuSystem _menuSystem = new();

    public void Init()
    {
        _cursorShader = Syscalls.R_RegisterShaderNoMip("menu/art/3_cursor2");
        _menuSystem.Init();
    }

    public void Shutdown()
    {
        _active = false;
        _menuSystem.Clear();
    }

    public bool SetActiveMenu(int menu)
    {
        if (menu == UIMENU_MAIN)
        {
            _active = true;
            _cursorX = 320f;
            _cursorY = 240f;

            Syscalls.CvarSet("sv_killserver", "1");
            Syscalls.Key_SetCatcher(Syscalls.Key_GetCatcher() | KEYCATCH_UI);

            _menuSystem.Clear();
            _menuSystem.Push(new MainMenuScreen(_menuSystem));
            return true;
        }

        if (_active) { _active = false; _menuSystem.Clear(); }
        return false;
    }

    public bool Refresh(int realtime)
    {
        if (!_active) return false;
        _realtime = realtime;

        var screen = _menuSystem.CurrentScreen;
        if (screen == null) { _active = false; return false; }

        screen.Draw(realtime);

        // Cursor
        Drawing.ClearColor();
        if (_cursorShader != 0)
            Drawing.DrawPic(_cursorX - 16, _cursorY - 16, 32, 32, _cursorShader);

        return true;
    }

    public bool KeyEvent(int key, int down)
    {
        if (!_active) return false;
        if (down == 0) return true;

        var screen = _menuSystem.CurrentScreen;
        if (screen == null) return true;

        // ESC on root screen → quit confirm
        if ((key == Keys.K_ESCAPE || key == Keys.K_MOUSE2) && screen is MainMenuScreen)
        {
            _menuSystem.Push(new QuitConfirmScreen(_menuSystem));
            return true;
        }

        screen.HandleKey(key, _realtime);

        // If all screens popped, deactivate
        if (!_menuSystem.HasScreens) _active = false;

        return true;
    }

    public bool MouseEvent(int dx, int dy)
    {
        if (!_active) return false;

        _cursorX = Math.Clamp(_cursorX + dx, 0, Drawing.SCREEN_W);
        _cursorY = Math.Clamp(_cursorY + dy, 0, Drawing.SCREEN_H);

        _menuSystem.CurrentScreen?.HandleMouse(_cursorX, _cursorY);
        return true;
    }

    public int IsFullscreen() => _active ? 1 : -1;
    public bool ConsoleCommand(int realtime) => false;
    public bool DrawConnectScreen(int overlay) => false;
}
