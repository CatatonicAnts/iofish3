namespace UiMod;

/// <summary>
/// Player settings with 3D model preview, model/skin chooser, name and handicap.
/// </summary>
public class PlayerSettingsScreen : MenuScreen
{
    private const float PANEL_X = 40f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 560f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 20f;
    private const float START_Y = PANEL_Y + 60f;

    // 3D preview viewport (right side of panel)
    private const float PREVIEW_X = 360f;
    private const float PREVIEW_Y = PANEL_Y + 50f;
    private const float PREVIEW_W = 220f;
    private const float PREVIEW_H = 300f;

    private static readonly string[] Handicaps =
        ["None", "95", "90", "85", "80", "75", "70", "65", "60", "55",
         "50", "45", "40", "35", "30", "25", "20", "15", "10", "5"];

    private string[] _models = [];
    private string[] _skins = [];
    private SpinWidget? _modelSpin;
    private SpinWidget? _skinSpin;
    private readonly PlayerModel _playerModel = new();

    public PlayerSettingsScreen(MenuSystem system) : base(system)
    {
        Title = "PLAYER SETTINGS";
        _models = PlayerModel.DiscoverModels();

        string currentModelFull = Syscalls.CvarGetString("model");
        string currentModelName = currentModelFull.Split('/')[0].ToLowerInvariant();
        string currentSkinName = currentModelFull.Contains('/') ? currentModelFull.Split('/')[1] : "default";

        _skins = PlayerModel.DiscoverSkins(currentModelName);

        float y = START_Y;

        string currentName = Syscalls.CvarGetString("name");
        Widgets.Add(new TextInputWidget("Name", FIELD_X, y, currentName, 20, name =>
        {
            Syscalls.CvarSet("name", name);
        }));
        y += 30;

        int handicapIdx = GetHandicapIndex();
        Widgets.Add(new SpinWidget("Handicap", FIELD_X, y, Handicaps, handicapIdx, idx =>
        {
            string val = idx == 0 ? "100" : Handicaps[idx];
            Syscalls.CvarSet("handicap", val);
        }));
        y += 30;

        int modelIdx = IndexOf(_models, currentModelName);
        if (modelIdx < 0) modelIdx = 0;
        _modelSpin = new SpinWidget("Model", FIELD_X, y, _models, modelIdx, OnModelChanged);
        Widgets.Add(_modelSpin);
        y += 30;

        int skinIdx = IndexOf(_skins, currentSkinName);
        if (skinIdx < 0) skinIdx = 0;
        _skinSpin = new SpinWidget("Skin", FIELD_X, y, _skins, skinIdx, OnSkinChanged);
        Widgets.Add(_skinSpin);
        y += 30;

        string[] teamModels = ["default", .. _models];
        Widgets.Add(new SpinWidget("Team Model", FIELD_X, y, teamModels, 0, idx =>
        {
            if (idx > 0)
            {
                Syscalls.CvarSet("team_model", teamModels[idx]);
                Syscalls.CvarSet("team_headmodel", teamModels[idx]);
            }
        }));
        y += 40;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });

        // Load initial model
        _playerModel.Load(currentModelName, currentSkinName);
    }

    private void OnModelChanged(int idx)
    {
        string name = _models[idx];
        _skins = PlayerModel.DiscoverSkins(name);
        _skinSpin?.UpdateOptions(_skins, 0);
        _playerModel.Load(name, _skins.Length > 0 ? _skins[0] : "default");
        ApplyModelCvar();
    }

    private void OnSkinChanged(int idx)
    {
        string skin = idx < _skins.Length ? _skins[idx] : "default";
        _playerModel.SetSkin(skin);
        ApplyModelCvar();
    }

    private void ApplyModelCvar()
    {
        string model = _models[_modelSpin!.SelectedIndex];
        string skin = _skinSpin!.SelectedIndex < _skins.Length ? _skins[_skinSpin.SelectedIndex] : "default";
        string val = skin == "default" ? model : $"{model}/{skin}";
        Syscalls.CvarSet("model", val);
        Syscalls.CvarSet("headmodel", val);
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        // Draw 3D model preview
        _playerModel.Draw(PREVIEW_X, PREVIEW_Y, PREVIEW_W, PREVIEW_H, realtime);

        // Draw preview border
        Drawing.SetColor(0.3f, 0.6f, 0.8f, 0.5f);
        Drawing.FillRect(PREVIEW_X, PREVIEW_Y - 1, PREVIEW_W, 1);
        Drawing.FillRect(PREVIEW_X, PREVIEW_Y + PREVIEW_H, PREVIEW_W, 1);
        Drawing.FillRect(PREVIEW_X - 1, PREVIEW_Y, 1, PREVIEW_H);
        Drawing.FillRect(PREVIEW_X + PREVIEW_W, PREVIEW_Y, 1, PREVIEW_H);
        Drawing.ClearColor();

        base.Draw(realtime);
        DrawFooterHint("ENTER to edit name  |  LEFT/RIGHT to change  |  ESC to go back");
    }

    private static int GetHandicapIndex()
    {
        string val = Syscalls.CvarGetString("handicap");
        if (string.IsNullOrEmpty(val) || val == "100" || val == "0") return 0;
        for (int i = 1; i < Handicaps.Length; i++)
            if (Handicaps[i] == val) return i;
        return 0;
    }

    private static int IndexOf(string[] arr, string value)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == value) return i;
        return -1;
    }
}

