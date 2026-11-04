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
    private double _cursorDistanceM = double.NaN;
    private bool _useImperialDistance;
    private List<Action<DrawingContext>>? _cachedStatic;
    private Size _cachedSize;

    public event Action<double>? DistanceHovered;

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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var plotWidth = Bounds.Width - 70;
        if (plotWidth <= 0)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        DistanceHovered?.Invoke(Math.Clamp((x - 55) / plotWidth * _maxDistanceM, 0, _maxDistanceM));
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
        if (double.IsFinite(_cursorDistanceM) && _cursorDistanceM >= 0 && _cursorDistanceM <= _maxDistanceM)
        {
            var plot = new Rect(55, 42, Math.Max(1, size.Width - 70), Math.Max(1, size.Height - 78));
            var x = plot.Left + _cursorDistanceM / _maxDistanceM * plot.Width;
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

        var plot = new Rect(55, 42, Math.Max(1, width - 70), Math.Max(1, height - 78));
        var values = _lines.SelectMany(line => line.Values)
            .Where(value => double.IsFinite(value.Value))
            .Select(value => value.Value)
            .ToArray();
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

        for (var tick = 0; tick <= 5; tick++)
        {
            var x = plot.Left + plot.Width * tick / 5;
            var distance = _maxDistanceM * tick / 5 / (UseImperialDistance ? 1609.344 : 1000);
            var label = Text($"{distance:0.0}", TextBrush, 9);
            ops.Add(context =>
            {
                context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                context.DrawText(label, new Point(x - 12, plot.Bottom + 5));
            });
        }

        var distanceLabel = Text(UseImperialDistance ? "Distance (mi)" : "Distance (km)", TextBrush, 9);
        ops.Add(context => context.DrawText(distanceLabel, new Point(plot.Left + plot.Width / 2 - 33, height - 15)));

        var boundaries = _segmentBoundariesM
            .Where(boundary => boundary > 0 && boundary < _maxDistanceM)
            .Select(boundary => plot.Left + boundary / _maxDistanceM * plot.Width)
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
            foreach (var point in values)
            {
                if (!double.IsFinite(point.Value) || !double.IsFinite(point.DistanceM))
                {
                    if (drawing)
                    {
                        context.EndFigure(false);
                    }

                    drawing = false;
                    continue;
                }

                var position = new Point(
                    plot.Left + point.DistanceM / _maxDistanceM * plot.Width,
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
