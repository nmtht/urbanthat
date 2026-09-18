using Eto.Drawing;

namespace UrbanBridge.Plugin;

/// <summary>
/// Accent palette for custom Drawable controls.
/// Rule: on white/light panel backgrounds all body text is black.
/// </summary>
public static class UiTheme
{
    /// <summary>Primary accent (teal-blue) — for chips, fills, section headers only.</summary>
    public static readonly Color Accent = Color.FromArgb(45, 125, 160);

    public static Color AccentLight => Lighten(Accent, 0.35f);
    public static Color AccentSoft => Soften(Accent, 0.55f);
    public static Color AccentDark => Darken(Accent, 0.25f);

    public static Color Track => Color.FromArgb(220, 226, 230);
    public static Color TrackFill => Accent;
    public static Color Thumb => Colors.White;
    public static Color ThumbBorder => AccentDark;

    public static Color ChipIdle => Color.FromArgb(235, 240, 243);
    public static Color ChipHover => AccentSoft;
    public static Color ChipSelected => Accent;
    public static Color ChipText => Colors.Black;
    public static Color ChipTextOn => Colors.White;

    /// <summary>Primary body / title text on light backgrounds — always black.</summary>
    public static Color Text => Colors.Black;

    /// <summary>Section titles — black (accent used only as underline/icon, not body).</summary>
    public static Color SectionTitle => Colors.Black;

    /// <summary>Secondary hints — dark gray (still readable on white).</summary>
    public static Color Muted => Color.FromArgb(70, 70, 70);

    public static Color PanelBg => Colors.White;
    public static Color CardBg => Colors.White;
    public static Color Danger => Color.FromArgb(180, 40, 30);

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

    public static Color Pastel(int uniqueIndex)
    {
        var random = new Random(uniqueIndex + 3);
        return Color.FromArgb(
            random.Next(150, 240),
            random.Next(140, 240),
            random.Next(120, 220));
    }
}