/// <summary>
/// Controls/Key bindings screen. Shows current bindings, allows rebinding.
/// </summary>
public class ControlsScreen : MenuScreen
{
    private const float PANEL_X = 60f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 520f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 20f;
    private const float START_Y = PANEL_Y + 60f;

    private static readonly (string action, string command)[] Bindings =
    [
        ("Move Forward", "+forward"),
        ("Move Back", "+back"),
        ("Strafe Left", "+moveleft"),
        ("Strafe Right", "+moveright"),
        ("Jump", "+moveup"),
        ("Crouch", "+movedown"),
        ("Attack", "+attack"),
        ("Next Weapon", "weapnext"),
        ("Prev Weapon", "weapprev"),
        ("Zoom", "+zoom"),
        ("Show Scores", "+scores"),
        ("Chat", "messagemode"),
        ("Team Chat", "messagemode2"),
    ];

    private int _waitingForBind = -1;

    public ControlsScreen(MenuSystem system) : base(system)
    {
        Title = "CONTROLS";

        float y = START_Y;
        for (int i = 0; i < Bindings.Length; i++)
        {
            int idx = i;
            Widgets.Add(new BindWidget(Bindings[i].action, Bindings[i].command, FIELD_X, y, () =>
            {
                _waitingForBind = idx;
            }));
            y += 22;
        }

        y += 14;
        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        if (_waitingForBind >= 0)
        {
            Drawing.SetColor(0, 0, 0, 0.5f);
            Drawing.FillRect(150, 220, 340, 40);
            Drawing.SetColor(1f, 1f, 0f, 1f);
            Drawing.DrawStringCentered(232, "Press a key to bind...", 10f, 12f);
        }

        base.Draw(realtime);
        DrawFooterHint("ENTER to rebind  |  ESC to go back");
    }

    public override bool HandleKey(int key, int realtime)
    {
        if (_waitingForBind >= 0)
        {
            if (key == Keys.K_ESCAPE)
            {
                _waitingForBind = -1;
                return true;
            }

            // Bind the key
            var (_, command) = Bindings[_waitingForBind];
            string keyName = GetKeyName(key);
            if (keyName.Length > 0)
            {
                Syscalls.ExecuteCommand($"bind {keyName} {command}\n");
                // Update widget display
                if (_waitingForBind < Widgets.Count && Widgets[_waitingForBind] is BindWidget bw)
                    bw.RefreshBinding();
            }
            _waitingForBind = -1;
            return true;
        }

        return base.HandleKey(key, realtime);
    }

