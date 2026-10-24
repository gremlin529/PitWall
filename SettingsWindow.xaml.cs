using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrackTelemetryViewer;

public partial class SettingsWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private readonly ObservableCollection<VehicleAliasSetting> _aliases;
    private readonly ObservableCollection<TrackAliasSetting> _trackAliases;
    private readonly ObservableCollection<string> _vehicleChoices;
    private readonly ObservableCollection<VehicleVisibilitySetting> _vehicles;
    private readonly ObservableCollection<ChartChannelSetting> _channels;
    private readonly HashSet<string> _catalogVehicleNames;
    private readonly HashSet<string> _customVehicleNames;
    private readonly HashSet<string> _hiddenVehicleNames;

    internal SettingsWindow(
        ViewerSettings settings,
        IEnumerable<string> aliases,
        IEnumerable<TrackAliasSetting> trackAliases,
        IEnumerable<string> vehicleNames,
        IEnumerable<string> catalogVehicleNames,
        IEnumerable<ChartChannelSetting> channels)
    {
        InitializeComponent();
        _customVehicleNames = new HashSet<string>(settings.CustomVehicleNames, StringComparer.CurrentCultureIgnoreCase);
        _hiddenVehicleNames = new HashSet<string>(settings.HiddenVehicleNames, StringComparer.CurrentCultureIgnoreCase);
        var allVehicleNames = vehicleNames
            .Concat(settings.CustomVehicleNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _vehicleChoices = new ObservableCollection<string>(allVehicleNames
            .Where(name => !_hiddenVehicleNames.Contains(name)));
        _vehicles = new ObservableCollection<VehicleVisibilitySetting>(allVehicleNames
            .Select(name => new VehicleVisibilitySetting(name, _hiddenVehicleNames.Contains(name))));
        _channels = new ObservableCollection<ChartChannelSetting>(channels);
        _catalogVehicleNames = new HashSet<string>(catalogVehicleNames, StringComparer.CurrentCultureIgnoreCase);
        _aliases = new ObservableCollection<VehicleAliasSetting>(aliases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .Select(alias => new VehicleAliasSetting(
                alias,
                settings.VehicleMappings.TryGetValue(alias, out var mappedName) ? mappedName : string.Empty)));
        _trackAliases = new ObservableCollection<TrackAliasSetting>(trackAliases
            .OrderBy(alias => alias.Alias, StringComparer.CurrentCultureIgnoreCase));
        VehicleNames = _vehicleChoices;
        DataContext = this;
        AliasList.ItemsSource = _aliases;
        TrackAliasList.ItemsSource = _trackAliases;
        VehicleNameList.ItemsSource = _vehicles;
        ChannelList.ItemsSource = _channels;
        DistanceUnitCombo.SelectedIndex = settings.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        Settings = settings;
    }

    public ObservableCollection<string> VehicleNames { get; }

    internal ViewerSettings Settings { get; private set; }

    private void AddVehicle_Click(object sender, RoutedEventArgs e)
    {
        var name = NewVehicleNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for the new vehicle.",
                "Vehicle name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existingName = _vehicles.FirstOrDefault(vehicle =>
            vehicle.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existingName is not null)
        {
            existingName.IsHidden = false;
            _hiddenVehicleNames.Remove(existingName.Name);
            if (!_vehicleChoices.Contains(existingName.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                AddVehicleChoice(existingName.Name);
            }

            VehicleNameList.Items.Refresh();
            NewVehicleNameBox.Clear();
            return;
        }

        var duplicateChoice = _vehicleChoices.FirstOrDefault(vehicleName =>
            vehicleName.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (duplicateChoice is not null)
        {
            MessageBox.Show(this, "That vehicle name is already available.",
                "Vehicle already exists", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddVehicleChoice(name);
        _vehicles.Add(new VehicleVisibilitySetting(name, false));
        _customVehicleNames.Add(name);
        NewVehicleNameBox.Clear();
    }

    private void ToggleVehicleVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VehicleVisibilitySetting vehicle })
        {
            return;
        }

        vehicle.IsHidden = !vehicle.IsHidden;
        if (vehicle.IsHidden)
        {
            _hiddenVehicleNames.Add(vehicle.Name);
            _vehicleChoices.Remove(vehicle.Name);
        }
        else
        {
            _hiddenVehicleNames.Remove(vehicle.Name);
            if (!_vehicleChoices.Contains(vehicle.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                AddVehicleChoice(vehicle.Name);
            }
        }

    }

    private void AddVehicleChoice(string name)
    {
        var index = 0;
        while (index < _vehicleChoices.Count &&
               StringComparer.CurrentCultureIgnoreCase.Compare(_vehicleChoices[index], name) < 0)
        {
            index++;
        }

        _vehicleChoices.Insert(index, name);
    }

    private void MoveChannelUp_Click(object sender, RoutedEventArgs e) =>
        MoveChannel(sender, -1);

    private void MoveChannelDown_Click(object sender, RoutedEventArgs e) =>
        MoveChannel(sender, 1);

    private void MoveChannel(object sender, int offset)
    {
        if (sender is not FrameworkElement { Tag: ChartChannelSetting channel })
        {
            return;
        }

        var index = _channels.IndexOf(channel);
        var targetIndex = index + offset;
        if (index < 0 || targetIndex < 0 || targetIndex >= _channels.Count)
        {
            return;
        }

        _channels.Move(index, targetIndex);
        ChannelList.SelectedItem = channel;
        ChannelList.ScrollIntoView(channel);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ChannelList.ItemContainerGenerator.ContainerFromItem(channel) is not ListBoxItem item)
            {
                return;
            }

            var arrow = offset < 0 ? "↑" : "↓";
            var button = FindChannelButton(item, channel, arrow);
            if (button is null || !button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            {
                return;
            }

            var screenPosition = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            if (!SetCursorPos((int)Math.Round(screenPosition.X), (int)Math.Round(screenPosition.Y)))
            {
                MessageBox.Show(this,
                    new Win32Exception(Marshal.GetLastWin32Error()).Message,
                    "Could not move the pointer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            button.Focus();
        });
    }

    private static Button? FindChannelButton(DependencyObject parent, ChartChannelSetting channel, string arrow)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button { Tag: ChartChannelSetting taggedChannel, Content: string content } button &&
                ReferenceEquals(taggedChannel, channel) && content == arrow)
            {
                return button;
            }

            var nested = FindChannelButton(child, channel, arrow);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in _aliases)
        {
            var vehicleName = alias.VehicleName.Trim();
            if (!string.IsNullOrWhiteSpace(vehicleName))
            {
                mappings[alias.Alias] = vehicleName;
            }
        }

        var trackMappings = new Dictionary<string, string>(
            Settings.TrackMappings, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in _trackAliases)
        {
            var name = alias.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                name.Equals(alias.DefaultName, StringComparison.Ordinal))
            {
                trackMappings.Remove(alias.Key);
            }
            else
            {
                trackMappings[alias.Key] = name;
            }
        }

        var channelVisibility = new Dictionary<string, bool>(
            Settings.ChannelVisibility, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in _channels)
        {
            channelVisibility[channel.Key] = channel.IsEnabled;
        }

        Settings = new ViewerSettings
        {
            DistanceUnits = DistanceUnitCombo.SelectedIndex == 1 ? "Imperial" : "Metric",
            CustomVehicleNames = _customVehicleNames
                .Where(name => !_catalogVehicleNames.Contains(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            HiddenVehicleNames = _hiddenVehicleNames
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            VehicleMappings = mappings,
            SessionVehicleMappings = new Dictionary<string, string>(
                Settings.SessionVehicleMappings, StringComparer.OrdinalIgnoreCase),
            ChannelVisibility = channelVisibility,
            ChannelOrder = _channels.Select(channel => channel.Key).ToList(),
            TrackMappings = trackMappings,
            SessionLayoutAssignments = new Dictionary<string, string>(
                Settings.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase)
        };
        DialogResult = true;
    }
}
