namespace QGameMod;

/// <summary>
/// Interface that all server-side game mods must implement.
/// The mod host calls these methods at the corresponding game module hook points.
/// </summary>
public interface IQGameMod
{
    /// <summary>Display name of the mod.</summary>
    string Name { get; }

    /// <summary>Called once when the game initializes (map load).</summary>
    void Init();

    /// <summary>Called once when the game shuts down (map change/restart).</summary>
    void Shutdown();

    /// <summary>Called every server frame with the current level time in milliseconds.</summary>
    void Frame(int levelTime);

    /// <summary>Called when a server console command is entered. Return true if handled.</summary>
    bool ConsoleCommand(string cmd);
}
