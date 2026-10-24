using System.Globalization;
using System.IO;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TrackTelemetryViewer;

internal static partial class TelemetryLoader
{
    private const double StandardGravity = 9.80665;
    private const int HeaderBytesToRead = 16 * 1024;

    [GeneratedRegex("\"startTimeUtcS\"\\s*:\\s*\"?(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex StartTimeRegex();

    [GeneratedRegex("\"utcToLocalOffsetS\"\\s*:\\s*(?<value>-?\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex("\"durationS\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex("\"durationMs\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LapDurationRegex();

    [GeneratedRegex("\"startTimeSessionMs\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LapStartTimeRegex();

    [GeneratedRegex("\"latitude\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LatitudeRegex();

    [GeneratedRegex("\"longitude\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LongitudeRegex();

    [GeneratedRegex("\"sessionGuid\"\\s*:\\s*\\{\\s*\"guid\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex SessionGuidRegex();

    [GeneratedRegex("\"vehicleAlias\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex VehicleAliasRegex();

    [GeneratedRegex("\"trackCondition\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TrackConditionRegex();

    [GeneratedRegex("\"raceType\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex RaceTypeRegex();

    [GeneratedRegex("\"garminGuid\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex GarminGuidRegex();

    [GeneratedRegex("\"trackName\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex TrackNameRegex();

    [GeneratedRegex("\"configurationName\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationNameRegex();

    [GeneratedRegex("\"lengthM\"\\s*:\\s*(?<value>-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LengthRegex();

    public static IEnumerable<string> EnumerateFilesRecursively(string folder, string pattern) =>
        Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDataFileIndex(string folder) =>
        EnumerateFilesRecursively(folder, "autosport-*.json")
            .GroupBy(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    public static SessionInfo ReadSessionInfo(
        string filePath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dataFileIndex)
    {
        using var stream = File.OpenRead(filePath);
        var header = new byte[Math.Min(HeaderBytesToRead, (int)Math.Min(stream.Length, HeaderBytesToRead))];
        var read = stream.Read(header, 0, header.Length);
        var text = Encoding.UTF8.GetString(header, 0, read);

        var startSeconds = ReadRequiredDouble(StartTimeRegex(), text, "startTimeUtcS", filePath);
        var offsetSeconds = ReadRequiredDouble(OffsetRegex(), text, "utcToLocalOffsetS", filePath);
        var durationSeconds = ReadRequiredDouble(DurationRegex(), text, "durationS", filePath);
        var latitude = ReadRequiredDouble(LatitudeRegex(), text, "GPS latitude", filePath);
        var longitude = ReadRequiredDouble(LongitudeRegex(), text, "GPS longitude", filePath);
        var sessionGuid = ReadOptionalString(SessionGuidRegex(), text);
        if (string.IsNullOrWhiteSpace(sessionGuid))
        {
            throw new InvalidDataException($"Could not read sessionGuid from {Path.GetFileName(filePath)}.");
        }

        var localStart = DateTimeOffset.FromUnixTimeSeconds((long)startSeconds)
            .ToOffset(TimeSpan.FromSeconds(offsetSeconds)).DateTime;
        var folder = Path.GetDirectoryName(filePath)!;
        var metadataPath = FindDataFile(
            folder, $"autosport-session-metadata-{sessionGuid}.json", dataFileIndex);
        var metadataText = metadataPath is not null
            ? ReadHeader(metadataPath)
            : string.Empty;
        var meanlinePath = FindDataFile(
            folder, $"autosport-meanline-{sessionGuid}.json", dataFileIndex);
        var meanlineText = meanlinePath is not null
            ? ReadHeader(meanlinePath)
            : string.Empty;
        var optimalLapPath = FindDataFile(
            folder, $"autosport-optimallap-{sessionGuid}.json", dataFileIndex);
        var optimalLapText = optimalLapPath is not null
            ? ReadHeader(optimalLapPath)
            : string.Empty;

        return new SessionInfo(
            filePath,
            Path.GetFileName(filePath),
            sessionGuid,
            ReadOptionalString(GarminGuidRegex(), metadataText),
            metadataPath,
            latitude,
            longitude,
            localStart,
            durationSeconds,
            ReadOptionalString(VehicleAliasRegex(), metadataText),
            ReadOptionalString(TrackConditionRegex(), metadataText),
            ReadOptionalString(RaceTypeRegex(), metadataText),
            meanlinePath,
            ReadOptionalString(TrackNameRegex(), meanlineText),
            ReadOptionalString(ConfigurationNameRegex(), meanlineText),
            ReadOptionalDouble(LengthRegex(), meanlineText),
            optimalLapPath,
            ReadOptionalDouble(LapDurationRegex(), optimalLapText),
            ReadOptionalDouble(LapStartTimeRegex(), optimalLapText));
    }

    public static void WriteTrackCondition(string metadataPath, string trackCondition)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
                       ?? throw new InvalidDataException(
                           $"Session metadata in {Path.GetFileName(metadataPath)} is not a JSON object.");
        metadata["trackCondition"] = trackCondition;
        File.WriteAllText(metadataPath, metadata.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    public static IReadOnlyList<TrackPoint> ReadMeanlinePoints(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Meanline path data was not found in {Path.GetFileName(filePath)}.");
        }

        var points = new List<TrackPoint>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("centerPositionDeg", out var position))
            {
                continue;
            }

            var latitude = GetNumber(position, "latitude");
            var longitude = GetNumber(position, "longitude");
            var distance = GetNumber(item, "distanceM");
            if (double.IsFinite(latitude) && double.IsFinite(longitude) && double.IsFinite(distance))
            {
                points.Add(new TrackPoint(distance, latitude, longitude, GetNumber(item, "trackWidthM", 0)));
            }
        }

        if (points.Count < 2)
        {
            throw new InvalidDataException($"Not enough GPS points were found in {Path.GetFileName(filePath)}.");
        }

        return points;
    }

    public static VehicleCatalog ReadVehicleCatalog(string folder)
    {
        var filePath = EnumerateFilesRecursively(folder, "autosport-vehicles.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (filePath is null)
        {
            return new VehicleCatalog([], []);
        }

        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The autosport-vehicles.json catalog must contain an array.");
        }

        var vehicles = document.RootElement.EnumerateArray()
            .Where(vehicle => GetString(vehicle, "type").Equals("CAR", StringComparison.OrdinalIgnoreCase))
            .Select(vehicle =>
            {
                var year = GetNumber(vehicle, "year", 0);
                var make = GetString(vehicle, "make");
                var model = GetString(vehicle, "model");
                var name = string.Join(" ", new[]
                {
                    year > 0 ? year.ToString("0", CultureInfo.InvariantCulture) : "",
                    make,
                    model
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
                var ids = vehicle.ValueKind == JsonValueKind.Object
                    ? vehicle.EnumerateObject()
                        .Where(property => IsVehicleIdProperty(property.Name))
                        .Select(property => property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : null)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .ToArray()
                    : [];
                return new VehicleCatalogEntry(name, GetString(vehicle, "garminGuid"), ids);
            })
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.Name))
            .ToArray();
        var names = vehicles.Select(vehicle => vehicle.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new VehicleCatalog(vehicles, names);
    }

    public static IReadOnlyDictionary<string, string> ReadDriverNames(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in EnumerateFilesRecursively(folder, "autosport-userprofile*.json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var profile in root.EnumerateArray())
                {
                    AddDriverName(result, profile);
                }
            }
            else
            {
                AddDriverName(result, root);
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("profiles", out var profiles) &&
                    profiles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var profile in profiles.EnumerateArray())
                    {
                        AddDriverName(result, profile);
                    }
                }
            }
        }

        return result;
    }

    public static int ReadLapCount(string filePath)
    {
        ReadOnlySpan<byte> marker = "\"drivenLaps\""u8;
        ReadOnlySpan<byte> lapNumber = "\"num\""u8;
        var buffer = new byte[64 * 1024 + marker.Length + lapNumber.Length];
        using var stream = File.OpenRead(filePath);
        var carry = 0;
        var foundLaps = false;
        var count = 0;

        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
            {
                break;
            }

            var available = carry + read;
            var content = buffer.AsSpan(0, available);
            var scanStart = Math.Max(0, carry - lapNumber.Length + 1);
            var nextCarry = Math.Min(available, Math.Max(marker.Length, lapNumber.Length) - 1);
            if (!foundLaps)
            {
                var markerIndex = content.IndexOf(marker);
                if (markerIndex >= 0)
                {
                    foundLaps = true;
                    scanStart = Math.Max(scanStart, markerIndex + marker.Length);
                }
            }

            if (foundLaps)
            {
                while (true)
                {
                    var lapIndex = content[scanStart..].IndexOf(lapNumber);
                    if (lapIndex < 0)
                    {
                        break;
                    }

                    lapIndex += scanStart;
                    if (lapIndex >= Math.Max(0, carry - lapNumber.Length + 1))
                    {
                        count++;
                    }

                    scanStart = lapIndex + lapNumber.Length;
                }
            }

            carry = nextCarry;
            content[^carry..].CopyTo(buffer);
        }

        if (count > 0)
        {
            return count;
        }

        var header = ReadHeader(filePath);
        return header.Contains("\"averageLap\"", StringComparison.Ordinal) &&
               header.Contains("\"data\"", StringComparison.Ordinal)
            ? 1
            : 0;
    }

    public static double? ReadFastestLapDurationMs(string filePath)
    {
        ReadOnlySpan<byte> marker = "\"drivenLaps\""u8;
        ReadOnlySpan<byte> durationMarker = "\"durationMs\""u8;
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[64 * 1024 + 64];
        var carry = 0;
        var foundDrivenLaps = false;
        double? fastestDurationMs = null;

        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
            {
                break;
            }

            var available = carry + read;
            var content = buffer.AsSpan(0, available);
            var scanStart = Math.Max(0, carry - durationMarker.Length + 1);
            if (!foundDrivenLaps)
            {
                var markerIndex = content.IndexOf(marker);
                if (markerIndex >= 0)
                {
                    foundDrivenLaps = true;
                    scanStart = Math.Max(scanStart, markerIndex + marker.Length);
                }
            }

            if (foundDrivenLaps)
            {
                while (true)
                {
                    var durationIndex = content[scanStart..].IndexOf(durationMarker);
                    if (durationIndex < 0)
                    {
                        break;
                    }

                    durationIndex += scanStart;
                    var valueStart = durationIndex + durationMarker.Length;
                    while (valueStart < available && (content[valueStart] == (byte)':' ||
                           content[valueStart] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    {
                        valueStart++;
                    }

                    var valueEnd = valueStart;
                    while (valueEnd < available && content[valueEnd] is not (byte)',' and not (byte)'}' and
                           not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
                    {
                        valueEnd++;
                    }

                    if (valueEnd == available)
                    {
                        break;
                    }

                    if (valueEnd > valueStart &&
                        Utf8Parser.TryParse(content[valueStart..valueEnd], out double duration, out _) &&
                        duration > 0 &&
                        (fastestDurationMs is null || duration < fastestDurationMs))
                    {
                        fastestDurationMs = duration;
                    }

                    scanStart = valueEnd;
                }
            }

            carry = Math.Min(available, 63);
            content[^carry..].CopyTo(buffer);
        }

        return fastestDurationMs;
    }

    public static IReadOnlyList<LapInfo> ReadLaps(SessionInfo session)
    {
        using var stream = File.OpenRead(session.FilePath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var result = new List<LapInfo>();
        if (root.TryGetProperty("drivenLaps", out var laps) && laps.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var candidate in laps.EnumerateArray())
            {
                if (!candidate.TryGetProperty("data", out var candidateData) ||
                    candidateData.ValueKind != JsonValueKind.Array ||
                    candidateData.GetArrayLength() == 0)
                {
                    index++;
                    continue;
                }

                var number = candidate.TryGetProperty("num", out var num)
                    ? num.ToString()
                    : (index + 1).ToString(CultureInfo.InvariantCulture);
                result.Add(new LapInfo(
                    session,
                    index,
                    false,
                    $"Lap {number}",
                    GetNumber(candidate, "durationMs"),
                    GetNumber(candidate, "startTimeSessionMs", 0)));
                index++;
            }
        }

        if (result.Count == 0 &&
            root.TryGetProperty("averageLap", out var averageLap) &&
            averageLap.TryGetProperty("data", out var averageData) &&
            averageData.ValueKind == JsonValueKind.Array)
        {
            result.Add(new LapInfo(
                session,
                -1,
                true,
                "Average lap",
                GetNumber(averageLap, "durationMs"),
                GetNumber(averageLap, "startTimeSessionMs", 0)));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException($"No lap telemetry was found in {session.FileName}.");
        }

        return result;
    }

    public static LapInfo? CreateOptimalLap(SessionInfo session)
    {
        return session.OptimalLapPath is not null && session.OptimalLapDurationMs > 0
            ? new LapInfo(
                session,
                -2,
                false,
                "Optimal lap",
                session.OptimalLapDurationMs,
                session.OptimalLapStartTimeSessionMs,
                true,
                session.OptimalLapPath)
            : null;
    }

    public static SessionTrace ReadTrace(LapInfo selectedLap)
    {
        var filePath = selectedLap.DataFilePath ?? selectedLap.Session.FilePath;
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        JsonElement lap;

        if (selectedLap.IsOptimal)
        {
            if (!root.TryGetProperty("optimalLap", out lap))
            {
                throw new InvalidDataException($"No optimal lap was found in {Path.GetFileName(filePath)}.");
            }
        }
        else if (selectedLap.IsAverage)
        {
            if (!root.TryGetProperty("averageLap", out lap))
            {
                throw new InvalidDataException($"No average lap was found in {selectedLap.Session.FileName}.");
            }
        }
        else
        {
            if (!root.TryGetProperty("drivenLaps", out var laps) ||
                laps.ValueKind != JsonValueKind.Array ||
                selectedLap.Index < 0 ||
                selectedLap.Index >= laps.GetArrayLength())
            {
                throw new InvalidDataException($"Lap {selectedLap.Index + 1} was not found in {selectedLap.Session.FileName}.");
            }

            lap = laps[selectedLap.Index];
        }

        if (!lap.TryGetProperty("data", out var pointsElement) ||
            pointsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Lap telemetry was not found in {selectedLap.Session.FileName}.");
        }

        var points = new List<TelemetryPoint>(pointsElement.GetArrayLength());
        foreach (var item in pointsElement.EnumerateArray())
        {
            var distance = GetNumber(item, "distanceM");
            var time = GetNumber(item, "timeMs");
            var position = item.TryGetProperty("gnssPositionDeg", out var gps) ? gps : default;
            var latitude = GetNumber(position, "latitude", double.NaN);
            var longitude = GetNumber(position, "longitude", double.NaN);

            if (!double.IsFinite(distance) || !double.IsFinite(time))
            {
                continue;
            }

            points.Add(new TelemetryPoint(
                distance,
                time,
                GetNumber(item, "gnssSpeedMps", double.NaN),
                GetNumber(item, "accelXMps2", double.NaN) / StandardGravity,
                GetNumber(item, "accelYMps2", double.NaN) / StandardGravity,
                latitude,
                longitude));
        }

        points.Sort(static (left, right) => left.DistanceM.CompareTo(right.DistanceM));
        if (points.Count < 2)
        {
            throw new InvalidDataException($"Not enough valid distance samples were found in {selectedLap.Session.FileName}.");
        }

        return new SessionTrace(selectedLap, points);
    }

    public static double InterpolateTimeMs(IReadOnlyList<TelemetryPoint> points, double distanceM)
    {
        var low = 0;
        var high = points.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].DistanceM < distanceM)
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
            return points[0].TimeMs;
        }

        if (low >= points.Count)
        {
            return points[^1].TimeMs;
        }

        var before = points[low - 1];
        var after = points[low];
        var span = after.DistanceM - before.DistanceM;
        if (span <= 0)
        {
            return after.TimeMs;
        }

        var progress = (distanceM - before.DistanceM) / span;
        return before.TimeMs + (after.TimeMs - before.TimeMs) * progress;
    }

    private static double ReadRequiredDouble(Regex regex, string text, string propertyName, string filePath)
    {
        var match = regex.Match(text);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"Could not read {propertyName} from {Path.GetFileName(filePath)}.");
        }

        return value;
    }

    private static string ReadHeader(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var header = new byte[Math.Min(HeaderBytesToRead, (int)Math.Min(stream.Length, HeaderBytesToRead))];
        var read = stream.Read(header, 0, header.Length);
        return Encoding.UTF8.GetString(header, 0, read);
    }

    private static string? FindDataFile(
        string preferredFolder,
        string fileName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dataFileIndex)
    {
        if (!dataFileIndex.TryGetValue(fileName, out var candidates))
        {
            return null;
        }

        return candidates.FirstOrDefault(path =>
                   Path.GetDirectoryName(path)?.Equals(preferredFolder, StringComparison.OrdinalIgnoreCase) == true)
               ?? candidates[0];
    }

    private static string ReadOptionalString(Regex regex, string text) =>
        regex.Match(text) is { Success: true } match ? match.Groups["value"].Value.Trim() : string.Empty;

    private static double ReadOptionalDouble(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static double GetNumber(JsonElement element, string propertyName, double fallback = double.NaN)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var number)
            ? number
            : fallback;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void AddDriverName(IDictionary<string, string> result, JsonElement profile)
    {
        var garminGuid = GetString(profile, "garminGuid");
        var nickname = GetString(profile, "nickname");
        if (!string.IsNullOrWhiteSpace(garminGuid) && !string.IsNullOrWhiteSpace(nickname))
        {
            result.TryAdd(garminGuid, nickname);
        }
    }

    private static bool IsVehicleIdProperty(string propertyName)
    {
        return propertyName.Equals("vehicleAlias", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("vehicleGuid", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("vehicleId", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("guid", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("id", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("uuid", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("aliasGuid", StringComparison.OrdinalIgnoreCase);
    }
}
