namespace CGameMod;

/// <summary>
/// Example mod that demonstrates the mod API.
/// Draws a small FPS/time overlay and handles a test console command.
/// </summary>
public class ExampleMod : ICGameMod
{
    public string Name => "Example Mod";

    private int _whiteShader;
    private int _charsetShader;
    private int _lastFrameTime;
    private int _frameCount;
    private int _fps;

    public void Init()
    {
        _whiteShader = Syscalls.R_RegisterShader("white");
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        Syscalls.AddCommand("mod_test");
    }

    public void Shutdown()
    {
        Syscalls.RemoveCommand("mod_test");
    }

    public void Frame(int serverTime)
    {
        _frameCount++;
        if (serverTime - _lastFrameTime >= 1000)
        {
            _fps = _frameCount;
            _frameCount = 0;
            _lastFrameTime = serverTime;
        }
    }

    public unsafe void Draw2D(int screenWidth, int screenHeight)
    {
        // Draw a small semi-transparent background in top-right
        Syscalls.R_SetColor(0.0f, 0.0f, 0.0f, 0.5f);
        Syscalls.R_DrawStretchPic(screenWidth - 120, 2, 118, 18, 0, 0, 1, 1, _whiteShader);

        // Draw FPS text
        Syscalls.R_SetColor(0.0f, 1.0f, 0.5f, 1.0f);
        DrawString(screenWidth - 116, 4, $"MOD FPS: {_fps}", 8);

        // Reset color
        float* reset = stackalloc float[4];
        reset[0] = 1; reset[1] = 1; reset[2] = 1; reset[3] = 1;
        Syscalls.R_SetColor(reset);
    }

    public bool ConsoleCommand(string cmd)
    {
        if (cmd == "mod_test")
        {
            Syscalls.Print("[MOD] ^2Example mod is working!\n");
            Syscalls.Print($"[MOD] Screen: {Syscalls.CvarGetString("r_customwidth")}x{Syscalls.CvarGetString("r_customheight")}\n");
            return true;
        }
        return false;
    }

    public void EntityEvent(int entityNum, int eventType, int eventParm)
    {
        // Example: could log kills, play custom sounds, etc.
    }

    private void DrawString(int x, int y, string text, int charSize)
    {
        // Draw using Q3's bigchars charset (16x16 grid of characters)
        for (int i = 0; i < text.Length; i++)
        {
            int ch = text[i];
            if (ch == ' ') { x += charSize; continue; }
            float row = (ch >> 4) / 16.0f;
            float col = (ch & 15) / 16.0f;
            float size = 1.0f / 16.0f;
            Syscalls.R_DrawStretchPic(x, y, charSize, charSize,
                col, row, col + size, row + size, _charsetShader);
            x += charSize;
        }
    }
}
