namespace TrackTelemetryViewer;

internal sealed record AimCsvTransform(
    double RefLatitude,
    double RefLongitude,
    double SourceLatitude,
    double SourceLongitude,
    double ShiftSeconds,
    bool Mirror,
    double RotationRad,
    double TranslateXM,
    double TranslateYM,
    double DistanceScale,
    double[]? PathLatitude = null,
    double[]? PathLongitude = null,
    double[]? PathDistanceM = null);

/// <summary>
/// Aligns a dead-reckoned AiM CSV lap (no real coordinates) onto a real GPS lap of the same track:
/// finds where the real start/finish sits along the CSV lap, then the rigid transform that overlays the two.
/// </summary>
internal static class AimCsvAlignment
{
    private const double MetersPerDegree = 111_320;
    private const int Samples = 1440;

    public static (double ShiftSeconds, bool Mirror) FindStartShift(
        IReadOnlyList<TelemetryPoint> csvLap,
        IReadOnlyList<TelemetryPoint> referenceLap)
    {
        var source = Resample(ToMeters(csvLap, csvLap[0].Latitude, csvLap[0].Longitude));
        var target = Resample(ToMeters(referenceLap, FirstFinite(referenceLap).Latitude, FirstFinite(referenceLap).Longitude));
        var best = (Residual: double.MaxValue, Shift: 0, Mirror: false);
        foreach (var mirror in new[] { false, true })
        {
            var candidate = mirror ? source.Select(point => (-point.X, point.Y)).ToArray() : source;
            for (var shift = 0; shift < Samples; shift++)
            {
                var residual = Fit(candidate, target, shift).Residual;
                if (residual < best.Residual)
                {
                    best = (residual, shift, mirror);
                }
            }
        }

        var distance = csvLap[^1].DistanceM * best.Shift / Samples;
        var seconds = TelemetryLoader.InterpolateTimeMs(csvLap, distance) / 1000d;
        if (best.Shift > Samples / 2)
        {
            seconds -= csvLap[^1].TimeMs / 1000d;
        }

        return (seconds, best.Mirror);
    }

    public static AimCsvTransform FitTransform(
        IReadOnlyList<TelemetryPoint> csvLap,
        IReadOnlyList<TelemetryPoint> referenceLap,
        double shiftSeconds,
        bool mirror)
    {
        var start = FirstFinite(referenceLap);
        var source = Resample(ToMeters(csvLap, csvLap[0].Latitude, csvLap[0].Longitude));
        if (mirror)
        {
            source = source.Select(point => (-point.X, point.Y)).ToArray();
        }

        var target = Resample(ToMeters(referenceLap, start.Latitude, start.Longitude));
        var fit = Fit(source, target, 0);
        var scale = referenceLap[^1].DistanceM > 0 && csvLap[^1].DistanceM > 0
            ? referenceLap[^1].DistanceM / csvLap[^1].DistanceM
            : 1;
        var path = BuildPath(referenceLap);
        return new AimCsvTransform(
            start.Latitude, start.Longitude, csvLap[0].Latitude, csvLap[0].Longitude,
            shiftSeconds, mirror, fit.Rotation, fit.TranslateX, fit.TranslateY, scale,
            path.Select(point => Math.Round(point.Latitude, 7)).ToArray(),
            path.Select(point => Math.Round(point.Longitude, 7)).ToArray(),
            path.Select(point => Math.Round(point.DistanceM, 2)).ToArray());
    }

    // The reference lap thinned to roughly 2 m spacing, kept small enough to store with the settings.
    private static List<TelemetryPoint> BuildPath(IReadOnlyList<TelemetryPoint> referenceLap)
    {
        var path = new List<TelemetryPoint>();
        foreach (var point in referenceLap.Where(point =>
                     double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) && double.IsFinite(point.DistanceM)))
        {
            if (path.Count == 0 || point.DistanceM - path[^1].DistanceM >= 2)
            {
                path.Add(point);
            }
        }

