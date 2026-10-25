using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TrackTelemetryViewer;

namespace PitWallApp;

internal static class Dialogs
{
    public static Task ShowMessageAsync(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok }
            }
        };
        ok.Click += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel }
                    }
                }
            }
        };
        ok.Click += (_, _) => window.Close(box.Text?.Trim());
        cancel.Click += (_, _) => window.Close(null);
        return await window.ShowDialog<string?>(owner);
    }
}

internal sealed record SessionDetailsResult(
    string VehicleName, string DriverName, string LayoutValue, string TrackCondition, string RaceType);

internal sealed class SessionDetailsWindow : Window
{
    private readonly AutoCompleteBox _vehicle;
    private readonly TextBox _driver;
    private readonly ComboBox _layout;
    private readonly ComboBox _condition;
    private readonly TextBox _raceType;
    private readonly IReadOnlyList<SessionLayoutChoice> _choices;

    public SessionDetailsWindow(SessionModel model, SessionInfo session, IEnumerable<string> vehicleNames)
    {
        Title = "Edit session details";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var edits = model.GetSessionEdits(session);
        _vehicle = new AutoCompleteBox
        {
            ItemsSource = vehicleNames.ToArray(),
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.Contains,
            Text = edits.VehicleName
        };
        _driver = new TextBox { Text = edits.DriverName };
        _choices = model.BuildSessionLayoutChoices(session);
        _layout = new ComboBox
        {
            ItemsSource = _choices.Select(choice => choice.Label).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var assigned = model.Settings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var layout)
            ? layout
            : SessionModel.UseSourceLayout;
        var layoutIndex = _choices.ToList().FindIndex(choice =>
            choice.Value.Equals(assigned, StringComparison.OrdinalIgnoreCase));
        _layout.SelectedIndex = Math.Max(0, layoutIndex);
        var conditions = new List<string> { "DRY", "WET", "UNKNOWN" };
        if (!conditions.Contains(edits.TrackCondition, StringComparer.OrdinalIgnoreCase))
        {
            conditions.Add(edits.TrackCondition);
        }

        _condition = new ComboBox { ItemsSource = conditions, HorizontalAlignment = HorizontalAlignment.Stretch };
        _condition.SelectedIndex = conditions.FindIndex(item =>
            item.Equals(edits.TrackCondition, StringComparison.OrdinalIgnoreCase));
        _raceType = new TextBox { Text = edits.RaceType };

        var ok = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            var vehicleName = _vehicle.Text?.Trim() ?? string.Empty;
            if (vehicleName.Length == 0)
            {
                _ = Dialogs.ShowMessageAsync(this, "Vehicle name required",
                    "Enter the car or vehicle name for this session.");
                return;
            }

            Close(new SessionDetailsResult(
                vehicleName,
                _driver.Text?.Trim() ?? string.Empty,
                _choices[Math.Max(0, _layout.SelectedIndex)].Value,
                _condition.SelectedItem as string ?? "UNKNOWN",
                string.IsNullOrWhiteSpace(_raceType.Text) ? "TRACK" : _raceType.Text.Trim()));
        };
        cancel.Click += (_, _) => Close(null);

