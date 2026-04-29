using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrataTheme.Controls;

namespace Rema.ViewModels;

// ── Base ──

public abstract partial class TranscriptItem : ObservableObject
{
    protected TranscriptItem(string stableId) => StableId = stableId;
    public string StableId { get; }
}

public sealed class TranscriptTurn : ObservableObject
{
    public ObservableCollection<TranscriptItem> Items { get; } = [];
    public string StableId { get; }
    public TranscriptTurn(string stableId) => StableId = stableId;
}

// ── Message Items ──

public sealed partial class UserMessageItem : TranscriptItem
{
    public UserMessageItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _timestampText = "";
}

public sealed partial class AssistantMessageItem : TranscriptItem
{
    public AssistantMessageItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _author = "Rema";
    [ObservableProperty] private string _timestampText = "";
    [ObservableProperty] private string? _modelName;
}

public sealed partial class ErrorMessageItem : TranscriptItem
{
    public ErrorMessageItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _timestampText = "";
    public System.Windows.Input.ICommand? RetryCommand { get; set; }
}

// ── Reasoning ──

public sealed partial class ReasoningItem : TranscriptItem
{
    public ReasoningItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isExpanded;
}

// ── Tool Calls ──

public partial class ToolCallItemBase : ObservableObject
{
    public string StableId { get; }
    public ToolCallItemBase(string stableId) => StableId = stableId;
}

public sealed partial class ToolCallItem : ToolCallItemBase
{
    public ToolCallItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _toolName = "";
    [ObservableProperty] private StrataAiToolCallStatus _status = StrataAiToolCallStatus.InProgress;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private string? _inputParameters;
    [ObservableProperty] private string? _moreInfo;
}

public sealed partial class ToolGroupItem : TranscriptItem
{
    public ToolGroupItem(string stableId) : base(stableId) { }

    public ObservableCollection<ToolCallItemBase> ToolCalls { get; } = [];

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _streamingSummary;

    public bool HasStreamingSummary => !string.IsNullOrEmpty(StreamingSummary);

    partial void OnStreamingSummaryChanged(string? value) =>
        OnPropertyChanged(nameof(HasStreamingSummary));
}

public sealed partial class SingleToolItem : TranscriptItem
{
    public SingleToolItem(string stableId, ToolCallItemBase inner) : base(stableId)
    {
        Inner = inner;

        // Forward property changes from inner item to ourselves
        if (inner is ToolCallItem tc)
        {
            tc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ToolCallItem.ToolName))
                    OnPropertyChanged(nameof(Label));
                else if (e.PropertyName is nameof(ToolCallItem.Status))
                {
                    OnPropertyChanged(nameof(IsActive));
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(Meta));
                }
                else if (e.PropertyName is nameof(ToolCallItem.IsExpanded))
                    OnPropertyChanged(nameof(IsExpanded));
                else if (e.PropertyName is nameof(ToolCallItem.InputParameters))
                    OnPropertyChanged(nameof(InputParameters));
                else if (e.PropertyName is nameof(ToolCallItem.MoreInfo))
                    OnPropertyChanged(nameof(MoreInfo));
            };
        }
    }

    public ToolCallItemBase Inner { get; }

    public string Label => Inner is ToolCallItem tc ? tc.ToolName : "";
    public bool IsActive => Inner is ToolCallItem tc
        ? tc.Status == StrataAiToolCallStatus.InProgress
        : false;
    public StrataAiToolCallStatus Status => Inner is ToolCallItem tc ? tc.Status : StrataAiToolCallStatus.InProgress;
    public bool IsExpanded => Inner is ToolCallItem tc && tc.IsExpanded;
    public string? Meta => Inner is ToolCallItem tc ? tc.Status switch
    {
        StrataAiToolCallStatus.Completed => "✓",
        StrataAiToolCallStatus.Failed => "✗",
        _ => null,
    } : null;
    public string? InputParameters => Inner is ToolCallItem tc ? tc.InputParameters : null;
    public string? MoreInfo => Inner is ToolCallItem tc ? tc.MoreInfo : null;
}

// ── Indicators ──

public sealed partial class TypingIndicatorItem : TranscriptItem
{
    public TypingIndicatorItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _label = "Thinking…";
    [ObservableProperty] private bool _isActive = true;
}

public sealed partial class TurnModelItem : TranscriptItem
{
    public TurnModelItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _modelName = "";
}

// ── Terminal Preview ──

public sealed partial class TerminalPreviewItem : ToolCallItemBase
{
    public TerminalPreviewItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _toolName = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isExpanded;
}

// ── Sub-agent ──

