using System.IO;
using System.Text.Json;

namespace TrackTelemetryViewer;

/// <summary>
/// A Garmin meanline: the track centreline (with distance from the start/finish line) and the start/finish line itself.
/// Garmin lap distances are the projection of each GPS fix onto this centreline, so projecting any other GPS
/// logger's fixes onto it puts every source on the same distance axis and the same lap boundaries.
/// </summary>
internal sealed class TrackReference
{
    private const double MetersPerDegree = 111_320;

    // How far beyond the meanline's track half-width a start-line crossing may be (GPS error, kerbs).
    private const double CrossingMarginM = 8;

    private readonly double _originLatitude;
    private readonly double _originLongitude;
    private readonly double _longitudeScale;
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _distance;
    private readonly (double X, double Y) _lineA;
    private readonly (double X, double Y) _lineB;
    private readonly (double X, double Y) _forward;
    private readonly double _crossingRadiusM;

    private TrackReference(string path, double[] latitude, double[] longitude, double[] distance, double startWidthM,
        (double Latitude, double Longitude) lineA, (double Latitude, double Longitude) lineB)
    {
        MeanlinePath = path;
        _originLatitude = latitude[0];
        _originLongitude = longitude[0];
        _longitudeScale = MetersPerDegree * Math.Cos(_originLatitude * Math.PI / 180);
        _x = longitude.Select(value => (value - _originLongitude) * _longitudeScale).ToArray();
        _y = latitude.Select(value => (value - _originLatitude) * MetersPerDegree).ToArray();
        _distance = distance;
        LengthM = distance[^1];
        _lineA = ToLocal(lineA.Latitude, lineA.Longitude);
        _lineB = ToLocal(lineB.Latitude, lineB.Longitude);

        // Direction of travel across the line: along the centreline over its first ~20 m.
        var ahead = Array.FindIndex(distance, value => value >= 20);
        ahead = ahead > 0 ? ahead : Math.Min(1, distance.Length - 1);
        var length = Math.Max(1e-9, Math.Sqrt(_x[ahead] * _x[ahead] + _y[ahead] * _y[ahead]));
        _forward = (_x[ahead] / length, _y[ahead] / length);
        _crossingRadiusM = Math.Max(startWidthM, 10) / 2 + CrossingMarginM;
    }

    public string MeanlinePath { get; }
    public double LengthM { get; }
    public double Latitude => _originLatitude;
    public double Longitude => _originLongitude;

