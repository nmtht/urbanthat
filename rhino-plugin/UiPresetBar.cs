using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Drawable row of selectable preset chips (massing type, zone type).
/// Click selects; selected chip uses accent color.
/// </summary>
public sealed class UiPresetBar : Drawable
{
    private readonly string[] _items;
    private int _selected;
    private int _hover = -1;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1));
            if (v == _selected) return;
            _selected = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectedItem =>
        _items.Length == 0 ? "" : _items[Math.Clamp(_selected, 0, _items.Length - 1)];

    public event EventHandler? SelectedIndexChanged;

    public UiPresetBar(string[] items, int selected = 0)
    {
        _items = items ?? Array.Empty<string>();
        _selected = Math.Clamp(selected, 0, Math.Max(0, _items.Length - 1));
        Size = new Size(300, 32);
        Cursor = Cursors.Pointer;

        MouseMove += (_, e) =>
        {
            var i = HitIndex(e.Location.X);
            if (i != _hover) { _hover = i; Invalidate(); }
        };
        MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
        MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary) return;
            var i = HitIndex(e.Location.X);
            if (i >= 0) SelectedIndex = i;
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.AntiAlias = true;
        if (_items.Length == 0) return;

        var font = Fonts.Sans(8);
        var gap = 4f;
        var padX = 8f;
        var h = Height - 4f;
        var y = 2f;
        var x = 0f;

        for (var i = 0; i < _items.Length; i++)
        {
            var text = _items[i];
            var tw = g.MeasureString(font, text).Width;
            var w = tw + padX * 2;

            var bg = i == _selected ? UiTheme.ChipSelected
                : i == _hover ? UiTheme.ChipHover
                : UiTheme.ChipIdle;
            var fg = i == _selected ? UiTheme.ChipTextOn : UiTheme.ChipText;

            g.FillRectangle(bg, x, y, w, h);
            // subtle border
            g.DrawRectangle(new Pen(UiTheme.Soft(AccentBorder(i == _selected), 0.3f), 1),
                x, y, w, h);

            var tx = x + (w - tw) * 0.5f;
            var ty = y + (h - font.LineHeight) * 0.5f;
            g.DrawText(font, fg, tx, ty, text);

            x += w + gap;
        }
    }

    private static Color AccentBorder(bool selected) =>
        selected ? UiTheme.AccentDark : UiTheme.Track;

    private int HitIndex(float mx)
    {
        // Approximate same layout as paint
        using var bmp = new Bitmap(new Size(4, 4), PixelFormat.Format32bppRgba);
        using var g = new Graphics(bmp);
        var font = Fonts.Sans(8);
        var gap = 4f;
        var padX = 8f;
        var x = 0f;
        for (var i = 0; i < _items.Length; i++)
        {
            var tw = g.MeasureString(font, _items[i]).Width;
            var w = tw + padX * 2;
            if (mx >= x && mx <= x + w) return i;
            x += w + gap;
        }
        return -1;
    }
}
