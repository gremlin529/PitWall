using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed class TreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private IBrush _foreground = Palette.Text;
    private IBrush _suffixForeground = Palette.Text;
    private string _tail = string.Empty;
    private string _text;
    private string _suffix = string.Empty;

    public TreeNode(string text) => _text = text;

    public string Text
    {
        get => _text;
        set { _text = value; Raise(nameof(Text)); }
    }

    public IBrush Foreground
    {
        get => _foreground;
        set { _foreground = value; Raise(nameof(Foreground)); }
    }

    public string Suffix
    {
        get => _suffix;
        set { _suffix = value; Raise(nameof(Suffix)); }
    }

    public IBrush SuffixForeground
    {
        get => _suffixForeground;
        set { _suffixForeground = value; Raise(nameof(SuffixForeground)); }
    }

    // Text after the suffix, drawn in the node's own foreground (e.g. "(wet)").
    public string Tail
    {
        get => _tail;
        set { _tail = value; Raise(nameof(Tail)); }
    }

    public string? Key { get; set; }
    public IReadOnlyList<SessionInfo>? DateSessions { get; init; }

    public ObservableCollection<TreeNode> Children { get; } = [];
    public SessionInfo? Session { get; init; }
    public LapInfo? Lap { get; set; }
    public TrackLocation? Location { get; init; }
    public TrackCarGroup? Car { get; init; }
    public TrackDriverGroup? Driver { get; init; }

    // Invoked the first time the node is expanded, to load children on demand.
    public Func<TreeNode, Task>? LoadChildren { get; set; }

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise(nameof(IsExpanded));
            if (value && LoadChildren is { } load)
            {
                LoadChildren = null;
                LoadTask = load(this);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