    public static TrackReference? Load(string meanlinePath)
    {
        using var stream = File.OpenRead(meanlinePath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("track", out var track) ||
            !track.TryGetProperty("startLine", out var startLine) ||
            !TryReadPosition(startLine, "point1ExtendedDeg", out var lineA) &&
            !TryReadPosition(startLine, "point1RealDeg", out lineA) ||
            !TryReadPosition(startLine, "point2ExtendedDeg", out var lineB) &&
            !TryReadPosition(startLine, "point2RealDeg", out lineB) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Point-to-point layouts have a separate finish line; laps there are not loops.
        if (track.TryGetProperty("finishLine", out var finishLine) &&
            TryReadPosition(finishLine, "point1RealDeg", out var finishA) &&
            TryReadPosition(startLine, "point1RealDeg", out var startA) &&
            Math.Abs(finishA.Latitude - startA.Latitude) + Math.Abs(finishA.Longitude - startA.Longitude) > 1e-4)
        {
            return null;
        }

        var count = data.GetArrayLength();
        var latitude = new List<double>(count);
        var longitude = new List<double>(count);
        var distance = new List<double>(count);
        var startWidth = double.NaN;
        foreach (var item in data.EnumerateArray())
        {
            if (!TryReadPosition(item, "centerPositionDeg", out var position) ||
                !item.TryGetProperty("distanceM", out var distanceElement) ||
                !distanceElement.TryGetDouble(out var distanceM) ||
                (distance.Count > 0 && distanceM <= distance[^1]))
            {
                continue;
            }

            if (double.IsNaN(startWidth) && item.TryGetProperty("trackWidthM", out var width) &&
                width.TryGetDouble(out var widthM))
            {
                startWidth = widthM;
            }

            latitude.Add(position.Latitude);
            longitude.Add(position.Longitude);
            distance.Add(distanceM);
        }

        return distance.Count >= 20 && distance[^1] > 200
            ? new TrackReference(meanlinePath, latitude.ToArray(), longitude.ToArray(), distance.ToArray(),
                double.IsFinite(startWidth) ? startWidth : 15, lineA, lineB)
            : null;
    }

    /// <summary>Times (on the stream's clock) at which the stream crosses the start/finish line in the direction of travel.</summary>
    public List<double> FindLineCrossings(IReadOnlyList<TelemetryPoint> points)
    {
        var result = new List<double>();
        if (points.Count < 2)
        {
            return result;
        }

        var previous = ToLocal(points[0].Latitude, points[0].Longitude);
        var lineX = _lineB.X - _lineA.X;
        var lineY = _lineB.Y - _lineA.Y;
        for (var index = 1; index < points.Count; index++)
        {
            var current = ToLocal(points[index].Latitude, points[index].Longitude);
            var moveX = current.X - previous.X;
            var moveY = current.Y - previous.Y;
            var denominator = moveX * lineY - moveY * lineX;
            if (Math.Abs(denominator) > 1e-9 && moveX * _forward.X + moveY * _forward.Y > 0 &&
                points[index].TimeMs - points[index - 1].TimeMs < 2_000)
            {
                var along = ((_lineA.X - previous.X) * lineY - (_lineA.Y - previous.Y) * lineX) / denominator;
                var across = ((_lineA.X - previous.X) * moveY - (_lineA.Y - previous.Y) * moveX) / denominator;
                var crossX = previous.X + along * moveX;
                var crossY = previous.Y + along * moveY;
                // Reject crossings of the extended line from the pit lane or paddock: they sit off the track.
                if (along is >= 0 and < 1 && across is >= 0 and <= 1 &&
                    Math.Sqrt(crossX * crossX + crossY * crossY) <= _crossingRadiusM)
                {
                    var time = points[index - 1].TimeMs + along * (points[index].TimeMs - points[index - 1].TimeMs);
                    if (result.Count == 0 || time - result[^1] > 10_000)
                    {
                        result.Add(time);
                    }
                }
            }

            previous = current;
        }

        return result;
    }

    /// <summary>Distance along the centreline of the closest point to a GPS fix, searching near <paramref name="hint"/> first.</summary>
    public (double DistanceM, double OffsetM, int Segment) Project(double latitude, double longitude, int hint = -1)
    {
        var (x, y) = ToLocal(latitude, longitude);
        var segments = _x.Length - 1;
        if (hint >= 0)
        {
            // Meanline points are ~1 m apart: look a little behind and well ahead of the last match.
            var local = Search(x, y, hint - 20, hint + 80);
            if (local.OffsetM <= 25)
            {
                return local;
            }
        }

        return Search(x, y, 0, segments - 1);
    }

    private (double DistanceM, double OffsetM, int Segment) Search(double x, double y, int from, int to)
    {
        var segments = _x.Length - 1;
        var bestSquared = double.MaxValue;
        var bestDistance = 0d;
        var bestSegment = 0;
        for (var step = from; step <= to; step++)
        {
            var i = ((step % segments) + segments) % segments;
            var dx = _x[i + 1] - _x[i];
            var dy = _y[i + 1] - _y[i];
            var lengthSquared = dx * dx + dy * dy;
            var fraction = lengthSquared > 0
                ? Math.Clamp(((x - _x[i]) * dx + (y - _y[i]) * dy) / lengthSquared, 0, 1)
                : 0;
            var ex = _x[i] + fraction * dx - x;
            var ey = _y[i] + fraction * dy - y;
            var squared = ex * ex + ey * ey;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                bestDistance = _distance[i] + fraction * (_distance[i + 1] - _distance[i]);
                bestSegment = i;
            }
        }

        return (bestDistance, Math.Sqrt(bestSquared), bestSegment);
    }

    /// <summary>
    /// A high percentile of how far the stream strays from this centreline at racing speed (so time in the paddock
    /// and pit lane doesn't count): small only for the layout that was driven.
    /// </summary>
    public double FitError(IReadOnlyList<TelemetryPoint> points)
    {
        const double racingSpeedMps = 20;
        var stride = Math.Max(1, points.Count / 4_000);
        var offsets = new List<double>(points.Count / stride + 1);
        var hint = -1;
        for (var index = 0; index < points.Count; index += stride)
        {
            if (!(points[index].SpeedMps > racingSpeedMps))
            {
                hint = -1;
                continue;
            }

            // Even with the stride, consecutive racing-speed samples are well inside Project's local search window
            // (and it falls back to a full search if they aren't).
            var projection = Project(points[index].Latitude, points[index].Longitude, hint);
            hint = projection.Segment;
            offsets.Add(projection.OffsetM);
        }

        offsets.Sort();
        return offsets.Count == 0 ? double.PositiveInfinity : offsets[(int)(offsets.Count * 0.9)];
    }

    private (double X, double Y) ToLocal(double latitude, double longitude) =>
        ((longitude - _originLongitude) * _longitudeScale, (latitude - _originLatitude) * MetersPerDegree);

