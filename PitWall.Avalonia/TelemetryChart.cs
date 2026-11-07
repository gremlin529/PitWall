using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record ChartLine(
    string Label,
    IReadOnlyList<ChartValue> Values,
    IBrush Color,
    bool Dashed = false,
    string DeltaKey = "");

/// <summary>Avalonia port of the WPF TelemetryChart: one distance plot with legend, segment lines and cursor readout.</summary>
internal sealed class TelemetryChart : Control
{
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromRgb(42, 51, 61));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(144, 157, 171));
    private static readonly IPen GridPen = new Pen(GridBrush, 1);
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.FromRgb(75, 87, 100)), 1);
    private static readonly IPen ZeroAxisPen = new Pen(new SolidColorBrush(Color.FromRgb(135, 148, 162)), 1.5);
    private static readonly IPen SegmentPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 150, 165, 180)), 1);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromRgb(236, 187, 83)), 1,
        DashStyle.Dash);

    private IReadOnlyList<ChartLine> _lines = [];
    private IReadOnlyList<double> _segmentBoundariesM = [];
    private double _maxDistanceM = 1;
    private double _viewStartM;
    private double _viewEndM = double.NaN; // NaN = the whole lap
    private double _cursorDistanceM = double.NaN;
    private bool _useImperialDistance;
    private List<Action<DrawingContext>>? _cachedStatic;
    private Size _cachedSize;

    // Smallest distance window the wheel can zoom to.
    public const double MinimumViewSpanM = 30;

    public event Action<double>? DistanceHovered;

    /// <summary>Raised when the mouse wheel zooms or pans; the host applies the new window to every chart.</summary>
    public event Action<double, double>? ViewChangeRequested;

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
        Invalidate();
    }

    public string Title { get; set; } = string.Empty;

    public string Unit
    {
        get => _unit;
        set
        {
            _unit = value;
            Invalidate();
        }
    }

    private string _unit = string.Empty;

    public bool SymmetricRange { get; set; }

    public bool UseImperialDistance
    {
        get => _useImperialDistance;
        set
        {
            _useImperialDistance = value;
            Invalidate();
        }
    }

    public IReadOnlyList<double> SegmentBoundariesM
    {
        get => _segmentBoundariesM;
        set
        {
            _segmentBoundariesM = value;
            Invalidate();
        }
    }

    public IReadOnlyList<ChartLine> Lines
    {
        get => _lines;
        set
        {
            _lines = value;
            Invalidate();
        }
    }

    public double MaxDistanceM
    {
        get => _maxDistanceM;
        set
        {
            _maxDistanceM = Math.Max(1, value);
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

    private static Rect PlotRect(Size size) =>
        new(55, 42, Math.Max(1, size.Width - 70), Math.Max(1, size.Height - 78));

    private double DistanceToX(double distanceM, Rect plot) =>
        plot.Left + (distanceM - ViewStartM) / ViewSpanM * plot.Width;

    private double XToDistance(double x, Rect plot) =>
        Math.Clamp(ViewStartM + (x - plot.Left) / plot.Width * ViewSpanM, 0, _maxDistanceM);

    // Axis unit for the visible span: miles/km for the lap, feet/metres once zoomed in close.
    private (double MetersPerUnit, string Name, string Format) DistanceAxisUnit()
    {
        if (UseImperialDistance)
        {
            return ViewSpanM < 0.5 * 1609.344
                ? (0.3048, "ft", "0")
                : (1609.344, "mi", ViewSpanM < 3 * 1609.344 ? "0.00" : "0.0");
        }

        return ViewSpanM < 1000 ? (1, "m", "0") : (1000, "km", ViewSpanM < 3000 ? "0.00" : "0.0");
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Bounds.Width - 70 <= 0)
        {
            return;
        }

        DistanceHovered?.Invoke(XToDistance(e.GetPosition(this).X, PlotRect(Bounds.Size)));
    }

    // Wheel zooms the distance axis around the pointer; Shift+wheel (or a horizontal wheel/trackpad swipe) pans.
    // The host applies the result to every chart.
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Bounds.Width - 70 <= 0 || _maxDistanceM <= MinimumViewSpanM)
        {
            return;
        }

        var start = ViewStartM;
        var span = ViewSpanM;
        var wholeLap = span >= _maxDistanceM - 0.5;
        // macOS turns Shift+wheel into a horizontal delta, so treat either as a pan.
        var pan = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || (e.Delta.Y == 0 && e.Delta.X != 0);
        var notches = pan ? (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) : e.Delta.Y;
        if (notches == 0 || (pan && wholeLap) || (!pan && notches < 0 && wholeLap))
        {
            return; // nothing to zoom out of or pan: let the wheel scroll the chart list instead
        }

        if (pan)
        {
            start -= notches * span * 0.15;
        }
        else
        {
            var pivot = XToDistance(e.GetPosition(this).X, PlotRect(Bounds.Size));
            var newSpan = Math.Clamp(span * Math.Pow(0.8, notches), MinimumViewSpanM, _maxDistanceM);
            start = pivot - (pivot - start) * newSpan / span;
            span = newSpan;
        }

        start = Math.Clamp(start, 0, _maxDistanceM - span);
        ViewChangeRequested?.Invoke(start, span >= _maxDistanceM - 0.5 ? double.NaN : start + span);
        e.Handled = true; // don't also scroll the chart list
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        // Transparent fill so the whole chart receives pointer moves, not just the drawn lines.
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        if (_cachedStatic is null || _cachedSize != size)
        {
            _cachedStatic = BuildStaticContent(size);
            _cachedSize = size;
        }

        foreach (var draw in _cachedStatic)
        {
            draw(context);
        }

        DrawCursorValues(context);
        if (double.IsFinite(_cursorDistanceM) && _cursorDistanceM >= ViewStartM && _cursorDistanceM <= ViewEndM)
        {
            var plot = PlotRect(size);
            var x = DistanceToX(_cursorDistanceM, plot);
            context.DrawLine(CursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private List<Action<DrawingContext>> BuildStaticContent(Size size)
    {
        var ops = new List<Action<DrawingContext>>();
        var width = size.Width;
        var height = size.Height;
        if (width <= 0 || height <= 0)
        {
            return ops;
        }

        var title = Text(Title, Palette.White, 12, FontWeight.SemiBold);
        ops.Add(context => context.DrawText(title, new Point(14, 10)));
        if (!string.IsNullOrWhiteSpace(Unit))
        {
            var unit = Text(Unit, TextBrush, 10);
            ops.Add(context => context.DrawText(unit, new Point(14 + title.Width + 7, 11)));
        }

        var legendWidth = _lines.Sum(line => Math.Max(48, line.Label.Length * 5.2) + 17);
        var legendX = Math.Max(155, width - legendWidth - 12);
        foreach (var line in _lines)
        {
            var samplePen = new Pen(line.Color, 1.5, line.Dashed ? DashStyle.Dash : null);
            var x = legendX;
            var label = Text(line.Label, TextBrush, 9);
            ops.Add(context =>
            {
                context.DrawLine(samplePen, new Point(x, 30), new Point(x + 14, 30));
                context.DrawText(label, new Point(x + 18, 24));
            });
            legendX += Math.Max(48, line.Label.Length * 5.2) + 17;
        }

        var plot = PlotRect(size);
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
            var empty = Text("No samples available", TextBrush, 11);
            ops.Add(context => context.DrawText(empty, new Point(plot.Left + 10, plot.Top + 10)));
            return ops;
        }

        var min = values.Min();
        var max = values.Max();
        var isRpm = IsRpm;
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

        ops.Add(context => context.DrawRectangle(null, AxisPen, plot));
        if (isRpm)
        {
            for (var value = max; value >= min; value -= 500)
            {
                var y = plot.Bottom - (value - min) / (max - min) * plot.Height;
                var label = Text(value.ToString("N0", CultureInfo.InvariantCulture), TextBrush, 9);
                ops.Add(context =>
                {
                    context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                    context.DrawText(label, new Point(6, y - 7));
                });
            }
        }
        else
        {
            for (var tick = 0; tick <= 4; tick++)
            {
                var y = plot.Top + plot.Height * tick / 4;
                var value = max - (max - min) * tick / 4;
                var label = Text(value.ToString("0.0", CultureInfo.InvariantCulture), TextBrush, 9);
                ops.Add(context =>
                {
                    context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                    context.DrawText(label, new Point(6, y - 7));
                });
            }
        }

        var axisUnit = DistanceAxisUnit();
        for (var tick = 0; tick <= 5; tick++)
        {
            var x = plot.Left + plot.Width * tick / 5;
            var distance = (viewStart + (viewEnd - viewStart) * tick / 5) / axisUnit.MetersPerUnit;
            var label = Text(distance.ToString(axisUnit.Format, CultureInfo.InvariantCulture), TextBrush, 9);
            ops.Add(context =>
            {
                context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                context.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 5));
            });
        }

        var distanceLabel = Text(viewEnd - viewStart < _maxDistanceM - 0.5
            ? $"Distance ({axisUnit.Name})  ·  zoomed — scroll wheel to zoom, Shift+wheel or the scrollbar to pan"
            : $"Distance ({axisUnit.Name})", TextBrush, 9);
        ops.Add(context => context.DrawText(distanceLabel,
            new Point(plot.Left + plot.Width / 2 - distanceLabel.Width / 2, height - 15)));

        var boundaries = _segmentBoundariesM
            .Where(boundary => boundary > viewStart && boundary < viewEnd)
            .Select(boundary => DistanceToX(boundary, plot))
            .ToArray();
        var zeroY = plot.Bottom - (0 - min) / (max - min) * plot.Height;
        var lineGeometries = _lines.Select(line => (
                Pen: (IPen)new Pen(line.Color, 1.5, line.Dashed ? DashStyle.Dash : null,
                    PenLineCap.Round, PenLineJoin.Round),
                Geometry: BuildGeometry(line.Values, plot, min, max)))
            .Where(item => item.Geometry is not null)
            .ToArray();
        ops.Add(context =>
        {
            using (context.PushClip(plot))
            {
                foreach (var boundaryX in boundaries)
                {
                    context.DrawLine(SegmentPen, new Point(boundaryX, plot.Top), new Point(boundaryX, plot.Bottom));
                }

                context.DrawLine(ZeroAxisPen, new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));
                foreach (var (pen, geometry) in lineGeometries)
                {
                    context.DrawGeometry(null, pen, geometry!);
                }
            }
        });
        return ops;
    }

    private StreamGeometry? BuildGeometry(IReadOnlyList<ChartValue> values, Rect plot, double min, double max)
    {
        var geometry = new StreamGeometry();
        var any = false;
        using (var context = geometry.Open())
        {
            var drawing = false;
            var viewStart = ViewStartM;
            var viewEnd = ViewEndM;
            for (var index = 0; index < values.Count; index++)
            {
                var point = values[index];
                // Skip what's off-screen, keeping one point either side so lines run to the plot edges.
                var invalid = !double.IsFinite(point.Value) || !double.IsFinite(point.DistanceM);
                var offScreen = !invalid &&
                    ((point.DistanceM < viewStart && index + 1 < values.Count && values[index + 1].DistanceM < viewStart) ||
                     (point.DistanceM > viewEnd && index > 0 && values[index - 1].DistanceM > viewEnd));
                if (invalid || offScreen)
                {
                    if (drawing)
                    {
                        context.EndFigure(false);
                    }

                    drawing = false;
                    continue;
                }

                var position = new Point(
                    DistanceToX(point.DistanceM, plot),
                    plot.Bottom - (point.Value - min) / (max - min) * plot.Height);
                if (!drawing)
                {
                    context.BeginFigure(position, false);
                    drawing = true;
                }
                else
                {
                    context.LineTo(position);
                    any = true;
                }
            }

            if (drawing)
            {
                context.EndFigure(false);
            }
        }

        return any ? geometry : null;
    }

    private bool IsRpm => Unit.Contains("rpm", StringComparison.OrdinalIgnoreCase) ||
                          Title.Contains("rpm", StringComparison.OrdinalIgnoreCase);

    private void DrawCursorValues(DrawingContext context)
    {
        if (!double.IsFinite(_cursorDistanceM))
        {
            return;
        }

        var titleWidth = Text(Title, Palette.White, 12, FontWeight.SemiBold).Width;
        var unitWidth = Text(Unit, TextBrush, 10).Width;
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
            var text = Text(double.IsFinite(delta) ? $"{name}: {FormatDelta(delta)}" : $"{name}: —",
                Palette.White, 9, FontWeight.SemiBold);
            context.DrawText(text, new Point(x, 10));
            x += text.Width + 18;
        }

        foreach (var line in _lines)
        {
            var value = GetValueAtDistance(line.Values, _cursorDistanceM);
            var text = Text(double.IsFinite(value) ? $"{line.Label}: {FormatValue(value)}" : $"{line.Label}: —",
                line.Color, 9, FontWeight.SemiBold);
            context.DrawText(text, new Point(x, 10));
            x += text.Width + 18;
        }
    }

    private string FormatDelta(double value)
    {
        var formatted = FormatValue(value);
        return value > 0 ? "+" + formatted : formatted;
    }

    private string FormatValue(double value)
    {
        var formatted = IsRpm
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
            : before.Value + (after.Value - before.Value) * ((distanceM - before.DistanceM) / span);
    }

    internal static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(Palette.FontFamilyName), FontStyle.Normal, weight), size, brush);
}
