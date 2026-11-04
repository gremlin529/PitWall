using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

/// <summary>Track summary page, laid out like the WPF TrackOverviewPanel: 3D meanline map, statistics and lap links.</summary>
internal sealed class TrackOverviewView : Grid
{
    private static readonly IBrush RowBorder = new SolidColorBrush(Color.Parse("#2B3641"));
    private static readonly IBrush DriverText = new SolidColorBrush(Color.Parse("#E8EDF2"));

    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, Text = "Track overview" };
    private readonly TextBlock _subtitle = new() { Foreground = Palette.Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _vehicles = StatValue("—");
    private readonly TextBlock _sessions = StatValue("—");
    private readonly TextBlock _laps = StatValue("Counting…");
    private readonly TextBlock _length = StatValue("—");
    private readonly TextBlock _status = new()
    {
        Foreground = Palette.Muted,
        FontSize = 10,
        Margin = new Thickness(3, 10, 0, 0),
        Text = "Select a track or car to view its overview."
    };
    private readonly TrackMap _map = new() { Enable3DOverview = true };
    private readonly StackPanel _drivers = new();

    public TrackOverviewView()
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        Children.Add(new StackPanel { Margin = new Thickness(2, 0, 2, 14), Children = { _title, _subtitle } });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("1.55*,14,0.85*") };
        SetRow(body, 1);
        Children.Add(body);
        body.Children.Add(new Border
        {
            Background = Palette.Panel,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = _map
        });

        var stats = new UniformGrid { Columns = 2, Rows = 2 };
        stats.Children.Add(Tile("VEHICLES", _vehicles, new Thickness(0, 0, 6, 6)));
        stats.Children.Add(Tile("SESSIONS", _sessions, new Thickness(6, 0, 0, 6)));
        stats.Children.Add(Tile("LAPS", _laps, new Thickness(0, 6, 6, 0)));
        stats.Children.Add(Tile("TRACK LENGTH", _length, new Thickness(6, 6, 0, 0)));
        var right = new DockPanel();
        SetColumn(right, 2);
        var top = new StackPanel
        {
            Children =
            {
                Caption("TRACK INTELLIGENCE", new Thickness(3, 3, 0, 10)),
                stats,
                Caption("CARS & DRIVERS", new Thickness(3, 16, 0, 8))
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new Border
        {
            Background = Palette.Panel,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _drivers
            }
        });
        body.Children.Add(right);

        SetRow(_status, 2);
        Children.Add(_status);
    }

    public event Action<LapNavigationTarget>? LapLinkClicked;

    public void SetHeader(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
    }

    public void SetMap(IReadOnlyList<TrackPoint> points, TrackSegmentMap? segments)
    {
        _map.SegmentMap = segments;
        _map.MeanlinePoints = points;
        _map.Traces = [];
    }

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
            title.Children.Add(new TextBlock
            {
                Text = row.DriverName, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = DriverText
            });
            var laps = new TextBlock
            {
                Text = $"{row.Laps} laps", FontSize = 10, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center
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
                        new TextBlock { Text = car.VehicleName, FontSize = 10, Foreground = Palette.Accent },
                        new TextBlock
                        {
                            Text = $"{car.Sessions} sessions · {car.Laps} laps", FontSize = 9, Foreground = Palette.Muted
                        }
                    }
                };
                var links = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Children =
                    {
                        Link(car.FastestLapLabel, car.FastestLap, car.FastestLapToolTip, Palette.Fastest, 0),
                        Link(car.OptimalLapLabel, car.OptimalLap, car.OptimalLapToolTip, Palette.Optimal, 2)
                    }
                };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,160"), Margin = new Thickness(8, 3, 0, 3) };
                SetColumn(links, 1);
                line.Children.Add(info);
                line.Children.Add(links);
                block.Children.Add(line);
            }

            _drivers.Children.Add(new Border
            {
                BorderBrush = RowBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 7),
                Child = block
            });
        }
    }

    private Button Link(string label, LapNavigationTarget? target, string? tip, IBrush brush, double topMargin)
    {
        var button = new Button
        {
            Content = label,
            Foreground = brush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, topMargin, 0, 0),
            MinHeight = 0,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            IsEnabled = target is not null,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        // Keep the link colour on hover/press instead of the Fluent button foreground.
        button.Resources["ButtonForegroundPointerOver"] = brush;
        button.Resources["ButtonForegroundPressed"] = brush;
        button.Resources["ButtonForegroundDisabled"] = brush;
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
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

    private static TextBlock StatValue(string text) => new() { FontSize = 23, FontWeight = FontWeight.SemiBold, Text = text, Margin = new Thickness(0, 5, 0, 0) };

    private static TextBlock Caption(string text, Thickness margin) =>
        new() { Text = text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Palette.Muted, Margin = margin };

    private static Border Tile(string caption, TextBlock value, Thickness margin) => new()
    {
        Background = Palette.Panel,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(13),
        Margin = margin,
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = caption, FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Palette.Muted },
                value
            }
        }
    };
}
