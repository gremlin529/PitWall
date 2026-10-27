using System.Windows;

namespace TrackTelemetryViewer;

internal sealed record SessionDetailsResult(
    string VehicleName,
    string DriverName,
    string LayoutValue,
    string TrackCondition,
    string RaceType);

public partial class SessionDetailsDialog : Window
{
    private readonly Func<SessionDetailsResult, bool> _save;

    internal SessionDetailsDialog(
        string header,
        IReadOnlyList<string> vehicleChoices,
        IReadOnlyList<SessionLayoutChoice> layoutChoices,
        string selectedLayout,
        SessionEdits edits,
        string aliasDescription,
        Func<SessionDetailsResult, bool> save)
    {
        InitializeComponent();
        _save = save;
        HeaderText.Text = header;
        VehicleNameCombo.ItemsSource = vehicleChoices;
        VehicleNameCombo.Text = edits.VehicleName;
        DriverNameBox.Text = edits.DriverName;
        SessionTrackCombo.ItemsSource = layoutChoices;
        SessionTrackCombo.SelectedValue = selectedLayout;
        TrackConditionCombo.Text = edits.TrackCondition;
        RaceTypeBox.Text = edits.RaceType;
        SessionAliasText.Text = aliasDescription;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var vehicleName = VehicleNameCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(vehicleName))
        {
            MessageBox.Show(this, "Enter the car or vehicle name for this session.",
                "Vehicle name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = new SessionDetailsResult(
            vehicleName,
            DriverNameBox.Text.Trim(),
            SessionTrackCombo.SelectedValue as string ?? string.Empty,
            string.IsNullOrWhiteSpace(TrackConditionCombo.Text)
                ? "UNKNOWN"
                : TrackConditionCombo.Text.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(RaceTypeBox.Text) ? "TRACK" : RaceTypeBox.Text.Trim());
        if (_save(result))
        {
            DialogResult = true;
        }
    }
}
