using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrackTelemetryViewer;

/// <summary>Progress of the background elevation pass, for the status bar.</summary>
internal sealed record ElevationProgress(
    string TrackName, int Sets, int CheckedSessions, int TotalSessions, int TrackNumber, int TrackCount, bool Finished)
{
    public string Text => Finished
        ? "Elevation profiles up to date"
        : $"Refining elevation · {TrackName} · {Sets}/{TrackElevation.TargetSets} sets · " +
          $"{CheckedSessions}/{TotalSessions} sessions checked · track {TrackNumber} of {TrackCount}";
}

/// <summary>
/// Refines a track's elevation profile in the background by averaging the altitude recorded in many sessions:
/// Garmin meanlines (per-session averaged altitude), Garmin driven laps (GNSS altitude) and logger height channels
/// (VBOX "height", AiM CSV "GPS_Altitude"). Each lap is aligned to the running estimate (offset and linear drift
/// removed, since GPS altitude bias differs from session to session), binned every 2 m along a geo-referenced
/// reference line, accumulated and smoothed. The result is stored per track and used for the overview's elevation
/// relief. Once <see cref="TargetSets"/> sessions have contributed, the track is marked done and never processed again.
/// </summary>
internal static class TrackElevation
{
    public const int TargetSets = 30;

    private const int FormatVersion = 1;
    private const double BinM = 2;
    private const double MatchRadiusM = 25;
    private const double HintWindowM = 150;
    private const int SmoothingRadiusBins = 6; // ±12 m box, applied twice
    private const double MinimumRunCoverage = 0.6;
    private const int MaximumLapsPerSession = 15;
    private const double MeanlineRunWeight = 2;

    // Base altitudes can be NaN where the outline had none.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly object FileLock = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitWall", "track-elevation");

    /// <summary>
    /// Processes every track that isn't done yet, one session at a time, on a low-priority background thread.
    /// <paramref name="updated"/> is called (on that thread) after each session that improved a track's profile.
    /// </summary>
    public static Task RunInBackground(
        IReadOnlyList<TrackLocation> locations,
        Action<TrackLocation> updated,
        Action<ElevationProgress> progress,
        CancellationToken cancellation)
    {
        var snapshot = locations.OrderByDescending(item => item.Sessions.Count).ToArray();
        return Task.Factory.StartNew(() =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                for (var index = 0; index < snapshot.Length; index++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    var location = snapshot[index];
                    var trackNumber = index + 1;
                    try
                    {
                        Refine(location, updated, (sets, checkedSessions, totalSessions) => progress(new ElevationProgress(
                            location.DisplayName, sets, checkedSessions, totalSessions, trackNumber, snapshot.Length,
                            false)), cancellation);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                       JsonException or InvalidDataException or ArgumentException)
                    {
                        // Leave this track as it is and carry on with the others.
                    }
                }

                progress(new ElevationProgress(string.Empty, 0, 0, 0, snapshot.Length, snapshot.Length, true));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                thread.Priority = priority;
            }
        }, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Returns <paramref name="outline"/> with its altitudes replaced by the refined profile for the track, when
    /// one has been built and it matches this outline; otherwise returns the outline unchanged.
    /// </summary>
    public static IReadOnlyList<TrackPoint> Apply(TrackLocation location, IReadOnlyList<TrackPoint> outline)
    {
        if (outline.Count < 10 || Load(location) is not { AltitudeM.Length: > 0 } file)
        {
            return outline;
        }

        var matcher = new Matcher(file.Latitude, file.Longitude);
        var result = new TrackPoint[outline.Count];
        var matched = 0;
        var hint = -1;
        for (var index = 0; index < outline.Count; index++)
        {
            var point = outline[index];
            var bin = matcher.Match(point.Latitude, point.Longitude, hint);
            if (bin >= 0)
            {
                hint = bin;
                matched++;
                // Interpolate between neighbouring bins so a finer outline doesn't get 2 m stair steps.
                var (from, to, ratio) = matcher.Locate(point.Latitude, point.Longitude, bin, file.IsClosed);
                result[index] = point with
                {
                    AltitudeM = file.AltitudeM[from] + (file.AltitudeM[to] - file.AltitudeM[from]) * ratio
                };
            }
            else
            {
                result[index] = point;
            }
        }

        if (matched < outline.Count * 0.9)
        {
            return outline;
        }

        // Points that missed the reference line keep their own altitude, which may sit on another datum: interpolate instead.
        if (matched < outline.Count)
        {
            var known = Enumerable.Range(0, outline.Count)
                .Where(index => !ReferenceEquals(result[index], outline[index]))
                .ToArray();
            for (var index = 0; index < outline.Count; index++)
            {
                if (ReferenceEquals(result[index], outline[index]))
                {
                    var after = Array.FindIndex(known, candidate => candidate > index);
                    var next = known[after < 0 ? 0 : after];
                    var previous = known[after <= 0 ? ^1 : after - 1];
                    var span = (next - previous + outline.Count) % outline.Count;
                    var offset = (index - previous + outline.Count) % outline.Count;
                    var ratio = span == 0 ? 0 : (double)offset / span;
                    result[index] = outline[index] with
                    {
                        AltitudeM = result[previous].AltitudeM +
                                    (result[next].AltitudeM - result[previous].AltitudeM) * ratio
                    };
                }
            }
        }

        return result;
    }

