namespace UiMod;

/// <summary>
/// Demo browser: lists available .dm_68 demo files and allows playback.
/// </summary>
public class DemosScreen : MenuScreen
{
    private const float PANEL_X = 60f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 520f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 20f;
    private const float START_Y = PANEL_Y + 60f;

    private ListWidget _list;
    private string[] _demoFiles = [];

    public DemosScreen(MenuSystem system) : base(system)
    {
        Title = "DEMOS";

        _list = new ListWidget(
            ["Demo Name", ""],
            [PANEL_W - 80, 0],
            FIELD_X, START_Y, PANEL_W - 60, 260,
            OnSelect);
        Widgets.Add(_list);

        float y = START_Y + 270;
        Widgets.Add(new ButtonWidget("PLAY", FIELD_X, y, PlaySelected) { CharW = 12f, CharH = 12f });
        y += 30;
        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop()) { CharW = 12f, CharH = 12f });

        RefreshList();
    }

    private void RefreshList()
    {
        // Q3 demo extension varies by protocol; try common ones
        _demoFiles = Syscalls.FS_GetFileList("demos", ".dm_68");
        if (_demoFiles.Length == 0)
            _demoFiles = Syscalls.FS_GetFileList("demos", ".dm_67");
        if (_demoFiles.Length == 0)
            _demoFiles = Syscalls.FS_GetFileList("demos", ".dm_66");

        var items = new List<string[]>(_demoFiles.Length);
        foreach (var f in _demoFiles)
        {
            string name = f;
            // Strip extension for display
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
            items.Add([name]);
        }
        _list.SetItems(items);
    }

    private void OnSelect(int idx)
    {
        PlayDemo(idx);
    }

    private void PlaySelected()
    {
        if (_list.SelectedIndex >= 0)
            PlayDemo(_list.SelectedIndex);
    }

    private void PlayDemo(int idx)
    {
        if (idx < 0 || idx >= _demoFiles.Length) return;
        string name = _demoFiles[idx];
        // Strip extension — demo command uses name without extension
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        System.PlaySound(MenuSystem.SFX_SELECT);
        Syscalls.ExecuteCommand($"demo {name}\n");
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        if (_demoFiles.Length == 0)
        {
            Drawing.SetColor(0.5f, 0.5f, 0.5f, 0.7f);
            Drawing.DrawStringCentered(START_Y + 100, "No demos found", 10f, 12f);
        }

        base.Draw(realtime);
        DrawFooterHint("UP/DOWN to select  |  ENTER to play  |  ESC to go back");
    }
}