        var vehicleDescription = string.IsNullOrWhiteSpace(session.VehicleAlias)
            ? string.IsNullOrWhiteSpace(session.AiMData?.VehicleName)
                ? "No vehicle alias was included in the session metadata."
                : $"AiM vehicle: {session.AiMData.VehicleName}"
            : model.Catalog.ResolveAlias(session.VehicleAlias) is null
                ? $"No catalog vehicle ID matches alias {session.VehicleAlias}."
                : $"Vehicle alias: {session.VehicleAlias}";
        var notes = new TextBlock
        {
            Text = $"{session.LocalStart:MMM d, yyyy h:mm tt} · {vehicleDescription}\n" +
                   "Car-name mappings set from the tree take priority over the vehicle name here.",
            Opacity = 0.6,
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Vehicle" }, _vehicle,
                new TextBlock { Text = "Driver" }, _driver,
                new TextBlock { Text = "Track / layout" }, _layout,
                new TextBlock { Text = "Track condition" }, _condition,
                new TextBlock { Text = "Session type" }, _raceType,
                notes,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { ok, cancel }
                }
            }
        };
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly ViewerSettings _original;
    private readonly ComboBox _units;
    private readonly HashSet<string> _hidden;
    private readonly HashSet<string> _custom;
    private readonly HashSet<string> _catalogNames;
    private readonly List<(TrackAliasSetting Setting, TextBox Box)> _trackBoxes = [];
    private readonly StackPanel _vehicleList = new() { Spacing = 2 };

    public SettingsWindow(
        ViewerSettings settings,
        IEnumerable<string> aliases,
        IEnumerable<TrackAliasSetting> trackAliases,
        IEnumerable<string> vehicleNames,
        IEnumerable<string> catalogNames,
        IEnumerable<ChartChannelSetting> channels)
    {
        Title = "Settings";
        _channels = channels.ToList();
        RebuildChannelRows();
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _original = settings;
        _hidden = new HashSet<string>(settings.HiddenVehicleNames, StringComparer.CurrentCultureIgnoreCase);
        _custom = new HashSet<string>(settings.CustomVehicleNames, StringComparer.CurrentCultureIgnoreCase);
        _catalogNames = new HashSet<string>(catalogNames, StringComparer.CurrentCultureIgnoreCase);

        _units = new ComboBox
        {
            ItemsSource = new[] { "Metric", "Imperial" },
            SelectedIndex = settings.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase) ? 1 : 0
        };

        var allNames = vehicleNames.Concat(settings.CustomVehicleNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var name in allNames)
        {
            AddVehicleRow(name);
        }

        var newName = new TextBox { Watermark = "New car name", MinWidth = 240 };
        var add = new Button { Content = "Add" };
        add.Click += (_, _) =>
        {
            var name = newName.Text?.Trim() ?? string.Empty;
            if (name.Length == 0 || allNames.Contains(name, StringComparer.CurrentCultureIgnoreCase))
            {
                return;
            }

            allNames.Add(name);
            _custom.Add(name);
            AddVehicleRow(name);
            newName.Text = string.Empty;
        };

        var aliasPanel = new StackPanel { Spacing = 4 };
        foreach (var alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase))
        {
            var box = new AutoCompleteBox
            {
                ItemsSource = allNames,
                MinimumPrefixLength = 0,
                FilterMode = AutoCompleteFilterMode.Contains,
                Text = settings.VehicleMappings.TryGetValue(alias, out var mapped) ? mapped : string.Empty,
                Width = 280
            };
            aliasPanel.Children.Add(Row(new TextBlock
            {
                Text = alias,
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            }, box));
            _autoBoxes.Add((alias, box));
        }

        var trackPanel = new StackPanel { Spacing = 4 };
        foreach (var track in trackAliases.OrderBy(item => item.Alias, StringComparer.CurrentCultureIgnoreCase))
        {
            var box = new TextBox { Text = track.Name, Width = 280 };
            _trackBoxes.Add((track, box));
            trackPanel.Children.Add(Row(new TextBlock
            {
                Text = track.Alias,
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }, box));
        }

        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => Close(BuildResult());
        cancel.Click += (_, _) => Close(null);

        var tabs = new TabControl
        {
            SelectedIndex = 0,
            Items =
            {
                new TabItem
                {
                    Header = "Channels",
                    Content = new DockPanel
                    {
                        Margin = new Thickness(12),
                        Children =
                        {
                            Dock(new TextBlock
                            {
                                Text = "Choose which plots are shown and their order.",
                                Margin = new Thickness(0, 0, 0, 8)
                            }, Avalonia.Controls.Dock.Top),
                            new ScrollViewer { Content = _channelList }
                        }
                    }
                },
                new TabItem
                {
                    Header = "General",
                    Content = new StackPanel
                    {
                        Margin = new Thickness(12),
                        Spacing = 6,
                        Children = { new TextBlock { Text = "Distance and speed units" }, _units }
                    }
                },
                new TabItem
                {
                    Header = "Vehicles",
                    Content = new DockPanel
                    {
                        Margin = new Thickness(12),
                        Children =
                        {
                            Dock(new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Margin = new Thickness(0, 0, 0, 8),
                                Children = { newName, add }
                            }, Avalonia.Controls.Dock.Top),
                            new ScrollViewer { Content = _vehicleList }
                        }
                    }
                },
                new TabItem
                {
                    Header = "Car aliases",
                    Content = new ScrollViewer { Margin = new Thickness(12), Content = aliasPanel }
                },
                new TabItem
                {
                    Header = "Track names",
                    Content = new ScrollViewer { Margin = new Thickness(12), Content = trackPanel }
                }
            }
        };

        var buttons = Dock(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { save, cancel }
        }, Avalonia.Controls.Dock.Bottom);
        Content = new DockPanel { Children = { buttons, tabs } };
    }

    private readonly List<(string Alias, AutoCompleteBox Box)> _autoBoxes = [];
    private readonly List<ChartChannelSetting> _channels;
    private readonly StackPanel _channelList = new() { Spacing = 2 };

    private void RebuildChannelRows()
    {
        _channelList.Children.Clear();
        for (var index = 0; index < _channels.Count; index++)
        {
            var channel = _channels[index];
            var position = index;
            var check = new CheckBox { Content = channel.Name, IsChecked = channel.IsEnabled, MinWidth = 320 };
            check.IsCheckedChanged += (_, _) => channel.IsEnabled = check.IsChecked == true;
            var up = new Button { Content = "▲", IsEnabled = index > 0 };
            up.Click += (_, _) => MoveChannel(position, -1);
            var down = new Button { Content = "▼", IsEnabled = index < _channels.Count - 1 };
            down.Click += (_, _) => MoveChannel(position, 1);
            _channelList.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { check, up, down }
            });
        }
    }

    private void MoveChannel(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= _channels.Count)
        {
            return;
        }

        (_channels[index], _channels[target]) = (_channels[target], _channels[index]);
        RebuildChannelRows();
    }

    private static T Dock<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static StackPanel Row(Control label, Control editor) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { label, editor }
    };

    private void AddVehicleRow(string name)
    {
        var check = new CheckBox { Content = name, IsChecked = !_hidden.Contains(name) };
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true)
            {
                _hidden.Remove(name);
            }
            else
            {
                _hidden.Add(name);
            }
        };
        _vehicleList.Children.Add(check);
    }

    private ViewerSettings BuildResult()
    {
        var result = SettingsStore.Clone(_original);
        result.DistanceUnits = _units.SelectedIndex == 1 ? "Imperial" : "Metric";
        result.HiddenVehicleNames = _hidden.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        result.VehicleMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, box) in _autoBoxes)
        {
            var name = box.Text?.Trim() ?? string.Empty;
            if (name.Length > 0)
            {
                result.VehicleMappings[alias] = name;
                if (!_catalogNames.Contains(name))
                {
                    _custom.Add(name);
                }
            }
        }

        result.CustomVehicleNames = _custom
            .Where(name => !_catalogNames.Contains(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var (setting, box) in _trackBoxes)
        {
            var name = box.Text?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Equals(setting.DefaultName, StringComparison.Ordinal))
            {
                result.TrackMappings.Remove(setting.Key);
            }
            else
            {
                result.TrackMappings[setting.Key] = name;
            }
        }

        foreach (var channel in _channels)
        {
            result.ChannelVisibility[channel.Key] = channel.IsEnabled;
        }

        result.ChannelOrder = _channels.Select(channel => channel.Key).ToList();
        return result;
    }
}
