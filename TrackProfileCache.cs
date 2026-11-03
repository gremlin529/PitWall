using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrackTelemetryViewer;

/// <summary>
/// Builds the fallback track outline (fastest GPS lap with smoothed elevation) for locations that have no meanline,
/// and caches it on disk keyed by the source files' path, size and modified time.
/// </summary>
internal static class TrackProfileCache
{
    private const int SmoothingRadius = 15;
    private const int Version = 2;

    private static string CacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitWall", "track-profiles");

    public static IReadOnlyList<TrackPoint> GetOrBuild(IReadOnlyList<SessionInfo> sessions)
    {
        var sources = sessions.Where(session => session.AiMData is not null)
            .Select(session => session.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            return [];
        }

        var cachePath = Path.Combine(CacheFolder, CreateKey(sources) + ".json");
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<List<TrackPoint>>(File.ReadAllText(cachePath));
                if (cached is { Count: > 10 })
                {
                    return cached;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        var profile = Build(sessions);
        if (profile.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(profile));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return profile;
    }

    private static string CreateKey(IEnumerable<string> paths)
    {
        var builder = new StringBuilder().Append(Version);
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            builder.Append('|').Append(path.ToUpperInvariant())
                .Append(':').Append(info.Exists ? info.Length : -1)
                .Append(':').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IReadOnlyList<TrackPoint> Build(IReadOnlyList<SessionInfo> sessions)
    {
        var lap = sessions.Where(session => session.AiMData is not null)
            .SelectMany(session => session.AiMData!.Laps)
            .Where(candidate => candidate.Points.Count > 10)
            .OrderBy(candidate => candidate.DurationMs)
            .FirstOrDefault();
        if (lap is null)
        {
            return [];
        }

        var height = lap.Channels
            .FirstOrDefault(channel => channel.Name.Equals("height", StringComparison.OrdinalIgnoreCase))
            ?.Samples;
        var altitude = height is not null && height.Count == lap.Points.Count
            ? Smooth(height.Select(sample => sample.Value).ToArray())
            : null;
        return lap.Points
            .Select((point, index) => new TrackPoint(
                point.DistanceM, point.Latitude, point.Longitude, 10,
                altitude is null ? double.NaN : altitude[index]))
            .ToArray();
    }

    private static double[] Smooth(double[] values)
    {
        // The lap is a closed loop: remove the linear GPS-height drift so the finish meets the start, then smooth circularly.
        var count = values.Length;
        var drift = values[^1] - values[0];
        var closed = new double[count];
        for (var index = 0; index < count; index++)
        {
            closed[index] = values[index] - drift * index / Math.Max(1, count - 1);
        }

        var radius = Math.Min(SmoothingRadius, count / 2 - 1);
        var result = new double[count];
        for (var index = 0; index < count; index++)
        {
            var sum = 0d;
            for (var offset = -radius; offset <= radius; offset++)
            {
                sum += closed[((index + offset) % count + count) % count];
            }

            result[index] = sum / (2 * radius + 1);
        }

        // Circular smoothing leaves a small residual gap; blend it out so the first and last points match exactly.
        var gap = result[^1] - result[0];
        for (var index = 0; index < count; index++)
        {
            result[index] -= gap * index / Math.Max(1, count - 1);
        }

        return result;
    }
}
