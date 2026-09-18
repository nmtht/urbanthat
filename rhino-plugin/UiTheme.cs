using Eto.Drawing;
using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>
/// Forced dark theme for the plugin panel — independent of Rhino/OS appearance.
/// Rule: black/dark surfaces, white (or near-white) text everywhere.
/// Never put dark text on dark backgrounds.
/// </summary>
public static class UiTheme
{
    // Surfaces
    public static readonly Color PanelBg = Color.FromArgb(28, 28, 30);
    public static readonly Color CardBg = Color.FromArgb(38, 38, 42);
    public static readonly Color InputBg = Color.FromArgb(48, 48, 52);

    // Text (always light on dark)
    public static readonly Color Text = Colors.White;
    public static readonly Color SectionTitle = Colors.White;
    public static readonly Color Muted = Color.FromArgb(180, 180, 185);
    public static readonly Color Danger = Color.FromArgb(255, 120, 100);

    // Accent
    public static readonly Color Accent = Color.FromArgb(70, 160, 200);
    public static Color AccentLight => Soften(Accent, 0.35f);
    public static Color AccentSoft => Soften(Accent, 0.55f);
    public static Color AccentDark => Darken(Accent, 0.25f);

    // Slider track
    public static readonly Color Track = Color.FromArgb(70, 70, 75);
    public static readonly Color TrackFill = Accent;
    public static readonly Color Thumb = Colors.White;
    public static readonly Color ThumbBorder = Accent;

    // Preset chips
    public static readonly Color ChipIdle = Color.FromArgb(55, 55, 60);
    public static readonly Color ChipHover = Color.FromArgb(70, 90, 110);
    public static readonly Color ChipSelected = Accent;
    public static readonly Color ChipText = Colors.White;
    public static readonly Color ChipTextOn = Colors.White;

    // Button styling (Eto Button has limited style control; TextColor helps on some platforms)
    public static readonly Color ButtonText = Colors.White;

    public static Color Soften(Color c, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(c.Rb + (255 - c.Rb) * amount),
            (int)(c.Gb + (255 - c.Gb) * amount),
            (int)(c.Bb + (255 - c.Bb) * amount));
    }

    public static Color Lighten(Color c, float amount) => Soften(c, amount);

    public static Color Darken(Color c, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(c.Rb * (1 - amount)),
            (int)(c.Gb * (1 - amount)),
            (int)(c.Bb * (1 - amount)));
    }

    /// <summary>Apply forced dark appearance to a control tree (labels, panels, text areas).</summary>
    public static void ApplyDark(Control root)
    {
        if (root is null) return;
        try
        {
            root.BackgroundColor = PanelBg;
        }
        catch { /* some controls ignore */ }

        switch (root)
        {
            case Label lb:
                lb.TextColor = Text;
                break;
            case CheckBox cb:
                cb.TextColor = Text;
                break;
            case Button btn:
                btn.TextColor = ButtonText;
                break;
            case TextArea ta:
                ta.TextColor = Text;
                ta.BackgroundColor = InputBg;
                break;
            case TextBox tb:
                tb.TextColor = Text;
                tb.BackgroundColor = InputBg;
                break;
            case DropDown dd:
                dd.TextColor = Text;
                dd.BackgroundColor = InputBg;
                break;
            case GroupBox gb:
                gb.TextColor = Text;
                gb.BackgroundColor = CardBg;
                break;
            case Scrollable sc:
                sc.BackgroundColor = PanelBg;
                break;
            case Panel p:
                p.BackgroundColor = PanelBg;
                break;
            case Drawable d:
                d.BackgroundColor = PanelBg;
                break;
        }

        if (root is Container container)
        {
            foreach (var child in container.Controls)
                ApplyDark(child);
        }
    }
}