    private static string GetKeyName(int key)
    {
        // Common key names matching Q3's key numbering
        if (key >= 'a' && key <= 'z') return ((char)key).ToString();
        if (key >= '0' && key <= '9') return ((char)key).ToString();
        return key switch
        {
            Keys.K_SPACE => "SPACE",
            Keys.K_ENTER => "ENTER",
            Keys.K_TAB => "TAB",
            Keys.K_MOUSE1 => "MOUSE1",
            Keys.K_MOUSE2 => "MOUSE2",
            Keys.K_UPARROW => "UPARROW",
            Keys.K_DOWNARROW => "DOWNARROW",
            Keys.K_LEFTARROW => "LEFTARROW",
            Keys.K_RIGHTARROW => "RIGHTARROW",
            _ when key >= 32 && key < 127 => ((char)key).ToString(),
            _ => ""
        };
    }
}

/// <summary>Widget for displaying and changing a key binding.</summary>
public class BindWidget : Widget
{
    private readonly string _command;
    private string _boundKey;
    private readonly Action _onActivate;

    private const float LABEL_W = 200f;
    private const float VALUE_X = 220f;
    private const float CHAR_W = 9f;
    private const float CHAR_H = 11f;

    public BindWidget(string label, string command, float x, float y, Action onActivate)
    {
        Label = label;
        _command = command;
        _onActivate = onActivate;
        X = x; Y = y;
        W = LABEL_W + 200; H = CHAR_H;
        _boundKey = LookupBinding(command);
    }

    public void RefreshBinding() => _boundKey = LookupBinding(_command);

    public override void Draw(int realtime)
    {
        DrawLabel(realtime, X, Y, CHAR_W, CHAR_H);

        if (Focused)
            Drawing.SetColor(1f, 1f, 1f, 1f);
        else
            Drawing.SetColor(0.6f, 0.6f, 0.6f, 0.7f);
        Drawing.DrawString(X + VALUE_X, Y, _boundKey, CHAR_W, CHAR_H);
    }

    public override void Activate() => _onActivate();

    private static string LookupBinding(string command)
    {
        // Scan common key numbers to find which one is bound to this command
        int[] keyNums = [
            'w','a','s','d',' ', 137, // CTRL
            178, 179, 182, 183, // MOUSE1, MOUSE2, MWHEELUP, MWHEELDOWN
            'e','q','r','f','g','t','y', 9, 13, 136, // TAB, ENTER, SHIFT
            'c','x','z','v','1','2','3','4','5','6','7','8','9','0',
            132, 133, 134, 135, // arrows
        ];
        string[] keyNames = [
            "W","A","S","D","SPACE","CTRL",
            "MOUSE1","MOUSE2","MWHEELUP","MWHEELDOWN",
            "E","Q","R","F","G","T","Y","TAB","ENTER","SHIFT",
            "C","X","Z","V","1","2","3","4","5","6","7","8","9","0",
            "UPARROW","DOWNARROW","LEFTARROW","RIGHTARROW",
        ];

        for (int i = 0; i < keyNums.Length; i++)
        {
            string binding = Syscalls.GetBindingBuf(keyNums[i]);
            if (binding.Equals(command, StringComparison.OrdinalIgnoreCase))
                return keyNames[i];
        }
        return "---";
    }
}

/// <summary>
/// Video/Display settings: resolution mode, fullscreen, brightness, geometry detail.
/// </summary>
public class VideoSettingsScreen : MenuScreen
{
    private const float PANEL_X = 100f;
    private const float PANEL_Y = 50f;
    private const float PANEL_W = 440f;
    private const float PANEL_H = 380f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 60f;

    private static readonly string[] VideoModes =
        ["-2 (Desktop)", "-1 (Custom)", "0: 320x240", "1: 400x300", "2: 512x384",
         "3: 640x480", "4: 800x600", "5: 960x720", "6: 1024x768",
         "7: 1152x864", "8: 1280x1024", "9: 1600x1200", "10: 2048x1536",
         "11: 856x480"];

