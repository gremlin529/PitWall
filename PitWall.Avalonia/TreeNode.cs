using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using TrackTelemetryViewer;

namespace PitWallApp;

// One session-library row. Header text is split into Text + Suffix + Tail so the suffix (fastest/optimal time)
// can be coloured independently, matching the WPF StackPanel headers.
internal sealed class TreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private IBrush _foreground = Palette.Text;
    private IBrush? _suffixForeground;
    private string _tail = string.Empty;
    private string _text;
    private string _suffix = string.Empty;
    private bool _suffixIsLink;

    public TreeNode(string text) => _text = text;

    public string Text
    {
        get => _text;
        set { _text = value; Raise(nameof(Text)); }
    }

    public IBrush Foreground
    {
        get => _foreground;
        set
        {
            _foreground = value;
            Raise(nameof(Foreground));
            Raise(nameof(EffectiveSuffixForeground));
        }
    }

    public FontWeight Weight { get; init; } = FontWeight.Normal;

    public string Suffix
    {
        get => _suffix;
        set { _suffix = value; Raise(nameof(Suffix)); }
    }

    // Null means the suffix inherits the node's own foreground, as WPF header TextBlocks do.
    public IBrush? SuffixForeground
    {
        get => _suffixForeground;
        set
        {
            _suffixForeground = value;
            Raise(nameof(SuffixForeground));
            Raise(nameof(EffectiveSuffixForeground));
        }
    }

    public IBrush EffectiveSuffixForeground => _suffixForeground ?? _foreground;

    // True for a session's fastest-lap time, which loads that lap when clicked.
    public bool SuffixIsLink
    {
        get => _suffixIsLink;
        set { _suffixIsLink = value; Raise(nameof(SuffixIsLink)); }
    }

    // Text after the suffix, drawn in the node's own foreground (e.g. "(wet)").
    public string Tail
    {
        get => _tail;
        set { _tail = value; Raise(nameof(Tail)); }
    }

    public string? Key { get; set; }

    public ObservableCollection<TreeNode> Children { get; } = [];
    public SessionInfo? Session { get; init; }
    public LapInfo? Lap { get; set; }
    public TrackLocation? Location { get; init; }
    public TrackCarGroup? Car { get; init; }
    public TrackDriverGroup? Driver { get; init; }
    public TrackDateGroup? Date { get; init; }

    // For date and session nodes: every session of the driver the node belongs to.
    public IReadOnlyList<SessionInfo>? DriverSessions { get; init; }

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
            if (value)
            {
                EnsureChildrenLoaded();
            }
        }
    }

    public Task EnsureChildrenLoaded()
    {
        if (LoadChildren is { } load)
        {
            LoadChildren = null;
            LoadTask = load(this);
        }

        return LoadTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
