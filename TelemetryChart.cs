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
    private double _viewStartM;
    private double _viewEndM = double.NaN; // NaN = the whole lap
    private double _cursorDistanceM = double.NaN;
    private bool _useImperialDistance;
    private DrawingGroup? _cachedStaticDrawing;
    private Size _cachedDrawingSize;

    // Smallest distance window the wheel can zoom to.
    public const double MinimumViewSpanM = 30;

    public event Action<double>? DistanceHovered;

    /// <summary>Raised when the mouse wheel zooms or pans; the host applies the new window to every chart.</summary>
    public event Action<double, double>? ViewChangeRequested;

    public TelemetryChart()
    {
        SnapsToDevicePixels = true;
        MouseMove += HandleMouseMove;
        MouseWheel += HandleMouseWheel;
    }

    public double ViewStartM => Math.Clamp(_viewStartM, 0, Math.Max(0, _maxDistanceM - MinimumViewSpanM));
    public double ViewEndM => double.IsFinite(_viewEndM)
        ? Math.Clamp(_viewEndM, ViewStartM + Math.Min(MinimumViewSpanM, _maxDistanceM), _maxDistanceM)
        : _maxDistanceM;
    private double ViewSpanM => Math.Max(1e-6, ViewEndM - ViewStartM);

    /// <summary>Shows [startM, endM] of the lap; NaN for <paramref name="endM"/> shows the whole lap.</summary>
    public void SetView(double startM, double endM)
    {
        if (_viewStartM.Equals(startM) && _viewEndM.Equals(endM))
        {
            return;
        }

        _viewStartM = startM;
        _viewEndM = endM;
        _cachedStaticDrawing = null;
        InvalidateVisual();
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
        if (double.IsFinite(_cursorDistanceM) && _cursorDistanceM >= ViewStartM && _cursorDistanceM <= ViewEndM)
        {
            var plot = PlotRect(ActualWidth, ActualHeight);
            var x = DistanceToX(_cursorDistanceM, plot);
            drawingContext.DrawLine(CursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private static Rect PlotRect(double width, double height) =>
        new(55, 42, Math.Max(1, width - 70), Math.Max(1, height - 78));

    private double DistanceToX(double distanceM, Rect plot) =>
        plot.Left + (distanceM - ViewStartM) / ViewSpanM * plot.Width;

    private double XToDistance(double x, Rect plot) =>
        Math.Clamp(ViewStartM + (x - plot.Left) / plot.Width * ViewSpanM, 0, _maxDistanceM);

    // Axis unit for the visible span: miles/km for the lap, feet/metres once zoomed in close.
    private (double MetersPerUnit, string Name, string Format) DistanceAxisUnit()
    {
        if (UseImperialDistance)
        {
            return ViewSpanM < 0.5 * 1609.344 ? (0.3048, "ft", "0") : (1609.344, "mi", ViewSpanM < 3 * 1609.344 ? "0.00" : "0.0");
        }

        return ViewSpanM < 1000 ? (1, "m", "0") : (1000, "km", ViewSpanM < 3000 ? "0.00" : "0.0");
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

        var plot = PlotRect(width, height);
        // Fit the vertical range to what is visible, so zooming in on a corner also zooms its values.
        var viewStart = ViewStartM;
        var viewEnd = ViewEndM;
        var values = _lines.SelectMany(line => line.Values)
            .Where(value => double.IsFinite(value.Value) && value.DistanceM >= viewStart && value.DistanceM <= viewEnd)
            .Select(value => value.Value)
            .ToArray();
        if (values.Length == 0)
        {
            values = _lines.SelectMany(line => line.Values)
                .Where(value => double.IsFinite(value.Value))
                .Select(value => value.Value)
                .ToArray();
        }
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

        var axisUnit = DistanceAxisUnit();
        for (var tick = 0; tick <= 5; tick++)
        {
            var x = plot.Left + plot.Width * tick / 5;
            drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var distance = (viewStart + (viewEnd - viewStart) * tick / 5) / axisUnit.MetersPerUnit;
            var label = distance.ToString(axisUnit.Format, CultureInfo.InvariantCulture);
            DrawText(drawingContext, label, x - MeasureText(label, 9, FontWeights.Normal) / 2, plot.Bottom + 5, TextBrush, 9);
        }

        var distanceLabel = viewEnd - viewStart < _maxDistanceM - 0.5
            ? $"Distance ({axisUnit.Name})  ·  zoomed — scroll wheel to zoom, Shift+wheel or the scrollbar to pan"
            : $"Distance ({axisUnit.Name})";
        DrawText(drawingContext, distanceLabel, plot.Left + plot.Width / 2 - MeasureText(distanceLabel, 9, FontWeights.Normal) / 2,
            height - 15, TextBrush, 9);
        drawingContext.PushClip(new RectangleGeometry(plot));
        foreach (var boundary in _segmentBoundariesM)
        {
            if (boundary <= viewStart || boundary >= viewEnd)
            {
                continue;
            }

            var boundaryX = DistanceToX(boundary, plot);
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
                var lineValues = line.Values;
                for (var index = 0; index < lineValues.Count; index++)
                {
                    var point = lineValues[index];
                    if (!double.IsFinite(point.Value) || !double.IsFinite(point.DistanceM))
                    {
                        drawing = false;
                        continue;
                    }

                    // Skip what's off-screen, keeping one point either side so lines run to the plot edges.
                    if ((point.DistanceM < viewStart && index + 1 < lineValues.Count && lineValues[index + 1].DistanceM < viewStart) ||
                        (point.DistanceM > viewEnd && index > 0 && lineValues[index - 1].DistanceM > viewEnd))
                    {
                        drawing = false;
                        continue;
                    }

                    var x = DistanceToX(point.DistanceM, plot);
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
        var x = 14 + titleWidth + unitWidth + 24;
        var session = _lines.Where(line => line.Label.StartsWith("Session", StringComparison.Ordinal)).ToArray();
        var reference = _lines.Where(line => line.Label.StartsWith("Reference", StringComparison.Ordinal)).ToArray();
        foreach (var sessionLine in session)
        {
            var referenceLine = reference.FirstOrDefault(line => line.DeltaKey == sessionLine.DeltaKey);
            if (referenceLine is null)
            {
                continue;
            }

            var delta = GetValueAtDistance(sessionLine.Values, _cursorDistanceM) -
                        GetValueAtDistance(referenceLine.Values, _cursorDistanceM);
            var name = string.IsNullOrEmpty(sessionLine.DeltaKey) ? "Δ" : $"Δ {sessionLine.DeltaKey}";
            var text = double.IsFinite(delta) ? $"{name}: {FormatDelta(delta)}" : $"{name}: —";
            DrawText(drawingContext, text, x, 10, Brushes.White, 9, FontWeights.SemiBold);
            x += MeasureText(text, 9, FontWeights.SemiBold) + 18;
        }

        foreach (var line in _lines)
        {
            var value = GetValueAtDistance(line.Values, _cursorDistanceM);
            var text = double.IsFinite(value)
                ? $"{line.Label}: {FormatValue(value)}"
                : $"{line.Label}: —";
            DrawText(drawingContext, text, x, 10, line.Color, 9, FontWeights.SemiBold);
            x += MeasureText(text, 9, FontWeights.SemiBold) + 18;
        }
    }

    private string FormatDelta(double value)
    {
        var formatted = FormatValue(value);
        return value > 0 ? "+" + formatted : formatted;
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
        if (ActualWidth - 70 <= 0)
        {
            return;
        }

        DistanceHovered?.Invoke(XToDistance(eventArgs.GetPosition(this).X, PlotRect(ActualWidth, ActualHeight)));
    }

    // Wheel zooms the distance axis around the pointer; Shift+wheel pans. The host applies it to every chart.
    private void HandleMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (ActualWidth - 70 <= 0 || _maxDistanceM <= MinimumViewSpanM)
        {
            return;
        }

        var notches = eventArgs.Delta / 120.0;
        var start = ViewStartM;
        var span = ViewSpanM;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if ((!shift && notches < 0 && span >= _maxDistanceM - 0.5) || (shift && span >= _maxDistanceM - 0.5))
        {
            return; // nothing to zoom out of or pan: let the wheel scroll the chart list instead
        }

        if (shift)
        {
            start -= notches * span * 0.15;
        }
        else
        {
            var pivot = XToDistance(eventArgs.GetPosition(this).X, PlotRect(ActualWidth, ActualHeight));
            var newSpan = Math.Clamp(span * Math.Pow(0.8, notches), MinimumViewSpanM, _maxDistanceM);
            start = pivot - (pivot - start) * newSpan / span;
            span = newSpan;
        }

        start = Math.Clamp(start, 0, _maxDistanceM - span);
        ViewChangeRequested?.Invoke(start, span >= _maxDistanceM - 0.5 ? double.NaN : start + span);
        eventArgs.Handled = true; // don't also scroll the chart list
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
