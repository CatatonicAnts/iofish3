namespace UiMod;

/// <summary>
/// Base class for all menu screens. Provides widget management,
/// keyboard/mouse navigation, and standard drawing helpers.
/// </summary>
public abstract class MenuScreen
{
    protected readonly MenuSystem System;
    protected readonly List<Widget> Widgets = new();
    protected int CursorIndex;
    protected string Title = "";

    // Layout constants
    protected const float SCREEN_W = Drawing.SCREEN_W;
    protected const float SCREEN_H = Drawing.SCREEN_H;

    protected MenuScreen(MenuSystem system) => System = system;

    public virtual void OnEnter() { CursorIndex = 0; }
    public virtual void OnExit() { }

    public virtual void Draw(int realtime)
    {
        for (int i = 0; i < Widgets.Count; i++)
        {
            var w = Widgets[i];
            w.Focused = (i == CursorIndex);
            w.Draw(realtime);
        }
    }

    public virtual bool HandleKey(int key, int realtime)
    {
        if (CursorIndex < 0 || CursorIndex >= Widgets.Count)
        {
            if (key == Keys.K_ESCAPE || key == Keys.K_MOUSE2) { System.Pop(); return true; }
            return true;
        }

        var widget = Widgets[CursorIndex];

        // Let the focused widget try to handle the key first (important for
        // text input ESC-to-stop-editing, backspace, arrow-cursor-move, etc.)
        if (widget.HandleKey(key))
            return true;

        // Widget didn't consume it — screen-level handling
        switch (key)
        {
            case Keys.K_ESCAPE or Keys.K_MOUSE2:
                System.Pop();
                return true;

            case Keys.K_UPARROW or Keys.K_KP_UPARROW:
                MoveCursor(-1);
                return true;

            case Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                MoveCursor(1);
                return true;

            case Keys.K_ENTER or Keys.K_KP_ENTER or Keys.K_MOUSE1:
                widget.Activate();
                return true;
        }

        // Try as character input (printable ASCII)
        if (key >= 32 && key < 127)
            return widget.HandleChar(key);

        return true;
    }

    public virtual bool HandleChar(int ch)
    {
        if (CursorIndex >= 0 && CursorIndex < Widgets.Count)
            return Widgets[CursorIndex].HandleChar(ch);
        return false;
    }

    public virtual void HandleMouse(float cursorX, float cursorY)
    {
        for (int i = 0; i < Widgets.Count; i++)
        {
            if (Widgets[i].HitTest(cursorX, cursorY))
            {
                if (CursorIndex != i)
                {
                    CursorIndex = i;
                    System.PlaySound(MenuSystem.SFX_MOVE);
                }
                break;
            }
        }
    }

    private void MoveCursor(int dir)
    {
        if (Widgets.Count == 0) return;
        int start = CursorIndex;
        int iterations = 0;
        do
        {
            CursorIndex = (CursorIndex + dir + Widgets.Count) % Widgets.Count;
            if (++iterations > Widgets.Count) break;
        } while (!Widgets[CursorIndex].Selectable && CursorIndex != start);
        System.PlaySound(MenuSystem.SFX_MOVE);
    }

    // Drawing helpers
    protected static void DrawTitle(string text)
    {
        Drawing.SetColor(0, 0, 0, 0.6f);
        Drawing.DrawStringCentered(22, text, 20f, 20f);
        Drawing.SetColor(0.85f, 0.25f, 0.0f, 1.0f);
        Drawing.DrawStringCentered(20, text, 20f, 20f);
    }

    protected static void DrawBackground()
    {
        Drawing.SetColor(0.02f, 0.02f, 0.04f, 1.0f);
        Drawing.FillRect(0, 0, SCREEN_W, SCREEN_H);
        Drawing.SetColor(0.6f, 0.15f, 0.0f, 0.4f);
        Drawing.FillRect(0, 0, SCREEN_W, 3);
    }

    protected static void DrawPanel(float x, float y, float w, float h)
    {
        Drawing.SetColor(0.04f, 0.04f, 0.08f, 0.85f);
        Drawing.FillRect(x, y, w, h);
        DrawBorder(x, y, w, h, 0.5f, 0.15f, 0.0f, 0.35f);
    }

