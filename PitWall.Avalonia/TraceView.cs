using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record ChartSeries(IReadOnlyList<ChartValue> Values, bool IsReference, bool Dashed = false);

internal sealed record ChartPanel(string Title, string Units, IReadOnlyList<ChartSeries> Series, bool Symmetric);

/// <summary>Offline GPS map beside a scrollable stack of distance plots with a shared hover cursor.</summary>
internal sealed class TraceView : Grid
{
    private readonly TraceMapView _map = new();
    private readonly PlotsView _plots = new();

    public TraceView()
    {
        ColumnDefinitions = new ColumnDefinitions("3*,5*");
        Background = Brushes.Transparent;
        Children.Add(_map);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _plots
        };
        SetColumn(scroller, 1);
        Children.Add(scroller);
        _plots.CursorChanged += distance =>
        {
            _map.SetCursor(distance);
            CursorChanged?.Invoke(distance);
        };
    }

    public event Action<double?>? CursorChanged;

    public void SetData(SessionTrace? primary, SessionTrace? reference, IReadOnlyList<ChartPanel> panels)
    {
        _map.SetData(primary, reference);
        _plots.SetData(primary, reference, panels);
    }

    internal static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    internal static readonly IBrush ReferenceBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    internal static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#1A222B"));
    internal static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#94A0AD"));
    internal static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#28323D")), 1);
    internal static readonly IPen CursorPen = new Pen(Brushes.White, 1);
    internal static readonly Typeface UiFont = new(Palette.FontFamilyName);

    internal static void DrawText(DrawingContext context, string text, Point origin, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            UiFont, size, TextBrush);
        context.DrawText(formatted, origin);
    }

    internal static void DrawPolyline(DrawingContext context, IEnumerable<(Point Point, bool Valid)> points, IPen pen)
    {
        var geometry = new StreamGeometry();
        var open = false;
        var any = false;
        using (var stream = geometry.Open())
        {
            foreach (var (point, valid) in points)
            {
                if (!valid)
                {
                    if (open)
                    {
                        stream.EndFigure(false);
                        open = false;
                    }

                    continue;
                }

                if (open)
                {
                    stream.LineTo(point);
                }
                else
                {
                    stream.BeginFigure(point, false);
                    open = true;
                }

                any = true;
            }

            if (open)
            {
                stream.EndFigure(false);
            }
        }

        if (any)
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}

internal sealed class TraceMapView : Control
{
    private SessionTrace? _primary;
    private SessionTrace? _reference;
    private double? _cursorDistanceM;

    public void SetData(SessionTrace? primary, SessionTrace? reference)
    {
        _primary = primary;
        _reference = reference;
        _cursorDistanceM = null;
        InvalidateVisual();
    }

    public void SetCursor(double? distanceM)
    {
        _cursorDistanceM = distanceM;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = new Rect(8, 8, Math.Max(0, Bounds.Width - 8), Math.Max(0, Bounds.Height - 16));
        context.FillRectangle(TraceView.PanelBrush, rect, 8);
        if (_primary is null)
        {
            TraceView.DrawText(context, "Select a lap", new Point(rect.X + 12, rect.Y + 12), 14);
            return;
        }

        DrawMap(context, rect);
    }

    private void DrawMap(DrawingContext context, Rect rect)
    {
        var traces = new[] { _reference, _primary }.OfType<SessionTrace>().ToArray();
        var coords = traces.SelectMany(trace => trace.Points)
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        if (coords.Length < 2 || rect.Width < 20 || rect.Height < 20)
        {
            return;
        }

        var minLat = coords.Min(point => point.Latitude);
        var maxLat = coords.Max(point => point.Latitude);
        var minLon = coords.Min(point => point.Longitude);
        var maxLon = coords.Max(point => point.Longitude);
        var lonScale = Math.Cos((minLat + maxLat) / 2 * Math.PI / 180);
        var spanX = Math.Max((maxLon - minLon) * lonScale, 1e-9);
        var spanY = Math.Max(maxLat - minLat, 1e-9);
        const double pad = 20;
        var scale = Math.Min((rect.Width - pad * 2) / spanX, (rect.Height - pad * 2) / spanY);
        var offsetX = rect.X + (rect.Width - spanX * scale) / 2;
        var offsetY = rect.Y + (rect.Height - spanY * scale) / 2;

        Point Project(TelemetryPoint point) => new(
            offsetX + (point.Longitude - minLon) * lonScale * scale,
            offsetY + (maxLat - point.Latitude) * scale);

        using (context.PushClip(rect))
        {
            foreach (var trace in traces)
            {
                var isPrimary = ReferenceEquals(trace, _primary);
                TraceView.DrawPolyline(context,
                    trace.Points.Where(point => double.IsFinite(point.Latitude) &&
                                                double.IsFinite(point.Longitude))
                        .Select(point => (Project(point), true)),
                    new Pen(isPrimary ? TraceView.PrimaryBrush : TraceView.ReferenceBrush, isPrimary ? 3 : 2,
                        isPrimary ? null : DashStyle.Dash));
            }

            if (_cursorDistanceM is { } distance)
            {
                var marker = NearestByDistance(_primary!.Points, distance);
                if (double.IsFinite(marker.Latitude) && double.IsFinite(marker.Longitude))
                {
                    context.DrawEllipse(Brushes.White, null, Project(marker), 5, 5);
                }
            }
        }
    }

