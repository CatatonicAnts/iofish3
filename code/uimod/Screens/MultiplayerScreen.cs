namespace UiMod;

/// <summary>
/// Multiplayer menu: Connect to a server by IP/hostname, or browse recent servers.
/// </summary>
public class MultiplayerScreen : MenuScreen
{
    private const float PANEL_X = 80f;
    private const float PANEL_Y = 60f;
    private const float PANEL_W = 480f;
    private const float PANEL_H = 360f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 70f;

    private TextInputWidget? _addressField;

    public MultiplayerScreen(MenuSystem system) : base(system)
    {
        Title = "MULTIPLAYER";

        float y = START_Y;

        _addressField = new TextInputWidget("Server Address", FIELD_X, y,
            Syscalls.CvarGetString("ui_lastServer"), 64);
        Widgets.Add(_addressField);
        y += 30;

        Widgets.Add(new ButtonWidget("CONNECT", FIELD_X, y, () =>
        {
            string addr = _addressField!.Text.Trim();
            if (addr.Length > 0)
            {
                Syscalls.CvarSet("ui_lastServer", addr);
                System.PlaySound(MenuSystem.SFX_SELECT);
                Syscalls.ExecuteCommand($"connect {addr}\n");
            }
        }));
        y += 40;

        // Separator
        Widgets.Add(new LabelWidget("--- QUICK START ---", FIELD_X, y));
        y += 20;

        // Quick play buttons for common maps
        string[] maps = ["q3dm1", "q3dm6", "q3dm7", "q3dm17", "q3tourney2"];
        foreach (string map in maps)
        {
            string m = map;
            Widgets.Add(new ButtonWidget(map.ToUpperInvariant(), FIELD_X, y, () =>
            {
                System.PlaySound(MenuSystem.SFX_SELECT);
                Syscalls.ExecuteCommand($"devmap {m}\n");
            }) { CharW = 10f, CharH = 12f });
            y += 24;
        }

        y += 10;
        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        // Section heading
        Drawing.SetColor(0.6f, 0.6f, 0.6f, 0.5f);
        Drawing.DrawString(FIELD_X, START_Y - 20, "Enter server address or pick a map:", 8f, 10f);

        base.Draw(realtime);
        DrawFooterHint("ENTER to select  |  ESC to go back  |  LEFT/RIGHT to edit");
    }

    public override bool HandleKey(int key, int realtime)
    {
        // Let text input handle chars first
        if (_addressField != null && CursorIndex == 0)
        {
            // Forward all keys to the widget when it's focused
        }
        return base.HandleKey(key, realtime);
    }
}