    protected static void DrawBorder(float x, float y, float w, float h,
        float r, float g, float b, float a)
    {
        Drawing.SetColor(r, g, b, a);
        Drawing.FillRect(x, y, w, 2);
        Drawing.FillRect(x, y + h - 2, w, 2);
        Drawing.FillRect(x, y, 2, h);
        Drawing.FillRect(x + w - 2, y, 2, h);
    }

    protected static void DrawFooterHint(string text)
    {
        Drawing.SetColor(0.35f, 0.35f, 0.35f, 0.5f);
        Drawing.DrawStringCentered(458, text, 6f, 10f);
    }
}

/// <summary>
/// Manages a stack of menu screens with push/pop navigation.
/// </summary>
public class MenuSystem
{
    public const int SFX_MOVE = 0;
    public const int SFX_SELECT = 1;
    public const int SFX_BACK = 2;

    private readonly Stack<MenuScreen> _stack = new();
    private int _sfxMove;
    private int _sfxSelect;
    private int _sfxBack;

    public MenuScreen? CurrentScreen => _stack.Count > 0 ? _stack.Peek() : null;
    public bool HasScreens => _stack.Count > 0;

    public void Init()
    {
        _sfxMove = Syscalls.S_RegisterSound("sound/misc/menu1.wav");
        _sfxSelect = Syscalls.S_RegisterSound("sound/misc/menu3.wav");
        _sfxBack = Syscalls.S_RegisterSound("sound/misc/menu3.wav");
    }

    public void Push(MenuScreen screen)
    {
        _stack.Push(screen);
        screen.OnEnter();
    }

    public void Pop()
    {
        if (_stack.Count > 0)
        {
            _stack.Pop().OnExit();
            PlaySound(SFX_BACK);
        }
    }

    public void Clear()
    {
        while (_stack.Count > 0)
            _stack.Pop().OnExit();
    }

    public void PlaySound(int sfxType)
    {
        int sfx = sfxType switch
        {
            SFX_MOVE => _sfxMove,
            SFX_SELECT => _sfxSelect,
            SFX_BACK => _sfxBack,
            _ => 0
        };
        if (sfx > 0) Syscalls.S_StartLocalSound(sfx, 1);
    }
}

/// <summary>Key code constants from keycodes.h.</summary>
public static class Keys
{
    public const int K_TAB = 9;
    public const int K_ENTER = 13;
    public const int K_ESCAPE = 27;
    public const int K_SPACE = 32;
    public const int K_BACKSPACE = 127;

    public const int K_COMMAND = 128;
    public const int K_CAPSLOCK = 129;
    public const int K_PAUSE = 131;
    public const int K_UPARROW = 132;
    public const int K_DOWNARROW = 133;
    public const int K_LEFTARROW = 134;
    public const int K_RIGHTARROW = 135;

    public const int K_ALT = 136;
    public const int K_CTRL = 137;
    public const int K_SHIFT = 138;
    public const int K_INS = 139;
    public const int K_DEL = 140;
    public const int K_PGDN = 141;
    public const int K_PGUP = 142;
    public const int K_HOME = 143;
    public const int K_END = 144;

    public const int K_F1 = 145;
    public const int K_F2 = 146;
    public const int K_F3 = 147;
    public const int K_F4 = 148;
    public const int K_F5 = 149;
    public const int K_F6 = 150;
    public const int K_F7 = 151;
    public const int K_F8 = 152;
    public const int K_F9 = 153;
    public const int K_F10 = 154;
    public const int K_F11 = 155;
    public const int K_F12 = 156;

    public const int K_KP_HOME = 160;
    public const int K_KP_UPARROW = 161;
    public const int K_KP_PGUP = 162;
    public const int K_KP_LEFTARROW = 163;
    public const int K_KP_5 = 164;
    public const int K_KP_RIGHTARROW = 165;
    public const int K_KP_END = 166;
    public const int K_KP_DOWNARROW = 167;
    public const int K_KP_PGDN = 168;
    public const int K_KP_ENTER = 169;
    public const int K_KP_INS = 170;
    public const int K_KP_DEL = 171;

    public const int K_MOUSE1 = 178;
    public const int K_MOUSE2 = 179;
    public const int K_MOUSE3 = 180;
    public const int K_MOUSE4 = 181;
    public const int K_MOUSE5 = 182;
    public const int K_MWHEELDOWN = 183;
    public const int K_MWHEELUP = 184;
}

#region Widgets

/// <summary>Base class for interactive UI widgets.</summary>
public abstract class Widget
{
    public float X, Y, W, H;
    public bool Focused;
    public bool Selectable = true;
    public string Label = "";