    /// <summary>The track outline the overview starts from: the meanline, or the logger-built profile without one.</summary>
    public static IReadOnlyList<TrackPoint> LoadBaseOutline(TrackLocation location)
    {
        if (location.MeanlinePath is { } path)
        {
            try
            {
                var points = TelemetryLoader.ReadMeanlinePoints(path);
                if (points.Count >= 10)
                {
                    return points;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
            }
        }

        return TrackProfileCache.GetOrBuild(location.Sessions);
    }

    // progress(sets so far, sessions checked, sessions at the track)
    private static void Refine(
        TrackLocation location,
        Action<TrackLocation> updated,
        Action<int, int, int> progress,
        CancellationToken cancellation)
    {
        var file = Load(location);
        if (file is { Done: true })
        {
            return;
        }

        var pending = location.Sessions
            .Where(session => file is null || !file.ProcessedSessions.Contains(SessionId(session)))
            .OrderByDescending(session => session.LocalStart)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var outline = LoadBaseOutline(location);
        if (outline.Count < 10)
        {
            return;
        }

        if (file is null || !ReferenceMatches(file, outline))
        {
            file = CreateFile(location, outline);
            pending = location.Sessions.OrderByDescending(session => session.LocalStart).ToArray();
        }

        var matcher = new Matcher(file.Latitude, file.Longitude);
        var total = location.Sessions.Count;
        int Checked() => location.Sessions.Count(session => file.ProcessedSessions.Contains(SessionId(session)));
        var checkedCount = Checked();
        progress(file.SetCount, checkedCount, total);
        foreach (var session in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            var contributed = false;
            try
            {
                foreach (var (run, weight) in ReadRuns(session, location.LengthM))
                {
                    cancellation.ThrowIfCancellationRequested();
                    contributed |= AddRun(file, matcher, run, weight);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
                // Unreadable session: record it as processed so it isn't retried every launch.
            }

            file.ProcessedSessions.Add(SessionId(session));
            if (contributed)
            {
                file.SetCount++;
                file.AltitudeM = BuildProfile(file);
                file.Done = file.SetCount >= TargetSets;
            }

            file.UpdatedUtc = DateTime.UtcNow;
            Save(location, file);
            progress(file.SetCount, ++checkedCount, total);
            if (contributed)
            {
                updated(location);
            }

            if (file.Done)
            {
                return;
            }
        }
    }

    // ---- Accumulation ----

    private static bool AddRun(ElevationFile file, Matcher matcher, IReadOnlyList<AltitudeSample> run, double weight)
    {
        var count = file.Latitude.Length;
        var runSum = new double[count];
        var runCount = new int[count];
        var hint = -1;
        foreach (var sample in run)
        {
            var bin = matcher.Match(sample.Latitude, sample.Longitude, hint);
            if (bin < 0)
            {
                continue;
            }

            hint = bin;
            runSum[bin] += sample.AltitudeM;
            runCount[bin]++;
        }

        var covered = runCount.Count(value => value > 0);
        if (covered < count * MinimumRunCoverage)
        {
            return false; // partial lap, pit lane or another layout
        }

        // Current estimate: the accumulated mean, or the outline's own altitude before anything has been added.
        var bins = new List<int>(covered);
        var progress = new List<double>(covered);
        var residuals = new List<double>(covered);
        var hasAccumulated = file.Weight.Any(value => value > 0);
        for (var bin = 0; bin < count; bin++)
        {
            if (runCount[bin] == 0)
            {
                continue;
            }

            var estimate = Estimate(file, bin, hasAccumulated);
            if (double.IsFinite(estimate))
            {
                bins.Add(bin);
                progress.Add((double)bin / count);
                residuals.Add(runSum[bin] / runCount[bin] - estimate);
            }
        }

        double intercept = 0, slope = 0, sigma = 0;
        if (residuals.Count >= count * MinimumRunCoverage * 0.5)
        {
            // Remove this lap's offset and linear drift relative to the estimate, ignoring outliers.
            (intercept, slope) = FitLine(progress, residuals, null);
            sigma = RobustSigma(progress, residuals, intercept, slope);
            var threshold = Math.Max(3 * sigma, 1.5);
            var keep = residuals.Select((residual, index) =>
                Math.Abs(residual - (intercept + slope * progress[index])) <= threshold).ToArray();
            (intercept, slope) = FitLine(progress, residuals, keep);
            sigma = RobustSigma(progress, residuals, intercept, slope);
            if (sigma > 6)
            {
                return false; // altitude too noisy to help
            }
        }
        else if (hasAccumulated)
        {
            return false;
        }

        var spikeLimit = Math.Max(4 * sigma, 3);
        var added = 0;
        for (var bin = 0; bin < count; bin++)
        {
            if (runCount[bin] == 0)
            {
                continue;
            }

            var corrected = runSum[bin] / runCount[bin] - (intercept + slope * bin / count);
            if (hasAccumulated && file.Weight[bin] > 0 && file.SetCount >= 2 &&
                Math.Abs(corrected - file.Sum[bin] / file.Weight[bin]) > spikeLimit)
            {
                continue;
            }

            file.Sum[bin] += corrected * weight;
            file.Weight[bin] += weight;
            added++;
        }

        return added > 0;
    }

    private static double Estimate(ElevationFile file, int bin, bool hasAccumulated)
    {
        if (hasAccumulated)
        {
            return file.Weight[bin] > 0 ? file.Sum[bin] / file.Weight[bin] : double.NaN;
        }

        return file.BaseAltitudeM[bin];
    }

    private static (double Intercept, double Slope) FitLine(List<double> x, List<double> y, bool[]? keep)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var index = 0; index < x.Count; index++)
        {
            if (keep is not null && !keep[index])
            {
                continue;
            }

            n++;
            sx += x[index];
            sy += y[index];
            sxx += x[index] * x[index];
            sxy += x[index] * y[index];
        }

        if (n == 0)
        {
            return (0, 0);
        }

        var denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-9)
        {
            return (sy / n, 0);
        }

