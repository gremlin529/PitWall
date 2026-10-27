using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrackTelemetryViewer;

internal static partial class TelemetryLoader
{
    private const double MetersPerDegreeLatitude = 111_320;

    public static IEnumerable<string> EnumerateAimCsvFiles(string folder) =>
        EnumerateFilesRecursively(folder, "*.csv").Where(IsAimCsv);

    public static bool IsAimCsv(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.Latin1);
            var first = reader.ReadLine();
            return first is not null &&
                   first.Replace("\"", string.Empty).StartsWith("Format,AIM CSV", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a Race Studio "AIM CSV File" export. The export carries no latitude/longitude, so the
    /// map is dead-reckoned from GPS speed and heading and closed at the start/finish line.
    /// </summary>
    public static SessionInfo ReadAimCsvSessionInfo(string filePath, Func<string, double>? shiftSecondsForVenue = null)
    {
        var lines = File.ReadAllLines(filePath, Encoding.Latin1);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerIndex = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var fields = SplitCsvLine(lines[index]);
            if (fields.Count == 0)
            {
                continue;
            }

            if (fields[0].Equals("Distance", StringComparison.OrdinalIgnoreCase) ||
                (fields[0].Equals("Time", StringComparison.OrdinalIgnoreCase) && fields.Count > 5))
            {
                headerIndex = index;
                break;
            }

            if (fields.Count >= 2 && fields[0].Length > 0)
            {
                metadata[fields[0]] = fields[1];
            }
        }

        if (headerIndex < 0)
        {
            throw new InvalidDataException($"No channel header row was found in {Path.GetFileName(filePath)}.");
        }

        var names = SplitCsvLine(lines[headerIndex]);
        var firstData = headerIndex + 1;
        while (firstData < lines.Length && !StartsWithNumber(lines[firstData]))
        {
            firstData++;
        }

        var unitsIndex = Enumerable.Range(headerIndex + 1, Math.Max(0, firstData - headerIndex - 1))
            .FirstOrDefault(index => SplitCsvLine(lines[index]) is { Count: > 0 } row &&
                                     !row[0].Equals(names[0], StringComparison.OrdinalIgnoreCase) &&
                                     row[0].Length > 0, -1);
        var units = unitsIndex >= 0 ? SplitCsvLine(lines[unitsIndex]) : [];

        var columnCount = names.Count;
        var rows = new List<double[]>(lines.Length - firstData);
        for (var index = firstData; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            var row = new double[columnCount];
            for (var column = 0; column < columnCount && column < parts.Length; column++)
            {
                row[column] = double.TryParse(parts[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                              double.IsFinite(value)
                    ? value
                    : 0;
            }

            rows.Add(row);
        }

        if (rows.Count < 20)
        {
            throw new InvalidDataException($"{Path.GetFileName(filePath)} has too few samples.");
        }

        int Find(params string[] candidates) => candidates
            .Select(candidate => names.FindIndex(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(found => found >= 0, -1);

        var speedColumn = Find("GPS_Speed", "GPS Speed", "Speed", "PSH_VEH_SPEED");
        var headingColumn = Find("GPS_Heading", "GPS Heading", "Heading");
        var longitudinalColumn = Find("Longitudinal_a", "GPS_LonAcc", "InlineAcc", "Longitudinal Acceleration");
        var lateralColumn = Find("Lateral_acc", "GPS_LatAcc", "LateralAcc", "Lateral Acceleration");
        var timeColumn = Find("Time");
        if (speedColumn < 0 || headingColumn < 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(filePath)} needs GPS speed and heading channels to draw the lap.");
        }

        var sampleRate = double.TryParse(
            metadata.GetValueOrDefault("Sample Rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) &&
                         rate > 0
            ? rate
            : 10;
        // Logger sample timing drifts slightly from the nominal rate, so scale to the recorded duration to stay in step with the beacons.
        var nominalStep = 1 / sampleRate;
        var step = nominalStep;
        if (double.TryParse(metadata.GetValueOrDefault("Duration"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var recordedDuration) &&
            recordedDuration > 0 && rows.Count > 1)
        {
            var measuredStep = recordedDuration / (rows.Count - 1);
            if (Math.Abs(measuredStep - nominalStep) < nominalStep * 0.05)
            {
                step = measuredStep;
            }
        }

        double TimeSeconds(int row) => timeColumn >= 0 ? rows[row][timeColumn] : row * step;

        var speedFactor = SpeedToMetersPerSecond(units.ElementAtOrDefault(speedColumn));
        var venue = metadata.GetValueOrDefault("Venue")?.Trim();
        if (string.IsNullOrWhiteSpace(venue))
        {
            venue = "AiM track";
        }

        var driver = metadata.GetValueOrDefault("User")?.Trim() ?? string.Empty;
        var vehicle = metadata.GetValueOrDefault("Vehicle")?.Trim() ?? string.Empty;
        var (originLatitude, originLongitude) = CreateSyntheticOrigin(venue);

        var totalSeconds = TimeSeconds(rows.Count - 1);
        var segments = ReadAimCsvSegments(metadata, totalSeconds, shiftSecondsForVenue?.Invoke(venue) ?? 0);
        var channelColumns = Enumerable.Range(0, columnCount)
            .Where(column => column != timeColumn && !names[column].Equals("Distance", StringComparison.OrdinalIgnoreCase))
            .Where(column => rows.Any(row => row[column] != rows[0][column]))
            .ToArray();

        var laps = new List<AiMLapData>();
        foreach (var (segment, segmentIndex) in segments.Select((segment, index) => (segment, index)))
        {
            var firstRow = FindRow(rows.Count, segment.StartS, TimeSeconds, true);
            var lastRow = FindRow(rows.Count, segment.EndS, TimeSeconds, false);
            if (lastRow - firstRow < 10)
            {
                continue;
            }

            var points = BuildAimCsvPoints(
                rows, firstRow, lastRow, TimeSeconds, speedColumn, headingColumn, speedFactor,
                longitudinalColumn, lateralColumn, originLatitude, originLongitude);
            var startTimeS = TimeSeconds(firstRow);
            var channels = channelColumns.Select(column => new AiMChannelData(
                    names[column],
                    units.ElementAtOrDefault(column) ?? string.Empty,
                    Enumerable.Range(firstRow, lastRow - firstRow + 1)
                        .Select(row => new AiMChannelSample((TimeSeconds(row) - startTimeS) * 1000, rows[row][column]))
                        .ToArray()))
                .ToArray();
            laps.Add(new AiMLapData(
                segment.Number ?? segmentIndex + 1,
                segment.StartS * 1000,
                (segment.EndS - segment.StartS) * 1000,
                points,
                channels));
        }

        if (laps.Count == 0)
        {
            throw new InvalidDataException($"No usable laps were found in {Path.GetFileName(filePath)}.");
        }

        var trackLengthM = laps.Max(lap => lap.Points[^1].DistanceM);
        var sessionType = metadata.GetValueOrDefault("Segment") is { Length: > 0 } segmentName &&
                          !segmentName.Equals("Session", StringComparison.OrdinalIgnoreCase)
            ? segmentName
            : "AiM";
        var durationSeconds = double.TryParse(
            metadata.GetValueOrDefault("Duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : totalSeconds;

        return new SessionInfo(
            filePath,
            Path.GetFileName(filePath),
            ComputeAiMSessionId(filePath),
            string.Empty,
            null,
            originLatitude,
            originLongitude,
            ReadAimCsvStartTime(metadata, filePath),
            durationSeconds,
            string.Empty,
            "UNKNOWN",
            sessionType,
            null,
            venue,
            string.Empty,
            trackLengthM,
            null,
            0,
            0,
            new AiMSessionData(driver, vehicle, venue, laps));
    }

    private sealed record AimCsvSegment(double StartS, double EndS, int? Number);

    // Beacon-to-beacon segments, dropping pit-lane crossings and in/out laps that are far off the typical lap.
    private static IReadOnlyList<AimCsvSegment> ReadAimCsvSegments(
        IReadOnlyDictionary<string, string> metadata,
        double totalSeconds,
        double shiftSeconds = 0)
    {
        var beacons = metadata.TryGetValue("Beacon Markers", out var beaconText)
            ? beaconText.Split(',')
                .Select(text => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN)
                .Where(double.IsFinite)
                .OrderBy(value => value)
                .ToArray()
            : [];
        if (beacons.Length < 2)
        {
            return [new AimCsvSegment(0, totalSeconds, 1)];
        }

        var segments = Enumerable.Range(0, beacons.Length - 1)
            .Select(index => new AimCsvSegment(beacons[index], beacons[index + 1], index + 1))
            .Where(segment => segment.EndS - segment.StartS > 5)
            .ToArray();
        if (segments.Length == 0)
        {
            return [];
        }

        var durations = segments.Select(segment => segment.EndS - segment.StartS).OrderBy(value => value).ToArray();
        var median = durations[durations.Length / 2];
        return segments
            .Where(segment => segment.EndS - segment.StartS is var length &&
                              length >= median * 0.6 && length <= median * 1.25)
            .Select(segment => segment with { StartS = segment.StartS + shiftSeconds, EndS = segment.EndS + shiftSeconds })
            .Where(segment => segment.StartS >= 0 && segment.EndS <= totalSeconds)
            .ToArray();
    }

    private static IReadOnlyList<TelemetryPoint> BuildAimCsvPoints(
        List<double[]> rows,
        int firstRow,
        int lastRow,
        Func<int, double> timeSeconds,
        int speedColumn,
        int headingColumn,
        double speedFactor,
        int longitudinalColumn,
        int lateralColumn,
        double originLatitude,
        double originLongitude)
    {
        var count = lastRow - firstRow + 1;
        var east = new double[count];
        var north = new double[count];
        var distance = new double[count];
        for (var index = 1; index < count; index++)
        {
            var previous = rows[firstRow + index - 1];
            var current = rows[firstRow + index];
            var seconds = timeSeconds(firstRow + index) - timeSeconds(firstRow + index - 1);
            var speed = Math.Max(0, (previous[speedColumn] + current[speedColumn]) / 2 * speedFactor);
            var heading = DegreesToRadians(current[headingColumn]);
            var step = speed * seconds;
            east[index] = east[index - 1] + step * Math.Sin(heading);
            north[index] = north[index - 1] + step * Math.Cos(heading);
            distance[index] = distance[index - 1] + step;
        }

        // The lap starts and ends at the same beacon, so spread the dead-reckoning drift along the lap.
        var totalDistance = distance[^1];
        var closureEast = east[^1];
        var closureNorth = north[^1];
        var longitudeScale = MetersPerDegreeLatitude * Math.Cos(DegreesToRadians(originLatitude));
        var startS = timeSeconds(firstRow);
        var points = new List<TelemetryPoint>(count);
        for (var index = 0; index < count; index++)
        {
            var fraction = totalDistance > 0 ? distance[index] / totalDistance : 0;
            var row = rows[firstRow + index];
            points.Add(new TelemetryPoint(
                distance[index],
                (timeSeconds(firstRow + index) - startS) * 1000,
                Math.Max(0, row[speedColumn] * speedFactor),
                longitudinalColumn >= 0 ? row[longitudinalColumn] : double.NaN,
                lateralColumn >= 0 ? row[lateralColumn] : double.NaN,
                originLatitude + (north[index] - closureNorth * fraction) / MetersPerDegreeLatitude,
                originLongitude + (east[index] - closureEast * fraction) / longitudeScale));
        }

        return points;
    }

    private static int FindRow(int count, double seconds, Func<int, double> timeSeconds, bool atOrAfter)
    {
        var low = 0;
        var high = count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (timeSeconds(middle) < seconds)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return atOrAfter || timeSeconds(low) <= seconds ? low : Math.Max(0, low - 1);
    }

    private static double SpeedToMetersPerSecond(string? units) => units?.Trim().ToLowerInvariant() switch
    {
        "mph" => 0.44704,
        "km/h" or "kph" or "kmh" => 1 / 3.6,
        "kn" or "kt" or "knots" => 0.514444,
        _ => 1
    };

    // The CSV has no coordinates, so each venue gets a stable fake origin so its sessions group together.
    private static (double Latitude, double Longitude) CreateSyntheticOrigin(string venue)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(venue.Trim().ToUpperInvariant()));
        var latitude = -20 + BitConverter.ToUInt16(hash, 0) / 65535d * 10;
        var longitude = -20 + BitConverter.ToUInt16(hash, 2) / 65535d * 10;
        return (latitude, longitude);
    }

    private static DateTime ReadAimCsvStartTime(IReadOnlyDictionary<string, string> metadata, string filePath)
    {
        var date = metadata.GetValueOrDefault("Date");
        var time = metadata.GetValueOrDefault("Time");
        string[] formats =
        [
            "MM/dd/yy HH:mm:ss", "M/d/yy H:mm:ss", "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss",
            "yyyy-MM-dd HH:mm:ss", "MM/dd/yy HH:mm", "M/d/yy H:mm"
        ];
        if (!string.IsNullOrWhiteSpace(date) &&
            DateTime.TryParseExact(
                $"{date.Trim()} {time?.Trim()}".Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return File.GetLastWriteTime(filePath);
    }

    private static bool StartsWithNumber(string line) =>
        line.Length > 0 && (char.IsDigit(line[0]) || line[0] == '-' || line[0] == '.');

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        fields.Add(builder.ToString());
        while (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }
}
