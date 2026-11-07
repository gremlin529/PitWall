using System.IO;

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
    double[]? PathDistanceM = null,
    // Fraction of the driven lap distance completed at every DrivenFractionStepM of track (meanline) distance,
    // learned from Garmin GPS laps of the layout. When present it replaces the heading warp (see MapByDrivenFraction).
    double[]? DrivenFraction = null);

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

    private const double WarpStepM = 2;
    private const double HeadingChordM = 8;
    private const int WarpBandSteps = 200;
    private const double WarpStretchPenalty = 0.03;

    // Replace dead-reckoned distance with distance along the reference lap by warping the CSV lap's heading
    // profile onto the reference's, so each corner lands on the same corner; straights follow integrated speed.
    // Snapping to the nearest track position instead turns the dead-reckoned map's sideways drift into 15-25 m
    // of along-track error through some corners.
    private static void ProjectDistances(TelemetryPoint[] points, AimCsvTransform transform)
    {
        var scale = MetersPerDegree * Math.Cos(transform.RefLatitude * Math.PI / 180);
        var pathDistance = transform.PathDistanceM!;
        var csvHeading = HeadingProfile(
            points.Select(point => (point.Longitude - transform.RefLongitude) * scale).ToArray(),
            points.Select(point => (point.Latitude - transform.RefLatitude) * MetersPerDegree).ToArray(),
            points.Select(point => point.DistanceM).ToArray());
        var referenceHeading = HeadingProfile(
            transform.PathLongitude!.Select(longitude => (longitude - transform.RefLongitude) * scale).ToArray(),
            transform.PathLatitude!.Select(latitude => (latitude - transform.RefLatitude) * MetersPerDegree).ToArray(),
            pathDistance);
        if (csvHeading.Length < 2 || referenceHeading.Length < 2)
        {
            return;
        }

        var warp = WarpHeadings(csvHeading, referenceHeading);
        var maxDistance = pathDistance[^1];
        for (var index = 0; index < points.Length; index++)
        {
            var position = Math.Clamp(points[index].DistanceM / WarpStepM, 0, warp.Length - 1);
            var low = Math.Min((int)position, warp.Length - 2);
            var distance = warp[low] + (warp[low + 1] - warp[low]) * (position - low);
            distance = Math.Clamp(distance, 0, maxDistance);
            var upper = Array.BinarySearch(pathDistance, distance);
            if (upper < 0)
            {
                upper = ~upper;
            }

            upper = Math.Clamp(upper, 1, pathDistance.Length - 1);
            var lower = upper - 1;
            var span = pathDistance[upper] - pathDistance[lower];
            var fraction = span > 0 ? (distance - pathDistance[lower]) / span : 0;
            var latitude = transform.PathLatitude![lower] +
                           (transform.PathLatitude[upper] - transform.PathLatitude[lower]) * fraction;
            var longitude = transform.PathLongitude![lower] +
                            (transform.PathLongitude[upper] - transform.PathLongitude[lower]) * fraction;
            points[index] = points[index] with
            {
                DistanceM = distance,
                Latitude = latitude,
                Longitude = longitude
            };
        }
    }

    // Heading (radians) every WarpStepM metres along a path, from the chord spanning ±HeadingChordM.
    private static double[] HeadingProfile(double[] x, double[] y, double[] distance)
    {
        var count = (int)Math.Ceiling(distance[^1] / WarpStepM) + 1;
        var result = new double[count];
        var behind = 0;
        var ahead = 0;
        for (var index = 0; index < count; index++)
        {
            var at = index * WarpStepM;
            while (behind < distance.Length - 1 && distance[behind + 1] < at - HeadingChordM)
            {
                behind++;
            }

            while (ahead < distance.Length - 1 && distance[ahead] < at + HeadingChordM)
            {
                ahead++;
            }

            result[index] = Math.Atan2(x[ahead] - x[behind], y[ahead] - y[behind]);
        }

        return result;
    }

    // Banded dynamic time warping of the CSV heading profile onto the reference one, start to start and end to end.
    // Returns the reference distance for each CSV profile step.
    private static double[] WarpHeadings(double[] source, double[] target)
    {
        var n = source.Length;
        var m = target.Length;
        var width = 2 * WarpBandSteps + 1;
        var cost = new double[n, width];
        var from = new byte[n, width];
        int Diagonal(int row) => (int)Math.Round((double)row * (m - 1) / (n - 1));

        for (var i = 0; i < n; i++)
        {
            var centre = Diagonal(i);
            var previousCentre = i > 0 ? Diagonal(i - 1) : 0;
            for (var offset = 0; offset < width; offset++)
            {
                cost[i, offset] = double.PositiveInfinity;
                var j = centre + offset - WarpBandSteps;
                if (j < 0 || j >= m)
                {
                    continue;
                }

                var difference = Math.IEEERemainder(source[i] - target[j], 2 * Math.PI);
                var local = difference * difference;
                if (i == 0 && j == 0)
                {
                    cost[i, offset] = local;
                    continue;
                }

                var best = double.PositiveInfinity;
                byte step = 0;
                if (i > 0)
                {
                    var diagonal = j - 1 - previousCentre + WarpBandSteps;
                    if (diagonal >= 0 && diagonal < width && cost[i - 1, diagonal] < best)
                    {
                        best = cost[i - 1, diagonal];
                        step = 1;
                    }

                    var up = j - previousCentre + WarpBandSteps;
                    if (up >= 0 && up < width && cost[i - 1, up] + WarpStretchPenalty < best)
                    {
                        best = cost[i - 1, up] + WarpStretchPenalty;
                        step = 2;
                    }
                }

                if (offset > 0 && cost[i, offset - 1] + WarpStretchPenalty < best)
                {
                    best = cost[i, offset - 1] + WarpStretchPenalty;
                    step = 3;
                }

                cost[i, offset] = best + local;
                from[i, offset] = step;
            }
        }

        var sum = new double[n];
        var hits = new int[n];
        var row = n - 1;
        var column = m - 1;
        while (true)
        {
            sum[row] += column;
            hits[row]++;
            var step = from[row, column - Diagonal(row) + WarpBandSteps];
            if (step == 0)
            {
                break;
            }

            if (step != 3)
            {
                row--;
            }

            if (step != 2)
            {
                column--;
            }
        }

        return sum.Select((total, index) => total / hits[index] * WarpStepM).ToArray();
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
        if (transform.DrivenFraction is { Length: > 10 } fraction && transform.PathDistanceM is { Length: > 10 } &&
            transform.PathLatitude is not null && transform.PathLongitude is not null)
        {
            return MapByDrivenFraction(points, fraction, transform);
        }

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

    // ---- Driven-distance profile: the generic CSV correction ----
    //
    // A CSV lap knows its own driven distance precisely (integrated GPS speed) but has no position. Garmin lap distance
    // is progress along the track centreline, which runs up to ~8% ahead of driven distance where the racing line cuts
    // corners. That relationship is a property of the layout and the racing line, so it is learned once from Garmin GPS
    // laps as "fraction of the driven lap completed at each metre of track" and every CSV sample is mapped through it.
    // Unlike the heading warp this cannot produce impossible local speeds (which showed up as time-delta drift).

    public const double DrivenFractionStepM = 2;

    /// <summary>
    /// Adds the driven-distance profile to a fitted CSV transform and corrects its start shift so the CSV laps start on
    /// the Garmin start/finish line. <paramref name="readCsv"/> re-reads the CSV with a given start shift.
    /// Returns the transform unchanged if there aren't enough Garmin laps on the layout.
    /// </summary>
    public static AimCsvTransform AddDrivenFraction(
        AimCsvTransform transform,
        IEnumerable<SessionInfo> trackSessions,
        IReadOnlyList<TelemetryPoint> referenceLap,
        Func<double, SessionInfo> readCsv)
    {
        var trackLength = referenceLap[^1].DistanceM;
        var garminLaps = new List<IReadOnlyList<TelemetryPoint>>();
        foreach (var session in trackSessions
                     .Where(session => session.AiMData is null &&
                                       (session.MeanlineLengthM <= 0 || Math.Abs(session.MeanlineLengthM - trackLength) < 2))
                     .Take(10))
        {
            try
            {
                garminLaps.AddRange(TelemetryLoader.ReadFastestDrivenLapTraces(session, 3));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
            }
        }

        if (BuildDrivenFraction(garminLaps, trackLength) is not { } fraction)
        {
            return transform;
        }

        var shift = transform.ShiftSeconds;
        for (var pass = 0; pass < 3; pass++)
        {
            var laps = readCsv(shift).AiMData!.Laps;
            var offset = StartOffsetMeters(laps, fraction, trackLength, garminLaps);
            if (Math.Abs(offset) < 1)
            {
                break;
            }

            var startSpeeds = laps.Select(lap => lap.Points[0].SpeedMps).Where(speed => speed > 5).Order().ToArray();
            if (startSpeeds.Length == 0)
            {
                break;
            }

            shift += offset / startSpeeds[startSpeeds.Length / 2];
        }

        return transform with { ShiftSeconds = shift, DrivenFraction = fraction };
    }

    /// <summary>Median, over Garmin GPS laps of one layout, of the driven-lap fraction completed at each track distance.</summary>
    public static double[]? BuildDrivenFraction(IEnumerable<IReadOnlyList<TelemetryPoint>> garminLaps, double trackLengthM)
    {
        var count = (int)(trackLengthM / DrivenFractionStepM) + 1;
        var profiles = new List<double[]>();
        foreach (var lap in garminLaps)
        {
            var points = lap.Where(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) &&
                                            double.IsFinite(point.DistanceM)).ToArray();
            if (points.Length < 100 || Math.Abs(points[^1].DistanceM - trackLengthM) > 5 || points[0].DistanceM > 5)
            {
                continue;
            }

            var driven = CumulativePathLength(points);
            if (driven[^1] < trackLengthM * 0.8)
            {
                continue;
            }

            var profile = new double[count];
            var cursor = 0;
            for (var index = 0; index < count; index++)
            {
                var distance = index * DrivenFractionStepM;
                while (cursor < points.Length - 2 && points[cursor + 1].DistanceM < distance)
                {
                    cursor++;
                }

                var span = points[cursor + 1].DistanceM - points[cursor].DistanceM;
                var fraction = span > 0 ? Math.Clamp((distance - points[cursor].DistanceM) / span, 0, 1) : 0;
                profile[index] = (driven[cursor] + (driven[cursor + 1] - driven[cursor]) * fraction) / driven[^1];
            }

            profile[0] = 0;
            profile[^1] = 1;
            profiles.Add(profile);
        }

        if (profiles.Count < 3)
        {
            return null;
        }

        var result = new double[count];
        var column = new double[profiles.Count];
        for (var index = 0; index < count; index++)
        {
            for (var lap = 0; lap < profiles.Count; lap++)
            {
                column[lap] = profiles[lap][index];
            }

            Array.Sort(column);
            result[index] = Math.Round(column[column.Length / 2], 6);
        }

        for (var index = 1; index < count; index++)
        {
            result[index] = Math.Max(result[index], result[index - 1] + 1e-6);
        }

        result[^1] = Math.Max(result[^1], 1);
        return result;
    }

    /// <summary>
    /// How far (m along the track) a CSV session's laps, mapped by the profile, sit from where the Garmin laps turn:
    /// one number for the whole session, since what it measures is where the CSV laps start relative to the
    /// Garmin start/finish line. Negative means the CSV laps start too late.
    /// </summary>
    public static double StartOffsetMeters(
        IReadOnlyList<AiMLapData> csvLaps,
        double[] drivenFraction,
        double trackLengthM,
        IReadOnlyList<IReadOnlyList<TelemetryPoint>> garminLaps)
    {
        var templates = garminLaps.Take(15)
            .Select(lap => HeadingByDistance(lap, lap.Select(point => point.DistanceM).ToArray(), trackLengthM))
            .ToArray();
        var offsets = new List<double>();
        foreach (var lap in csvLaps.Where(lap => lap.Points.Count > 100))
        {
            var mapped = MapDistances(lap.Points, drivenFraction, trackLengthM);
            var heading = HeadingByDistance(lap.Points, mapped, trackLengthM);
            var best = (Error: double.MaxValue, Shift: 0);
            for (var shift = -40; shift <= 40; shift++)
            {
                double error = 0;
                for (var index = 50; index < heading.Length - 50; index++)
                {
                    foreach (var template in templates)
                    {
                        var difference = Math.IEEERemainder(heading[index + shift] - template[index], 2 * Math.PI);
                        error += Math.Min(difference * difference, 0.1);
                    }
                }

                if (error < best.Error)
                {
                    best = (error, shift);
                }
            }

            offsets.Add(best.Shift * DrivenFractionStepM);
        }

        return offsets.Count == 0 ? 0 : offsets.Order().ElementAt(offsets.Count / 2);
    }

    private static IReadOnlyList<TelemetryPoint> MapByDrivenFraction(
        IReadOnlyList<TelemetryPoint> points, double[] drivenFraction, AimCsvTransform transform)
    {
        var path = transform.PathDistanceM!;
        var trackLength = (drivenFraction.Length - 1) * DrivenFractionStepM;
        var distances = MapDistances(points, drivenFraction, Math.Min(trackLength, path[^1]));
        var result = new TelemetryPoint[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var distance = distances[index];
            var upper = Array.BinarySearch(path, distance);
            upper = Math.Clamp(upper < 0 ? ~upper : upper, 1, path.Length - 1);
            var lower = upper - 1;
            var span = path[upper] - path[lower];
            var fraction = span > 0 ? Math.Clamp((distance - path[lower]) / span, 0, 1) : 0;
            result[index] = points[index] with
            {
                DistanceM = distance,
                Latitude = transform.PathLatitude![lower] + (transform.PathLatitude[upper] - transform.PathLatitude[lower]) * fraction,
                Longitude = transform.PathLongitude![lower] + (transform.PathLongitude[upper] - transform.PathLongitude[lower]) * fraction
            };
        }

        return result;
    }

    // Track distance of each point: invert the profile at the point's fraction of its own driven lap distance.
    private static double[] MapDistances(IReadOnlyList<TelemetryPoint> points, double[] drivenFraction, double trackLengthM)
    {
        var total = points[^1].DistanceM - points[0].DistanceM;
        var result = new double[points.Count];
        var cursor = 1;
        for (var index = 0; index < points.Count; index++)
        {
            var fraction = total > 0 ? Math.Clamp((points[index].DistanceM - points[0].DistanceM) / total, 0, 1) : 0;
            while (cursor < drivenFraction.Length - 1 && drivenFraction[cursor] < fraction)
            {
                cursor++;
            }

            while (cursor > 1 && drivenFraction[cursor - 1] > fraction)
            {
                cursor--;
            }

            var low = drivenFraction[cursor - 1];
            var high = drivenFraction[cursor];
            var step = high > low ? Math.Clamp((fraction - low) / (high - low), 0, 1) : 0;
            result[index] = Math.Min(((cursor - 1) + step) * DrivenFractionStepM, trackLengthM);
        }

        return result;
    }

    private static double[] CumulativePathLength(IReadOnlyList<TelemetryPoint> points)
    {
        var result = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            var scale = MetersPerDegree * Math.Cos(points[index].Latitude * Math.PI / 180);
            var dx = (points[index].Longitude - points[index - 1].Longitude) * scale;
            var dy = (points[index].Latitude - points[index - 1].Latitude) * MetersPerDegree;
            result[index] = result[index - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        return result;
    }

    // Heading every DrivenFractionStepM of the given distance coordinate, from the chord over ±6 m of positions.
    private static double[] HeadingByDistance(IReadOnlyList<TelemetryPoint> path, IReadOnlyList<double> distance, double length)
    {
        var scale = MetersPerDegree * Math.Cos(path[0].Latitude * Math.PI / 180);
        var count = (int)(length / DrivenFractionStepM) + 1;
        var result = new double[count];
        var behind = 0;
        var ahead = 0;
        for (var index = 0; index < count; index++)
        {
            var at = index * DrivenFractionStepM;
            while (behind < distance.Count - 1 && distance[behind + 1] < at - 6)
            {
                behind++;
            }

            while (ahead < distance.Count - 1 && distance[ahead] < at + 6)
            {
                ahead++;
            }

            result[index] = Math.Atan2((path[ahead].Longitude - path[behind].Longitude) * scale,
                (path[ahead].Latitude - path[behind].Latitude) * MetersPerDegree);
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
