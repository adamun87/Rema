using System.Collections.ObjectModel;
using Rema.Models;
using Rema.Services;
using StrataTheme.Controls;

namespace Rema.ViewModels;

/// <summary>
/// Converts a flat list of ChatMessages into a hierarchical transcript
/// of TranscriptTurns containing typed TranscriptItems.
/// </summary>
public sealed class TranscriptBuilder
{
    private readonly List<TranscriptTurn> _target = [];
    private TranscriptTurn? _currentTurn;
    private ToolGroupItem? _currentToolGroup;
    private readonly Dictionary<string, ToolCallItem> _toolCallsById = new(StringComparer.Ordinal);
    private string? _lastRole;
    private int _turnCounter;
    private int _itemCounter;

    public ObservableCollection<TranscriptTurn> Rebuild(IReadOnlyList<ChatMessageViewModel> messages)
    {
        _target.Clear();
        _currentTurn = null;
        _currentToolGroup = null;
        _toolCallsById.Clear();
        _lastRole = null;
        _turnCounter = 0;
        _itemCounter = 0;

        foreach (var msg in messages)
            ProcessMessageToTranscript(msg);

        CloseCurrentToolGroup();

        return new ObservableCollection<TranscriptTurn>(_target);
    }

    public void ProcessMessageToTranscript(ChatMessageViewModel msgVm)
    {
        var msg = msgVm.Message;

        switch (msg.Role)
        {
            case "user":
                ProcessUserMessage(msgVm);
                break;
            case "assistant":
                ProcessAssistantMessage(msgVm);
                break;
            case "reasoning":
                ProcessReasoningMessage(msgVm);
                break;
            case "tool":
                ProcessToolMessage(msgVm);
                break;
            case "error":
                ProcessErrorMessage(msgVm);
                break;
            default:
                ProcessAssistantMessage(msgVm);
                break;
        }
    }

    // ── Typing indicator ──

    public TypingIndicatorItem AddTypingIndicator(string label = "Thinking…")
    {
        EnsureTurn("user"); // Typing appears at end of current turn
        var item = new TypingIndicatorItem(NextItemId()) { Label = label, IsActive = true };
        _currentTurn!.Items.Add(item);
        return item;
    }

    public void RemoveTypingIndicator(TypingIndicatorItem indicator)
    {
        foreach (var turn in _target)
            if (turn.Items.Remove(indicator))
                return;
    }

    // ── Model label ──