        return path;
    }

    // Replace dead-reckoned distance with the distance along the reference lap at the nearest track position,
    // so a given distance means the same spot on the circuit in both laps.
    private static void ProjectDistances(TelemetryPoint[] points, AimCsvTransform transform)
    {
        var pathLat = transform.PathLatitude!;
        var pathLon = transform.PathLongitude!;
        var pathDistance = transform.PathDistanceM!;
        var count = pathLat.Length;
        var scale = MetersPerDegree * Math.Cos(transform.RefLatitude * Math.PI / 180);
        var px = new double[count];
        var py = new double[count];
        for (var index = 0; index < count; index++)
        {
            px[index] = (pathLon[index] - transform.RefLongitude) * scale;
            py[index] = (pathLat[index] - transform.RefLatitude) * MetersPerDegree;
        }

        var current = 0;
        var previousDistance = 0d;
        for (var index = 0; index < points.Length; index++)
        {
            var x = (points[index].Longitude - transform.RefLongitude) * scale;
            var y = (points[index].Latitude - transform.RefLatitude) * MetersPerDegree;
            var window = index == 0 ? 100 : 30;
            var best = current;
            var bestDistance = double.MaxValue;
            for (var candidate = current; candidate <= Math.Min(count - 1, current + window); candidate++)
            {
                var d = (px[candidate] - x) * (px[candidate] - x) + (py[candidate] - y) * (py[candidate] - y);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }
            }

            var along = pathDistance[best];
            foreach (var other in new[] { best - 1, best + 1 })
            {
                if (other < 0 || other >= count)
                {
                    continue;
                }

                var dx = px[other] - px[best];
                var dy = py[other] - py[best];
                var length = dx * dx + dy * dy;
                var t = length > 0 ? ((x - px[best]) * dx + (y - py[best]) * dy) / length : 0;
                if (t > 0 && t <= 1)
                {
                    along = pathDistance[best] + t * (pathDistance[other] - pathDistance[best]);
                }
            }

            previousDistance = Math.Max(previousDistance, along);
            points[index] = points[index] with { DistanceM = previousDistance };
            current = Math.Max(current, best - 1);
        }
    }

    // Shape matching can't pin the start/finish along a straight, so slide the CSV lap until its speed-vs-distance
    // profile (braking and acceleration points) best matches the reference lap. Returns seconds to move the CSV start.
    public static double SpeedCorrectionSeconds(
        IReadOnlyList<TelemetryPoint> csvLap,
        IReadOnlyList<TelemetryPoint> referenceLap)
    {
        const int grid = 1000;
        var csvLength = csvLap[^1].DistanceM;
        var referenceLength = referenceLap[^1].DistanceM;
        if (csvLength <= 0 || referenceLength <= 0)
        {
            return 0;
        }

        var csvSpeed = SpeedByDistance(csvLap, grid, csvLength);
        var referenceSpeed = SpeedByDistance(referenceLap, grid, referenceLength);
        if (csvSpeed is null || referenceSpeed is null)
        {
            return 0;
        }

        var bestShift = 0;
        var bestScore = double.MinValue;
        for (var shift = -grid / 2; shift < grid / 2; shift++)
        {
            double score = 0;
            for (var index = 0; index < grid; index++)
            {
                score += csvSpeed[((index + shift) % grid + grid) % grid] * referenceSpeed[index];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestShift = shift;
            }
        }

        var distance = csvLength * bestShift / grid;
        return distance >= 0
            ? TelemetryLoader.InterpolateTimeMs(csvLap, distance) / 1000d
            : -(csvLap[^1].TimeMs - TelemetryLoader.InterpolateTimeMs(csvLap, csvLength + distance)) / 1000d;
    }

    private static double[]? SpeedByDistance(IReadOnlyList<TelemetryPoint> lap, int grid, double length)
    {
        var finite = lap.Where(point => double.IsFinite(point.SpeedMps)).ToArray();
        if (finite.Length < 20)
        {
            return null;
        }

        var result = new double[grid];
        var cursor = 0;
        for (var index = 0; index < grid; index++)
        {
            var distance = length * index / grid;
            while (cursor < finite.Length - 2 && finite[cursor + 1].DistanceM < distance)
            {
                cursor++;
            }

            var span = finite[cursor + 1].DistanceM - finite[cursor].DistanceM;
            var fraction = span > 0 ? Math.Clamp((distance - finite[cursor].DistanceM) / span, 0, 1) : 0;
            result[index] = finite[cursor].SpeedMps + (finite[cursor + 1].SpeedMps - finite[cursor].SpeedMps) * fraction;
        }

        var mean = result.Average();
        for (var index = 0; index < grid; index++)
        {
            result[index] -= mean;
        }

        return result;
    }

    public static IReadOnlyList<TelemetryPoint> Apply(IReadOnlyList<TelemetryPoint> points, AimCsvTransform transform)
    {
        var sourceScale = MetersPerDegree * Math.Cos(transform.SourceLatitude * Math.PI / 180);
        var targetScale = MetersPerDegree * Math.Cos(transform.RefLatitude * Math.PI / 180);
        var cos = Math.Cos(transform.RotationRad);
        var sin = Math.Sin(transform.RotationRad);
        var result = points.Select(point =>
        {
            var x = (point.Longitude - transform.SourceLongitude) * sourceScale;
            var y = (point.Latitude - transform.SourceLatitude) * MetersPerDegree;
            if (transform.Mirror)
            {
                x = -x;
            }

            var rotatedX = x * cos - y * sin + transform.TranslateXM;
            var rotatedY = x * sin + y * cos + transform.TranslateYM;
            return point with
            {
                DistanceM = point.DistanceM * transform.DistanceScale,
                Latitude = transform.RefLatitude + rotatedY / MetersPerDegree,
                Longitude = transform.RefLongitude + rotatedX / targetScale
            };
        }).ToArray();
        if (transform.PathDistanceM is { Length: > 10 } && transform.PathLatitude is not null &&
            transform.PathLongitude is not null)
        {
            ProjectDistances(result, transform);
        }

        return result;
    }

    private static TelemetryPoint FirstFinite(IReadOnlyList<TelemetryPoint> points) =>
        points.First(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude));

    private static List<(double X, double Y)> ToMeters(
        IReadOnlyList<TelemetryPoint> points, double originLatitude, double originLongitude)
    {
        var scale = MetersPerDegree * Math.Cos(originLatitude * Math.PI / 180);
        return points
            .Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude))
            .Select(point => ((point.Longitude - originLongitude) * scale, (point.Latitude - originLatitude) * MetersPerDegree))
            .ToList();
    }

    private static (double X, double Y)[] Resample(List<(double X, double Y)> points)
    {
        var cumulative = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] +
                Math.Sqrt(Math.Pow(points[index].X - points[index - 1].X, 2) +
                          Math.Pow(points[index].Y - points[index - 1].Y, 2));
        }

        var total = cumulative[^1];
        var result = new (double X, double Y)[Samples];
        var cursor = 0;
        for (var sample = 0; sample < Samples; sample++)
        {
            var wanted = total * sample / Samples;
            while (cursor < points.Count - 2 && cumulative[cursor + 1] < wanted)
            {
                cursor++;
            }

            var span = cumulative[cursor + 1] - cumulative[cursor];
            var fraction = span > 0 ? (wanted - cumulative[cursor]) / span : 0;
            result[sample] = (
                points[cursor].X + (points[cursor + 1].X - points[cursor].X) * fraction,
                points[cursor].Y + (points[cursor + 1].Y - points[cursor].Y) * fraction);
        }

        return result;
    }

    // Best rigid rotation + translation mapping source[(i + shift) % n] onto target[i].
    private static (double Residual, double Rotation, double TranslateX, double TranslateY) Fit(
        (double X, double Y)[] source, (double X, double Y)[] target, int shift)
    {
        double sourceX = 0, sourceY = 0, targetX = 0, targetY = 0;
        for (var index = 0; index < Samples; index++)
        {
            var s = source[(index + shift) % Samples];
            sourceX += s.X;
            sourceY += s.Y;
            targetX += target[index].X;
            targetY += target[index].Y;
        }

        sourceX /= Samples;
        sourceY /= Samples;
        targetX /= Samples;
        targetY /= Samples;

        double dot = 0, cross = 0, sourceEnergy = 0, targetEnergy = 0;
        for (var index = 0; index < Samples; index++)
        {
            var s = source[(index + shift) % Samples];
            var ax = s.X - sourceX;
            var ay = s.Y - sourceY;
            var bx = target[index].X - targetX;
            var by = target[index].Y - targetY;
            dot += ax * bx + ay * by;
            cross += ax * by - ay * bx;
            sourceEnergy += ax * ax + ay * ay;
            targetEnergy += bx * bx + by * by;
        }

        var rotation = Math.Atan2(cross, dot);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        var residual = sourceEnergy + targetEnergy - 2 * (dot * cos + cross * sin);
        return (residual, rotation,
            targetX - (sourceX * cos - sourceY * sin),
            targetY - (sourceX * sin + sourceY * cos));
    }
}
