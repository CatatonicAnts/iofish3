namespace UiMod;

/// <summary>
/// Interface that all UI mods must implement.
/// Each method returns true to override C handling, false for pass-through.
/// </summary>
public interface IUiMod
{
    string Name { get; }

    void Init();
    void Shutdown();

    /// <summary>Called when the active menu changes. Return true to override C menu.</summary>
    bool SetActiveMenu(int menu);

    /// <summary>Called each frame to draw the UI. Return true to skip C drawing.</summary>
    bool Refresh(int realtime);

    /// <summary>Called on key events. Return true if consumed.</summary>
    bool KeyEvent(int key, int down);

    /// <summary>Called on mouse movement. Return true if consumed.</summary>
    bool MouseEvent(int dx, int dy);

    /// <summary>Called to check if UI is fullscreen. Return -1 to defer to C, 0/1 otherwise.</summary>
    int IsFullscreen();

    /// <summary>Called for console commands. Return true if handled.</summary>
    bool ConsoleCommand(int realtime);

    /// <summary>Called to draw the loading/connect screen. Return true to override.</summary>
    bool DrawConnectScreen(int overlay);
}
