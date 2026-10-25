using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrackTelemetryViewer;

internal sealed class TelemetryChart : FrameworkElement
{
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(42, 51, 61));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(144, 157, 171));
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.FromRgb(75, 87, 100)), 1);
    private static readonly Pen ZeroAxisPen = new(new SolidColorBrush(Color.FromRgb(135, 148, 162)), 1.5);
    private static readonly Pen SegmentPen = new(new SolidColorBrush(Color.FromArgb(150, 150, 165, 180)), 1);
    private static readonly Pen CursorPen = new(new SolidColorBrush(Color.FromRgb(236, 187, 83)), 1)
    {
        DashStyle = DashStyles.Dash
    };

    private IReadOnlyList<ChartLine> _lines = [];
    private IReadOnlyList<double> _segmentBoundariesM = [];
    private double _maxDistanceM = 1;
    private double _cursorDistanceM = double.NaN;
    private bool _useImperialDistance;
    private DrawingGroup? _cachedStaticDrawing;
    private Size _cachedDrawingSize;

    public event Action<double>? DistanceHovered;

    public TelemetryChart()
    {
        SnapsToDevicePixels = true;
        MouseMove += HandleMouseMove;
    }

    public string Title { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public bool UseImperialDistance
    {
        get => _useImperialDistance;
        set
        {
            _useImperialDistance = value;
            _cachedStaticDrawing = null;
            InvalidateVisual();
        }
    }
    public bool SymmetricRange { get; set; }

    public IReadOnlyList<double> SegmentBoundariesM
    {
        get => _segmentBoundariesM;
        set
        {
            _segmentBoundariesM = value;
            _cachedStaticDrawing = null;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<ChartLine> Lines
    {
        get => _lines;
        set
        {
            _lines = value;
            _cachedStaticDrawing = null;
            InvalidateVisual();
        }
    }

    public double MaxDistanceM
    {
        get => _maxDistanceM;
        set
        {
            _maxDistanceM = Math.Max(1, value);
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
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
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
        DrawCursorValues(drawingContext);
        if (double.IsFinite(_cursorDistanceM) && _cursorDistanceM >= 0 && _cursorDistanceM <= _maxDistanceM)
        {
            var plot = new Rect(55, 42, Math.Max(1, ActualWidth - 70), Math.Max(1, ActualHeight - 78));
            var x = plot.Left + _cursorDistanceM / _maxDistanceM * plot.Width;
            drawingContext.DrawLine(CursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private void DrawStaticContent(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawText(drawingContext, Title, 14, 10, Brushes.White, 12, FontWeights.SemiBold);
        var titleWidth = MeasureText(Title, 12, FontWeights.SemiBold);
        if (!string.IsNullOrWhiteSpace(Unit))
        {
            DrawText(drawingContext, Unit, 14 + titleWidth + 7, 11, TextBrush, 10);
        }

        var legendWidth = _lines.Sum(line => Math.Max(48, line.Label.Length * 5.2) + 17);
        var legendX = Math.Max(155, width - legendWidth - 12);
        foreach (var line in _lines)
        {
            var samplePen = new Pen(line.Color, 1.5)
            {
                DashStyle = line.Dashed ? DashStyles.Dash : DashStyles.Solid
            };
            drawingContext.DrawLine(samplePen, new Point(legendX, 30), new Point(legendX + 14, 30));
            DrawText(drawingContext, line.Label, legendX + 18, 24, TextBrush, 9);
            legendX += Math.Max(48, line.Label.Length * 5.2) + 17;
        }

        var plot = new Rect(55, 42, Math.Max(1, width - 70), Math.Max(1, height - 78));
        var values = _lines.SelectMany(line => line.Values)
            .Where(value => double.IsFinite(value.Value))
            .Select(value => value.Value)
            .ToArray();
        if (values.Length == 0)
        {
            DrawText(drawingContext, "No samples available", plot.Left + 10, plot.Top + 10, TextBrush, 11);
            return;
        }

        var min = values.Min();
        var max = values.Max();
        var isRpm = Unit.Contains("rpm", StringComparison.OrdinalIgnoreCase) ||
                    Title.Contains("rpm", StringComparison.OrdinalIgnoreCase);
        if (isRpm)
        {
            min = Math.Max(0, Math.Floor(min / 500) * 500);
            max = Math.Ceiling(max / 500) * 500;
            if (max - min < 500)
            {
                max = min + 500;
            }
        }
        else if (SymmetricRange)
        {
            var extent = Math.Max(Math.Abs(min), Math.Abs(max));
            min = -Math.Max(extent, 0.1);
            max = Math.Max(extent, 0.1);
        }
        else if (Math.Abs(max - min) < 0.001)
        {
            min -= 1;
            max += 1;
        }
        else
        {
            var padding = (max - min) * 0.08;
            min -= padding;
            max += padding;
        }

        if (!isRpm)
        {
            min = Math.Min(min, 0);
            max = Math.Max(max, 0);
        }

        drawingContext.DrawRectangle(null, AxisPen, plot);
        if (isRpm)
        {
            for (var value = max; value >= min; value -= 500)
            {
                var y = plot.Bottom - (value - min) / (max - min) * plot.Height;
                drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(drawingContext, value.ToString("N0", CultureInfo.InvariantCulture),
                    6, y - 7, TextBrush, 9);
            }
        }
        else
        {
            for (var tick = 0; tick <= 4; tick++)
            {
                var y = plot.Top + plot.Height * tick / 4;
                drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
                var value = max - (max - min) * tick / 4;
                DrawText(drawingContext, value.ToString("0.0", CultureInfo.InvariantCulture),
                    6, y - 7, TextBrush, 9);
            }
        }

        for (var tick = 0; tick <= 5; tick++)
        {
            var x = plot.Left + plot.Width * tick / 5;
            drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var distance = _maxDistanceM * tick / 5 /
                           (UseImperialDistance ? 1609.344 : 1000);
            DrawText(drawingContext, $"{distance:0.0}",
                x - 12, plot.Bottom + 5, TextBrush, 9);
        }

        var distanceLabel = UseImperialDistance ? "Distance (mi)" : "Distance (km)";
        DrawText(drawingContext, distanceLabel, plot.Left + plot.Width / 2 - 33,
            height - 15, TextBrush, 9);
        drawingContext.PushClip(new RectangleGeometry(plot));
        foreach (var boundary in _segmentBoundariesM)
        {
            if (boundary <= 0 || boundary >= _maxDistanceM)
            {
                continue;
            }

            var boundaryX = plot.Left + boundary / _maxDistanceM * plot.Width;
            drawingContext.DrawLine(SegmentPen, new Point(boundaryX, plot.Top), new Point(boundaryX, plot.Bottom));
        }

        var zeroY = plot.Bottom - (0 - min) / (max - min) * plot.Height;
        drawingContext.DrawLine(ZeroAxisPen, new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));
        foreach (var line in _lines)
        {
            var pen = new Pen(line.Color, 1.5)
            {
                DashStyle = line.Dashed ? DashStyles.Dash : DashStyles.Solid,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var drawing = false;
                foreach (var point in line.Values)
                {
                    if (!double.IsFinite(point.Value) || !double.IsFinite(point.DistanceM))
                    {
                        drawing = false;
                        continue;
                    }

                    var x = plot.Left + point.DistanceM / _maxDistanceM * plot.Width;
                    var y = plot.Bottom - (point.Value - min) / (max - min) * plot.Height;
                    var position = new Point(x, y);
                    if (!drawing)
                    {
                        context.BeginFigure(position, false, false);
                        drawing = true;
                    }
                    else
                    {
                        context.LineTo(position, true, false);
                    }
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        drawingContext.Pop();
    }

    private void DrawCursorValues(DrawingContext drawingContext)
    {
        if (!double.IsFinite(_cursorDistanceM))
        {
            return;
        }

        var titleWidth = MeasureText(Title, 12, FontWeights.SemiBold);
        var unitWidth = MeasureText(Unit, 10, FontWeights.Normal);
        var firstValueX = Math.Max(210, 14 + titleWidth + unitWidth + 24);
        const double valueColumnWidth = 145;
        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            var value = GetValueAtDistance(line.Values, _cursorDistanceM);
            var text = double.IsFinite(value)
                ? $"{line.Label}: {FormatValue(value)}"
                : $"{line.Label}: —";
            DrawText(drawingContext, text, firstValueX + index * valueColumnWidth,
                10, line.Color, 9, FontWeights.SemiBold);
        }
    }

    private string FormatValue(double value)
    {
        var formatted = Unit.Contains("rpm", StringComparison.OrdinalIgnoreCase) ||
                        Title.Contains("rpm", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("0.00", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(Unit) ? formatted : $"{formatted} {Unit}";
    }

    private static double GetValueAtDistance(IReadOnlyList<ChartValue> values, double distanceM)
    {
        var low = 0;
        var high = values.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle].DistanceM < distanceM)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low < values.Count && Math.Abs(values[low].DistanceM - distanceM) < 0.001)
        {
            return values[low].Value;
        }

        if (low == 0 || low >= values.Count)
        {
            return double.NaN;
        }

        var before = values[low - 1];
        var after = values[low];
        var span = after.DistanceM - before.DistanceM;
        return !double.IsFinite(before.Value) || !double.IsFinite(after.Value) || span <= 0
            ? double.NaN
            : before.Value + (after.Value - before.Value) *
                ((distanceM - before.DistanceM) / span);
    }

    private static double MeasureText(string text, double size, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            Brushes.White,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow!).PixelsPerDip);
        return formatted.Width;
    }

    private void HandleMouseMove(object sender, MouseEventArgs eventArgs)
    {
        var plotWidth = ActualWidth - 70;
        if (plotWidth <= 0)
        {
            return;
        }

        var x = eventArgs.GetPosition(this).X;
        DistanceHovered?.Invoke(Math.Clamp((x - 55) / plotWidth * _maxDistanceM, 0, _maxDistanceM));
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        Brush brush,
        double size,
        FontWeight? weight = null)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal,
                FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow!).PixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}
