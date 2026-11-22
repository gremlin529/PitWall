using System.Globalization;
using System.IO;
using System.Text;

namespace TrackTelemetryViewer;

internal static partial class TelemetryLoader
{
    public static IEnumerable<string> EnumerateVboFiles(string folder) =>
        EnumerateFilesRecursively(folder, "*.vbo");

    private static readonly Dictionary<string, string> VboChannelUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VBOX_rpm"] = "rpm",
        ["VBOX_ath"] = "%",
        ["VBOX_aps"] = "%",
        ["VBOX_pbrk"] = "bar",
        ["VBOX_asteer"] = "°",
        ["VBOX_accx"] = "G",
        ["VBOX_accy"] = "G",
        ["VBOX_accz"] = "G",
        ["VBOX_yaw"] = "°/s",
        ["height"] = "m"
    };

    // Longitudinal/lateral acceleration feed the built-in G-force graphs, so they are not listed again as channels.
    private static readonly HashSet<string> VboSkippedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "sats", "time", "lat", "long", "velocity", "heading", "Tsample", "solution_type",
        "avifileindex", "avitime", "VBOX_lapnumber", "VBOX_laptime", "VBOX_accx", "VBOX_accy"
    };

    // RaceBox writes .vbo files with its own column names; map them onto the Racelogic names so sessions from
    // either logger share channels and compare directly. Both loggers use the same signs (accx + when accelerating,
    // accy − in a right-hand turn); lateral is flipped on load to match Garmin (+ in a right-hand turn).
    private static readonly Dictionary<string, string> RaceBoxColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lng"] = "long",
        ["LongAcc"] = "VBOX_accx",
        ["LatAcc"] = "VBOX_accy",
        ["VertAcc"] = "VBOX_accz"
    };

    /// <summary>
    /// Reads a Racelogic VBOX .vbo log. Positions are in minutes (longitude positive west) and time is UTC hhmmss.ss;
    /// laps are split on the logger's lap-number channel and in/out laps far from the typical lap are dropped.
    /// </summary>
    public static SessionInfo ReadVboSessionInfo(string filePath)
    {
        var lines = File.ReadAllLines(filePath, Encoding.Latin1);
        var dataIndex = Array.FindIndex(lines, line => line.Trim().Equals("[data]", StringComparison.OrdinalIgnoreCase));
        var namesIndex = Array.FindIndex(lines, line => line.Trim().Equals("[column names]", StringComparison.OrdinalIgnoreCase));
        if (dataIndex < 0 || namesIndex < 0 || namesIndex + 1 >= lines.Length)
        {
            throw new InvalidDataException($"{Path.GetFileName(filePath)} is not a valid VBOX file.");
        }

        var names = lines[namesIndex + 1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var comments = ReadVboSection(lines, "comments");
        var isRaceBox = comments.Any(line => line.Contains("RaceBox", StringComparison.OrdinalIgnoreCase)) ||
                        names.Any(name => RaceBoxColumnNames.ContainsKey(name));
        if (isRaceBox)
        {
            names = names
                .Select(name => RaceBoxColumnNames.TryGetValue(name, out var mapped) &&
                                !names.Contains(mapped, StringComparer.OrdinalIgnoreCase)
                    ? mapped
                    : name)
                .ToArray();
        }

        int Find(string name) => Array.FindIndex(names, candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
        var timeColumn = Find("time");
        var latColumn = Find("lat");
        var lonColumn = Find("long");
        var speedColumn = Find("velocity");
        var lapColumn = Find("VBOX_lapnumber");
        var longitudinalColumn = Find("VBOX_accx");
        var lateralColumn = Find("VBOX_accy");
        if (timeColumn < 0 || latColumn < 0 || lonColumn < 0 || speedColumn < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(filePath)} needs time, position and velocity channels.");
        }

        var rows = new List<double[]>(lines.Length - dataIndex);
        for (var index = dataIndex + 1; index < lines.Length; index++)
        {
            var parts = lines[index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < names.Length)
            {
                continue;
            }

            var row = new double[names.Length];
            for (var column = 0; column < names.Length; column++)
            {
                row[column] = double.TryParse(parts[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                              double.IsFinite(value)
                    ? value
                    : 0;
            }

            if (Math.Abs(row[latColumn]) > 0 && Math.Abs(row[lonColumn]) > 0)
            {
                rows.Add(row);
            }
        }

        if (rows.Count < 20)
        {
            throw new InvalidDataException($"{Path.GetFileName(filePath)} has too few GPS samples.");
        }

        // VBOX/RaceBox lateral acceleration is negative in a right-hand turn; Garmin's is positive. Flip it so
        // lateral G traces from every logger point the same way.
        if (lateralColumn >= 0)
        {
            foreach (var row in rows)
            {
                row[lateralColumn] = -row[lateralColumn];
            }
        }

        // hhmmss.sss to seconds, continuing across midnight.
        var seconds = new double[rows.Count];
        var dayOffset = 0d;
        for (var index = 0; index < rows.Count; index++)
        {
            var raw = rows[index][timeColumn];
            var value = Math.Floor(raw / 10000) * 3600 + Math.Floor(raw % 10000 / 100) * 60 + raw % 100;
            if (index > 0 && value + dayOffset < seconds[index - 1] - 1000)
            {
                dayOffset += 86400;
            }

            seconds[index] = value + dayOffset;
        }

        double Latitude(int row) => rows[row][latColumn] / 60;
        double Longitude(int row) => -rows[row][lonColumn] / 60;

        var channelColumns = Enumerable.Range(0, names.Length)
            .Where(column => !VboSkippedColumns.Contains(names[column]))
            .Where(column => rows.Any(row => row[column] != rows[0][column]))
            .ToArray();

        // Lap numbers come from the logger's lap channel; files without one (RaceBox) are split on the
        // [laptiming] start/finish line instead.
        var lapNumbers = lapColumn >= 0
            ? rows.Select(row => (int)row[lapColumn]).ToArray()
            : SplitLapsOnStartLine(ReadVboSection(lines, "laptiming"), rows, latColumn, lonColumn, seconds);
        var ranges = new List<(int Start, int End, int Number)>();
        var rangeStart = 0;
        for (var index = 1; index <= rows.Count; index++)
        {
            if (index == rows.Count || lapNumbers[index] != lapNumbers[index - 1])
            {
                ranges.Add((rangeStart, index - 1, lapNumbers[rangeStart]));
                rangeStart = index;
            }
        }

        var durations = ranges.Select(range => seconds[range.End] - seconds[range.Start]).OrderBy(value => value).ToArray();
        var median = durations[durations.Length / 2];
        var laps = new List<AiMLapData>();
        foreach (var (start, end, number) in ranges)
        {
            var duration = seconds[end] - seconds[start];
            if (end - start < 10 || (ranges.Count > 2 && (duration < median * 0.6 || duration > median * 1.25)))
            {
                continue;
            }

            var points = new List<TelemetryPoint>(end - start + 1);
            var distance = 0d;
            for (var index = start; index <= end; index++)
            {
                if (index > start)
                {
                    distance += DistanceBetweenMeters(Latitude(index - 1), Longitude(index - 1), Latitude(index), Longitude(index));
                }

                points.Add(new TelemetryPoint(
                    distance,
                    (seconds[index] - seconds[start]) * 1000,
                    Math.Max(0, rows[index][speedColumn] / 3.6),
                    longitudinalColumn >= 0 ? rows[index][longitudinalColumn] : double.NaN,
                    lateralColumn >= 0 ? rows[index][lateralColumn] : double.NaN,
                    Latitude(index),
                    Longitude(index)));
            }

            var channels = channelColumns.Select(column => new AiMChannelData(
                    names[column],
                    VboChannelUnits.GetValueOrDefault(names[column], string.Empty),
                    Enumerable.Range(start, end - start + 1)
                        .Select(row => new AiMChannelSample((seconds[row] - seconds[start]) * 1000, rows[row][column]))
                        .ToArray()))
                .ToArray();
            laps.Add(new AiMLapData(number, seconds[start] * 1000, duration * 1000, points, channels));
        }

        if (laps.Count == 0)
        {
            throw new InvalidDataException($"No usable laps were found in {Path.GetFileName(filePath)}.");
        }

        var venue = isRaceBox ? "RaceBox track" : "VBOX track";
        var driver = string.Empty;
        var vehicle = string.Empty;
        var startMeters = DistanceBetweenMeters(Latitude(0), Longitude(0), 36.5845, -121.7535);
        var commentVenue = comments
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Venue", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim())
            .FirstOrDefault(value => value.Length > 0);
        if (startMeters < 20_000)
        {
            venue = "WeatherTech Raceway Laguna Seca";
        }
        else if (commentVenue is not null)
        {
            venue = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(commentVenue);
        }

        var localStart = File.GetLastWriteTime(filePath);
        if (lines.Length > 0 && lines[0].Contains("File created on", StringComparison.OrdinalIgnoreCase))
        {
            var text = lines[0].Replace("File created on", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("@", string.Empty).Trim();
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var created))
            {
                localStart = created;
            }
        }

        return new SessionInfo(
            filePath,
            Path.GetFileName(filePath),
            ComputeAiMSessionId(filePath),
            string.Empty,
            null,
            Enumerable.Range(0, rows.Count).Average(Latitude),
            Enumerable.Range(0, rows.Count).Average(Longitude),
            localStart,
            seconds[^1] - seconds[0],
            string.Empty,
            "UNKNOWN",
            "VBOX",
            null,
            venue,
            string.Empty,
            laps.Max(lap => lap.Points[^1].DistanceM),
            null,
            0,
            0,
            new AiMSessionData(driver, vehicle, venue, laps, CreateVboStream()));

        AiMSessionStream CreateVboStream()
        {
            var points = new TelemetryPoint[rows.Count];
            var distance = 0d;
            for (var index = 0; index < rows.Count; index++)
            {
                if (index > 0)
                {
                    distance += DistanceBetweenMeters(Latitude(index - 1), Longitude(index - 1), Latitude(index), Longitude(index));
                }

                points[index] = new TelemetryPoint(
                    distance,
                    seconds[index] * 1000,
                    Math.Max(0, rows[index][speedColumn] / 3.6),
                    longitudinalColumn >= 0 ? rows[index][longitudinalColumn] : double.NaN,
                    lateralColumn >= 0 ? rows[index][lateralColumn] : double.NaN,
                    Latitude(index),
                    Longitude(index));
            }

            var channels = channelColumns.Select(column => new AiMChannelData(
                    names[column],
                    VboChannelUnits.GetValueOrDefault(names[column], string.Empty),
                    Enumerable.Range(0, rows.Count)
                        .Select(row => new AiMChannelSample(seconds[row] * 1000, rows[row][column]))
                        .ToArray()))
                .ToArray();
            return new AiMSessionStream(points, channels);
        }
    }

    // Lines between "[name]" and the next section header, excluding blanks.
    private static string[] ReadVboSection(string[] lines, string name)
    {
        var start = Array.FindIndex(lines, line => line.Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return [];
        }

        return lines.Skip(start + 1)
            .TakeWhile(line => !line.TrimStart().StartsWith('['))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    /// <summary>
    /// Numbers each row by how many times the car has crossed the "Start" gate from [laptiming]
    /// ("Start lat1 long1 lat2 long2", in minutes). Rows before the first crossing are lap 0.
    /// Without a gate the whole file is lap 1.
    /// </summary>
    private static int[] SplitLapsOnStartLine(
        string[] lapTiming, IReadOnlyList<double[]> rows, int latColumn, int lonColumn, double[] seconds)
    {
        var lapNumbers = new int[rows.Count];
        var gate = lapTiming
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 5 && parts[0].Equals("Start", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts.Skip(1).Take(4)
                .Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN)
                .ToArray())
            .FirstOrDefault(values => values.All(double.IsFinite));
        if (gate is null)
        {
            Array.Fill(lapNumbers, 1);
            return lapNumbers;
        }

        // Work in a local frame where both axes are in the same units (longitude minutes shrink with latitude),
        // and widen the gate by half its length at each end so a slightly wide line still counts.
        var scale = Math.Cos(gate[0] / 60 * Math.PI / 180);
        (double X, double Y) Point(double latitude, double longitude) => (longitude * scale, latitude);
        var a = Point(gate[0], gate[1]);
        var b = Point(gate[2], gate[3]);
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        a = (a.X - dx * 0.5, a.Y - dy * 0.5);
        b = (b.X + dx * 0.5, b.Y + dy * 0.5);

        static double Cross((double X, double Y) o, (double X, double Y) p, (double X, double Y) q) =>
            (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);

        const double minimumLapSeconds = 10;
        var lap = 0;
        var lastCrossing = double.NegativeInfinity;
        for (var index = 1; index < rows.Count; index++)
        {
            var p = Point(rows[index - 1][latColumn], rows[index - 1][lonColumn]);
            var q = Point(rows[index][latColumn], rows[index][lonColumn]);
            var crosses = Math.Sign(Cross(a, b, p)) * Math.Sign(Cross(a, b, q)) < 0 &&
                          Math.Sign(Cross(p, q, a)) * Math.Sign(Cross(p, q, b)) < 0;
            if (crosses && seconds[index] - lastCrossing >= minimumLapSeconds)
            {
                lap++;
                lastCrossing = seconds[index];
            }

            lapNumbers[index] = lap;
        }

        return lapNumbers;
    }
}
