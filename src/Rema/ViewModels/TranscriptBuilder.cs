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
    private const int DefaultPageSize = 30;
    private readonly List<TranscriptTurn> _allTurns = [];
    private int _mountedFromIndex;

    private readonly ObservableCollection<TranscriptTurn> _target = [];

    /// <summary>
    /// The live collection that powers the transcript ItemsControl.
    /// ChatViewModel binds MountedTranscriptTurns directly to this reference.
    /// </summary>
    public ObservableCollection<TranscriptTurn> Turns => _target;

    public bool HasOlderTurns { get; private set; }

    private TranscriptTurn? _currentTurn;
    private ToolGroupItem? _currentToolGroup;
    private readonly Dictionary<string, ToolCallItemBase> _toolCallsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentToolCallItem> _subagentsById = new(StringComparer.Ordinal);
    private FileChangesSummaryItem? _currentFileChanges;
    private string? _lastRole;
    private int _turnCounter;
    private int _itemCounter;
    private RemaSettings _settings = new();

    /// <summary>
    /// Callback set by ChatViewModel — invoked when a user confirms an inline edit
    /// on a previous message. Parameters: (ChatMessage originalMsg, string editedText).
    /// </summary>
    public Action<ChatMessage, string>? OnUserEditConfirmed { get; set; }

    public void ApplySettings(RemaSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Clears and rebuilds <see cref="Turns"/> in-place from a message list.
    /// The ItemsControl binding stays live because the ObservableCollection reference never changes.
    /// </summary>
    public void Rebuild(IReadOnlyList<ChatMessageViewModel> messages)
    {
        _target.Clear();
        _currentTurn = null;
        _currentToolGroup = null;
        _currentFileChanges = null;
        _toolCallsById.Clear();
        _subagentsById.Clear();
        _lastRole = null;
        _turnCounter = 0;
        _itemCounter = 0;
        _allTurns.Clear();
        _mountedFromIndex = 0;

        foreach (var msg in messages)
            ProcessMessageToTranscript(msg);

        CloseCurrentToolGroup();
        CloseCurrentFileChanges();

        // If there are many turns from history, only mount the recent page
        // to avoid rendering overhead. The user can load older ones.
        if (_target.Count > DefaultPageSize)
        {
            _allTurns.Clear();
            _allTurns.AddRange(_target);
            _target.Clear();
            _mountedFromIndex = Math.Max(0, _allTurns.Count - DefaultPageSize);
            for (var i = _mountedFromIndex; i < _allTurns.Count; i++)
                _target.Add(_allTurns[i]);
            HasOlderTurns = _mountedFromIndex > 0;
        }
        else
        {
            _allTurns.Clear();
            _allTurns.AddRange(_target);
            HasOlderTurns = false;
        }
    }

    /// <summary>Resets the transcript to empty (used when starting a new chat).</summary>
    public void Reset()
    {
        _target.Clear();
        _currentTurn = null;
        _currentToolGroup = null;
        _currentFileChanges = null;
        _toolCallsById.Clear();
        _subagentsById.Clear();
        _lastRole = null;
        _turnCounter = 0;
        _itemCounter = 0;
        _allTurns.Clear();
        _mountedFromIndex = 0;
        HasOlderTurns = false;
    }

    public void LoadOlderTurns(int count = 20)
    {
        if (_mountedFromIndex <= 0 || _allTurns.Count == 0) return;

        var loadFrom = Math.Max(0, _mountedFromIndex - count);
        var turnsToInsert = _allTurns.GetRange(loadFrom, _mountedFromIndex - loadFrom);
        _mountedFromIndex = loadFrom;

        for (var i = turnsToInsert.Count - 1; i >= 0; i--)
            _target.Insert(0, turnsToInsert[i]);

        HasOlderTurns = _mountedFromIndex > 0;
    }

    public void ProcessMessageToTranscript(ChatMessageViewModel msgVm,
        System.Windows.Input.ICommand? errorRetryCommand = null)
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
                ProcessErrorMessage(msgVm, errorRetryCommand);
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

    // ── Direct items (bypass tool groups) ──

    /// <summary>Adds a standalone transcript item to the current turn (e.g. QuestionItem).</summary>
    public void AddDirectItem(TranscriptItem item)
    {
        CloseCurrentToolGroup();
        EnsureTurn("assistant");
        _currentTurn!.Items.Add(item);
    }

    // ── Sub-agent tracking ──

    public void AddSubagent(string toolCallId, SubagentToolCallItem item)
    {
        _subagentsById[toolCallId] = item;
        CloseCurrentToolGroup();
        EnsureTurn("assistant");
        _currentTurn!.Items.Add(item);
    }

    public void UpdateSubagentStatus(string toolCallId, StrataAiToolCallStatus status,
        string? error = null)
    {
        if (!_subagentsById.TryGetValue(toolCallId, out var item)) return;
        item.Status = status;
        if (error is not null) item.Meta = error;
    }

    // ── File changes tracking ──

    public void AddFileChange(string filePath, bool isCreate, string? oldText, string? newText)
    {
        EnsureTurn("assistant");

        if (_currentFileChanges is null)
        {
            _currentFileChanges = new FileChangesSummaryItem(NextItemId())
            {
                Label = "File changes",
            };
            _currentTurn!.Items.Add(_currentFileChanges);
        }

        // Find or create a FileChangeItem for this path
        var existing = _currentFileChanges.FileChanges
            .FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.AddEdit(oldText, newText);
        }
        else
        {
            var fci = new FileChangeItem(filePath, isCreate);
            fci.AddEdit(oldText, newText);
            _currentFileChanges.FileChanges.Add(fci);
        }

        UpdateFileChangesSummary();
    }

    public void CloseCurrentFileChanges()
    {
        _currentFileChanges = null;
    }

    private void UpdateFileChangesSummary()
    {
        if (_currentFileChanges is null) return;

        var totalAdded = _currentFileChanges.FileChanges.Sum(f => f.LinesAdded);
        var totalRemoved = _currentFileChanges.FileChanges.Sum(f => f.LinesRemoved);

        _currentFileChanges.Label = _currentFileChanges.FileChanges.Count == 1
            ? "1 file changed"
            : $"{_currentFileChanges.FileChanges.Count} files changed";
        _currentFileChanges.TotalStatsAdded = $"+{totalAdded}";
        _currentFileChanges.TotalStatsRemoved = totalRemoved > 0 ? $"−{totalRemoved}" : "";
        _currentFileChanges.HasTotalRemovals = totalRemoved > 0;
    }

    // ── Update tool status ──

    public void UpdateToolStatus(string toolCallId, StrataAiToolCallStatus status,
        double durationMs = 0, string? moreInfo = null)
    {
        if (!_toolCallsById.TryGetValue(toolCallId, out var tcBase)) return;

        if (tcBase is ToolCallItem tc)
        {
            tc.Status = status;
            if (durationMs > 0) tc.DurationMs = durationMs;
            if (moreInfo is not null) tc.MoreInfo = moreInfo;
        }
        else if (tcBase is TerminalPreviewItem tp)
        {
            tp.Status = status;
            if (durationMs > 0) tp.DurationMs = durationMs;
        }

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
            TimestampText = TimestampOrEmpty(msgVm),
        };

        // Wire inline-edit callback
        var msg = msgVm.Message;
        item.OnEditConfirmed = (editedText) => OnUserEditConfirmed?.Invoke(msg, editedText);

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
            TimestampText = TimestampOrEmpty(msgVm),
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
        if (!_settings.ShowReasoning)
            return;

        CloseCurrentToolGroup();
        EnsureTurn("assistant");

        var item = new ReasoningItem(NextItemId())
        {
            Content = msgVm.Content,
            IsActive = msgVm.IsStreaming,
            IsExpanded = msgVm.IsStreaming && _settings.ExpandReasoningWhileStreaming,
        };

        msgVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.Content))
                item.Content = msgVm.Content;
            else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming))
            {
                item.IsActive = msgVm.IsStreaming;
                if (!msgVm.IsStreaming)
                    item.IsExpanded = false;
            }
        };

        _currentTurn!.Items.Add(item);
    }

    private void ProcessToolMessage(ChatMessageViewModel msgVm)
    {
        if (!_settings.ShowToolCalls)
            return;

        var msg = msgVm.Message;
        var toolCallId = msg.ToolCallId ?? Guid.NewGuid().ToString();
        var toolName = msg.ToolName;

        // Skip result-only records (no ToolName) and hidden tools
        if (string.IsNullOrEmpty(toolName) || toolName is "report_intent" or "think") return;

        EnsureTurn("assistant");

        var (friendlyName, info) = ToolDisplayHelper.GetFriendlyToolDisplay(
            toolName, msg.Author, msg.Content);

        var status = msg.ToolStatus switch
        {
            "Completed" or "completed" => StrataAiToolCallStatus.Completed,
            "Failed" or "failed" => StrataAiToolCallStatus.Failed,
            _ => StrataAiToolCallStatus.InProgress,
        };

        // ── File attachment (announce_file) ──
        if (toolName is "announce_file")
        {
            CloseCurrentToolGroup();
            var filePath = ToolDisplayHelper.ExtractJsonField(msg.Content, "path")
                ?? ToolDisplayHelper.ExtractJsonField(msg.Content, "file_path") ?? "";
            var attachment = new FileAttachmentItem(NextItemId())
            {
                FileName = System.IO.Path.GetFileName(filePath),
                FilePath = filePath,
            };
            _currentTurn!.Items.Add(attachment);
            return;
        }

        // ── File changes tracking ──
        if (toolName is "edit" or "edit_file" or "create" or "create_file"
            && status is StrataAiToolCallStatus.Completed or StrataAiToolCallStatus.InProgress)
        {
            var filePath = ToolDisplayHelper.ExtractJsonField(msg.Content, "path")
                ?? ToolDisplayHelper.ExtractJsonField(msg.Content, "file_path");
            if (filePath is not null)
            {
                var isCreate = toolName is "create" or "create_file";
                var oldText = ToolDisplayHelper.ExtractJsonField(msg.Content, "old_str");
                var newText = ToolDisplayHelper.ExtractJsonField(msg.Content, "new_str")
                    ?? ToolDisplayHelper.ExtractJsonField(msg.Content, "file_text");
                AddFileChange(filePath, isCreate, oldText, newText);
            }
            // Still show in tool group too — fall through
        }
        else
        {
            // Non-file tool encountered → close any pending file changes summary
            CloseCurrentFileChanges();
        }

        // ── Terminal preview (shell tools) ──
        if (toolName is "powershell" or "bash" or "shell" or "read_powershell" or "write_powershell")
        {
            var command = ToolDisplayHelper.ExtractJsonField(msg.Content, "command")
                ?? ToolDisplayHelper.ExtractJsonField(msg.Content, "input") ?? "";
            var terminal = new TerminalPreviewItem(NextItemId())
            {
                ToolName = friendlyName,
                Command = command,
                Output = msg.ToolOutput ?? "",
                Status = status,
                IsExpanded = false,
            };

            _toolCallsById[toolCallId] = terminal;

            // Wire streaming updates
            msgVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessageViewModel.ToolStatus))
                {
                    terminal.Status = msgVm.ToolStatus switch
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
                _currentToolGroup.ToolCalls.Add(terminal);
            }
            else
            {
                _currentToolGroup = new ToolGroupItem(NextItemId())
                {
                    Label = $"{ToolDisplayHelper.GetToolGlyph(toolName)} {friendlyName}",
                    IsActive = status == StrataAiToolCallStatus.InProgress,
                    IsExpanded = false,
                };
                _currentToolGroup.ToolCalls.Add(terminal);
                _currentTurn!.Items.Add(_currentToolGroup);
            }

            UpdateGroupMeta();
            return;
        }

        // ── Default tool call ──
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

    private void ProcessErrorMessage(ChatMessageViewModel msgVm,
        System.Windows.Input.ICommand? retryCommand = null)
    {
        CloseCurrentToolGroup();
        EnsureTurn("assistant");

        var item = new ErrorMessageItem(NextItemId())
        {
            Content = msgVm.Content,
            TimestampText = TimestampOrEmpty(msgVm),
            RetryCommand = retryCommand,
        };
        _currentTurn!.Items.Add(item);
    }

    // ── Group management ──

    internal void CloseCurrentToolGroup()
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
            var itemStatus = tc switch
            {
                ToolCallItem tci => tci.Status,
                TerminalPreviewItem tpi => tpi.Status,
                _ => StrataAiToolCallStatus.InProgress,
            };
            switch (itemStatus)
            {
                case StrataAiToolCallStatus.Completed: completed++; break;
                case StrataAiToolCallStatus.Failed: failed++; break;
                default: inProgress++; break;
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
        if (_currentToolGroup.ToolCalls.Count > 0)
        {
            var glyph = "⚙️";
            var firstName = _currentToolGroup.ToolCalls[0] switch
            {
                ToolCallItem tci => tci.ToolName,
                TerminalPreviewItem tpi => tpi.ToolName,
                _ => "",
            };
            _currentToolGroup.Label = total > 1
                ? $"{glyph} {firstName} +{total - 1} more"
                : $"{glyph} {firstName}";
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

    private string TimestampOrEmpty(ChatMessageViewModel msgVm) =>
        _settings.ShowTimestamps ? msgVm.TimestampText : "";
}