    public abstract void Draw(int realtime);
    public virtual void Activate() { }
    public virtual bool HandleKey(int key) => false;
    public virtual bool HandleChar(int ch) => false;

    public bool HitTest(float cx, float cy) =>
        Selectable && cx >= X - 8 && cx <= X + W + 8 && cy >= Y - 4 && cy <= Y + H + 4;

    protected void DrawLabel(int realtime, float labelX, float labelY, float charW, float charH)
    {
        if (Focused)
        {
            float pulse = 0.7f + 0.3f * MathF.Sin(realtime * 0.004f);
            Drawing.SetColor(1.0f, pulse, pulse * 0.25f, 1.0f);
        }
        else
        {
            Drawing.SetColor(0.65f, 0.15f, 0.0f, 0.75f);
        }
        Drawing.DrawString(labelX, labelY, Label, charW, charH);
    }
}

/// <summary>Clickable text button, optionally centered.</summary>
public class ButtonWidget : Widget
{
    public Action? OnActivate;
    public float CharW = 14f;
    public float CharH = 14f;
    public bool Centered;

    public ButtonWidget(string label, float x, float y, Action? action = null)
    {
        Label = label;
        OnActivate = action;
        float tw = Drawing.StripColors(label).Length * CharW;
        X = x; Y = y; W = tw; H = CharH;
    }

    public static ButtonWidget CreateCentered(string label, float y, float charW, float charH, Action? action)
    {
        float tw = Drawing.StripColors(label).Length * charW;
        float x = (Drawing.SCREEN_W - tw) * 0.5f;
        return new ButtonWidget(label, x, y, action) { CharW = charW, CharH = charH, Centered = true, W = tw, H = charH };
    }

    public override void Draw(int realtime)
    {
        if (Focused)
        {
            float barW = W + 24;
            float barX = Centered ? (Drawing.SCREEN_W - barW) * 0.5f : X - 12;
            Drawing.SetColor(0.7f, 0.2f, 0.0f, 0.2f);
            Drawing.FillRect(barX, Y - 4, barW, H + 8);
        }
        DrawLabel(realtime, X, Y, CharW, CharH);
    }

    public override void Activate() => OnActivate?.Invoke();
}

/// <summary>Left-label + right-side value cycling with left/right arrows.</summary>
public class SpinWidget : Widget
{
    public string[] Options;
    public int SelectedIndex;
    public Action<int>? OnChanged;

    private const float LABEL_W = 200f;
    private const float VALUE_X_OFFSET = 210f;
    private const float CHAR_W = 10f;
    private const float CHAR_H = 12f;

    public SpinWidget(string label, float x, float y, string[] options, int selected, Action<int>? onChange = null)
    {
        Label = label;
        X = x; Y = y;
        W = LABEL_W + 200f; H = CHAR_H;
        Options = options;
        SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, options.Length - 1));
        OnChanged = onChange;
    }

    public override void Draw(int realtime)
    {
        DrawLabel(realtime, X, Y, CHAR_W, CHAR_H);

        string val = SelectedIndex >= 0 && SelectedIndex < Options.Length ? Options[SelectedIndex] : "---";
        if (Focused)
            Drawing.SetColor(1f, 1f, 1f, 1f);
        else
            Drawing.SetColor(0.7f, 0.7f, 0.7f, 0.8f);
        Drawing.DrawString(X + VALUE_X_OFFSET, Y, val, CHAR_W, CHAR_H);
    }

    public override bool HandleKey(int key)
    {
        if (Options.Length == 0) return false;
        if (key == Keys.K_LEFTARROW || key == Keys.K_KP_LEFTARROW)
        {
            SelectedIndex = (SelectedIndex - 1 + Options.Length) % Options.Length;
            OnChanged?.Invoke(SelectedIndex);
            return true;
        }
        if (key == Keys.K_RIGHTARROW || key == Keys.K_KP_RIGHTARROW)
        {
            SelectedIndex = (SelectedIndex + 1) % Options.Length;
            OnChanged?.Invoke(SelectedIndex);
            return true;
        }
        return false;
    }

    /// <summary>Replace options and reset selection index.</summary>
    public void UpdateOptions(string[] options, int selected = 0)
    {
        Options = options;
        SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, options.Length - 1));
    }

    public override void Activate()
    {
        if (Options.Length == 0) return;
        SelectedIndex = (SelectedIndex + 1) % Options.Length;
        OnChanged?.Invoke(SelectedIndex);
    }
}

