using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrackTelemetryViewer;

internal sealed class TrackMap : FrameworkElement
{
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(35, 44, 54));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(144, 157, 171));
    private IReadOnlyList<SessionTrace> _traces = [];
    private IReadOnlyList<TrackPoint> _meanlinePoints = [];
    private double _cursorDistanceM = double.NaN;
    private DrawingGroup? _cachedStaticDrawing;
    private Size _cachedDrawingSize;
    private IReadOnlyList<IReadOnlyList<ProjectedTrackPoint>> _projectedTraces = [];

    public TrackMap()
    {
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<SessionTrace> Traces
    {
        get => _traces;
        set
        {
            _traces = value;
            _cachedStaticDrawing = null;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<TrackPoint> MeanlinePoints
    {
        get => _meanlinePoints;
        set
        {
            _meanlinePoints = value;
            _cachedStaticDrawing = null;
            InvalidateVisual();
        }
    }

    public double CursorDistanceM
    {
        get => _cursorDistanceM;
        set
        {
            _cursorDistanceM = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = new Size(ActualWidth, ActualHeight);
        if (_cachedStaticDrawing is null || _cachedDrawingSize != size)
        {
            var cachedDrawing = new DrawingGroup();
            using (var cachedContext = cachedDrawing.Open())
            {
                DrawStaticContent(cachedContext);
            }

            cachedDrawing.Freeze();
            _cachedStaticDrawing = cachedDrawing;
            _cachedDrawingSize = size;
        }

        drawingContext.DrawDrawing(_cachedStaticDrawing);
        for (var index = 0; index < _projectedTraces.Count; index++)
        {
            var sample = FindNearestPoint(_projectedTraces[index], _cursorDistanceM);
            if (sample is null)
            {
                continue;
            }

            var color = index == 0 ? Brushes.DeepSkyBlue : Brushes.Orange;
            var radius = index == 0 ? 5 : 4;
            drawingContext.DrawEllipse(color, new Pen(Brushes.White, 1),
                sample.Value.Position, radius, radius);
        }
    }

    private void DrawStaticContent(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 24, 30)), null, bounds);

        for (var x = 0d; x < ActualWidth; x += 36)
        {
            drawingContext.DrawLine(new Pen(GridBrush, 0.7), new Point(x, 0), new Point(x, ActualHeight));
        }

        for (var y = 0d; y < ActualHeight; y += 36)
        {
            drawingContext.DrawLine(new Pen(GridBrush, 0.7), new Point(0, y), new Point(ActualWidth, y));
        }

        var gpsPoints = _traces.SelectMany(trace => trace.Points)
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        var meanlinePoints = _meanlinePoints
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        var projectedTraces = new List<IReadOnlyList<ProjectedTrackPoint>>(_traces.Count);
        if (gpsPoints.Length == 0 && meanlinePoints.Length == 0)
        {
            DrawText(drawingContext, "GPS track unavailable", 16, 18, TextBrush, 12);
            _projectedTraces = projectedTraces;
            return;
        }

        var coordinates = gpsPoints.Select(point => (point.Latitude, point.Longitude))
            .Concat(meanlinePoints.Select(point => (point.Latitude, point.Longitude)))
            .ToArray();
        var minLatitude = coordinates.Min(point => point.Latitude);
        var maxLatitude = coordinates.Max(point => point.Latitude);
        var minLongitude = coordinates.Min(point => point.Longitude);
        var maxLongitude = coordinates.Max(point => point.Longitude);
        var centerLatitude = (minLatitude + maxLatitude) / 2;
        var cosine = Math.Cos(centerLatitude * Math.PI / 180);
        var longitudeScale = Math.Max(0.01, cosine);
        var longitudeSpan = Math.Max((maxLongitude - minLongitude) * longitudeScale, 0.000001);
        var latitudeSpan = Math.Max(maxLatitude - minLatitude, 0.000001);
        var mapRect = new Rect(24, 36, Math.Max(1, ActualWidth - 48), Math.Max(1, ActualHeight - 64));
        var scale = Math.Min(mapRect.Width / longitudeSpan, mapRect.Height / latitudeSpan);
        var scaledWidth = longitudeSpan * scale;
        var scaledHeight = latitudeSpan * scale;
        var left = mapRect.Left + (mapRect.Width - scaledWidth) / 2;
        var top = mapRect.Top + (mapRect.Height - scaledHeight) / 2;

        Point ProjectCoordinates(double latitude, double longitude) => new(
            left + (longitude - minLongitude) * longitudeScale * scale,
            top + (maxLatitude - latitude) * scale);
        Point ProjectTelemetry(TelemetryPoint point) => ProjectCoordinates(point.Latitude, point.Longitude);
        Point ProjectTrackPoint(TrackPoint point) => ProjectCoordinates(point.Latitude, point.Longitude);

        DrawText(drawingContext, meanlinePoints.Length > 0 ? "TRACK MEANLINE" : "GPS TRACE  ·  DISTANCE ALIGNED",
            14, 12, TextBrush, 9);
        if (meanlinePoints.Length > 0)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(ProjectTrackPoint(meanlinePoints[0]), false, false);
                foreach (var point in meanlinePoints.Skip(1))
                {
                    context.LineTo(ProjectTrackPoint(point), true, false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(Brushes.LightSeaGreen, 3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            }, geometry);
        }

        var legendY = 12d;
        for (var index = 0; index < _traces.Count; index++)
        {
            var trace = _traces[index];
            var color = index == 0 ? Brushes.DeepSkyBlue : Brushes.Orange;
            var legendX = ActualWidth - 155 - index * 105;
            drawingContext.DrawEllipse(color, null, new Point(legendX, legendY + 6), 4, 4);
            DrawText(drawingContext, index == 0 ? "Session A" : "Session B",
                legendX + 9, legendY, TextBrush, 9);

            var geometry = new StreamGeometry();
            var projectedPoints = new List<ProjectedTrackPoint>(trace.Points.Count);
            using (var context = geometry.Open())
            {
                var drawing = false;
                foreach (var point in trace.Points)
                {
                    if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude))
                    {
                        drawing = false;
                        continue;
                    }

                    var projected = ProjectTelemetry(point);
                    projectedPoints.Add(new ProjectedTrackPoint(point.DistanceM, projected));
                    if (!drawing)
                    {
                        context.BeginFigure(projected, false, false);
                        drawing = true;
                    }
                    else
                    {
                        context.LineTo(projected, true, false);
                    }
                }
            }

            geometry.Freeze();
            var pen = new Pen(color, index == 0 ? 2.5 : 1.8)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            drawingContext.DrawGeometry(null, pen, geometry);
            projectedTraces.Add(projectedPoints);
        }

        _projectedTraces = projectedTraces;
    }

    private static ProjectedTrackPoint? FindNearestPoint(
        IReadOnlyList<ProjectedTrackPoint> points,
        double distanceM)
    {
        if (points.Count == 0 || !double.IsFinite(distanceM))
        {
            return null;
        }

        var low = 0;
        var high = points.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].DistanceM < distanceM)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low > 0 && Math.Abs(points[low - 1].DistanceM - distanceM) <
            Math.Abs(points[low].DistanceM - distanceM))
        {
            low--;
        }

        return points[low];
    }

    private readonly record struct ProjectedTrackPoint(double DistanceM, Point Position);

    private static void DrawText(DrawingContext context, string text, double x, double y, Brush brush, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow!).PixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}
