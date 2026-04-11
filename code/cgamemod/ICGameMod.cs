namespace CGameMod;

/// <summary>
/// Interface that all cgame mods must implement.
/// The mod host calls these methods at the corresponding cgame hook points.
/// </summary>
public interface ICGameMod
{
    /// <summary>Display name of the mod.</summary>
    string Name { get; }

    /// <summary>Called once when the cgame initializes (map load).</summary>
    void Init();

    /// <summary>Called once when the cgame shuts down (map change/disconnect).</summary>
    void Shutdown();

    /// <summary>Called every frame with the current server time in milliseconds.</summary>
    void Frame(int serverTime);

    /// <summary>Called after all standard HUD elements are drawn. Use Syscalls.R_* to draw.</summary>
    void Draw2D(int screenWidth, int screenHeight);

    /// <summary>Called when a console command is entered. Return true if handled.</summary>
    bool ConsoleCommand(string cmd);

    /// <summary>Called when a game entity event fires (kills, pickups, etc.).</summary>
    void EntityEvent(int entityNum, int eventType, int eventParm);
}