    public void AddTurnModelLabel(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName) || _currentTurn is null) return;
        var item = new TurnModelItem(NextItemId()) { ModelName = modelName };
        _currentTurn.Items.Add(item);
    }

    // ── Update tool status ──

    public void UpdateToolStatus(string toolCallId, StrataAiToolCallStatus status,
        double durationMs = 0, string? moreInfo = null)
    {
        if (!_toolCallsById.TryGetValue(toolCallId, out var tc)) return;

        tc.Status = status;
        if (durationMs > 0) tc.DurationMs = durationMs;
        if (moreInfo is not null) tc.MoreInfo = moreInfo;

        // Update parent group meta
        UpdateGroupMeta();
    }

    // ── Private processing ──

    private void ProcessUserMessage(ChatMessageViewModel msgVm)
    {
        CloseCurrentToolGroup();
        EnsureNewTurn("user");

        var item = new UserMessageItem(NextItemId())
        {
            Content = msgVm.Content,
            Author = msgVm.Author ?? "You",
            TimestampText = msgVm.TimestampText,
        };
        _currentTurn!.Items.Add(item);
    }

    private void ProcessAssistantMessage(ChatMessageViewModel msgVm)
    {
        CloseCurrentToolGroup();
        EnsureTurn("assistant");

        var item = new AssistantMessageItem(NextItemId())
        {
            Content = msgVm.Content,
            IsStreaming = msgVm.IsStreaming,
            TimestampText = msgVm.TimestampText,
            ModelName = ChatViewModel.FormatModelDisplay(msgVm.Message.Model),
        };

        // Wire streaming updates
        msgVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.Content))
                item.Content = msgVm.Content;
            else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming))
                item.IsStreaming = msgVm.IsStreaming;
        };

        _currentTurn!.Items.Add(item);
    }

    private void ProcessReasoningMessage(ChatMessageViewModel msgVm)
    {
        CloseCurrentToolGroup();
        EnsureTurn("assistant");

        var item = new ReasoningItem(NextItemId())
        {
            Content = msgVm.Content,
            IsActive = msgVm.IsStreaming,
            IsExpanded = false,
        };

        msgVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.Content))
                item.Content = msgVm.Content;
            else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming))
                item.IsActive = msgVm.IsStreaming;
        };

        _currentTurn!.Items.Add(item);
    }

    private void ProcessToolMessage(ChatMessageViewModel msgVm)
    {
        var msg = msgVm.Message;
        var toolCallId = msg.ToolCallId ?? Guid.NewGuid().ToString();
        var toolName = msg.ToolName ?? "unknown";

        // Skip hidden tools
        if (toolName is "report_intent" or "think") return;

        EnsureTurn("assistant");

        var (friendlyName, info) = ToolDisplayHelper.GetFriendlyToolDisplay(
            toolName, msg.Author, msg.Content);

        var status = msg.ToolStatus switch
        {
            "Completed" or "completed" => StrataAiToolCallStatus.Completed,
            "Failed" or "failed" => StrataAiToolCallStatus.Failed,
            _ => StrataAiToolCallStatus.InProgress,
        };

        var toolCall = new ToolCallItem(NextItemId())
        {
            ToolName = friendlyName,
            Status = status,
            IsCompact = ToolDisplayHelper.IsCompactEligible(toolName),
            InputParameters = ToolDisplayHelper.FormatToolArgsFriendly(toolName, msg.Content),
            MoreInfo = msg.ToolOutput,
        };

        _toolCallsById[toolCallId] = toolCall;

        // Wire streaming updates
        msgVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.ToolStatus))
            {
                toolCall.Status = msgVm.ToolStatus switch
                {
                    "Completed" or "completed" => StrataAiToolCallStatus.Completed,
                    "Failed" or "failed" => StrataAiToolCallStatus.Failed,
                    _ => StrataAiToolCallStatus.InProgress,
                };
                UpdateGroupMeta();
            }
        };

        // Add to current group or start a new one
        if (_currentToolGroup is not null)
        {
            _currentToolGroup.ToolCalls.Add(toolCall);
        }
        else
        {
            _currentToolGroup = new ToolGroupItem(NextItemId())
            {
                Label = $"{ToolDisplayHelper.GetToolGlyph(toolName)} {friendlyName}",
                IsActive = status == StrataAiToolCallStatus.InProgress,
                IsExpanded = false,
            };
            _currentToolGroup.ToolCalls.Add(toolCall);
            _currentTurn!.Items.Add(_currentToolGroup);
        }

        // Update group label to reflect count
        UpdateGroupMeta();
    }

    private void ProcessErrorMessage(ChatMessageViewModel msgVm)
    {
        CloseCurrentToolGroup();
        EnsureTurn("assistant");

        var item = new ErrorMessageItem(NextItemId())
        {
            Content = msgVm.Content,
            TimestampText = msgVm.TimestampText,
        };
        _currentTurn!.Items.Add(item);
    }

    // ── Group management ──

    private void CloseCurrentToolGroup()
    {
        if (_currentToolGroup is null) return;

        // If the group has only one tool, replace with SingleToolItem
        if (_currentToolGroup.ToolCalls.Count == 1 && _currentTurn is not null)
        {
            var idx = _currentTurn.Items.IndexOf(_currentToolGroup);
            if (idx >= 0)
            {
                var single = new SingleToolItem(
                    _currentToolGroup.StableId, _currentToolGroup.ToolCalls[0]);
                _currentTurn.Items[idx] = single;
            }
        }

        _currentToolGroup.IsActive = false;
        UpdateGroupMeta();
        _currentToolGroup = null;
    }

    private void UpdateGroupMeta()
    {
        if (_currentToolGroup is null) return;

        var total = _currentToolGroup.ToolCalls.Count;
        var completed = 0;
        var failed = 0;
        var inProgress = 0;

        foreach (var tc in _currentToolGroup.ToolCalls)
        {
            if (tc is ToolCallItem item)
            {
                switch (item.Status)
                {
                    case StrataAiToolCallStatus.Completed: completed++; break;
                    case StrataAiToolCallStatus.Failed: failed++; break;
                    default: inProgress++; break;
                }
            }
        }

        _currentToolGroup.IsActive = inProgress > 0;
        _currentToolGroup.ProgressValue = total > 0 && inProgress > 0
            ? (double)(completed + failed) / total * 100
            : -1;

        if (total <= 1)
            _currentToolGroup.Meta = null;
        else if (inProgress > 0)
            _currentToolGroup.Meta = $"{completed + failed}/{total}";
        else if (failed > 0)
            _currentToolGroup.Meta = $"{total} · {failed} failed";
        else
            _currentToolGroup.Meta = $"{total} done";

        // Update group label with first tool's friendly name
        if (_currentToolGroup.ToolCalls.Count > 0 &&
            _currentToolGroup.ToolCalls[0] is ToolCallItem first)
        {
            var glyph = "⚙️";
            _currentToolGroup.Label = total > 1
                ? $"{glyph} {first.ToolName} +{total - 1} more"
                : $"{glyph} {first.ToolName}";
        }
    }

    // ── Turn management ──

    private void EnsureNewTurn(string role)
    {
        _currentTurn = new TranscriptTurn($"turn-{_turnCounter++}");
        _target.Add(_currentTurn);
        _lastRole = role;
    }

    private void EnsureTurn(string role)
    {
        if (_currentTurn is null || (_lastRole == "user" && role != "user"))
        {
            _currentTurn = new TranscriptTurn($"turn-{_turnCounter++}");
            _target.Add(_currentTurn);
        }
        _lastRole = role;
    }

    private string NextItemId() => $"item-{_itemCounter++}";
}
