namespace UiMod;

/// <summary>
/// Mods screen: lists available game directories (mods) and lets the player switch.
/// </summary>
public class ModsScreen : MenuScreen
{
    private const float PANEL_X = 60f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 520f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 20f;
    private const float START_Y = PANEL_Y + 60f;

    private ListWidget _list;
    private string[] _mods = [];

    public ModsScreen(MenuSystem system) : base(system)
    {
        Title = "MODS";

        _list = new ListWidget(
            ["Mod Directory"],
            [PANEL_W - 80],
            FIELD_X, START_Y, PANEL_W - 60, 260,
            OnSelect);
        Widgets.Add(_list);

        float y = START_Y + 270;
        Widgets.Add(new ButtonWidget("ACTIVATE", FIELD_X, y, ActivateSelected) { CharW = 12f, CharH = 12f });
        y += 30;
        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop()) { CharW = 12f, CharH = 12f });

        RefreshList();
    }

    private void RefreshList()
    {
        // FS_GetFileList with "$modlist" extension returns mod directories
        _mods = Syscalls.FS_GetFileList("", "$modlist");

        var items = new List<string[]>(_mods.Length);
        // Always include baseq3 at the top
        items.Add(["baseq3 (default)"]);

        // Parse modlist: pairs of (dir, description)
        for (int i = 0; i + 1 < _mods.Length; i += 2)
        {
            string dir = _mods[i];
            string desc = _mods[i + 1];
            items.Add([desc.Length > 0 ? $"{dir} - {desc}" : dir]);
        }
        _list.SetItems(items);
    }

    private void OnSelect(int idx) => ActivateMod(idx);

    private void ActivateSelected()
    {
        if (_list.SelectedIndex >= 0)
            ActivateMod(_list.SelectedIndex);
    }

    private void ActivateMod(int idx)
    {
        System.PlaySound(MenuSystem.SFX_SELECT);

        if (idx == 0)
        {
            // Default baseq3
            Syscalls.CvarSet("fs_game", "");
            Syscalls.ExecuteCommand("vid_restart\n");
        }
        else
        {
            // Mod directory — adjust for the extra baseq3 entry
            int modIdx = (idx - 1) * 2;
            if (modIdx >= 0 && modIdx < _mods.Length)
            {
                Syscalls.CvarSet("fs_game", _mods[modIdx]);
                Syscalls.ExecuteCommand("vid_restart\n");
            }
        }
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        base.Draw(realtime);
        DrawFooterHint("UP/DOWN to select  |  ENTER to activate  |  ESC to go back");
    }
}
