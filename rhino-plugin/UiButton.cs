using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Custom painted button — ignores macOS/Rhino system theme.
/// Primary = accent fill + white text; Secondary = dark chip + white text.
/// </summary>
public sealed class UiButton : Drawable
{
    public enum Style
    {
        Primary,
        Secondary,
    }

    private readonly string _text;
    private readonly Style _style;
    private bool _hover;
    private bool _pressed;

    public event EventHandler? Click;

    public UiButton(string text, Style style = Style.Secondary)
    {
        _text = text ?? "";
        _style = style;
        BackgroundColor = UiTheme.PanelBg;
        Cursor = Cursors.Pointer;

        // Auto-size roughly from text length
        var approxW = Math.Max(72, 10 + _text.Length * 7);
        Size = new Size(approxW, 28);

        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Invalidate(); };
        MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
            {
                _pressed = true;
                Invalidate();
            }
        };
        MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary && _pressed)
            {
                _pressed = false;
                Invalidate();
                if (new RectangleF(0, 0, Width, Height).Contains(e.Location))
                    Click?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Preferred width after first paint (optional).</summary>
    public void FitToText(int minWidth = 72, int padX = 14)
    {
        // Will be refined on paint; set a reasonable minimum now
        Width = Math.Max(minWidth, padX * 2 + _text.Length * 7);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.AntiAlias = true;
        g.Clear(UiTheme.PanelBg);

        Color bg;
        if (_style == Style.Primary)
        {
            bg = _pressed ? UiTheme.AccentDark
                : _hover ? UiTheme.AccentLight
                : UiTheme.Accent;
        }
        else
        {
            bg = _pressed ? Color.FromArgb(80, 80, 88)
                : _hover ? Color.FromArgb(70, 70, 78)
                : UiTheme.ChipIdle;
        }

        const float radius = 4f;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        FillRoundRect(g, bg, rect, radius);

        if (_style == Style.Secondary)
            g.DrawRectangle(new Pen(UiTheme.Track, 1), rect.X, rect.Y, rect.Width, rect.Height);

        var font = Fonts.Sans(9);
        var tw = g.MeasureString(font, _text).Width;
        var th = font.LineHeight;
        var tx = (Width - tw) * 0.5f;
        var ty = (Height - th) * 0.5f;
        g.DrawText(font, Colors.White, tx, ty, _text);
    }

    private static void FillRoundRect(Graphics g, Color color, RectangleF r, float radius)
    {
        // Eto has no FillRoundedRectangle on all platforms — approximate with rect
        // (sharp corners are fine for dense tool panels).
        g.FillRectangle(color, r);
    }
}
