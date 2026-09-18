using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Drawable horizontal value slider (drag via MouseDown/Move).
/// Used for FAR / green_ratio / height.
/// </summary>
public sealed class UiValueSlider : Drawable
{
    private double _value;
    private bool _dragging;
    private readonly string _label;

    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double Step { get; set; } = 0.05;
    public string Unit { get; set; } = "";
    public string FormatString { get; set; } = "0.##";

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(v - _value) < 1e-9) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    public UiValueSlider(string label, double min, double max, double initial)
    {
        _label = label;
        Minimum = min;
        Maximum = max;
        _value = Math.Clamp(initial, min, max);
        Size = new Size(280, 36);
        Cursor = Cursors.Pointer;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => _dragging = false;
        MouseLeave += (_, _) => _dragging = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.AntiAlias = true;

        var labelFont = Fonts.Sans(8);
        var valueFont = Fonts.Sans(9);

        // Label + value
        g.DrawText(labelFont, UiTheme.Muted, 0, 0, _label);
        var valueText = _value.ToString(FormatString, System.Globalization.CultureInfo.InvariantCulture) + Unit;
        var vw = g.MeasureString(valueFont, valueText).Width;
        g.DrawText(valueFont, UiTheme.SectionTitle, Width - vw - 2, 0, valueText);

        // Track
        var trackY = 20f;
        var trackH = 6f;
        var trackX = 4f;
        var trackW = Width - 8f;
        g.FillRectangle(UiTheme.Track, trackX, trackY, trackW, trackH);

        var t = Maximum > Minimum ? (_value - Minimum) / (Maximum - Minimum) : 0;
        t = Math.Clamp(t, 0, 1);
        var fillW = (float)(trackW * t);
        if (fillW > 0)
            g.FillRectangle(UiTheme.TrackFill, trackX, trackY, fillW, trackH);

        // Thumb
        var thumbR = 8f;
        var cx = trackX + fillW;
        var cy = trackY + trackH * 0.5f;
        g.FillEllipse(UiTheme.Thumb, cx - thumbR, cy - thumbR, thumbR * 2, thumbR * 2);
        g.DrawEllipse(new Pen(UiTheme.ThumbBorder, 1.5f), cx - thumbR, cy - thumbR, thumbR * 2, thumbR * 2);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Buttons != MouseButtons.Primary) return;
        _dragging = true;
        SetFromX(e.Location.X);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || e.Buttons != MouseButtons.Primary) return;
        SetFromX(e.Location.X);
    }

    private void SetFromX(float x)
    {
        var trackX = 4f;
        var trackW = Width - 8f;
        var t = trackW > 0 ? (x - trackX) / trackW : 0;
        t = Math.Clamp(t, 0, 1);
        var raw = Minimum + t * (Maximum - Minimum);
        if (Step > 0)
            raw = Math.Round(raw / Step) * Step;
        Value = raw;
    }
}