/// <summary>Slider widget with left-label and a horizontal bar.</summary>
public class SliderWidget : Widget
{
    public float Value;
    public float Min, Max, Step;
    public Action<float>? OnChanged;

    private const float LABEL_W = 200f;
    private const float BAR_X_OFFSET = 210f;
    private const float BAR_W = 160f;
    private const float BAR_H = 10f;
    private const float CHAR_W = 10f;
    private const float CHAR_H = 12f;

    public SliderWidget(string label, float x, float y, float min, float max, float step, float value, Action<float>? onChange = null)
    {
        Label = label;
        X = x; Y = y;
        W = LABEL_W + BAR_W + 60; H = CHAR_H;
        Min = min; Max = max; Step = step;
        Value = Math.Clamp(value, min, max);
        OnChanged = onChange;
    }

    public override void Draw(int realtime)
    {
        DrawLabel(realtime, X, Y, CHAR_W, CHAR_H);

        float barX = X + BAR_X_OFFSET;
        float barY = Y + (CHAR_H - BAR_H) * 0.5f;

        // Track
        Drawing.SetColor(0.15f, 0.15f, 0.2f, 0.8f);
        Drawing.FillRect(barX, barY, BAR_W, BAR_H);

        // Fill
        float frac = (Max > Min) ? (Value - Min) / (Max - Min) : 0;
        Drawing.SetColor(0.7f, 0.2f, 0.0f, Focused ? 0.9f : 0.6f);
        Drawing.FillRect(barX, barY, BAR_W * frac, BAR_H);

        // Border
        Drawing.SetColor(0.5f, 0.15f, 0.0f, 0.5f);
        Drawing.FillRect(barX, barY, BAR_W, 1);
        Drawing.FillRect(barX, barY + BAR_H - 1, BAR_W, 1);
        Drawing.FillRect(barX, barY, 1, BAR_H);
        Drawing.FillRect(barX + BAR_W - 1, barY, 1, BAR_H);

        // Value text
        Drawing.SetColor(0.7f, 0.7f, 0.7f, 0.8f);
        Drawing.DrawString(barX + BAR_W + 8, Y, $"{Value:F1}", 8f, CHAR_H);
    }

    public override bool HandleKey(int key)
    {
        if (key == Keys.K_LEFTARROW || key == Keys.K_KP_LEFTARROW)
        {
            Value = Math.Max(Min, Value - Step);
            OnChanged?.Invoke(Value);
            return true;
        }
        if (key == Keys.K_RIGHTARROW || key == Keys.K_KP_RIGHTARROW)
        {
            Value = Math.Min(Max, Value + Step);
            OnChanged?.Invoke(Value);
            return true;
        }
        return false;
    }
}

/// <summary>Text input field with cursor and editing.</summary>
public class TextInputWidget : Widget
{
    public string Text;
    public int MaxLength;
    public Action<string>? OnChanged;
    private int _cursorPos;
    private bool _editing;

    private const float LABEL_W = 200f;
    private const float FIELD_X_OFFSET = 210f;
    private const float FIELD_W = 200f;
    private const float CHAR_W = 10f;
    private const float CHAR_H = 12f;

    public TextInputWidget(string label, float x, float y, string text, int maxLen, Action<string>? onChange = null)
    {
        Label = label;
        X = x; Y = y;
        W = LABEL_W + FIELD_W + 20; H = CHAR_H;
        Text = text;
        MaxLength = maxLen;
        _cursorPos = text.Length;
        OnChanged = onChange;
    }

    public override void Draw(int realtime)
    {
        DrawLabel(realtime, X, Y, CHAR_W, CHAR_H);

        // Safety: clamp cursor in case text was changed externally
        if (_cursorPos > Text.Length) _cursorPos = Text.Length;

        float fx = X + FIELD_X_OFFSET;

        // Field background
        Drawing.SetColor(0.08f, 0.08f, 0.12f, Focused ? 0.9f : 0.6f);
        Drawing.FillRect(fx, Y - 2, FIELD_W, CHAR_H + 4);

        // Field border
        if (Focused)
            Drawing.SetColor(0.8f, 0.3f, 0.0f, 0.7f);
        else
            Drawing.SetColor(0.3f, 0.1f, 0.0f, 0.4f);
        Drawing.FillRect(fx, Y - 2, FIELD_W, 1);
        Drawing.FillRect(fx, Y + CHAR_H + 2, FIELD_W, 1);
        Drawing.FillRect(fx, Y - 2, 1, CHAR_H + 4);
        Drawing.FillRect(fx + FIELD_W - 1, Y - 2, 1, CHAR_H + 4);

        // Text
        Drawing.SetColor(1f, 1f, 1f, 1f);
        Drawing.DrawString(fx + 4, Y, Text, CHAR_W, CHAR_H);

        // Cursor
        if (Focused && _editing && ((realtime / 500) & 1) == 0)
        {
            float cx = fx + 4 + _cursorPos * CHAR_W;
            Drawing.SetColor(1f, 1f, 1f, 0.8f);
            Drawing.FillRect(cx, Y, 2, CHAR_H);
        }
    }