    private static readonly string[] FullscreenOpts = ["Windowed", "Fullscreen", "Borderless"];
    private static readonly string[] VsyncOpts = ["Off", "On"];
    private static readonly string[] TextureQuality = ["Low", "Medium", "High"];
    private static readonly string[] TextureFilter = ["Bilinear", "Trilinear"];
    private static readonly string[] GeometryDetail = ["Low", "Medium", "High"];

    private bool _needsRestart;

    public VideoSettingsScreen(MenuSystem system) : base(system)
    {
        Title = "VIDEO SETTINGS";

        float y = START_Y;

        int modeVal = (int)Syscalls.CvarGetValue("r_mode");
        int modeIdx = modeVal + 2; // -2 maps to index 0
        if (modeIdx < 0 || modeIdx >= VideoModes.Length) modeIdx = 0;
        Widgets.Add(new SpinWidget("Resolution", FIELD_X, y, VideoModes, modeIdx, idx =>
        {
            Syscalls.CvarSet("r_mode", (idx - 2).ToString());
            _needsRestart = true;
        }));
        y += 26;

        int fsVal = (int)Syscalls.CvarGetValue("r_fullscreen");
        Widgets.Add(new SpinWidget("Display Mode", FIELD_X, y, FullscreenOpts, Math.Clamp(fsVal, 0, 2), idx =>
        {
            Syscalls.CvarSet("r_fullscreen", idx > 0 ? "1" : "0");
            _needsRestart = true;
        }));
        y += 26;

        float gamma = Syscalls.CvarGetValue("r_gamma");
        if (gamma < 0.5f) gamma = 1.0f;
        Widgets.Add(new SliderWidget("Brightness", FIELD_X, y, 0.5f, 2.0f, 0.1f, gamma, val =>
        {
            Syscalls.CvarSet("r_gamma", $"{val:F1}");
        }));
        y += 26;

        int vsync = (int)Syscalls.CvarGetValue("r_swapInterval");
        Widgets.Add(new SpinWidget("VSync", FIELD_X, y, VsyncOpts, vsync > 0 ? 1 : 0, idx =>
        {
            Syscalls.CvarSet("r_swapInterval", idx.ToString());
            _needsRestart = true;
        }));
        y += 26;

        int texMode = (int)Syscalls.CvarGetValue("r_texturebits");
        int texIdx = texMode switch { 16 => 0, 32 => 2, _ => 1 };
        Widgets.Add(new SpinWidget("Texture Quality", FIELD_X, y, TextureQuality, texIdx, idx =>
        {
            string val = idx switch { 0 => "16", 2 => "32", _ => "0" };
            Syscalls.CvarSet("r_texturebits", val);
            _needsRestart = true;
        }));
        y += 26;

        int filterVal = (int)Syscalls.CvarGetValue("r_textureMode");
        // GL_LINEAR_MIPMAP_NEAREST=0 (bilinear), GL_LINEAR_MIPMAP_LINEAR=1 (trilinear)
        Widgets.Add(new SpinWidget("Texture Filter", FIELD_X, y, TextureFilter, filterVal > 0 ? 1 : 0, idx =>
        {
            Syscalls.CvarSet("r_textureMode", idx == 0 ? "GL_LINEAR_MIPMAP_NEAREST" : "GL_LINEAR_MIPMAP_LINEAR");
        }));
        y += 26;

        int subdivisions = (int)Syscalls.CvarGetValue("r_subdivisions");
        int geoIdx = subdivisions switch { <= 4 => 2, <= 12 => 1, _ => 0 };
        Widgets.Add(new SpinWidget("Geometry Detail", FIELD_X, y, GeometryDetail, geoIdx, idx =>
        {
            string val = idx switch { 0 => "20", 1 => "12", _ => "4" };
            Syscalls.CvarSet("r_subdivisions", val);
            _needsRestart = true;
        }));
        y += 40;

        Widgets.Add(new ButtonWidget("APPLY (VID_RESTART)", FIELD_X, y, () =>
        {
            System.PlaySound(MenuSystem.SFX_SELECT);
            Syscalls.ExecuteCommand("vid_restart\n");
        }));
        y += 30;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        if (_needsRestart)
        {
            Drawing.SetColor(1f, 1f, 0f, 0.8f);
            Drawing.DrawStringCentered(PANEL_Y + 38, "* Changes require APPLY to take effect *", 6f, 9f);
        }

        base.Draw(realtime);
        DrawFooterHint("LEFT/RIGHT to change  |  ESC to go back");
    }
}

