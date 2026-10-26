using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TrackTelemetryViewer;

internal sealed record XrkExportSample(
    double TimeMs,
    double Latitude,
    double Longitude,
    double AltitudeM,
    double SpeedMps,
    double HeadingDeg,
    double InlineG,
    double LateralG);

internal sealed record XrkExportLap(double StartMs, double EndMs);

internal sealed record XrkExportData(
    string Driver,
    string Vehicle,
    string Venue,
    string SessionType,
    DateTime LocalStart,
    IReadOnlyList<XrkExportLap> Laps,
    IReadOnlyList<XrkExportSample> Samples);

/// <summary>
/// Writes a best-effort AiM XRK stream. The layout follows what libxrk (the reader PitWall uses) parses:
/// header frames for metadata, track, laps and channel definitions, 56-byte GPS NAV-SOL frames, and
/// single-sample channel messages. G-forces in the GPS channels are derived by readers from the ECEF
/// velocity, so the Garmin accelerometer values are also written as InlineAcc / LateralAcc channels.
/// </summary>
internal static class XrkWriter
{
    private const double StandardGravity = 9.80665;
    private const double MinimumFirstLapStartMs = 1000;
    private const int GpsRecordSize = 56;

    public static XrkExportData ReadGarminSession(
        SessionInfo session, string driver, string vehicle, string venue)
    {
        using var stream = File.OpenRead(session.FilePath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("drivenLaps", out var lapsElement) ||
            lapsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{session.FileName} has no driven laps to export.");
        }

        var laps = new List<XrkExportLap>();
        var samples = new List<XrkExportSample>();
        var lastTimeMs = double.NegativeInfinity;
        var lastHeading = 0d;
        foreach (var lap in lapsElement.EnumerateArray())
        {
            if (!lap.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() < 2)
            {
                continue;
            }

            var lapStartMs = Number(lap, "startTimeSessionMs");
            var lapDurationMs = Number(lap, "durationMs");
            if (!double.IsFinite(lapStartMs) || !double.IsFinite(lapDurationMs) || lapDurationMs <= 0)
            {
                continue;
            }

            var added = 0;
            foreach (var point in data.EnumerateArray())
            {
                var timeMs = Math.Round(lapStartMs + Number(point, "timeMs"));
                if (!double.IsFinite(timeMs) || timeMs <= lastTimeMs ||
                    !point.TryGetProperty("gnssPositionDeg", out var position))
                {
                    continue;
                }

                var latitude = Number(position, "latitude");
                var longitude = Number(position, "longitude");
                var speed = Number(point, "gnssSpeedMps");
                if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(speed))
                {
                    continue;
                }

                var heading = Number(point, "gnssHeadingDeg");
                lastHeading = double.IsFinite(heading) ? heading : lastHeading;
                samples.Add(new XrkExportSample(
                    timeMs,
                    latitude,
                    longitude,
                    Finite(Number(point, "gnssAltitudeM")),
                    Math.Max(0, speed),
                    lastHeading,
                    Finite(Number(point, "accelXMps2")) / StandardGravity,
                    Finite(Number(point, "accelYMps2")) / StandardGravity));
                lastTimeMs = timeMs;
                added++;
            }

            if (added >= 2)
            {
                laps.Add(new XrkExportLap(lapStartMs, lapStartMs + lapDurationMs));
            }
        }

        if (laps.Count == 0 || samples.Count < 2)
        {
            throw new InvalidDataException($"{session.FileName} has no usable lap telemetry to export.");
        }

        // The first lap must start after time zero, otherwise readers classify it as the out lap.
        var shiftMs = Math.Max(0, MinimumFirstLapStartMs - laps[0].StartMs);
        if (shiftMs > 0)
        {
            laps = laps.Select(lap => new XrkExportLap(lap.StartMs + shiftMs, lap.EndMs + shiftMs)).ToList();
            samples = samples.Select(sample => sample with { TimeMs = sample.TimeMs + shiftMs }).ToList();
        }

