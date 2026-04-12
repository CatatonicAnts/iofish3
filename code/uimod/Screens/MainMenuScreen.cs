namespace UiMod;

/// <summary>
/// Root main menu screen with all Q3 menu buttons.
/// </summary>
public class MainMenuScreen : MenuScreen
{
    private const float TITLE_Y = 80f;
    private const float SUBTITLE_Y = 115f;
    private const float ITEM_START_Y = 175f;
    private const float ITEM_SPACING = 36f;
    private const float ITEM_CHAR_W = 14f;
    private const float ITEM_CHAR_H = 14f;
    private const float FOOTER_Y = 456f;

    public MainMenuScreen(MenuSystem system) : base(system)
    {
        float y = ITEM_START_Y;
        Widgets.Add(ButtonWidget.CreateCentered("SERVER BROWSER", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new ServerBrowserScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("START SERVER", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new StartServerScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("SETUP", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new SetupScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("DEMOS", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new DemosScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("MODS", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new ModsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("CONSOLE", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); Syscalls.ExecuteCommand("toggleconsole\n"); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("QUIT", y, ITEM_CHAR_W, ITEM_CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new QuitConfirmScreen(System)); }));
    }

    public override bool HandleKey(int key, int realtime)
    {
        // Don't let base class pop on ESC — MainMenuMod handles that
        if (key == Keys.K_ESCAPE || key == Keys.K_MOUSE2) return true;
        return base.HandleKey(key, realtime);
    }

    public override void Draw(int realtime)
    {
        DrawBackground();

        // Title
        Drawing.SetColor(0, 0, 0, 0.6f);
        Drawing.DrawStringCentered(TITLE_Y + 2, "QUAKE III ARENA", 22f, 22f);
        Drawing.SetColor(0.85f, 0.25f, 0.0f, 1.0f);
        Drawing.DrawStringCentered(TITLE_Y, "QUAKE III ARENA", 22f, 22f);

        // Subtitle
        Drawing.SetColor(0.45f, 0.45f, 0.45f, 0.6f);
        Drawing.DrawStringCentered(SUBTITLE_Y, "iofish3", 8f, 12f);

        // Separator
        Drawing.SetColor(0.5f, 0.15f, 0.0f, 0.25f);
        Drawing.FillRect(220, ITEM_START_Y - 14, 200, 1);

        base.Draw(realtime);

        // Footer
        Drawing.SetColor(0.25f, 0.25f, 0.25f, 0.4f);
        Drawing.DrawStringCentered(FOOTER_Y, "(c) 1999-2000 id Software", 6f, 10f);

        // Error message (com_errorMessage) if set
        string err = Syscalls.CvarGetString("com_errorMessage");
        if (!string.IsNullOrEmpty(err))
        {
            Drawing.SetColor(0, 0, 0, 0.7f);
            Drawing.FillRect(100, 420, 440, 30);
            Drawing.SetColor(1f, 0.3f, 0.3f, 1f);
            Drawing.DrawStringCentered(428, err, 6f, 10f);
        }
    }
}

/// <summary>Quit confirmation dialog overlaid on the main menu.</summary>
public class QuitConfirmScreen : MenuScreen
{
    public QuitConfirmScreen(MenuSystem system) : base(system)
    {
        Widgets.Add(ButtonWidget.CreateCentered("YES", 255, 14f, 14f,
            () => Syscalls.ExecuteCommand("quit\n")));
        Widgets.Add(ButtonWidget.CreateCentered("NO", 280, 14f, 14f,
            () => System.Pop()));
        CursorIndex = 1; // default NO
    }

    public override void Draw(int realtime)
    {
        // Keep parent visible behind — just draw overlay
        Drawing.SetColor(0.0f, 0.0f, 0.0f, 0.5f);
        Drawing.FillRect(0, 0, SCREEN_W, SCREEN_H);

        DrawPanel(170, 200, 300, 100);

        Drawing.SetColor(1.0f, 1.0f, 1.0f, 1.0f);
        Drawing.DrawStringCentered(215, "EXIT GAME?", 14f, 14f);

        base.Draw(realtime);
    }
}
