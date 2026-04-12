namespace UiMod;

/// <summary>
/// Drawing helpers for UI rendering in 640x480 virtual coordinate space.
/// All coordinates are specified in virtual space and scaled to actual
/// screen pixels via AdjustFrom640 (matching Q3's UI_AdjustFrom640).
/// </summary>
public static unsafe class Drawing
{
    public const float SCREEN_W = 640f;
    public const float SCREEN_H = 480f;
    public const float BIGCHAR_W = 16f;
    public const float BIGCHAR_H = 16f;
    public const float SMALLCHAR_W = 8f;
    public const float SMALLCHAR_H = 16f;

    private static int _charsetShader;
    private static int _whiteShader;

    // Coordinate scaling (640x480 virtual → actual screen pixels)
    private static float _xScale = 1f;
    private static float _yScale = 1f;

    /// <summary>Scale factor: actual pixels per virtual X unit.</summary>
    public static float XScale => _xScale;
    /// <summary>Scale factor: actual pixels per virtual Y unit.</summary>
    public static float YScale => _yScale;

    public static void Init()
    {
        _charsetShader = Syscalls.R_RegisterShaderNoMip("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShaderNoMip("white");

        var (vidW, vidH) = Syscalls.GetGlConfig();
        if (vidW > 0 && vidH > 0)
        {
            _xScale = vidW / SCREEN_W;
            _yScale = vidH / SCREEN_H;
        }
        Syscalls.Print($"[UIMOD] Screen {vidW}x{vidH}, scale {_xScale:F2}x{_yScale:F2}\n");
    }

    /// <summary>Convert 640x480 virtual coordinates to actual screen pixels.</summary>
    private static void AdjustFrom640(ref float x, ref float y, ref float w, ref float h)
    {
        x *= _xScale;
        y *= _yScale;
        w *= _xScale;
        h *= _yScale;
    }

    public static void SetColor(float r, float g, float b, float a) =>
        Syscalls.R_SetColor(r, g, b, a);

    public static void ClearColor() => Syscalls.R_ClearColor();

    public static void FillRect(float x, float y, float w, float h)
    {
        AdjustFrom640(ref x, ref y, ref w, ref h);
        if (_whiteShader != 0)
            Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 0, 0, _whiteShader);
    }

    public static void DrawPic(float x, float y, float w, float h, int shader)
    {
        AdjustFrom640(ref x, ref y, ref w, ref h);
        if (shader != 0)
            Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 1, 1, shader);
    }

    /// <summary>Draw a single character from the bigchars charset.</summary>
    public static void DrawChar(float x, float y, float w, float h, int ch)
    {
        if (ch <= ' ' || _charsetShader == 0) return;
        AdjustFrom640(ref x, ref y, ref w, ref h);
        int row = ch >> 4;
        int col = ch & 15;
        float s = col * 0.0625f;
        float t = row * 0.0625f;
        Syscalls.R_DrawStretchPic(x, y, w, h, s, t, s + 0.0625f, t + 0.0625f, _charsetShader);
    }

    /// <summary>Draw a string using bigchars. Handles Q3 color codes (^1, ^2, etc.).</summary>
    public static void DrawString(float x, float y, string text, float charW = BIGCHAR_W, float charH = BIGCHAR_H)
    {
        float cx = x;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '^' && i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9')
            {
                ApplyQ3Color(text[i + 1]);
                i++;
                continue;
            }
            DrawChar(cx, y, charW, charH, c);
            cx += charW;
        }
    }

    /// <summary>Draw a string centered horizontally on screen.</summary>
    public static void DrawStringCentered(float y, string text, float charW = BIGCHAR_W, float charH = BIGCHAR_H)
    {
        string stripped = StripColors(text);
        float x = (SCREEN_W - stripped.Length * charW) * 0.5f;
        DrawString(x, y, text, charW, charH);
    }

    /// <summary>Measure the pixel width of a string (after stripping color codes).</summary>
    public static float MeasureString(string text, float charW = BIGCHAR_W)
    {
        return StripColors(text).Length * charW;
    }

    public static string StripColors(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9')
            { i++; continue; }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static void ApplyQ3Color(char code)
    {
        switch (code)
        {
            case '0': SetColor(0, 0, 0, 1); break;
            case '1': SetColor(1, 0, 0, 1); break;
            case '2': SetColor(0, 1, 0, 1); break;
            case '3': SetColor(1, 1, 0, 1); break;
            case '4': SetColor(0, 0, 1, 1); break;
            case '5': SetColor(0, 1, 1, 1); break;
            case '6': SetColor(1, 0, 1, 1); break;
            default: SetColor(1, 1, 1, 1); break;
        }
    }
}
