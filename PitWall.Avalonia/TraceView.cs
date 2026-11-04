using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record ChartSpec(string Key, string Title, string Unit, bool Symmetric, IReadOnlyList<ChartLine> Lines);

/// <summary>
/// Data view body, laid out like the WPF DataViewPanel: GPS map on the left, a scrollable stack of
/// 190px telemetry charts on the right, sharing one distance cursor.
/// </summary>
internal sealed class TraceView : Grid
{
    private readonly TrackMap _map = new();
    private readonly StackPanel _charts = new();
    private readonly Dictionary<string, TelemetryChart> _chartByKey = new(StringComparer.OrdinalIgnoreCase);
    private double _cursorDistanceM;

    public TraceView()
    {
        ColumnDefinitions = new ColumnDefinitions("0.78*,12,1.22*");
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(new Border
        {
            Background = Palette.Panel,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _map
        });
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _charts
        };
        SetColumn(scroller, 2);
        Children.Add(scroller);
        var hint = new TextBlock
        {
            Text = "Move over any chart to inspect the same distance on the GPS trace and other plots.",
            FontSize = 10,
            Foreground = Palette.Muted,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetRow(hint, 1);
        SetColumnSpan(hint, 3);
        Children.Add(hint);
    }

    public event Action<double>? CursorChanged;

    public double CursorDistanceM => _cursorDistanceM;

    public void SetData(
        IReadOnlyList<SessionTrace> traces,
        TrackSegmentMap? segments,
        double maxDistanceM,
        bool imperial,
        IReadOnlyList<ChartSpec> charts,
        bool resetCursor)
    {
        _map.SegmentMap = segments;
        _map.Traces = traces;
        _charts.Children.Clear();
        foreach (var spec in charts)
        {
            if (!_chartByKey.TryGetValue(spec.Key, out var chart))
            {
                chart = new TelemetryChart();
                chart.DistanceHovered += SetCursor;
                _chartByKey[spec.Key] = chart;
            }

            chart.Title = spec.Title;
            chart.Unit = spec.Unit;
            chart.SymmetricRange = spec.Symmetric;
            chart.UseImperialDistance = imperial;
            chart.MaxDistanceM = maxDistanceM;
            chart.SegmentBoundariesM = segments?.BoundariesM ?? [];
            chart.Lines = spec.Lines;
            if (chart.Parent is Border previous)
            {
                previous.Child = null;
            }

            _charts.Children.Add(new Border
            {
                Background = Palette.Panel,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Height = 190,
                Margin = new Thickness(0, 0, 0, 9),
                Child = chart
            });
        }

        SetCursor(resetCursor ? 0 : _cursorDistanceM);
    }

    public void SetCursor(double distanceM)
    {
        _cursorDistanceM = distanceM;
        foreach (var chart in _chartByKey.Values)
        {
            chart.CursorDistanceM = distanceM;
        }

        _map.CursorDistanceM = distanceM;
        CursorChanged?.Invoke(distanceM);
    }
}
