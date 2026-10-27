using Avalonia.Media;

namespace PitWallApp;

internal static class Palette
{
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D5DCE3"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A0AD"));
    public static readonly IBrush Optimal = new SolidColorBrush(Color.Parse("#CE93D8"));
    public static readonly IBrush Fastest = new SolidColorBrush(Color.Parse("#81C784"));
    public static readonly IBrush Wet = new SolidColorBrush(Color.Parse("#64B5F6"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1A222B"));
    public const string FontFamilyName = "Helvetica Neue, Helvetica, Arial";
}
