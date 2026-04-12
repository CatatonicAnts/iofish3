namespace UiMod;

/// <summary>
/// Server browser: scan LAN/Internet for Q3 servers, display list with info, connect.
/// </summary>
public class ServerBrowserScreen : MenuScreen
{
    private const float PANEL_X = 20f;
    private const float PANEL_Y = 30f;
    private const float PANEL_W = 600f;
    private const float PANEL_H = 420f;
    private const float FIELD_X = PANEL_X + 15f;
    private const float START_Y = PANEL_Y + 60f;

    // Server sources: 0=local, 1=internet (master 0)
    private const int SOURCE_LOCAL = 0;
    private const int SOURCE_INTERNET = 1;

    private int _source = SOURCE_LOCAL;
    private ListWidget _list;
    private SpinWidget _sourceSpin = null!;
    private bool _refreshing;
    private int _lastRefreshTime;

    private static readonly string[] SourceOpts = ["LAN", "Internet"];

    public ServerBrowserScreen(MenuSystem system) : base(system)
    {
        Title = "SERVER BROWSER";

        float y = START_Y - 26;
        _sourceSpin = new SpinWidget("Source", FIELD_X, y, SourceOpts, _source, idx =>
        {
            _source = idx == 0 ? SOURCE_LOCAL : SOURCE_INTERNET;
        });
        Widgets.Add(_sourceSpin);

        _list = new ListWidget(
            ["Hostname", "Map", "Players", "Ping", "Type"],
            [220f, 100f, 60f, 50f, 60f],
            FIELD_X, START_Y, PANEL_W - 40, 250,
            OnSelect);
        Widgets.Add(_list);

        y = START_Y + 260;
        Widgets.Add(new ButtonWidget("REFRESH", FIELD_X, y, StartRefresh) { CharW = 10f, CharH = 12f });
        Widgets.Add(new ButtonWidget("CONNECT", FIELD_X + 120, y, ConnectSelected) { CharW = 10f, CharH = 12f });
        y += 30;

        // Manual connect
        var addressField = new TextInputWidget("Server Address", FIELD_X, y,
            Syscalls.CvarGetString("ui_lastServer"), 64);
        Widgets.Add(addressField);
        y += 30;

        Widgets.Add(new ButtonWidget("CONNECT TO ADDRESS", FIELD_X, y, () =>
        {
            string addr = addressField.Text.Trim();
            if (addr.Length > 0)
            {
                Syscalls.CvarSet("ui_lastServer", addr);
                System.PlaySound(MenuSystem.SFX_SELECT);
                Syscalls.ExecuteCommand($"connect {addr}\n");
            }
        }) { CharW = 9f, CharH = 11f });
        y += 30;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop()) { CharW = 12f, CharH = 12f });
    }

    private void StartRefresh()
    {
        System.PlaySound(MenuSystem.SFX_SELECT);
        Syscalls.LAN_ResetPings(_source);
        _refreshing = true;
        _lastRefreshTime = 0;
    }

    public override void Draw(int realtime)
    {
        // Update visible pings while refreshing
        if (_refreshing)
        {
            if (_lastRefreshTime == 0 || realtime - _lastRefreshTime > 500)
            {
                int stillPinging = Syscalls.LAN_UpdateVisiblePings(_source);
                UpdateServerList();
                _lastRefreshTime = realtime;
                if (stillPinging == 0 && _lastRefreshTime != 0)
                    _refreshing = false;
            }
        }

        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        if (_refreshing)
        {
            Drawing.SetColor(1f, 1f, 0f, 0.8f);
            Drawing.DrawString(PANEL_X + PANEL_W - 150, PANEL_Y + 12, "Refreshing...", 7f, 10f);
        }

        base.Draw(realtime);
        DrawFooterHint("ENTER to connect  |  LEFT/RIGHT source  |  ESC to go back");
    }

    private void UpdateServerList()
    {
        int count = Syscalls.LAN_GetServerCount(_source);
        var items = new List<string[]>();

        for (int i = 0; i < count && i < 200; i++)
        {
            string info = Syscalls.LAN_GetServerInfo(_source, i);
            if (info.Length == 0) continue;

            string hostname = InfoValueForKey(info, "hostname");
            string mapname = InfoValueForKey(info, "mapname");
            string clients = InfoValueForKey(info, "clients");
            string maxclients = InfoValueForKey(info, "sv_maxclients");
            string gametype = InfoValueForKey(info, "gametype");
            int ping = Syscalls.LAN_GetServerPing(_source, i);

            string players = $"{clients}/{maxclients}";
            string gt = gametype switch
            {
                "0" => "FFA",
                "1" => "1v1",
                "3" => "TDM",
                "4" => "CTF",
                _ => gametype
            };

            items.Add([
                hostname.Length > 0 ? hostname : "???",
                mapname,
                players,
                ping > 0 ? ping.ToString() : "?",
                gt
            ]);
        }

        int prevSel = _list.SelectedIndex;
        _list.SetItems(items);
        if (prevSel >= 0 && prevSel < items.Count)
            _list.SelectedIndex = prevSel;
    }

    private void OnSelect(int idx) => ConnectToServer(idx);

    private void ConnectSelected()
    {
        if (_list.SelectedIndex >= 0)
            ConnectToServer(_list.SelectedIndex);
    }

    private void ConnectToServer(int idx)
    {
        string addr = Syscalls.LAN_GetServerAddressString(_source, idx);
        if (addr.Length > 0)
        {
            System.PlaySound(MenuSystem.SFX_SELECT);
            Syscalls.ExecuteCommand($"connect {addr}\n");
        }
    }

    public override bool HandleKey(int key, int realtime)
    {
        // 'R' to refresh
        if (key == 'r' || key == 'R')
        {
            StartRefresh();
            return true;
        }
        return base.HandleKey(key, realtime);
    }

    /// <summary>Parse Q3 info string (\key\value\key2\value2) for a specific key.</summary>
    private static string InfoValueForKey(string info, string key)
    {
        if (info.Length == 0) return "";
        int idx = 0;
        while (idx < info.Length)
        {
            // Skip leading backslash
            if (info[idx] == '\\') idx++;

            // Read key
            int keyStart = idx;
            while (idx < info.Length && info[idx] != '\\') idx++;
            string k = info[keyStart..idx];

            // Skip separator
            if (idx < info.Length && info[idx] == '\\') idx++;

            // Read value
            int valStart = idx;
            while (idx < info.Length && info[idx] != '\\') idx++;
            string v = info[valStart..idx];

            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return "";
    }
}
