using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Drawable preset chips — white text on dark chips; selected = accent.
/// </summary>
public sealed class UiPresetBar : Drawable
{
    private readonly string[] _items;
    private int _selected;
    private int _hover = -1;
    private readonly List<float> _widths = new();

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
        Size = new Size(320, 34);
        BackgroundColor = UiTheme.PanelBg;
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

    public void SelectByName(string name)
    {
        var idx = Array.FindIndex(_items, t =>
            t.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) SelectedIndex = idx;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.AntiAlias = true;
        g.Clear(UiTheme.PanelBg);
        if (_items.Length == 0) return;

        var font = Fonts.Sans(8);
        var gap = 4f;
        var padX = 8f;
        var h = Height - 4f;
        var y = 2f;
        var x = 0f;
        _widths.Clear();

        for (var i = 0; i < _items.Length; i++)
        {
            var text = _items[i];
            var tw = g.MeasureString(font, text).Width;
            var w = tw + padX * 2;
            _widths.Add(w);

            var bg = i == _selected ? UiTheme.ChipSelected
                : i == _hover ? UiTheme.ChipHover
                : UiTheme.ChipIdle;

            g.FillRectangle(bg, x, y, w, h);
            g.DrawRectangle(new Pen(UiTheme.Track, 1), x, y, w, h);

            // Always white text on dark/accent chips
            var tx = x + (w - tw) * 0.5f;
            var ty = y + (h - font.LineHeight) * 0.5f;
            g.DrawText(font, Colors.White, tx, ty, text);

            x += w + gap;
        }
    }

    private int HitIndex(float mx)
    {
        if (_widths.Count != _items.Length)
        {
            var approx = 56f;
            for (var i = 0; i < _items.Length; i++)
            {
                if (mx >= i * (approx + 4) && mx < (i + 1) * (approx + 4))
                    return i;
            }
            return -1;
        }

        var x = 0f;
        const float gap = 4f;
        for (var i = 0; i < _widths.Count; i++)
        {
            if (mx >= x && mx <= x + _widths[i]) return i;
            x += _widths[i] + gap;
        }
        return -1;
    }
}