/// <summary>
/// Sound settings: effects volume, music volume, sound quality.
/// </summary>
public class SoundSettingsScreen : MenuScreen
{
    private const float PANEL_X = 100f;
    private const float PANEL_Y = 80f;
    private const float PANEL_W = 440f;
    private const float PANEL_H = 300f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 60f;

    public SoundSettingsScreen(MenuSystem system) : base(system)
    {
        Title = "SOUND SETTINGS";

        float y = START_Y;

        float sfxVol = Syscalls.CvarGetValue("s_volume");
        Widgets.Add(new SliderWidget("Effects Volume", FIELD_X, y, 0f, 1f, 0.05f, sfxVol, val =>
        {
            Syscalls.CvarSet("s_volume", $"{val:F2}");
        }));
        y += 30;

        float musicVol = Syscalls.CvarGetValue("s_musicvolume");
        Widgets.Add(new SliderWidget("Music Volume", FIELD_X, y, 0f, 1f, 0.05f, musicVol, val =>
        {
            Syscalls.CvarSet("s_musicvolume", $"{val:F2}");
        }));
        y += 30;

        string[] quality = ["Low (11 KHz)", "Medium (22 KHz)", "High (44 KHz)"];
        int sdlSpeed = (int)Syscalls.CvarGetValue("s_sdlSpeed");
        int qIdx = sdlSpeed switch { <= 11025 => 0, >= 44100 => 2, _ => 1 };
        Widgets.Add(new SpinWidget("Sound Quality", FIELD_X, y, quality, qIdx, idx =>
        {
            string val = idx switch { 0 => "11025", 2 => "44100", _ => "22050" };
            Syscalls.CvarSet("s_sdlSpeed", val);
        }));
        y += 30;

        string[] doppler = ["Off", "On"];
        int dopplerVal = (int)Syscalls.CvarGetValue("s_doppler");
        Widgets.Add(new SpinWidget("Doppler Effect", FIELD_X, y, doppler, dopplerVal > 0 ? 1 : 0, idx =>
        {
            Syscalls.CvarSet("s_doppler", idx.ToString());
        }));
        y += 40;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        base.Draw(realtime);
        DrawFooterHint("LEFT/RIGHT to adjust  |  ESC to go back");
    }
}

/// <summary>
/// Game Options/Preferences: crosshair, simple items, marks, dynamic lights, etc.
/// </summary>
public class GameOptionsScreen : MenuScreen
{
    private const float PANEL_X = 80f;
    private const float PANEL_Y = 40f;
    private const float PANEL_W = 480f;
    private const float PANEL_H = 400f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 60f;

    private static readonly string[] OnOff = ["Off", "On"];
    private static readonly string[] Crosshairs =
        ["None", "Cross", "Dot", "Circle", "Plus", "X", "Diamond", "Star", "Triangle", "Chevron"];

