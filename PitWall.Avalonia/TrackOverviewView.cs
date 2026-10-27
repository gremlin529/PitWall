using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

/// <summary>Draws a track meanline, mirroring the WPF TrackMap's meanline-only mode.</summary>
internal sealed class MeanlineView : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1A222B"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#20B2AA"));
    private IReadOnlyList<TrackPoint> _points = [];

    public void SetPoints(IReadOnlyList<TrackPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var points = _points.Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        if (points.Length < 2)
        {
            TraceView.DrawText(context, "Track map unavailable", new Point(16, 18), 12);
            return;
        }

        TraceView.DrawText(context, "TRACK MEANLINE", new Point(14, 12), 9);
        var minLat = points.Min(point => point.Latitude);
        var maxLat = points.Max(point => point.Latitude);
        var minLon = points.Min(point => point.Longitude);
        var maxLon = points.Max(point => point.Longitude);
        var lonScale = Math.Max(0.01, Math.Cos((minLat + maxLat) / 2 * Math.PI / 180));
        var spanX = Math.Max((maxLon - minLon) * lonScale, 1e-6);
        var spanY = Math.Max(maxLat - minLat, 1e-6);
        var area = new Rect(24, 36, Math.Max(1, Bounds.Width - 48), Math.Max(1, Bounds.Height - 64));
        var scale = Math.Min(area.Width / spanX, area.Height / spanY);
        var left = area.X + (area.Width - spanX * scale) / 2;
        var top = area.Y + (area.Height - spanY * scale) / 2;
        TraceView.DrawPolyline(context,
            points.Select(point => (new Point(
                left + (point.Longitude - minLon) * lonScale * scale,
                top + (maxLat - point.Latitude) * scale), true)),
            new Pen(LineBrush, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
    }
}

/// <summary>Track summary page: title, statistics, meanline map and per-driver fastest/optimal lap links.</summary>
internal sealed class TrackOverviewView : Grid
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A0AD"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1A222B"));
    private static readonly IBrush Fastest = new SolidColorBrush(Color.Parse("#81C784"));
    private static readonly IBrush Optimal = new SolidColorBrush(Color.Parse("#CE93D8"));

    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _subtitle = new() { Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _vehicles = StatValue();
    private readonly TextBlock _sessions = StatValue();
    private readonly TextBlock _laps = StatValue();
    private readonly TextBlock _length = StatValue();
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly MeanlineView _map = new();
    private readonly StackPanel _drivers = new();

    public TrackOverviewView()
    {
        Margin = new Thickness(8);
        RowDefinitions = new RowDefinitions("Auto,*");
        Children.Add(new StackPanel { Margin = new Thickness(2, 0, 2, 12), Children = { _title, _subtitle } });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,4*") };
        SetRow(body, 1);
        Children.Add(body);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _map });

        var stats = new UniformGrid { Columns = 2, Rows = 2 };
        stats.Children.Add(Tile("VEHICLES", _vehicles));
        stats.Children.Add(Tile("SESSIONS", _sessions));
        stats.Children.Add(Tile("LAPS", _laps));
        stats.Children.Add(Tile("TRACK LENGTH", _length));
        var right = new DockPanel();
        SetColumn(right, 2);
        var top = new StackPanel
        {
            Children =
            {
                Caption("TRACK INTELLIGENCE", new Thickness(3, 3, 0, 10)),
                stats,
                _status,
                Caption("CARS & DRIVERS", new Thickness(3, 16, 0, 8))
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new Border
        {
            Background = Panel,
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = _drivers }
        });
        body.Children.Add(right);
    }

    public event Action<LapNavigationTarget>? LapLinkClicked;

    public void SetHeader(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
    }

    public void SetSubtitle(string subtitle) => _subtitle.Text = subtitle;

    public void SetMeanline(IReadOnlyList<TrackPoint> points) => _map.SetPoints(points);

    public void SetStats(string vehicles, string sessions, string laps, string length, string status)
    {
        _vehicles.Text = vehicles;
        _sessions.Text = sessions;
        _laps.Text = laps;
        _length.Text = length;
        _status.Text = status;
    }

    public void SetStatus(string status) => _status.Text = status;

    public void SetLength(string length) => _length.Text = length;

    public void SetDrivers(IReadOnlyList<TrackDriverStats> rows)
    {
        _drivers.Children.Clear();
        foreach (var row in rows)
        {
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            title.Children.Add(new TextBlock { Text = row.DriverName, FontSize = 12, FontWeight = FontWeight.SemiBold });
            var laps = new TextBlock
            {
                Text = $"{row.Laps} laps", FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
            };
            SetColumn(laps, 1);
            title.Children.Add(laps);

            var block = new StackPanel { Children = { title } };
            foreach (var car in row.Cars)
            {
                var info = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = car.VehicleName, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#4FC3F7")) },
                        new TextBlock { Text = $"{car.Sessions} sessions", FontSize = 9, Foreground = Muted }
                    }
                };
                var links = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        Link(car.FastestLapLabel, car.FastestLap, car.FastestLapToolTip, Fastest),
                        Link(car.OptimalLapLabel, car.OptimalLap, car.OptimalLapToolTip, Optimal)
                    }
                };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,170"), Margin = new Thickness(8, 3, 0, 3) };
                SetColumn(links, 1);
                line.Children.Add(info);
                line.Children.Add(links);
                block.Children.Add(line);
            }

            _drivers.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2B3641")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 7),
                Child = block
            });
        }
    }

    private Button Link(string label, LapNavigationTarget? target, string? tip, IBrush brush)
    {
        var button = new Button
        {
            Content = label,
            Foreground = brush,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 2),
            FontSize = 11,
            IsEnabled = target is not null
        };
        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
        }

        button.Click += (_, _) =>
        {
            if (target is not null)
            {
                LapLinkClicked?.Invoke(target);
            }
        };
        return button;
    }

    private static TextBlock StatValue() => new() { FontSize = 23, FontWeight = FontWeight.SemiBold, Text = "—" };

    private static TextBlock Caption(string text, Thickness margin) =>
        new() { Text = text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Muted, Margin = margin };

    private static Border Tile(string caption, TextBlock value) => new()
    {
        Background = Panel,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(13),
        Margin = new Thickness(3),
        Child = new StackPanel { Children = { Caption(caption, new Thickness(0, 0, 0, 5)), value } }
    };
}
