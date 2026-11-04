using Avalonia.Media;

namespace PitWallApp;

// Colours match the WPF app exactly (it uses the named WPF brushes MediumPurple, LimeGreen, DeepSkyBlue, Orange).
internal static class Palette
{
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D5DCE3"));
    public static readonly IBrush TreeMuted = new SolidColorBrush(Color.FromRgb(151, 164, 177));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A0AD"));
    public static readonly IBrush Optimal = new SolidColorBrush(Colors.MediumPurple);
    public static readonly IBrush Fastest = new SolidColorBrush(Colors.LimeGreen);
    public static readonly IBrush Wet = new SolidColorBrush(Colors.DeepSkyBlue);
    public static readonly IBrush Primary = new SolidColorBrush(Colors.DeepSkyBlue);
    public static readonly IBrush Reference = new SolidColorBrush(Colors.Orange);
    public static readonly IBrush DeltaGain = new SolidColorBrush(Colors.LightGreen);
    public static readonly IBrush DeltaLoss = new SolidColorBrush(Colors.Orange);
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#39C3D8"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1A222B"));
    public static readonly IBrush White = new SolidColorBrush(Colors.White);
    public const string FontFamilyName = "Segoe UI, Helvetica Neue, Helvetica, Arial";
}