    public override void Activate()
    {
        _editing = !_editing;
        if (_editing) _cursorPos = Text.Length;
    }

    public override bool HandleKey(int key)
    {
        if (!_editing) return false;

        if (key == Keys.K_BACKSPACE && _cursorPos > 0)
        {
            Text = Text.Remove(_cursorPos - 1, 1);
            _cursorPos--;
            OnChanged?.Invoke(Text);
            return true;
        }
        if (key == Keys.K_LEFTARROW && _cursorPos > 0) { _cursorPos--; return true; }
        if (key == Keys.K_RIGHTARROW && _cursorPos < Text.Length) { _cursorPos++; return true; }
        if (key == Keys.K_ENTER || key == Keys.K_KP_ENTER || key == Keys.K_ESCAPE)
        {
            _editing = false;
            return true;
        }
        return false;
    }

    public override bool HandleChar(int ch)
    {
        if (!_editing) return false;
        if (ch < 32 || ch > 126) return false;
        if (Text.Length >= MaxLength) return false;

        Text = Text.Insert(_cursorPos, ((char)ch).ToString());
        _cursorPos++;
        OnChanged?.Invoke(Text);
        return true;
    }
}

/// <summary>Non-interactive label (separator or heading).</summary>
public class LabelWidget : Widget
{
    public float CharW = 8f;
    public float CharH = 10f;
    public float R = 0.45f, G = 0.45f, B = 0.45f, A = 0.6f;

    public LabelWidget(string text, float x, float y)
    {
        Label = text;
        X = x; Y = y;
        W = text.Length * CharW; H = CharH;
        Selectable = false;
    }

    public override void Draw(int realtime)
    {
        Drawing.SetColor(R, G, B, A);
        Drawing.DrawString(X, Y, Label, CharW, CharH);
    }
}

/// <summary>
/// Scrollable list widget for server browsers, demos, mods, etc.
/// Displays rows of items with selection highlight and page scrolling.
/// </summary>
public class ListWidget : Widget
{
    public string[] Columns;
    public float[] ColumnWidths;
    public List<string[]> Items = new();
    public int SelectedIndex = -1;
    public int ScrollOffset;
    public Action<int>? OnSelected;

    private const float ROW_H = 14f;
    private const float CHAR_W = 7f;
    private const float CHAR_H = 10f;
    private const float HEADER_H = 16f;
    private int _visibleRows;

    public ListWidget(string[] columns, float[] columnWidths, float x, float y, float w, float h,
        Action<int>? onSelected = null)
    {
        Columns = columns;
        ColumnWidths = columnWidths;
        X = x; Y = y; W = w; H = h;
        OnSelected = onSelected;
        _visibleRows = Math.Max(1, (int)((h - HEADER_H) / ROW_H));
    }

