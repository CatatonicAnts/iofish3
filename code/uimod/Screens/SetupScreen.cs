namespace UiMod;

/// <summary>
/// Setup menu hub: links to Player, Controls, Video, Sound, Game Options.
/// </summary>
public class SetupScreen : MenuScreen
{
    private const float ITEM_START_Y = 140f;
    private const float ITEM_SPACING = 36f;
    private const float CHAR_W = 14f;
    private const float CHAR_H = 14f;

    public SetupScreen(MenuSystem system) : base(system)
    {
        Title = "SETUP";

        float y = ITEM_START_Y;
        Widgets.Add(ButtonWidget.CreateCentered("PLAYER", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new PlayerSettingsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("CONTROLS", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new ControlsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("VIDEO", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new VideoSettingsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("SOUND", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new SoundSettingsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("GAME OPTIONS", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new GameOptionsScreen(System)); }));
        y += ITEM_SPACING;
        Widgets.Add(ButtonWidget.CreateCentered("NETWORK", y, CHAR_W, CHAR_H,
            () => { System.PlaySound(MenuSystem.SFX_SELECT); System.Push(new NetworkSettingsScreen(System)); }));
        y += ITEM_SPACING + 10;
        Widgets.Add(ButtonWidget.CreateCentered("BACK", y, 12f, 12f,
            () => System.Pop()));
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawTitle(Title);

        Drawing.SetColor(0.5f, 0.15f, 0.0f, 0.25f);
        Drawing.FillRect(220, ITEM_START_Y - 14, 200, 1);

        base.Draw(realtime);
        DrawFooterHint("ENTER to select  |  ESC to go back");
    }
}
