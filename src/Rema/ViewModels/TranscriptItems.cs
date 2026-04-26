using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
                    OnPropertyChanged(nameof(Meta));
                }
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
