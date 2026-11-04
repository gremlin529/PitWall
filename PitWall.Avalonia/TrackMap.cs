using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TrackTelemetryViewer;

namespace PitWallApp;

/// <summary>
/// Avalonia port of the WPF TrackMap: GPS traces (session + reference) with segment colouring, or a
/// meanline that can be shown as an animated 3D overview with elevation relief and drag-to-orbit.
/// </summary>
internal sealed class TrackMap : Control
{
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(35, 44, 54)), 0.7);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(144, 157, 171));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(18, 24, 30));
    private static readonly IBrush GroundBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
    private static readonly IPen PostPen = new Pen(new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)), 1);
    private static readonly IPen MarkerPen = new Pen(Brushes.White, 1);
    private static readonly IPen[] ElevationPens = Enumerable.Range(0, 32)
        .Select(index => CreateElevationPen(index / 31d))
        .ToArray();
    private static readonly Color[] SegmentPalette =
    [
        Color.FromRgb(229, 72, 77),
        Color.FromRgb(70, 167, 88),
        Color.FromRgb(142, 78, 198),
        Color.FromRgb(245, 217, 10),
        Color.FromRgb(18, 165, 148),
        Color.FromRgb(233, 61, 130),
        Color.FromRgb(194, 121, 43),
        Color.FromRgb(153, 163, 173)
    ];

    private const double DefaultOverviewPitchDegrees = 52;
    private readonly long _overviewStartTicks = Environment.TickCount64;
    private readonly DispatcherTimer _overviewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _orbitIdleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private IReadOnlyList<SessionTrace> _traces = [];
    private IReadOnlyList<TrackPoint> _meanlinePoints = [];
    private TrackSegmentMap? _segmentMap;
    private double _cursorDistanceM = double.NaN;
    private List<Action<DrawingContext>>? _cachedStatic;
    private Size _cachedSize;
    private IReadOnlyList<IReadOnlyList<ProjectedTrackPoint>> _projectedTraces = [];
    private bool _enable3DOverview;
    private bool _manualOrbit;
    private double _orbitYawDegrees;
    private double _orbitPitchDegrees = DefaultOverviewPitchDegrees;
    private Point? _orbitDragPosition;
    private bool _attached;

    public TrackMap()
    {
        ClipToBounds = true;
        _overviewTimer.Tick += (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                _cachedStatic = null;
                InvalidateVisual();
            }
        };
        _orbitIdleTimer.Tick += (_, _) => ResumeOverviewAnimation();
    }

    public bool Enable3DOverview
    {
        get => _enable3DOverview;
        set
        {
            if (_enable3DOverview == value)
            {
                return;
            }

            _enable3DOverview = value;
            _cachedStatic = null;
            ResumeOverviewAnimation();
        }
    }

    public IReadOnlyList<SessionTrace> Traces
    {
        get => _traces;
        set
        {
            _traces = value;
            Invalidate();
        }
    }

    public IReadOnlyList<TrackPoint> MeanlinePoints
    {
        get => _meanlinePoints;
        set
        {
            _meanlinePoints = value;
            _cachedStatic = null;
            ResumeOverviewAnimation();
        }
    }

    public TrackSegmentMap? SegmentMap
    {
        get => _segmentMap;
        set
        {
            _segmentMap = value;
            Invalidate();
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

    private void Invalidate()
    {
        _cachedStatic = null;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateOverviewTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        UpdateOverviewTimer();
        _orbitIdleTimer.Stop();
    }

    private void UpdateOverviewTimer()
    {
        if (_attached && Enable3DOverview && !_manualOrbit && _meanlinePoints.Count > 1)
        {
            _overviewTimer.Start();
        }
        else
        {
            _overviewTimer.Stop();
        }
    }

    private void ResumeOverviewAnimation()
    {
        _orbitIdleTimer.Stop();
        _manualOrbit = false;
        _orbitPitchDegrees = DefaultOverviewPitchDegrees;
        _orbitDragPosition = null;
        Cursor = null;
        UpdateOverviewTimer();
        Invalidate();
    }

    private double AnimatedYawDegrees() =>
        24 * Math.Sin((Environment.TickCount64 - _overviewStartTicks) / 1000d * 2 * Math.PI / 26);

    // Clicking the 3D overview freezes the orbit where it is; dragging then spins (horizontal) and tilts (vertical)
    // the map, and a double-click or 10 seconds without dragging hands it back to the animation.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Enable3DOverview || _meanlinePoints.Count < 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ResumeOverviewAnimation();
            e.Handled = true;
            return;
        }

        if (!_manualOrbit)
        {
            _orbitYawDegrees = AnimatedYawDegrees();
            _manualOrbit = true;
            UpdateOverviewTimer();
        }

        _orbitIdleTimer.Stop();
        _orbitDragPosition = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_orbitDragPosition is not { } previous || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        _orbitYawDegrees = (_orbitYawDegrees + (position.X - previous.X) * 0.5) % 360;
        _orbitPitchDegrees = Math.Clamp(_orbitPitchDegrees + (position.Y - previous.Y) * 0.4, 5, 90);
        _orbitDragPosition = position;
        Invalidate();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_orbitDragPosition is null)
        {
            return;
        }

        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _orbitDragPosition = null;
        Cursor = null;
        if (_manualOrbit)
        {
            _orbitIdleTimer.Start();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (_cachedStatic is null || _cachedSize != size)
        {
            _cachedStatic = BuildStaticContent(size);
            _cachedSize = size;
        }

        foreach (var draw in _cachedStatic)
        {
            draw(context);
        }

        for (var index = 0; index < _projectedTraces.Count; index++)
        {
            var sample = FindNearestPoint(_projectedTraces[index], _cursorDistanceM);
            if (sample is null)
            {
                continue;
            }

            var radius = index == 0 ? 5 : 4;
            context.DrawEllipse(index == 0 ? Palette.Primary : Palette.Reference, MarkerPen,
                sample.Value.Position, radius, radius);
        }
    }

    private List<Action<DrawingContext>> BuildStaticContent(Size size)
    {
        var ops = new List<Action<DrawingContext>>();
        var width = size.Width;
        var height = size.Height;
        var bounds = new Rect(size);
        ops.Add(context =>
        {
            // Opaque fill: also makes the whole map hit-testable for the orbit handlers.
            context.FillRectangle(BackgroundBrush, bounds);
            for (var x = 0d; x < width; x += 36)
            {
                context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
            }

            for (var y = 0d; y < height; y += 36)
            {
                context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
            }
        });

        var gpsPoints = _traces.SelectMany(trace => trace.Points)
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        var meanlinePoints = _meanlinePoints
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .ToArray();
        var projectedTraces = new List<IReadOnlyList<ProjectedTrackPoint>>(_traces.Count);
        _projectedTraces = projectedTraces;
        if (gpsPoints.Length == 0 && meanlinePoints.Length == 0)
        {
            AddText(ops, "GPS track unavailable", 16, 18, 12);
            return ops;
        }

        if (Enable3DOverview && meanlinePoints.Length > 1)
        {
            Build3DOverview(ops, meanlinePoints, size);
            return ops;
        }

        var coordinates = gpsPoints.Select(point => (point.Latitude, point.Longitude))
            .Concat(meanlinePoints.Select(point => (point.Latitude, point.Longitude)))
            .ToArray();
        var minLatitude = coordinates.Min(point => point.Latitude);
        var maxLatitude = coordinates.Max(point => point.Latitude);
        var minLongitude = coordinates.Min(point => point.Longitude);
        var maxLongitude = coordinates.Max(point => point.Longitude);
        var centerLatitude = (minLatitude + maxLatitude) / 2;
        var longitudeScale = Math.Max(0.01, Math.Cos(centerLatitude * Math.PI / 180));
        var longitudeSpan = Math.Max((maxLongitude - minLongitude) * longitudeScale, 0.000001);
        var latitudeSpan = Math.Max(maxLatitude - minLatitude, 0.000001);
        var mapRect = new Rect(24, 36, Math.Max(1, width - 48), Math.Max(1, height - 64));
        var scale = Math.Min(mapRect.Width / longitudeSpan, mapRect.Height / latitudeSpan);
        var left = mapRect.Left + (mapRect.Width - longitudeSpan * scale) / 2;
        var top = mapRect.Top + (mapRect.Height - latitudeSpan * scale) / 2;

        Point Project(double latitude, double longitude) => new(
            left + (longitude - minLongitude) * longitudeScale * scale,
            top + (maxLatitude - latitude) * scale);

        AddText(ops, meanlinePoints.Length > 0 ? "TRACK MEANLINE" : "GPS TRACE  ·  DISTANCE ALIGNED", 14, 12, 9);
        if (meanlinePoints.Length > 0)
        {
            var projected = meanlinePoints.Select(point => (point.DistanceM, Project(point.Latitude, point.Longitude)))
                .ToArray();
            if (_segmentMap is { } overviewSegments)
            {
                AddSegmentedPath(ops, projected, overviewSegments, 4, 1);
            }
            else
            {
                AddPolyline(ops, projected.Select(point => point.Item2),
                    new Pen(new SolidColorBrush(Colors.LightSeaGreen), 3, null, PenLineCap.Round, PenLineJoin.Round));
            }
        }
        else if (_segmentMap is { } sessionSegments && _traces.Count > 0)
        {
            AddSegmentedPath(ops,
                _traces[0].Points
                    .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
                    .Select(point => (point.DistanceM, Project(point.Latitude, point.Longitude)))
                    .ToArray(),
                sessionSegments, 9, 0.5);
        }

        for (var index = 0; index < _traces.Count; index++)
        {
            var trace = _traces[index];
            var color = index == 0 ? Palette.Primary : Palette.Reference;
            var legendX = width - 155 - (_traces.Count - 1 - index) * 105;
            var legend = TelemetryChart.Text(index == 0 ? "Session" : "Reference", TextBrush, 9);
            ops.Add(context =>
            {
                context.DrawEllipse(color, null, new Point(legendX, 18), 4, 4);
                context.DrawText(legend, new Point(legendX + 9, 12));
            });

            var geometry = new StreamGeometry();
            var projectedPoints = new List<ProjectedTrackPoint>(trace.Points.Count);
            using (var stream = geometry.Open())
            {
                var drawing = false;
                foreach (var point in trace.Points)
                {
                    if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude))
                    {
                        if (drawing)
                        {
                            stream.EndFigure(false);
                        }

                        drawing = false;
                        continue;
                    }

                    var position = Project(point.Latitude, point.Longitude);
                    projectedPoints.Add(new ProjectedTrackPoint(point.DistanceM, position));
                    if (!drawing)
                    {
                        stream.BeginFigure(position, false);
                        drawing = true;
                    }
                    else
                    {
                        stream.LineTo(position);
                    }
                }

                if (drawing)
                {
                    stream.EndFigure(false);
                }
            }

            var pen = new Pen(color, index == 0 ? 2.5 : 1.8, null, PenLineCap.Round, PenLineJoin.Round);
            if (projectedPoints.Count > 1)
            {
                ops.Add(context => context.DrawGeometry(null, pen, geometry));
            }

            projectedTraces.Add(projectedPoints);
        }

        return ops;
    }

    private void Build3DOverview(List<Action<DrawingContext>> ops, IReadOnlyList<TrackPoint> points, Size size)
    {
        const double metersPerDegree = 111320;
        var centerLatitude = points.Average(point => point.Latitude);
        var centerLongitude = points.Average(point => point.Longitude);
        var longitudeScale = Math.Max(0.01, Math.Cos(centerLatitude * Math.PI / 180)) * metersPerDegree;
        var x = points.Select(point => (point.Longitude - centerLongitude) * longitudeScale).ToArray();
        var y = points.Select(point => (point.Latitude - centerLatitude) * metersPerDegree).ToArray();
        var validAltitudes = points.Where(point => double.IsFinite(point.AltitudeM))
            .Select(point => point.AltitudeM).ToArray();
        var hasAltitude = validAltitudes.Length >= (points.Count + 1) / 2;
        var minimumAltitude = hasAltitude ? validAltitudes.Min() : 0;
        var altitudeRange = hasAltitude ? validAltitudes.Max() - minimumAltitude : 0;
        var radius = Math.Max(1, points.Select((_, index) => Math.Sqrt(x[index] * x[index] + y[index] * y[index])).Max());
        var exaggeration = altitudeRange > 0.5 ? Math.Clamp(radius * 0.25 / altitudeRange, 1, 8) : 1;
        var maxHeight = altitudeRange * exaggeration;
        var z = points.Select(point => hasAltitude && double.IsFinite(point.AltitudeM)
            ? (point.AltitudeM - minimumAltitude) * exaggeration
            : 0).ToArray();

        var pitch = _orbitPitchDegrees * Math.PI / 180;
        var sinPitch = Math.Sin(pitch);
        var cosPitch = Math.Cos(pitch);
        var yaw = (_manualOrbit ? _orbitYawDegrees : AnimatedYawDegrees()) * Math.PI / 180;
        var sinYaw = Math.Sin(yaw);
        var cosYaw = Math.Cos(yaw);
        var mapRect = new Rect(24, 36, Math.Max(1, size.Width - 48), Math.Max(1, size.Height - 64));
        var scale = Math.Min(mapRect.Width / (2 * radius),
            mapRect.Height / (2 * radius * sinPitch + maxHeight * cosPitch));
        var centerX = mapRect.Left + mapRect.Width / 2;
        var centerY = mapRect.Top + mapRect.Height / 2 + maxHeight * cosPitch * scale / 2;

        Point Project(int index, double height)
        {
            var rotatedX = x[index] * cosYaw - y[index] * sinYaw;
            var rotatedY = x[index] * sinYaw + y[index] * cosYaw;
            return new Point(centerX + rotatedX * scale, centerY - (height * cosPitch + rotatedY * sinPitch) * scale);
        }

        var groundPoints = points.Select((point, index) => (point.DistanceM, Project(index, 0))).ToArray();
        if (_segmentMap is { } segmentMap)
        {
            AddSegmentedPath(ops, groundPoints, segmentMap, 3, 0.48);
        }
        else
        {
            AddPolyline(ops, groundPoints.Select(point => point.Item2),
                new Pen(GroundBrush, 3, null, PenLineCap.Round, PenLineJoin.Round));
        }

        var posts = new List<(Point Bottom, Point Top)>();
        if (hasAltitude)
        {
            var postStride = Math.Max(1, points.Count / 80);
            for (var index = 0; index < points.Count; index += postStride)
            {
                posts.Add((Project(index, 0), Project(index, z[index])));
            }
        }

        var segments = new List<(IPen Pen, Point From, Point To)>(points.Count);
        for (var index = 1; index < points.Count; index++)
        {
            var ratio = altitudeRange > 0 ? Math.Clamp(z[index] / maxHeight, 0, 1) : 0.5;
            segments.Add((ElevationPens[(int)Math.Round(ratio * (ElevationPens.Length - 1))],
                Project(index - 1, z[index - 1]), Project(index, z[index])));
        }

        ops.Add(context =>
        {
            foreach (var (bottom, top) in posts)
            {
                context.DrawLine(PostPen, bottom, top);
            }

            foreach (var (pen, from, to) in segments)
            {
                context.DrawLine(pen, from, to);
            }
        });

        AddText(ops,
            _manualOrbit
                ? "TRACK MEANLINE  ·  3D  ·  DRAG TO ORBIT  ·  DOUBLE-CLICK TO RESUME"
                : "TRACK MEANLINE  ·  3D  ·  CLICK TO ORBIT",
            14, 12, 9);
        if (hasAltitude)
        {
            AddText(ops, $"ELEVATION {altitudeRange:0} m  ·  RELIEF x{exaggeration:0.#}",
                14, Math.Max(20, size.Height - 22), 9);
        }
    }

    private static IPen CreateElevationPen(double ratio)
    {
        var low = Color.FromRgb(63, 167, 255);
        var high = Color.FromRgb(255, 179, 71);
        var color = Color.FromRgb(
            (byte)(low.R + (high.R - low.R) * ratio),
            (byte)(low.G + (high.G - low.G) * ratio),
            (byte)(low.B + (high.B - low.B) * ratio));
        return new Pen(new SolidColorBrush(color), 3.2, null, PenLineCap.Round);
    }

    private static void AddText(List<Action<DrawingContext>> ops, string text, double x, double y, double size)
    {
        var formatted = TelemetryChart.Text(text, TextBrush, size);
        ops.Add(context => context.DrawText(formatted, new Point(x, y)));
    }

    private static void AddPolyline(List<Action<DrawingContext>> ops, IEnumerable<Point> points, IPen pen)
    {
        var list = points.ToArray();
        if (list.Length < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(list[0], false);
            foreach (var point in list.Skip(1))
            {
                stream.LineTo(point);
            }

            stream.EndFigure(false);
        }

        ops.Add(context => context.DrawGeometry(null, pen, geometry));
    }

    private static void AddSegmentedPath(
        List<Action<DrawingContext>> ops,
        IReadOnlyList<(double DistanceM, Point Position)> points,
        TrackSegmentMap segments,
        double thickness,
        double opacity)
    {
        if (points.Count < 2)
        {
            return;
        }

        var runIndex = segments.IndexAt(points[1].DistanceM);
        var run = new List<Point> { points[0].Position };

        void Flush()
        {
            if (run.Count < 2)
            {
                return;
            }

            var color = SegmentPalette[runIndex % SegmentPalette.Length];
            var brush = new SolidColorBrush(color, opacity);
            AddPolyline(ops, run, new Pen(brush, thickness, null, PenLineCap.Flat, PenLineJoin.Round));
        }

        for (var index = 1; index < points.Count; index++)
        {
            var pointIndex = segments.IndexAt(points[index].DistanceM);
            if (pointIndex != runIndex)
            {
                Flush();
                runIndex = pointIndex;
                run = [points[index - 1].Position];
            }

            run.Add(points[index].Position);
        }

        Flush();
    }

    private static ProjectedTrackPoint? FindNearestPoint(IReadOnlyList<ProjectedTrackPoint> points, double distanceM)
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

        if (low > 0 && Math.Abs(points[low - 1].DistanceM - distanceM) < Math.Abs(points[low].DistanceM - distanceM))
        {
            low--;
        }

        return points[low];
    }

    private readonly record struct ProjectedTrackPoint(double DistanceM, Point Position);
}