public sealed partial class SubagentToolCallItem : TranscriptItem
{
    public SubagentToolCallItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _taskDescription;
    [ObservableProperty] private string? _modeLabel;
    [ObservableProperty] private string? _modelDisplayName;
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private double _durationMs;

    public ObservableCollection<ToolCallItemBase> Activities { get; } = [];

    public string Title => !string.IsNullOrWhiteSpace(TaskDescription) ? TaskDescription! : DisplayName;
    public bool IsActive => Status == StrataAiToolCallStatus.InProgress;
    public bool HasModeLabel => !string.IsNullOrEmpty(ModeLabel);
    public bool HasModelName => !string.IsNullOrEmpty(ModelDisplayName);
    public bool HasActivities => Activities.Count > 0;
    public string? DurationText => DurationMs <= 0 ? null : DurationMs >= 1000 ? $"{DurationMs / 1000:F1}s" : $"{DurationMs:F0}ms";

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Title));
    partial void OnTaskDescriptionChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnStatusChanged(StrataAiToolCallStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        if (value != StrataAiToolCallStatus.InProgress)
            IsExpanded = false;
    }
    partial void OnModeLabelChanged(string? value) => OnPropertyChanged(nameof(HasModeLabel));
    partial void OnModelDisplayNameChanged(string? value) => OnPropertyChanged(nameof(HasModelName));
    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(DurationText));
}

// ── Source Citations ──

public sealed partial class SourceItem : ObservableObject
{
    public string Title { get; }
    public string Domain { get; }
    public string Url { get; }

    public SourceItem(string title, string url)
    {
        Title = title;
        Url = url;
        Domain = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : url;
    }

    [RelayCommand]
    private void Open()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch { }
    }
}

// ── File Changes ──

public sealed partial class FileChangeItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string? Directory { get; }
    public string ActionIcon { get; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string StatsAdded => $"+{LinesAdded}";
    public string StatsRemoved => LinesRemoved > 0 ? $"−{LinesRemoved}" : "";
    public bool HasRemovals => LinesRemoved > 0;

    public FileChangeItem(string filePath, bool isCreate)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        Directory = System.IO.Path.GetDirectoryName(filePath);
        ActionIcon = isCreate ? "📄" : "📝";
    }

    public void AddEdit(string? oldText, string? newText)
    {
        LinesAdded += CountLines(newText);
        LinesRemoved += CountLines(oldText);
    }

    private static int CountLines(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
}

public sealed partial class FileChangesSummaryItem : TranscriptItem
{
    public FileChangesSummaryItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _totalStatsAdded = "";
    [ObservableProperty] private string _totalStatsRemoved = "";
    [ObservableProperty] private bool _hasTotalRemovals;

    public ObservableCollection<FileChangeItem> FileChanges { get; } = [];
}

// ── Question Card ──

public sealed partial class QuestionItem : TranscriptItem
{
    private readonly Action<string, string>? _submitAction;
    private bool _isSubmitting;

    public string QuestionId { get; }
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private IList<string>? _optionsList;
    [ObservableProperty] private bool _allowFreeText = true;
    [ObservableProperty] private string? _selectedAnswer;
    [ObservableProperty] private bool _isAnswered;

    public QuestionItem(string questionId, string question, IList<string>? options,
        bool allowFreeText, Action<string, string>? submitAction, string stableId)
        : base(stableId)
    {
        QuestionId = questionId;
        _question = question;
        _optionsList = options;
        _allowFreeText = allowFreeText;
        _submitAction = submitAction;
    }

    partial void OnIsAnsweredChanged(bool value)
    {
        if (value && !_isSubmitting && !string.IsNullOrEmpty(SelectedAnswer))
        {
            _isSubmitting = true;
            _submitAction?.Invoke(QuestionId, SelectedAnswer);
            _isSubmitting = false;
        }
    }

    public void Submit(string answer)
    {
        _isSubmitting = true;
        SelectedAnswer = answer;
        IsAnswered = true;
        _submitAction?.Invoke(QuestionId, answer);
        _isSubmitting = false;
    }
}

// ── Source Citations List ──

public sealed partial class SourcesListItem : TranscriptItem
{
    public SourcesListItem(string stableId) : base(stableId) { }

    public ObservableCollection<SourceItem> Sources { get; } = [];
    [ObservableProperty] private bool _isExpanded;
}

// ── File Attachment ──

public sealed partial class FileAttachmentItem : TranscriptItem
{
    public FileAttachmentItem(string stableId) : base(stableId) { }

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _filePath = "";

    [RelayCommand]
    private void OpenFile()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FilePath) { UseShellExecute = true }); }
        catch { }
    }
}