    private static TelemetryPoint NearestByDistance(IReadOnlyList<TelemetryPoint> points, double distanceM) =>
        points.MinBy(point => Math.Abs(point.DistanceM - distanceM));
}

internal sealed class PlotsView : Control
{
    private const double MinPanelHeight = 150;
    private SessionTrace? _primary;
    private SessionTrace? _reference;
    private IReadOnlyList<ChartPanel> _panels = [];
    private double? _cursorDistanceM;

    public event Action<double?>? CursorChanged;

    public void SetData(SessionTrace? primary, SessionTrace? reference, IReadOnlyList<ChartPanel> panels)
    {
        _primary = primary;
        _reference = reference;
        _panels = panels;
        _cursorDistanceM = null;
        MinHeight = panels.Count * MinPanelHeight + 16;
        InvalidateMeasure();
        InvalidateVisual();
        CursorChanged?.Invoke(null);
    }

    private double MaxDistanceM => Math.Max(_primary?.DistanceM ?? 0, _reference?.DistanceM ?? 0);

    private void SetCursor(double? distance)
    {
        _cursorDistanceM = distance;
        InvalidateVisual();
        CursorChanged?.Invoke(distance);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_primary is null || Bounds.Width <= 60 || MaxDistanceM <= 0)
        {
            return;
        }

        var plotLeft = 8 + 56;
        var fraction = (e.GetPosition(this).X - plotLeft) / (Bounds.Width - 8 - plotLeft);
        SetCursor(fraction is >= 0 and <= 1 ? fraction * MaxDistanceM : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetCursor(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = new Rect(8, 8, Math.Max(0, Bounds.Width - 16), Math.Max(0, Bounds.Height - 16));
        context.FillRectangle(TraceView.PanelBrush, rect, 8);
        if (_primary is not null)
        {
            DrawPlots(context, rect);
        }
    }

    private void DrawPlots(DrawingContext context, Rect rect)
    {
        if (_panels.Count == 0 || MaxDistanceM <= 0)
        {
            return;
        }

        var panelHeight = rect.Height / _panels.Count;
        var left = rect.X + 56;
        var right = rect.Right - 8;
        for (var index = 0; index < _panels.Count; index++)
        {
            var panel = _panels[index];
            var area = new Rect(left, rect.Y + index * panelHeight + 18, right - left,
                Math.Max(10, panelHeight - 26));
            var values = panel.Series.SelectMany(series => series.Values)
                .Select(value => value.Value).Where(double.IsFinite).ToArray();
            TraceView.DrawText(context, $"{panel.Title} ({panel.Units})", new Point(left, area.Y - 17), 11);
            context.DrawRectangle(null, TraceView.GridPen, area);
            if (values.Length == 0)
            {
                TraceView.DrawText(context, "No data", new Point(area.X + 8, area.Y + 6), 11);
                continue;
            }

            var min = values.Min();
            var max = values.Max();
            if (panel.Symmetric)
            {
                var extent = Math.Max(Math.Abs(min), Math.Abs(max));
                min = -extent;
                max = extent;
            }

            if (max - min < 1e-9)
            {
                min -= 1;
                max += 1;
            }

            Point Map(ChartValue value) => new(
                area.X + value.DistanceM / MaxDistanceM * area.Width,
                area.Bottom - (value.Value - min) / (max - min) * area.Height);

            TraceView.DrawText(context, max.ToString("0.##", CultureInfo.InvariantCulture),
                new Point(rect.X + 4, area.Y - 2), 10);
            TraceView.DrawText(context, min.ToString("0.##", CultureInfo.InvariantCulture),
                new Point(rect.X + 4, area.Bottom - 12), 10);
            if (min < 0 && max > 0)
            {
                var zeroY = area.Bottom - (0 - min) / (max - min) * area.Height;
                context.DrawLine(TraceView.GridPen, new Point(area.X, zeroY), new Point(area.Right, zeroY));
            }

            using (context.PushClip(area))
            {
                foreach (var series in panel.Series.OrderByDescending(item => item.IsReference))
                {
                    var brush = series.IsReference ? TraceView.ReferenceBrush : TraceView.PrimaryBrush;
                    var pen = new Pen(brush, series.IsReference ? 1.5 : 1.75,
                        series.Dashed || series.IsReference ? DashStyle.Dash : null);
                    DrawSeries(context, series.Values, Map, pen);
                }
            }

            if (_cursorDistanceM is { } cursor)
            {
                var x = area.X + cursor / MaxDistanceM * area.Width;
                context.DrawLine(TraceView.CursorPen, new Point(x, area.Y), new Point(x, area.Bottom));
                var nearest = panel.Series.FirstOrDefault(series => !series.IsReference)?.Values
                    .Where(value => double.IsFinite(value.Value))
                    .OrderBy(value => Math.Abs(value.DistanceM - cursor)).FirstOrDefault();
                if (nearest is not null)
                {
                    TraceView.DrawText(context, nearest.Value.ToString("0.###", CultureInfo.InvariantCulture),
                        new Point(Math.Min(x + 4, area.Right - 50), area.Y + 2), 11);
                }
            }
        }
    }

    private static void DrawSeries(
        DrawingContext context, IReadOnlyList<ChartValue> values, Func<ChartValue, Point> map, IPen pen) =>
        TraceView.DrawPolyline(context, values.Select(value => (map(value), double.IsFinite(value.Value))), pen);
}