    public GameOptionsScreen(MenuSystem system) : base(system)
    {
        Title = "GAME OPTIONS";

        float y = START_Y;

        int xhair = Math.Clamp((int)Syscalls.CvarGetValue("cg_drawCrosshair"), 0, Crosshairs.Length - 1);
        Widgets.Add(new SpinWidget("Crosshair", FIELD_X, y, Crosshairs, xhair, idx =>
        {
            Syscalls.CvarSet("cg_drawCrosshair", idx.ToString());
        }));
        y += 24;

        Widgets.Add(MakeBoolSpin("Simple Items", FIELD_X, y, "cg_simpleItems"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Wall Marks", FIELD_X, y, "cg_marks"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Dynamic Lights", FIELD_X, y, "r_dynamiclight"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Identify Target", FIELD_X, y, "cg_drawCrosshairNames"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Ejecting Brass", FIELD_X, y, "cg_brassTime", invert: false, trueVal: "2500"));
        y += 24;

        string[] skyOpts = ["Fast Sky", "High Quality Sky"];
        int sky = (int)Syscalls.CvarGetValue("r_fastsky") > 0 ? 0 : 1;
        Widgets.Add(new SpinWidget("Sky Quality", FIELD_X, y, skyOpts, sky, idx =>
        {
            Syscalls.CvarSet("r_fastsky", idx == 0 ? "1" : "0");
        }));
        y += 24;
        Widgets.Add(MakeBoolSpin("Force Player Model", FIELD_X, y, "cg_forcemodel"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Allow Downloads", FIELD_X, y, "cl_allowDownload"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Draw FPS", FIELD_X, y, "cg_drawFPS"));
        y += 24;
        Widgets.Add(MakeBoolSpin("Draw Timer", FIELD_X, y, "cg_drawTimer"));
        y += 34;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        base.Draw(realtime);
        DrawFooterHint("LEFT/RIGHT to change  |  ESC to go back");
    }

    private static SpinWidget MakeBoolSpin(string label, float x, float y, string cvar,
        bool invert = false, string trueVal = "1")
    {
        float val = Syscalls.CvarGetValue(cvar);
        bool isOn = invert ? val == 0 : val != 0;
        return new SpinWidget(label, x, y, OnOff, isOn ? 1 : 0, idx =>
        {
            if (invert)
                Syscalls.CvarSet(cvar, idx == 0 ? trueVal : "0");
            else
                Syscalls.CvarSet(cvar, idx > 0 ? trueVal : "0");
        });
    }
}

/// <summary>
/// Network settings: connection speed/rate.
/// </summary>
public class NetworkSettingsScreen : MenuScreen
{
    private const float PANEL_X = 120f;
    private const float PANEL_Y = 100f;
    private const float PANEL_W = 400f;
    private const float PANEL_H = 250f;
    private const float FIELD_X = PANEL_X + 30f;
    private const float START_Y = PANEL_Y + 60f;

    private static readonly string[] Rates = ["Modem (4000)", "ISDN (5000)", "Cable (25000)", "LAN (0)"];
    private static readonly string[] RateValues = ["4000", "5000", "25000", "0"];

    public NetworkSettingsScreen(MenuSystem system) : base(system)
    {
        Title = "NETWORK";

        float y = START_Y;

        string rateStr = Syscalls.CvarGetString("rate");
        int rateIdx = Array.IndexOf(RateValues, rateStr);
        if (rateIdx < 0) rateIdx = 2;
        Widgets.Add(new SpinWidget("Connection Speed", FIELD_X, y, Rates, rateIdx, idx =>
        {
            Syscalls.CvarSet("rate", RateValues[idx]);
        }));
        y += 30;

        string[] snaps = ["20", "30", "40"];
        string snapStr = Syscalls.CvarGetString("snaps");
        int snapIdx = Array.IndexOf(snaps, snapStr);
        if (snapIdx < 0) snapIdx = 1;
        Widgets.Add(new SpinWidget("Snapshot Rate", FIELD_X, y, snaps, snapIdx, idx =>
        {
            Syscalls.CvarSet("snaps", snaps[idx]);
        }));
        y += 40;

        Widgets.Add(new ButtonWidget("BACK", FIELD_X, y, () => System.Pop())
            { CharW = 12f, CharH = 12f });
    }

    public override void Draw(int realtime)
    {
        DrawBackground();
        DrawPanel(PANEL_X, PANEL_Y, PANEL_W, PANEL_H);
        DrawTitle(Title);

        base.Draw(realtime);
        DrawFooterHint("LEFT/RIGHT to change  |  ESC to go back");
    }
}