        var slope = (n * sxy - sx * sy) / denominator;
        return ((sy - slope * sx) / n, slope);
    }

    private static double RobustSigma(List<double> x, List<double> y, double intercept, double slope)
    {
        var deviations = y.Select((value, index) => Math.Abs(value - (intercept + slope * x[index])))
            .OrderBy(value => value)
            .ToArray();
        return deviations.Length == 0 ? 0 : 1.4826 * deviations[deviations.Length / 2];
    }

    private static double[] BuildProfile(ElevationFile file)
    {
        var count = file.Latitude.Length;
        var mean = new double[count];
        var known = new List<int>();
        for (var bin = 0; bin < count; bin++)
        {
            mean[bin] = file.Weight[bin] > 0 ? file.Sum[bin] / file.Weight[bin] : double.NaN;
            if (file.Weight[bin] > 0)
            {
                known.Add(bin);
            }
        }

        if (known.Count < 2)
        {
            return [];
        }

        // Fill gaps by linear interpolation (around the loop when the track is closed).
        for (var index = 0; index < known.Count; index++)
        {
            var from = known[index];
            var last = index + 1 == known.Count;
            if (last && !file.IsClosed)
            {
                for (var bin = from + 1; bin < count; bin++)
                {
                    mean[bin] = mean[from];
                }

                break;
            }

            var to = last ? known[0] + count : known[index + 1];
            for (var step = from + 1; step < to; step++)
            {
                var ratio = (double)(step - from) / (to - from);
                mean[step % count] = mean[from] + (mean[to % count] - mean[from]) * ratio;
            }
        }

        if (!file.IsClosed)
        {
            for (var bin = 0; bin < known[0]; bin++)
            {
                mean[bin] = mean[known[0]];
            }
        }

        var smoothed = BoxSmooth(BoxSmooth(mean, file.IsClosed), file.IsClosed);
        return smoothed.Select(value => Math.Round(value, 3)).ToArray();
    }

    private static double[] BoxSmooth(double[] values, bool closed)
    {
        var count = values.Length;
        var radius = Math.Min(SmoothingRadiusBins, Math.Max(0, count / 2 - 1));
        var result = new double[count];
        for (var index = 0; index < count; index++)
        {
            double sum = 0;
            var samples = 0;
            for (var offset = -radius; offset <= radius; offset++)
            {
                var position = index + offset;
                if (closed)
                {
                    position = (position % count + count) % count;
                }
                else if (position < 0 || position >= count)
                {
                    continue;
                }

                sum += values[position];
                samples++;
            }

            result[index] = sum / samples;
        }

        return result;
    }

    // ---- Sources ----

    private readonly record struct AltitudeSample(double Latitude, double Longitude, double AltitudeM);

    private static IEnumerable<(IReadOnlyList<AltitudeSample> Run, double Weight)> ReadRuns(
        SessionInfo session, double trackLengthM)
    {
        if (session.AiMData is { } aiMData)
        {
            foreach (var run in ReadLoggerRuns(aiMData))
            {
                yield return (run, 1);
            }

            yield break;
        }

        if (session.MeanlinePath is { } meanlinePath && ReadMeanlineRun(meanlinePath) is { } meanline)
        {
            yield return (meanline, MeanlineRunWeight);
        }

        foreach (var run in ReadDrivenLapRuns(session.FilePath, trackLengthM))
        {
            yield return (run, 1);
        }
    }

    private static IReadOnlyList<AltitudeSample>? ReadMeanlineRun(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var samples = new List<AltitudeSample>(data.GetArrayLength());
        var measured = false;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("centerPositionDeg", out var position))
            {
                continue;
            }

            var sample = new AltitudeSample(
                Number(position, "latitude"), Number(position, "longitude"), Number(item, "avgAltitudeM"));
            if (double.IsFinite(sample.Latitude) && double.IsFinite(sample.Longitude) &&
                double.IsFinite(sample.AltitudeM))
            {
                measured |= sample.AltitudeM != 0;
                samples.Add(sample);
            }
        }

        // Meanlines without measured altitude are all zeros; their terrain altitude is already the base outline's.
        return measured && samples.Count > 10 ? samples : null;
    }

    private static IEnumerable<IReadOnlyList<AltitudeSample>> ReadDrivenLapRuns(string path, double trackLengthM)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("drivenLaps", out var laps) || laps.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        // The fastest laps are the cleanest: full laps on the racing line, no pit lane.
        var candidates = laps.EnumerateArray()
            .Select(lap => (Lap: lap, DurationMs: Number(lap, "durationMs")))
            .Where(item => item.DurationMs > 0 && item.Lap.TryGetProperty("data", out var data) &&
                           data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 10)
            .OrderBy(item => item.DurationMs)
            .Take(MaximumLapsPerSession)
            .ToArray();
        foreach (var (lap, _) in candidates)
        {
            var data = lap.GetProperty("data");
            var samples = new List<AltitudeSample>(data.GetArrayLength());
            var lastDistance = 0d;
            foreach (var point in data.EnumerateArray())
            {
                if (!point.TryGetProperty("gnssPositionDeg", out var position))
                {
                    continue;
                }

                var sample = new AltitudeSample(
                    Number(position, "latitude"), Number(position, "longitude"), Number(point, "gnssAltitudeM"));
                if (double.IsFinite(sample.Latitude) && double.IsFinite(sample.Longitude) &&
                    double.IsFinite(sample.AltitudeM) && sample.AltitudeM != 0)
                {
                    samples.Add(sample);
                }

                var distance = Number(point, "distanceM");
                lastDistance = double.IsFinite(distance) ? Math.Max(lastDistance, distance) : lastDistance;
            }

            if (samples.Count > 10 &&
                (trackLengthM <= 0 || Math.Abs(lastDistance - trackLengthM) < trackLengthM * 0.15))
            {
                yield return samples;
            }
        }
    }

    private static IEnumerable<IReadOnlyList<AltitudeSample>> ReadLoggerRuns(AiMSessionData aiMData)
    {
        foreach (var lap in aiMData.Laps.OrderBy(lap => lap.DurationMs).Take(MaximumLapsPerSession))
        {
            var channel = lap.Channels.FirstOrDefault(candidate => IsAltitudeChannel(candidate.Name));
            if (channel is null || channel.Samples.Count < 2 || lap.Points.Count < 10)
            {
                continue;
            }

            var scale = channel.Units.Trim().Equals("ft", StringComparison.OrdinalIgnoreCase) ? 0.3048 : 1;
            var samples = new List<AltitudeSample>(lap.Points.Count);
            foreach (var point in lap.Points)
            {
                var altitude = InterpolateByTime(channel.Samples, point.TimeMs) * scale;
                if (double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) && double.IsFinite(altitude))
                {
                    samples.Add(new AltitudeSample(point.Latitude, point.Longitude, altitude));
                }
            }

            if (samples.Count > 10)
            {
                yield return samples;
            }
        }
    }

    private static bool IsAltitudeChannel(string name)
    {
        var normalized = name.Replace('_', ' ').Trim();
        return normalized.Equals("height", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("altitude", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("GPS Altitude", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("GPS Height", StringComparison.OrdinalIgnoreCase);
    }

    private static double InterpolateByTime(IReadOnlyList<AiMChannelSample> samples, double timeMs)
    {
        var low = 0;
        var high = samples.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].TimeMs < timeMs)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low == 0)
        {
            return samples[0].TimeMs - timeMs < 200 ? samples[0].Value : double.NaN;
        }

        if (low >= samples.Count)
        {
            return timeMs - samples[^1].TimeMs < 200 ? samples[^1].Value : double.NaN;
        }

        var before = samples[low - 1];
        var after = samples[low];
        var span = after.TimeMs - before.TimeMs;
        return span <= 0
            ? after.Value
            : before.Value + (after.Value - before.Value) * ((timeMs - before.TimeMs) / span);
    }

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : double.NaN;

    private static string SessionId(SessionInfo session) => Path.GetFullPath(session.FilePath).ToUpperInvariant();

    // ---- Reference line and storage ----

    private static ElevationFile CreateFile(TrackLocation location, IReadOnlyList<TrackPoint> outline)
    {
        var projection = new Projection(outline[0].Latitude, outline[0].Longitude);
        var xy = outline.Select(point => projection.ToXy(point.Latitude, point.Longitude)).ToArray();
        var cumulative = new double[outline.Count];
        for (var index = 1; index < outline.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] + Distance(xy[index - 1], xy[index]);
        }

        var length = cumulative[^1];
        var closed = Distance(xy[0], xy[^1]) < 50;
        var count = Math.Max(10, (int)Math.Round(length / BinM));
        var latitude = new double[count];
        var longitude = new double[count];
        var baseAltitude = new double[count];
        var segment = 0;
        for (var bin = 0; bin < count; bin++)
        {
            var target = length * bin / count;
            while (segment < outline.Count - 2 && cumulative[segment + 1] < target)
            {
                segment++;
            }

            var span = cumulative[segment + 1] - cumulative[segment];
            var ratio = span <= 0 ? 0 : Math.Clamp((target - cumulative[segment]) / span, 0, 1);
            var from = outline[segment];
            var to = outline[segment + 1];
            latitude[bin] = from.Latitude + (to.Latitude - from.Latitude) * ratio;
            longitude[bin] = from.Longitude + (to.Longitude - from.Longitude) * ratio;
            baseAltitude[bin] = double.IsFinite(from.AltitudeM) && double.IsFinite(to.AltitudeM)
                ? from.AltitudeM + (to.AltitudeM - from.AltitudeM) * ratio
                : double.NaN;
        }

        return new ElevationFile
        {
            Version = FormatVersion,
            TrackKey = TrackKey(location),
            TrackName = location.DisplayName,
            IsClosed = closed,
            BinM = length / count,
            Latitude = latitude,
            Longitude = longitude,
            BaseAltitudeM = baseAltitude,
            Sum = new double[count],
            Weight = new double[count],
            AltitudeM = []
        };
    }

    // The stored reference line still describes this outline (the track hasn't been remapped to another layout).
    private static bool ReferenceMatches(ElevationFile file, IReadOnlyList<TrackPoint> outline)
    {
        var matcher = new Matcher(file.Latitude, file.Longitude);
        var step = Math.Max(1, outline.Count / 200);
        var tested = 0;
        var matched = 0;
        for (var index = 0; index < outline.Count; index += step)
        {
            tested++;
            matched += matcher.Match(outline[index].Latitude, outline[index].Longitude, -1) >= 0 ? 1 : 0;
        }

        return matched >= tested * 0.9;
    }

    private static string TrackKey(TrackLocation location) => string.Create(CultureInfo.InvariantCulture,
        $"{location.Latitude:F1},{location.Longitude:F1}|{location.DisplayName.Trim().ToUpperInvariant()}");

    private static string FilePath(TrackLocation location)
    {
        var key = TrackKey(location);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var name = new string(location.DisplayName.Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (name.Length > 40)
        {
            name = name[..40];
        }

        return Path.Combine(Folder, $"{name}-{hash}.json");
    }

    private static ElevationFile? Load(TrackLocation location)
    {
        var path = FilePath(location);
        try
        {
            lock (FileLock)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var file = JsonSerializer.Deserialize<ElevationFile>(File.ReadAllText(path), JsonOptions);
                var count = file?.Latitude.Length ?? 0;
                return file is not null && file.Version == FormatVersion && count >= 10 &&
                       file.Longitude.Length == count && file.Sum.Length == count && file.Weight.Length == count &&
                       file.BaseAltitudeM.Length == count && (file.AltitudeM.Length == 0 || file.AltitudeM.Length == count)
                    ? file
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Save(TrackLocation location, ElevationFile file)
    {
        var path = FilePath(location);
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporary, path, true);
        }
    }

    private sealed class ElevationFile
    {
        public int Version { get; set; }
        public string TrackKey { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public int SetCount { get; set; }
        public bool Done { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public bool IsClosed { get; set; }
        public double BinM { get; set; }
        public HashSet<string> ProcessedSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double[] Latitude { get; set; } = [];
        public double[] Longitude { get; set; } = [];
        public double[] BaseAltitudeM { get; set; } = [];
        public double[] Sum { get; set; } = [];
        public double[] Weight { get; set; } = [];
        public double[] AltitudeM { get; set; } = [];
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private readonly struct Projection(double latitude0, double longitude0)
    {
        private readonly double _metresPerDegreeLongitude = 111_320 * Math.Cos(latitude0 * Math.PI / 180);

        public (double X, double Y) ToXy(double latitude, double longitude) =>
            ((longitude - longitude0) * _metresPerDegreeLongitude, (latitude - latitude0) * 110_540);
    }

    /// <summary>
    /// Finds the reference-line bin nearest a GPS position, preferring bins near the previous match so a lap
    /// follows the line through hairpins and alongside parallel straights.
    /// </summary>
    private sealed class Matcher
    {
        private const double CellM = 10;
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly Projection _projection;
        private readonly Dictionary<(int, int), List<int>> _grid = new();
        private readonly int _windowBins;

        public Matcher(double[] latitude, double[] longitude)
        {
            _projection = new Projection(latitude[0], longitude[0]);
            _x = new double[latitude.Length];
            _y = new double[latitude.Length];
            var length = 0d;
            for (var index = 0; index < latitude.Length; index++)
            {
                (_x[index], _y[index]) = _projection.ToXy(latitude[index], longitude[index]);
                if (index > 0)
                {
                    length += Distance((_x[index - 1], _y[index - 1]), (_x[index], _y[index]));
                }

                var cell = ((int)Math.Floor(_x[index] / CellM), (int)Math.Floor(_y[index] / CellM));
                if (!_grid.TryGetValue(cell, out var members))
                {
                    _grid[cell] = members = [];
                }

                members.Add(index);
            }

            var spacing = latitude.Length > 1 ? length / (latitude.Length - 1) : BinM;
            _windowBins = (int)Math.Ceiling(HintWindowM / Math.Max(0.1, spacing));
        }

        public int Match(double latitude, double longitude, int hint)
        {
            var (x, y) = _projection.ToXy(latitude, longitude);
            var cx = (int)Math.Floor(x / CellM);
            var cy = (int)Math.Floor(y / CellM);
            var reach = (int)Math.Ceiling(MatchRadiusM / CellM);
            var limit = MatchRadiusM * MatchRadiusM;
            int best = -1, bestNear = -1;
            double bestDistance = limit, bestNearDistance = limit;
            var count = _x.Length;
            for (var dx = -reach; dx <= reach; dx++)
            {
                for (var dy = -reach; dy <= reach; dy++)
                {
                    if (!_grid.TryGetValue((cx + dx, cy + dy), out var members))
                    {
                        continue;
                    }

                    foreach (var index in members)
                    {
                        var distance = (_x[index] - x) * (_x[index] - x) + (_y[index] - y) * (_y[index] - y);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = index;
                        }

                        if (hint >= 0 && distance < bestNearDistance)
                        {
                            var gap = Math.Abs(index - hint);
                            if (Math.Min(gap, count - gap) <= _windowBins)
                            {
                                bestNearDistance = distance;
                                bestNear = index;
                            }
                        }
                    }
                }
            }

            return bestNear >= 0 ? bestNear : best;
        }

        /// <summary>The segment of the reference line next to <paramref name="bin"/> the position falls on, and how far along it.</summary>
        public (int From, int To, double Ratio) Locate(double latitude, double longitude, int bin, bool closed)
        {
            var (x, y) = _projection.ToXy(latitude, longitude);
            var count = _x.Length;
            var next = closed ? (bin + 1) % count : Math.Min(bin + 1, count - 1);
            var previous = closed ? (bin - 1 + count) % count : Math.Max(bin - 1, 0);
            var forward = Project(bin, next, x, y);
            if (forward > 0 || previous == bin)
            {
                return (bin, next, Math.Clamp(forward, 0, 1));
            }

            return (previous, bin, Math.Clamp(Project(previous, bin, x, y), 0, 1));
        }

        private double Project(int from, int to, double x, double y)
        {
            var dx = _x[to] - _x[from];
            var dy = _y[to] - _y[from];
            var lengthSquared = dx * dx + dy * dy;
            return lengthSquared <= 1e-9 ? 0 : ((x - _x[from]) * dx + (y - _y[from]) * dy) / lengthSquared;
        }
    }
}
