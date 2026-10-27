using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TrackTelemetryViewer;

internal enum ExportFormat
{
    PitWall,
    Xrk
}

public partial class ExportDialog : Window
{
    private readonly string _defaultFileName;

    internal ExportDialog(string description, string defaultFileName)
    {
        InitializeComponent();
        InfoText.Text = description;
        _defaultFileName = defaultFileName;
        DestinationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), _defaultFileName + ".pitwall");
        UpdateFormatUi();
    }

    internal ExportFormat Format => XrkRadio.IsChecked == true ? ExportFormat.Xrk : ExportFormat.PitWall;
    internal string Destination => DestinationBox.Text.Trim();

    private void Format_Changed(object sender, RoutedEventArgs e) => UpdateFormatUi();

    private void UpdateFormatUi()
    {
        if (XrkRadio is null || DestinationBox is null)
        {
            return;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Format == ExportFormat.Xrk)
        {
            XrkNote.Visibility = Visibility.Visible;
            DestinationLabel.Text = "Save into folder";
            DestinationBox.Text = Path.GetDirectoryName(DestinationBox.Text) is { Length: > 0 } directory &&
                                  Path.HasExtension(DestinationBox.Text)
                ? directory
                : DestinationBox.Text;
            if (string.IsNullOrWhiteSpace(DestinationBox.Text))
            {
                DestinationBox.Text = documents;
            }
        }
        else
        {
            XrkNote.Visibility = Visibility.Collapsed;
            DestinationLabel.Text = "Save as";
            if (!Path.HasExtension(DestinationBox.Text))
            {
                DestinationBox.Text = Path.Combine(
                    string.IsNullOrWhiteSpace(DestinationBox.Text) ? documents : DestinationBox.Text,
                    _defaultFileName + ".pitwall");
            }
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Format == ExportFormat.Xrk)
        {
            var folderDialog = new OpenFolderDialog { Title = "Choose where to save the XRK files" };
            if (folderDialog.ShowDialog(this) == true)
            {
                DestinationBox.Text = folderDialog.FolderName;
            }

            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Save PitWall package",
            Filter = "PitWall package (*.pitwall)|*.pitwall",
            DefaultExt = ".pitwall",
            FileName = Path.GetFileName(DestinationBox.Text) is { Length: > 0 } name ? name : _defaultFileName + ".pitwall",
            InitialDirectory = Path.GetDirectoryName(DestinationBox.Text) is { Length: > 0 } dir && Directory.Exists(dir)
                ? dir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (saveDialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = saveDialog.FileName;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Destination))
        {
            MessageBox.Show(this, "Choose where to save the export.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
