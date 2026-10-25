using TrackTelemetryViewer;

namespace PitWallApp;

// Channel keys, labels and ordering mirror the WPF MainWindow so both apps share ChannelVisibility/ChannelOrder.
internal static class ChannelCatalog
{
    public const string AimPrefix = "aim:";

    private static readonly IReadOnlyList<ChartChannelSetting> Core =
    [
        new("speed", "Speed", "km/h", true),
        new("combined-g", "Combined G-forces", "g", true),
        new("longitudinal-g", "Longitudinal G", "g", true),
        new("lateral-g", "Lateral G", "g", true),
        new("delta-time", "Delta Time", "seconds", true)
    ];

    public static IReadOnlyList<AiMChannelData> DiscoverAim(IEnumerable<SessionInfo> sessions) =>
        sessions.Where(session => session.AiMData is not null)
            .SelectMany(session => session.AiMData!.Laps)
            .SelectMany(lap => lap.Channels)
            .GroupBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    // Returns every channel in the configured order; XRK channels are hidden until enabled.
    public static IReadOnlyList<ChartChannelSetting> Build(
        IReadOnlyList<AiMChannelData> aimChannels, ViewerSettings settings)
    {
        var channels = Core.Select(channel => new ChartChannelSetting(
                channel.Key, channel.Name, channel.Units,
                settings.ChannelVisibility.TryGetValue(channel.Key, out var enabled) ? enabled : channel.IsEnabled))
            .ToList();
        channels.AddRange(aimChannels
            .OrderBy(channel => AimOrder(channel.Name))
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(channel =>
            {
                var key = AimPrefix + channel.Name;
                return new ChartChannelSetting(
                    key,
                    string.IsNullOrWhiteSpace(channel.Units)
                        ? AimLabel(channel.Name)
                        : $"{AimLabel(channel.Name)} ({channel.Units})",
                    channel.Units,
                    settings.ChannelVisibility.TryGetValue(key, out var enabled) && enabled);
            }));
        var order = settings.ChannelOrder
            .Select((key, index) => (key, index))
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        return channels
            .Select((channel, index) => (channel, index))
            .OrderBy(item => order.TryGetValue(item.channel.Key, out var rank) ? rank : int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.channel)
            .ToArray();
    }

    public static int AimOrder(string name)
    {
        if (name.Contains("rpm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("throttle", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.Contains("brake", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pressure", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return name.Contains("steer", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    public static string AimLabel(string name)
    {
        if (name.Contains("rpm", StringComparison.OrdinalIgnoreCase))
        {
            return "RPM";
        }

        if (name.Contains("throttle", StringComparison.OrdinalIgnoreCase))
        {
            return "Throttle Position";
        }

        if (name.Contains("brake", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("press", StringComparison.OrdinalIgnoreCase) ? "Brake Pressure" : "Brake Position";
        }

        return name.Contains("steer", StringComparison.OrdinalIgnoreCase) ? "Steering" : name;
    }

    public static string PanelTitle(ChartChannelSetting channel) => channel.Key switch
    {
        "speed" => "Speed",
        "combined-g" => "G-force (lateral solid, longitudinal dashed)",
        "longitudinal-g" => "Longitudinal G",
        "lateral-g" => "Lateral G",
        "delta-time" => "Time delta (A − B)",
        _ => channel.Key.StartsWith(AimPrefix, StringComparison.OrdinalIgnoreCase)
            ? AimLabel(channel.Key[AimPrefix.Length..])
            : channel.Name
    };
}