        return new XrkExportData(
            driver, vehicle, venue,
            string.IsNullOrWhiteSpace(session.RaceType) ? "TRACK" : session.RaceType,
            session.LocalStart, laps, samples);
    }

    public static void WriteFile(XrkExportData data, string path)
    {
        var bytes = Serialize(data);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static byte[] Serialize(XrkExportData data)
    {
        using var stream = new MemoryStream();
        WriteString(stream, "RCR", data.Driver);
        WriteString(stream, "VEH", data.Vehicle);
        WriteString(stream, "TMD", data.LocalStart.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
        WriteString(stream, "TMT", data.LocalStart.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        WriteString(stream, "VTY", data.SessionType);
        WriteString(stream, "NDV", "PitWall export");

        var first = data.Samples[0];
        WriteTrack(stream, data.Venue, first.Latitude, first.Longitude);
        WriteLaps(stream, data.Laps);
        WriteChannelDefinition(stream, 0, "InlnAcc", "InlineAcc");
        WriteChannelDefinition(stream, 1, "LatAcc", "LateralAcc");

        Span<byte> floatBytes = stackalloc byte[4];
        foreach (var sample in data.Samples)
        {
            var timecode = (int)Math.Round(sample.TimeMs);
            WriteFrame(stream, "GPS", 1, CreateGpsRecord(timecode, sample));
            BinaryPrimitives.WriteSingleLittleEndian(floatBytes, (float)sample.InlineG);
            WriteSampleMessage(stream, timecode, 0, floatBytes);
            BinaryPrimitives.WriteSingleLittleEndian(floatBytes, (float)sample.LateralG);
            WriteSampleMessage(stream, timecode, 1, floatBytes);
        }

        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string token, string value)
    {
        var text = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var payload = new byte[text.Length + 1];
        text.CopyTo(payload, 0);
        WriteFrame(stream, token, 0, payload);
    }

    private static void WriteTrack(Stream stream, string venue, double latitude, double longitude)
    {
        var payload = new byte[44];
        var name = Encoding.UTF8.GetBytes(venue ?? string.Empty);
        Array.Copy(name, payload, Math.Min(name.Length, 31));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(36), (int)Math.Round(latitude * 1e7));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(40), (int)Math.Round(longitude * 1e7));
        WriteFrame(stream, "TRK", 0, payload);
    }

    private static void WriteLaps(Stream stream, IReadOnlyList<XrkExportLap> laps)
    {
        // The out lap spans from log start to the first timed lap so every real lap is classified "full".
        WriteLap(stream, 0, laps[0].StartMs, laps[0].StartMs);
        for (var index = 0; index < laps.Count; index++)
        {
            WriteLap(stream, index + 1, laps[index].EndMs - laps[index].StartMs, laps[index].EndMs);
        }
    }

    private static void WriteLap(Stream stream, int number, double durationMs, double endMs)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)number);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)Math.Round(durationMs));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), (uint)Math.Round(endMs));
        WriteFrame(stream, "LAP", 1, payload);
    }

    private static void WriteChannelDefinition(Stream stream, ushort index, string shortName, string longName)
    {
        const byte accelerationUnit = 3;
        const byte float32Decoder = 6;
        var payload = new byte[112];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, index);
        payload[12] = accelerationUnit;
        payload[20] = float32Decoder;
        Encoding.ASCII.GetBytes(shortName.AsSpan(0, Math.Min(shortName.Length, 7)), payload.AsSpan(24, 8));
        Encoding.ASCII.GetBytes(longName.AsSpan(0, Math.Min(longName.Length, 23)), payload.AsSpan(32, 24));
        payload[72] = 4;
        WriteFrame(stream, "CHS", 1, payload);
    }

    private static void WriteSampleMessage(Stream stream, int timecode, ushort channel, ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[8];
        header[0] = (byte)'(';
        header[1] = (byte)'S';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], timecode);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], channel);
        stream.Write(header);
        stream.Write(value);
        stream.WriteByte((byte)')');
    }

    private static void WriteFrame(Stream stream, string token, byte version, ReadOnlySpan<byte> payload)
    {
        Span<byte> wireToken = stackalloc byte[4];
        for (var index = 0; index < token.Length; index++)
        {
            wireToken[index] = (byte)token[index];
        }

        if (token.Length == 3)
        {
            wireToken[3] = 0x20;
        }

        ushort checksum = 0;
        foreach (var value in payload)
        {
            checksum = unchecked((ushort)(checksum + value));
        }

        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'<';
        header[1] = (byte)'h';
        wireToken.CopyTo(header[2..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], payload.Length);
        header[10] = version;
        header[11] = (byte)'>';
        stream.Write(header);
        stream.Write(payload);

        Span<byte> footer = stackalloc byte[8];
        footer[0] = (byte)'<';
        wireToken.CopyTo(footer[1..]);
        BinaryPrimitives.WriteUInt16LittleEndian(footer[5..], checksum);
        footer[7] = (byte)'>';
        stream.Write(footer);
    }

    private static byte[] CreateGpsRecord(int timecode, XrkExportSample sample)
    {
        var record = new byte[GpsRecordSize];
        var (x, y, z) = ToEcef(sample.Latitude, sample.Longitude, sample.AltitudeM);
        var (dx, dy, dz) = ToEcefVelocity(sample.Latitude, sample.Longitude, sample.SpeedMps, sample.HeadingDeg);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0), timecode);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), timecode);
        record[14] = 3;
        record[15] = 0x0D;
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), ToCentimeters(x));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), ToCentimeters(y));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), ToCentimeters(z));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), 100);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(32), ToCentimeters(dx));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(36), ToCentimeters(dy));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(40), ToCentimeters(dz));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 150);
        record[51] = 12;
        return record;
    }

    private static int ToCentimeters(double meters) => (int)Math.Round(meters * 100);

    private static (double X, double Y, double Z) ToEcef(double latitudeDeg, double longitudeDeg, double altitudeM)
    {
        const double semiMajorM = 6378137.0;
        const double flattening = 1 / 298.257223563;
        const double eccentricitySquared = flattening * (2 - flattening);
        var latitude = latitudeDeg * Math.PI / 180;
        var longitude = longitudeDeg * Math.PI / 180;
        var sinLatitude = Math.Sin(latitude);
        var radius = semiMajorM / Math.Sqrt(1 - eccentricitySquared * sinLatitude * sinLatitude);
        return (
            (radius + altitudeM) * Math.Cos(latitude) * Math.Cos(longitude),
            (radius + altitudeM) * Math.Cos(latitude) * Math.Sin(longitude),
            (radius * (1 - eccentricitySquared) + altitudeM) * sinLatitude);
    }

    private static (double X, double Y, double Z) ToEcefVelocity(
        double latitudeDeg, double longitudeDeg, double speedMps, double headingDeg)
    {
        var latitude = latitudeDeg * Math.PI / 180;
        var longitude = longitudeDeg * Math.PI / 180;
        var heading = headingDeg * Math.PI / 180;
        var east = speedMps * Math.Sin(heading);
        var north = speedMps * Math.Cos(heading);
        return (
            -Math.Sin(longitude) * east - Math.Sin(latitude) * Math.Cos(longitude) * north,
            Math.Cos(longitude) * east - Math.Sin(latitude) * Math.Sin(longitude) * north,
            Math.Cos(latitude) * north);
    }

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.NaN;

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