    public override void Draw(int realtime)
    {
        // Background
        Drawing.SetColor(0.06f, 0.06f, 0.1f, Focused ? 0.9f : 0.7f);
        Drawing.FillRect(X, Y, W, H);

        // Header
        Drawing.SetColor(0.12f, 0.12f, 0.18f, 0.95f);
        Drawing.FillRect(X, Y, W, HEADER_H);

        float cx = X + 4;
        Drawing.SetColor(0.8f, 0.4f, 0.0f, 1f);
        for (int c = 0; c < Columns.Length; c++)
        {
            Drawing.DrawString(cx, Y + 2, Columns[c], CHAR_W, CHAR_H + 2);
            cx += ColumnWidths[c];
        }

        // Rows
        float rowY = Y + HEADER_H;
        for (int i = 0; i < _visibleRows && ScrollOffset + i < Items.Count; i++)
        {
            int idx = ScrollOffset + i;
            bool selected = (idx == SelectedIndex);

            if (selected)
            {
                Drawing.SetColor(0.6f, 0.18f, 0.0f, 0.35f);
                Drawing.FillRect(X, rowY, W, ROW_H);
            }
            else if ((i & 1) == 0)
            {
                Drawing.SetColor(0.08f, 0.08f, 0.12f, 0.3f);
                Drawing.FillRect(X, rowY, W, ROW_H);
            }

            cx = X + 4;
            var row = Items[idx];
            for (int c = 0; c < row.Length && c < Columns.Length; c++)
            {
                if (selected)
                    Drawing.SetColor(1f, 1f, 1f, 1f);
                else
                    Drawing.SetColor(0.7f, 0.7f, 0.7f, 0.85f);

                string text = row[c];
                int maxChars = (int)(ColumnWidths[c] / CHAR_W) - 1;
                if (text.Length > maxChars && maxChars > 3)
                    text = text[..(maxChars - 2)] + "..";
                Drawing.DrawString(cx, rowY + 2, text, CHAR_W, CHAR_H);
                cx += ColumnWidths[c];
            }
            rowY += ROW_H;
        }

        // Border
        float br = Focused ? 0.8f : 0.4f;
        Drawing.SetColor(br * 0.625f, br * 0.1875f, 0f, 0.5f);
        Drawing.FillRect(X, Y, W, 1);
        Drawing.FillRect(X, Y + H - 1, W, 1);
        Drawing.FillRect(X, Y, 1, H);
        Drawing.FillRect(X + W - 1, Y, 1, H);

        // Scrollbar
        if (Items.Count > _visibleRows)
        {
            float sbH = H - HEADER_H;
            float thumbH = Math.Max(10, sbH * _visibleRows / Items.Count);
            float thumbY = Y + HEADER_H + (sbH - thumbH) * ScrollOffset / Math.Max(1, Items.Count - _visibleRows);
            Drawing.SetColor(0.3f, 0.1f, 0.0f, 0.5f);
            Drawing.FillRect(X + W - 6, Y + HEADER_H, 5, sbH);
            Drawing.SetColor(0.7f, 0.25f, 0.0f, 0.7f);
            Drawing.FillRect(X + W - 6, thumbY, 5, thumbH);
        }
    }

    public override bool HandleKey(int key)
    {
        if (Items.Count == 0) return false;

        switch (key)
        {
            case Keys.K_UPARROW or Keys.K_KP_UPARROW:
                if (SelectedIndex > 0) SelectedIndex--;
                EnsureVisible();
                return true;

            case Keys.K_DOWNARROW or Keys.K_KP_DOWNARROW:
                if (SelectedIndex < Items.Count - 1) SelectedIndex++;
                EnsureVisible();
                return true;

            case Keys.K_PGUP or Keys.K_KP_PGUP:
                SelectedIndex = Math.Max(0, SelectedIndex - _visibleRows);
                EnsureVisible();
                return true;

            case Keys.K_PGDN or Keys.K_KP_PGDN:
                SelectedIndex = Math.Min(Items.Count - 1, SelectedIndex + _visibleRows);
                EnsureVisible();
                return true;

            case Keys.K_HOME or Keys.K_KP_HOME:
                SelectedIndex = 0;
                EnsureVisible();
                return true;

            case Keys.K_END or Keys.K_KP_END:
                SelectedIndex = Items.Count - 1;
                EnsureVisible();
                return true;

            case Keys.K_MWHEELUP:
                ScrollOffset = Math.Max(0, ScrollOffset - 3);
                return true;

            case Keys.K_MWHEELDOWN:
                ScrollOffset = Math.Min(Math.Max(0, Items.Count - _visibleRows), ScrollOffset + 3);
                return true;
        }
        return false;
    }

    public override void Activate()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Items.Count)
            OnSelected?.Invoke(SelectedIndex);
    }

    private void EnsureVisible()
    {
        if (SelectedIndex < ScrollOffset)
            ScrollOffset = SelectedIndex;
        else if (SelectedIndex >= ScrollOffset + _visibleRows)
            ScrollOffset = SelectedIndex - _visibleRows + 1;
    }

    public void SetItems(List<string[]> items)
    {
        Items = items;
        SelectedIndex = items.Count > 0 ? 0 : -1;
        ScrollOffset = 0;
    }
}

#endregion
