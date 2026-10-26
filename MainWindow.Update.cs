using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace TrackTelemetryViewer;

public partial class MainWindow
{
    private const string ReleasesPageUrl = "https://github.com/gremlin529/PitWall/releases/latest";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/gremlin529/PitWall/releases/latest";
    private string _updateUrl = ReleasesPageUrl;

    private void ShowAppVersion()
    {
        var version = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0));
        VersionText.Text = $"v{version}";
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PitWall");
            var json = await client.GetStringAsync(LatestReleaseApiUrl);
            using var document = JsonDocument.Parse(json);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseVersion(tag, out var latest))
            {
                return;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (latest == Normalize(current))
            {
                return;
            }

            if (document.RootElement.TryGetProperty("html_url", out var urlElement) &&
                urlElement.GetString() is { Length: > 0 } url)
            {
                _updateUrl = url;
            }

            UpdateButton.Content = $"Update available ({tag})";
            UpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           JsonException or InvalidOperationException)
        {
            // Offline or rate-limited: the update check is best effort.
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        // Accepts tags such as v1.2.3, 1.2, or 0_1_Alpha (numeric groups split by '.' or '_').
        var match = System.Text.RegularExpressions.Regex.Match(tag.Trim(), @"(\d+)(?:[._](\d+))?(?:[._](\d+))?");
        if (match.Success)
        {
            version = new Version(
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0,
                match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
            return true;
        }

        version = new Version();
        return false;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Could not open the release page",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