    private static bool TryReadPosition(JsonElement element, string name, out (double Latitude, double Longitude) position)
    {
        position = default;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("latitude", out var latitude) || !latitude.TryGetDouble(out var lat) ||
            !value.TryGetProperty("longitude", out var longitude) || !longitude.TryGetDouble(out var lon))
        {
            return false;
        }

        position = (lat, lon);
        return double.IsFinite(lat) && double.IsFinite(lon);
    }
}

/// <summary>
/// Puts GPS logger sessions (AiM XRK/XRZ, VBOX) on the Garmin track definition: laps are re-cut where the logger's
/// GPS crosses the Garmin start/finish line and lap distance is the projection onto the Garmin meanline, exactly as
/// Garmin computes its own laps. (AiM clock time and drift come from GPS satellite time in the XRK reader, not from
/// Garmin: Garmin session start times are not reliable enough to correct another device's clock against.)
/// </summary>
internal static class AimGpsSessionAlignment
{
    private const double SameVenueM = 5_000;

    // 90th-percentile distance from the centreline above which a meanline is not the layout that was driven.
    private const double MaxFitErrorM = 15;

    public static IReadOnlyList<SessionInfo> CorrectSessions(IReadOnlyList<SessionInfo> input)
    {
        var corrected = input.ToArray();
        var loggers = Enumerable.Range(0, corrected.Length)
            .Where(index => corrected[index].AiMData?.Stream is not null)
            .ToArray();
        if (loggers.Length == 0)
        {
            return corrected;
        }

        var garminSessions = input.Where(session => session.AiMData is null).ToArray();
        var references = LoadReferences(garminSessions, loggers.Select(index => corrected[index]).ToArray());
        Parallel.ForEach(loggers, index =>
        {
            var session = corrected[index];
            var stream = session.AiMData!.Stream!;
            try
            {
                var reference = references
                    .Where(item => DistanceMeters(session.Latitude, session.Longitude, item.Latitude, item.Longitude) <= SameVenueM)
                    .Select(item => (Reference: item, Error: item.FitError(stream.Points)))
                    .Where(item => item.Error <= MaxFitErrorM)
                    .OrderBy(item => item.Error)
                    .Select(item => item.Reference)
                    .FirstOrDefault();
                if (reference is not null && CutLaps(stream, reference) is { Count: > 0 } laps)
                {
                    session = session with
                    {
                        MeanlineLengthM = reference.LengthM,
                        AiMData = session.AiMData with { Laps = laps }
                    };
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
            }

            // The stream is only needed to re-cut laps; don't keep a second copy of every channel in memory.
            corrected[index] = session with { AiMData = session.AiMData! with { Stream = null } };
        });

        return corrected;
    }

    // One reference per distinct meanline near a logger session. Garmin writes a copy of the meanline beside
    // every session, so dozens of sessions usually share a handful of layouts.
    private static IReadOnlyList<TrackReference> LoadReferences(
        IReadOnlyList<SessionInfo> garminSessions,
        IReadOnlyList<SessionInfo> loggers)
    {
        var paths = garminSessions
            .Where(session => session.MeanlinePath is not null &&
                              loggers.Any(logger => DistanceMeters(logger.Latitude, logger.Longitude,
                                  session.Latitude, session.Longitude) <= SameVenueM))
            .GroupBy(session => $"{session.MeanlineTrackName}|{session.MeanlineConfigurationName}|{session.MeanlineLengthM:F1}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(session => session.LocalStart).First().MeanlinePath!)
            .ToArray();
        var result = new TrackReference?[paths.Length];
        Parallel.For(0, paths.Length, index =>
        {
            try
            {
                result[index] = TrackReference.Load(paths[index]);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            {
            }
        });

        return result.OfType<TrackReference>().ToArray();
    }

    private static IReadOnlyList<AiMLapData> CutLaps(AiMSessionStream stream, TrackReference reference)
    {
        var points = stream.Points;
        var crossings = reference.FindLineCrossings(points);
        if (crossings.Count < 2)
        {
            return [];
        }

        var durations = crossings.Zip(crossings.Skip(1), (start, end) => end - start).Order().ToArray();
        var median = durations[durations.Length / 2];
        var laps = new List<AiMLapData>(crossings.Count - 1);
        var cursor = 0;
        for (var lap = 0; lap + 1 < crossings.Count; lap++)
        {
            var start = crossings[lap];
            var end = crossings[lap + 1];
            while (cursor < points.Count - 1 && points[cursor + 1].TimeMs <= start)
            {
                cursor++;
            }

            if (end - start > median * 2 || end - start < median * 0.5)
            {
                continue; // a pit stop, a stoppage or a short cut: not a comparable lap
            }

            var lapPoints = ProjectLap(points, cursor, start, end, reference);
            if (lapPoints is null)
            {
                continue;
            }

            laps.Add(new AiMLapData(
                laps.Count + 1,
                start,
                end - start,
                lapPoints,
                stream.Channels
                    .Select(channel => channel with { Samples = Slice(channel.Samples, start, end) })
                    .Where(channel => channel.Samples.Count > 0)
                    .ToArray()));
        }

        return laps;
    }

    // Points between two line crossings, with interpolated fixes on each line, distance = meanline projection.
    private static TelemetryPoint[]? ProjectLap(
        IReadOnlyList<TelemetryPoint> points, int cursor, double start, double end, TrackReference reference)
    {
        var lap = new List<TelemetryPoint> { Interpolate(points, cursor, start) };
        for (var index = cursor + 1; index < points.Count && points[index].TimeMs < end; index++)
        {
            if (points[index].TimeMs > start)
            {
                lap.Add(points[index]);
            }
        }

        var endCursor = cursor;
        while (endCursor < points.Count - 2 && points[endCursor + 1].TimeMs <= end)
        {
            endCursor++;
        }

        lap.Add(Interpolate(points, endCursor, end));
        if (lap.Count < 20)
        {
            return null;
        }

        // Accumulate wrapped steps along the closed centreline so the lap runs 0 → length even though the
        // first and last fixes project either side of the line.
        var length = reference.LengthM;
        var result = new TelemetryPoint[lap.Count];
        var first = reference.Project(lap[0].Latitude, lap[0].Longitude);
        var previous = first.DistanceM;
        var hint = first.Segment;
        var progress = 0d;
        var distance = 0d;
        for (var index = 0; index < lap.Count; index++)
        {
            if (index > 0)
            {
                var projection = reference.Project(lap[index].Latitude, lap[index].Longitude, hint);
                hint = projection.Segment;
                progress += Math.IEEERemainder(projection.DistanceM - previous, length);
                previous = projection.DistanceM;
                distance = Math.Clamp(Math.Max(distance, progress), 0, length);
            }

            result[index] = lap[index] with { DistanceM = distance, TimeMs = lap[index].TimeMs - start };
        }

        if (Math.Abs(progress - length) > length * 0.05)
        {
            return null; // didn't follow this layout all the way round
        }

        result[^1] = result[^1] with { DistanceM = length };
        return result;
    }

    private static TelemetryPoint Interpolate(IReadOnlyList<TelemetryPoint> points, int index, double time)
    {
        var a = points[index];
        var b = points[Math.Min(index + 1, points.Count - 1)];
        var span = b.TimeMs - a.TimeMs;
        var f = span > 0 ? Math.Clamp((time - a.TimeMs) / span, 0, 1) : 0;
        static double Lerp(double x, double y, double f) =>
            double.IsFinite(x) && double.IsFinite(y) ? x + (y - x) * f : f < 0.5 ? x : y;
        return new TelemetryPoint(
            Lerp(a.DistanceM, b.DistanceM, f), time,
            Lerp(a.SpeedMps, b.SpeedMps, f),
            Lerp(a.LongitudinalG, b.LongitudinalG, f),
            Lerp(a.LateralG, b.LateralG, f),
            Lerp(a.Latitude, b.Latitude, f),
            Lerp(a.Longitude, b.Longitude, f));
    }

    private static AiMChannelSample[] Slice(IReadOnlyList<AiMChannelSample> samples, double start, double end)
    {
        var first = LowerBound(samples, start);
        var count = UpperBound(samples, end) - first;
        var result = new AiMChannelSample[Math.Max(0, count)];
        for (var index = 0; index < result.Length; index++)
        {
            var sample = samples[first + index];
            result[index] = new AiMChannelSample(sample.TimeMs - start, sample.Value);
        }

        return result;

        // First index with TimeMs >= time / first index with TimeMs > time.
        static int LowerBound(IReadOnlyList<AiMChannelSample> samples, double time) =>
            Search(samples, value => value < time);

        static int UpperBound(IReadOnlyList<AiMChannelSample> samples, double time) =>
            Search(samples, value => value <= time);

        static int Search(IReadOnlyList<AiMChannelSample> samples, Func<double, bool> before)
        {
            var low = 0;
            var high = samples.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (before(samples[middle].TimeMs))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double metersPerDegree = 111_320;
        var x = (longitudeA - longitudeB) * metersPerDegree * Math.Cos(latitudeA * Math.PI / 180);
        var y = (latitudeA - latitudeB) * metersPerDegree;
        return Math.Sqrt(x * x + y * y);
    }
}
