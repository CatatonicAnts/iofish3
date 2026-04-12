namespace UiMod;

/// <summary>
/// Start Server menu: Select map, gametype, and settings to create a local server.
/// </summary>
public class StartServerScreen : MenuScreen
{
    private const float PANEL_X = 80f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 480f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 60f;

    private static readonly string[] GameTypes = ["Free For All", "Tournament", "Team Deathmatch", "CTF"];
    private static readonly string[] GameTypeCmds = ["0", "1", "3", "4"];

    private static readonly string[] BotCounts = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "11", "15"];
    private static readonly string[] BotSkills = ["1 - I Can Win", "2 - Bring It On", "3 - Hurt Me Plenty", "4 - Hardcore", "5 - Nightmare"];
    private static readonly string[] FragLimits = ["0 (No Limit)", "5", "10", "15", "20", "25", "30", "50"];
    private static readonly string[] TimeLimits = ["0 (No Limit)", "5", "10", "15", "20", "25", "30"];

    private SpinWidget _mapSpin = null!;
    private SpinWidget _gameTypeSpin = null!;
    private SpinWidget _botCountSpin = null!;
    private SpinWidget _botSkillSpin = null!;
    private SpinWidget _fragLimitSpin = null!;
    private SpinWidget _timeLimitSpin = null!;

    private string[] _maps = [];

    public StartServerScreen(MenuSystem system) : base(system)
    {
        Title = "START SERVER";

        // Discover maps dynamically
        _maps = DiscoverMaps();

        float y = START_Y;

        _mapSpin = new SpinWidget("Map", FIELD_X, y, _maps, FindIndex(_maps, "q3dm17"));
        Widgets.Add(_mapSpin);
        y += 26;

        _gameTypeSpin = new SpinWidget("Game Type", FIELD_X, y, GameTypes, 0);
        Widgets.Add(_gameTypeSpin);
        y += 26;

        _fragLimitSpin = new SpinWidget("Frag Limit", FIELD_X, y, FragLimits, 3);
        Widgets.Add(_fragLimitSpin);
        y += 26;

        _timeLimitSpin = new SpinWidget("Time Limit", FIELD_X, y, TimeLimits, 2);
        Widgets.Add(_timeLimitSpin);
        y += 26;

        _botCountSpin = new SpinWidget("Bot Count", FIELD_X, y, BotCounts, 3);
        Widgets.Add(_botCountSpin);
        y += 26;

        _botSkillSpin = new SpinWidget("Bot Skill", FIELD_X, y, BotSkills, 2);
        Widgets.Add(_botSkillSpin);
        y += 40;

        Widgets.Add(new ButtonWidget("START GAME", FIELD_X, y, StartGame));
        y += 30;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    private static string[] DiscoverMaps()
    {
        var files = Syscalls.FS_GetFileList("maps", ".bsp");
        if (files.Length == 0)
            return ["q3dm1", "q3dm6", "q3dm7", "q3dm17"];

        var maps = new List<string>(files.Length);
        foreach (var f in files)
        {
            string name = f;
            if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.Length > 0)
                maps.Add(name);
        }
        maps.Sort(StringComparer.OrdinalIgnoreCase);
        return maps.Count > 0 ? maps.ToArray() : ["q3dm1"];
    }

    private void StartGame()
    {
        System.PlaySound(MenuSystem.SFX_SELECT);

        string map = _maps[_mapSpin.SelectedIndex];
        string gt = GameTypeCmds[_gameTypeSpin.SelectedIndex];
        string frag = FragLimits[_fragLimitSpin.SelectedIndex].Split(' ')[0];
        string time = TimeLimits[_timeLimitSpin.SelectedIndex].Split(' ')[0];
        int botCount = int.Parse(BotCounts[_botCountSpin.SelectedIndex]);
        int botSkill = _botSkillSpin.SelectedIndex + 1;

        Syscalls.ExecuteCommand($"g_gametype {gt}\n");
        Syscalls.ExecuteCommand($"fraglimit {frag}\n");
        Syscalls.ExecuteCommand($"timelimit {time}\n");
        Syscalls.ExecuteCommand($"bot_minplayers 0\n");
        Syscalls.ExecuteCommand($"map {map}\n");

        // Add bots after map loads
        string[] bots = ["sarge", "grunt", "major", "visor", "slash", "razor",
                         "keel", "lucy", "tankjr", "bitterman", "xaero", "uriel",
                         "hunter", "klesk", "anarki", "orbb"];

        for (int i = 0; i < botCount && i < bots.Length; i++)
        {
            Syscalls.ExecuteCommand($"addbot {bots[i]} {botSkill}\n");
        }
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        base.Draw(realtime);
        DrawFooterHint("LEFT/RIGHT to change  |  ENTER to start  |  ESC to go back");
    }

    private static int FindIndex(string[] arr, string val)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == val) return i;
        return 0;
    }
}
