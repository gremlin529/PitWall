using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record ChartSpec(string Key, string Title, string Unit, bool Symmetric, IReadOnlyList<ChartLine> Lines);

/// <summary>
/// Data view body, laid out like the WPF DataViewPanel: GPS map on the left, a scrollable stack of
/// resizable telemetry charts on the right, sharing one distance cursor and one zoomed distance window.
/// </summary>
internal sealed class TraceView : Grid
{
    private const double DefaultChartHeight = 190;
    private const double MinimumChartHeight = 110;
    private const double MaximumChartHeight = 900;

    private static readonly IBrush GripIdleBrush = new SolidColorBrush(Color.FromArgb(90, 144, 157, 171));
    private static readonly IBrush GripHoverBrush = new SolidColorBrush(Color.FromArgb(220, 236, 187, 83));

    private readonly TrackMap _map = new();
    private readonly StackPanel _charts = new();
    private readonly ScrollBar _distanceScrollBar;
    private readonly Dictionary<string, TelemetryChart> _chartByKey = new(StringComparer.OrdinalIgnoreCase);
    private double _cursorDistanceM;

    // The distance window every chart shows: [start, end] metres, or the whole lap when end is NaN.
    private double _viewStartM;
    private double _viewEndM = double.NaN;
    private bool _updatingScrollBar;

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
        // Pans the distance axis of every chart once zoomed in with the mouse wheel.
        _distanceScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            AllowAutoHide = false,
            Height = 14,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };
        _distanceScrollBar.Scroll += (_, e) => OnDistanceScroll(e.NewValue);
        var chartColumn = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        chartColumn.Children.Add(scroller);
        SetRow(_distanceScrollBar, 1);
        chartColumn.Children.Add(_distanceScrollBar);
        SetColumn(chartColumn, 2);
        Children.Add(chartColumn);
        var hint = new TextBlock
        {
            Text = "Move over any chart to inspect the same distance on the GPS trace and other plots. " +
                   "Scroll the wheel over a chart to zoom; drag a chart's bottom edge to resize it.",
            FontSize = 10,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetRow(hint, 1);
        SetColumnSpan(hint, 3);
        Children.Add(hint);
    }

    public event Action<double>? CursorChanged;

    /// <summary>Raised when the user finishes dragging a chart's bottom edge: chart key and new height.</summary>
    public event Action<string, double>? ChartHeightChanged;

    public double CursorDistanceM => _cursorDistanceM;

    public void SetData(
        IReadOnlyList<SessionTrace> traces,
        TrackSegmentMap? segments,
        double maxDistanceM,
        bool imperial,
        IReadOnlyList<ChartSpec> charts,
        IReadOnlyDictionary<string, double> chartHeights,
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
                chart.ViewChangeRequested += SetView;
                _chartByKey[spec.Key] = chart;
            }

            chart.Title = spec.Title;
            chart.Unit = spec.Unit;
            chart.SymmetricRange = spec.Symmetric;
            chart.UseImperialDistance = imperial;
            chart.MaxDistanceM = maxDistanceM;
            chart.SegmentBoundariesM = segments?.BoundariesM ?? [];
            chart.Lines = spec.Lines;
            if (chart.Parent is Panel previous)
            {
                previous.Children.Remove(chart);
            }

            var height = chartHeights.TryGetValue(spec.Key, out var saved) && double.IsFinite(saved)
                ? Math.Clamp(saved, MinimumChartHeight, MaximumChartHeight)
                : DefaultChartHeight;
            _charts.Children.Add(CreateResizableChartHost(spec.Key, chart, height));
        }

        if (resetCursor)
        {
            // Picking a lap shows the whole lap again.
            _viewStartM = 0;
            _viewEndM = double.NaN;
        }

        ApplyView();
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

    // A chart in a panel whose bottom edge can be dragged to change its height.
    private Border CreateResizableChartHost(string chartKey, TelemetryChart chart, double height)
    {
        var gripLine = new Border
        {
            Height = 2,
            Width = 36,
            CornerRadius = new CornerRadius(1),
            Background = GripIdleBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var grip = new Border
        {
            Height = 7,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = gripLine
        };
        ToolTip.SetTip(grip, "Drag to resize this chart");
        var host = new Border
        {
            Background = Palette.Panel,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Height = height,
            Margin = new Thickness(0, 0, 0, 9),
            Child = new Grid { Children = { chart, grip } }
        };

        double? dragOffset = null; // host height minus pointer Y at drag start
        grip.PointerEntered += (_, _) => gripLine.Background = GripHoverBrush;
        grip.PointerExited += (_, _) =>
        {
            if (dragOffset is null)
            {
                gripLine.Background = GripIdleBrush;
            }
        };
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }

            dragOffset = host.Height - e.GetPosition(host).Y;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragOffset is { } offset)
            {
                host.Height = Math.Clamp(e.GetPosition(host).Y + offset, MinimumChartHeight, MaximumChartHeight);
            }
        };
        void EndDrag()
        {
            if (dragOffset is null)
            {
                return;
            }

            dragOffset = null;
            if (!grip.IsPointerOver)
            {
                gripLine.Background = GripIdleBrush;
            }

            ChartHeightChanged?.Invoke(chartKey, Math.Round(host.Height));
        }

        grip.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            EndDrag();
        };
        grip.PointerCaptureLost += (_, _) => EndDrag();
        return host;
    }

    private void SetView(double startM, double endM)
    {
        _viewStartM = startM;
        _viewEndM = endM;
        ApplyView();
    }

    private void ApplyView()
    {
        foreach (var chart in _chartByKey.Values)
        {
            chart.SetView(_viewStartM, _viewEndM);
        }

        UpdateScrollBar();
    }

    private void UpdateScrollBar()
    {
        var chart = _chartByKey.Values.FirstOrDefault();
        var maxDistance = chart?.MaxDistanceM ?? 0;
        var start = chart?.ViewStartM ?? 0;
        var span = chart is null ? 0 : chart.ViewEndM - start;
        var zoomed = chart is not null && double.IsFinite(_viewEndM) && span < maxDistance - 0.5;
        _distanceScrollBar.IsVisible = zoomed;
        if (!zoomed)
        {
            return;
        }

        _updatingScrollBar = true;
        _distanceScrollBar.Minimum = 0;
        _distanceScrollBar.Maximum = maxDistance - span;
        _distanceScrollBar.ViewportSize = span;
        _distanceScrollBar.SmallChange = span * 0.05;
        _distanceScrollBar.LargeChange = span * 0.9;
        _distanceScrollBar.Value = start;
        _updatingScrollBar = false;
    }

    private void OnDistanceScroll(double value)
    {
        if (_updatingScrollBar || !double.IsFinite(_viewEndM))
        {
            return;
        }

        var span = _viewEndM - _viewStartM;
        SetView(value, value + span);
    }
}
