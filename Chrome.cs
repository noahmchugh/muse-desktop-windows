using System.Drawing;

namespace MuseDesktop;

/// <summary>
/// The shell's color palettes — one per frame shade. The values are
/// the standard dark-desktop family (the Discord/Slack register):
/// neutral grays tinted very slightly cool, one hairline, text in
/// two weights. The page inside the window is muse.ai and is never
/// restyled; these colors dress only the window's own frame and the
/// Settings form.
/// </summary>
internal sealed record ChromePalette(
    Color Bar,
    Color Border,
    Color Text,
    Color SubText,
    Color Hover,
    Color Field,
    Color FormBack);

internal static class Chrome
{
    // The form's own visible frame around the docked content; it is
    // also the resize grip, so it stays a few pixels wide.
    public const int BorderWidth = 5;

    public static readonly Color StatusGreen = Color.FromArgb(59, 165, 93);
    public static readonly Color StatusAmber = Color.FromArgb(250, 168, 26);
    public static readonly Color StatusRed = Color.FromArgb(237, 66, 69);
    public static readonly Color StatusOff = Color.FromArgb(128, 132, 138);
    public static readonly Color AccentBlue = Color.FromArgb(45, 127, 249);
    public static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

    public static ChromePalette For(string? shade) => shade switch
    {
        "Light" => new ChromePalette(
            Bar: Color.FromArgb(242, 243, 245),
            Border: Color.FromArgb(220, 222, 226),
            Text: Color.FromArgb(30, 31, 34),
            SubText: Color.FromArgb(84, 86, 94),
            Hover: Color.FromArgb(227, 229, 232),
            Field: Color.FromArgb(255, 255, 255),
            FormBack: Color.FromArgb(249, 249, 250)),
        "Graphite" => new ChromePalette(
            Bar: Color.FromArgb(43, 45, 49),
            Border: Color.FromArgb(62, 65, 71),
            Text: Color.FromArgb(237, 238, 240),
            SubText: Color.FromArgb(190, 194, 200),
            Hover: Color.FromArgb(65, 68, 74),
            Field: Color.FromArgb(50, 52, 57),
            FormBack: Color.FromArgb(36, 38, 41)),
        _ => new ChromePalette(
            Bar: Color.FromArgb(30, 31, 34),
            Border: Color.FromArgb(43, 45, 49),
            Text: Color.FromArgb(227, 229, 232),
            SubText: Color.FromArgb(181, 186, 193),
            Hover: Color.FromArgb(53, 55, 60),
            Field: Color.FromArgb(38, 39, 43),
            FormBack: Color.FromArgb(30, 31, 34)),
    };
}
