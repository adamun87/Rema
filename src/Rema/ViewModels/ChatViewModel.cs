using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly AzureDevOpsService _azureDevOpsService;

    private readonly TranscriptBuilder _transcriptBuilder = new();
    private readonly List<ChatMessageViewModel> _messages = [];
    private CopilotSession? _activeSession;
    private CancellationTokenSource? _sendCts;
    private TypingIndicatorItem? _typingIndicator;
    private string? _lastUserPrompt;
    private bool _activeSessionWasRecreated;

    // ── Observable Properties ──

    [ObservableProperty] private Chat? _currentChat;
    // Bind directly to the builder's ObservableCollection so ProcessMessageToTranscript()
    // updates are immediately visible in the ItemsControl without any reassignment.
    public ObservableCollection<TranscriptTurn> MountedTranscriptTurns => _transcriptBuilder.Turns;
    // Chat history list shown in sidebar — newest first.
    public ObservableCollection<Chat> Chats { get; } = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _promptText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private long _totalInputTokens;
    [ObservableProperty] private long _totalOutputTokens;
    [ObservableProperty] private long _contextCurrentTokens;
    [ObservableProperty] private long _contextTokenLimit;
    public int ChatFontSize => _dataStore.Data.Settings.FontSize;

    public bool HasTokenUsage => TotalInputTokens > 0 || TotalOutputTokens > 0;
    public string TokenUsageSummary => ContextTokenLimit > 0
        ? $"{ContextUsagePercent}%"
        : FormatTokenCount(TotalInputTokens + TotalOutputTokens);
    public string TokenUsageSuffixText => ContextTokenLimit > 0 ? "context" : "tokens";
    public string TokenInputDisplay => $"{TotalInputTokens:N0}";
    public string TokenOutputDisplay => $"{TotalOutputTokens:N0}";
    public string TokenTotalDisplay => $"{TotalInputTokens + TotalOutputTokens:N0}";
    public bool HasContextUsage => ContextTokenLimit > 0;
    public int ContextUsagePercent => ContextTokenLimit > 0
        ? (int)Math.Round(100.0 * ContextCurrentTokens / ContextTokenLimit)
        : 0;
    public string ContextUsageDisplay => ContextTokenLimit > 0
        ? $"{FormatTokenCount(ContextCurrentTokens)} / {FormatTokenCount(ContextTokenLimit)}"
        : "";

    partial void OnTotalInputTokensChanged(long value) => NotifyTokenPropertiesChanged();
    partial void OnTotalOutputTokensChanged(long value) => NotifyTokenPropertiesChanged();
    partial void OnContextCurrentTokensChanged(long value) => NotifyTokenPropertiesChanged();
    partial void OnContextTokenLimitChanged(long value) => NotifyTokenPropertiesChanged();

    private void NotifyTokenPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasTokenUsage));
        OnPropertyChanged(nameof(TokenUsageSummary));
        OnPropertyChanged(nameof(TokenUsageSuffixText));
        OnPropertyChanged(nameof(TokenInputDisplay));
        OnPropertyChanged(nameof(TokenOutputDisplay));
        OnPropertyChanged(nameof(TokenTotalDisplay));
        OnPropertyChanged(nameof(HasContextUsage));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageDisplay));
    }

    private static string FormatTokenCount(long tokens) => tokens switch
    {
        < 1_000 => $"{tokens}",
        < 1_000_000 => $"{tokens / 1_000.0:0.#}K",
        _ => $"{tokens / 1_000_000.0:0.##}M"
    };

    // Suggestion chips shown on the welcome panel
    public ObservableCollection<string> SuggestionChips { get; } =
    [
        "What's the status of all active deployments?",
        "Show recent pipeline runs for my services",
        "Which deployments need approval right now?",
        "Start a new shift and brief me",
    ];

    // ── Events ──

    public event Action? ScrollToEndRequested;
    public event Action? UserMessageSent;
    public event Action? TranscriptRebuilt;

    public ChatViewModel(DataStore dataStore, CopilotService copilotService, AzureDevOpsService azureDevOpsService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _azureDevOpsService = azureDevOpsService;
        _transcriptBuilder.ApplySettings(_dataStore.Data.Settings);

        // Populate chat history newest-first
        for (var i = _dataStore.Data.Chats.Count - 1; i >= 0; i--)
            Chats.Add(_dataStore.Data.Chats[i]);
    }

    public void RefreshSettings()
    {
        _transcriptBuilder.ApplySettings(_dataStore.Data.Settings);
        _transcriptBuilder.Rebuild(_messages);
        TranscriptRebuilt?.Invoke();
        OnPropertyChanged(nameof(ChatFontSize));
    }

    // ── Suggestion Chips ──

    [RelayCommand]
    private Task SelectSuggestion(string chip)
    {
        PromptText = chip;
        return SendMessageAsync();
    }

    // ── Send Message ──

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var prompt = PromptText?.Trim();
        if (string.IsNullOrEmpty(prompt)) return;
        // Note: no IsConnected check here — EnsureSessionAsync handles connection/reconnect

        _lastUserPrompt = prompt;
        PromptText = "";
        IsBusy = true;
        StatusText = "Thinking…";

        try
        {
            // Ensure chat exists
            if (CurrentChat is null)
            {
                var chat = new Chat
                {
                    Title = _dataStore.Data.Settings.AutoGenerateTitles
                        ? prompt.Length > 40 ? prompt[..40].Trim() + "…" : prompt
                        : "New chat",
                };
                _dataStore.Data.Chats.Add(chat);
                Chats.Insert(0, chat); // Prepend so newest appears first in sidebar
                _ = _dataStore.SaveAsync(); // Persist the new chat entry to data.json
                CurrentChat = chat;
            }

            var targetChat = CurrentChat;

            // Add user message immediately
            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = prompt,
                Author = _dataStore.Data.Settings.UserName ?? "You",
                Timestamp = DateTimeOffset.Now,
            };
            targetChat.Messages.Add(userMsg);
            var userVm = new ChatMessageViewModel(userMsg);
            _messages.Add(userVm);
            _transcriptBuilder.ProcessMessageToTranscript(userVm);
            UserMessageSent?.Invoke();
            ScrollToEndRequested?.Invoke();

            await _dataStore.SaveChatAsync(targetChat);

            // Add typing indicator
            _typingIndicator = _transcriptBuilder.AddTypingIndicator("Rema is thinking…");
            ScrollToEndRequested?.Invoke();

            var retainedContext = targetChat.Messages
                .Take(Math.Max(targetChat.Messages.Count - 1, 0))
                .ToList();

            // Create or resume session
            _sendCts?.Cancel();
            _sendCts = new CancellationTokenSource();
            var ct = _sendCts.Token;

            if (_activeSession?.SessionId != targetChat.CopilotSessionId ||
                targetChat.CopilotSessionId is null)
            {
                await EnsureSessionAsync(targetChat, ct);
            }

            if (_activeSession is null)
            {
                RemoveTypingIndicator();
                AddErrorMessage("Could not connect to Copilot. Please check your connection.");
                return;
            }

            // Send
            var sendOptions = new MessageOptions
            {
                Prompt = _activeSessionWasRecreated
                    ? BuildSessionRecoveryReplayPrompt(retainedContext, prompt)
                    : prompt
            };

            try
            {
                await _activeSession.SendAsync(sendOptions, ct);
            }
            catch (Exception ex) when (IsSessionNotFoundError(ex))
            {
                DetachPersistedSession(targetChat);
                await EnsureSessionAsync(targetChat, ct);
                if (_activeSession is null)
                    throw;

                sendOptions.Prompt = BuildSessionRecoveryReplayPrompt(retainedContext, prompt);
                await _activeSession.SendAsync(sendOptions, ct);
            }

            // Turn complete
            RemoveTypingIndicator();

            // Add model label at end of turn
            _transcriptBuilder.AddTurnModelLabel(targetChat.LastModelUsed);

            await _dataStore.SaveChatAsync(targetChat);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RemoveTypingIndicator();
            AddErrorMessage($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = "";
        }
    }

    [RelayCommand]
    private void StopGeneration()
    {
        _sendCts?.Cancel();
        RemoveTypingIndicator();
        IsBusy = false;
        IsStreaming = false;
        StatusText = "";
    }

    // ── Session Management ──

    private async Task EnsureSessionAsync(Chat chat, CancellationToken ct)
    {
        try
        {
            await _copilotService.ConnectAsync();
            var client = _copilotService.Client;
            if (client is null) return;

            // Build system prompt with active shift context
            var activeShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
            var trackedItems = activeShift is not null
                ? _dataStore.Data.TrackedItems.Where(t => t.ShiftId == activeShift.Id).ToList()
                : [];
            var systemPrompt = SystemPromptBuilder.Build(
                _dataStore.Data.Settings,
                _dataStore.Data.ServiceProjects,
                trackedItems,
                _dataStore.Data.Memories,
                _dataStore.Data.Capabilities);

            var model = !string.IsNullOrWhiteSpace(_dataStore.Data.Settings.PreferredModel)
                ? _dataStore.Data.Settings.PreferredModel
                : "claude-sonnet-4";
            var reasoningEffort = _dataStore.Data.Settings.ReasoningEffort;

            // Collect MCP servers from all enabled service projects
            var mcpServers = BuildMcpServers();
            var tools = RemaChatToolService.CreateTools(_dataStore, _azureDevOpsService);

            _activeSessionWasRecreated = false;

            if (chat.CopilotSessionId is not null)
            {
                // Resume existing session
                try
                {
                    var resumeConfig = SessionConfigBuilder.BuildForResume(
                        systemPrompt, model, reasoningEffort, tools, mcpServers, null, null);
                    var session = await client.ResumeSessionAsync(
                        chat.CopilotSessionId, resumeConfig, ct);
                    _activeSession = session;
                }
                catch (Exception ex) when (IsSessionNotFoundError(ex))
                {
                    DetachPersistedSession(chat);
                    var config = SessionConfigBuilder.Build(
                        systemPrompt, model, reasoningEffort, tools, mcpServers, null, null);
                    var session = await client.CreateSessionAsync(config, ct);
                    chat.CopilotSessionId = session.SessionId;
                    _activeSession = session;
                    _activeSessionWasRecreated = true;
                    await _dataStore.SaveAsync(ct);
                }
            }
            else
            {
                // New session
                var config = SessionConfigBuilder.Build(
                    systemPrompt, model, reasoningEffort, tools, mcpServers, null, null);
                var session = await client.CreateSessionAsync(config, ct);
                chat.CopilotSessionId = session.SessionId;
                _activeSession = session;
                await _dataStore.SaveAsync(ct);
            }

            chat.LastModelUsed = model;
            SubscribeToSession(_activeSession, chat);
            await _dataStore.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            AddErrorMessage($"Session error: {ex.Message}");
            _activeSession = null;
        }
    }

    private void SubscribeToSession(CopilotSession session, Chat chat)
    {
        var assistantContent = new StringBuilder();
        var reasoningContent = new StringBuilder();
        ChatMessageViewModel? assistantVm = null;
        ChatMessageViewModel? reasoningVm = null;

        session.On(ev =>
        {
            switch (ev)
            {
                case AssistantTurnStartEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsStreaming = true;
                        StatusText = "Generating…";
                        if (_typingIndicator is not null) _typingIndicator.Label = "Generating…";
                        RemoveTypingIndicator();
                    });
                    break;

                case AssistantReasoningDeltaEvent rd:
                    reasoningContent.Append(rd.Data.DeltaContent);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_dataStore.Data.Settings.ShowStreamingUpdates)
                        {
                            StatusText = "Thinking…";
                            return;
                        }

                        if (reasoningVm is null)
                        {
                            var msg = new ChatMessage
                            {
                                Role = "reasoning",
                                Content = reasoningContent.ToString(),
                                Author = "Thinking",
                                IsStreaming = true,
                                Timestamp = DateTimeOffset.Now,
                            };
                            reasoningVm = new ChatMessageViewModel(msg);
                            _messages.Add(reasoningVm);
                            _transcriptBuilder.ProcessMessageToTranscript(reasoningVm);
                        }
                        else
                        {
                            reasoningVm.Content = reasoningContent.ToString();
                        }
                        ScrollToEndRequested?.Invoke();
                    });
                    break;

                case AssistantReasoningEvent r:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (reasoningVm is not null)
                        {
                            reasoningVm.Content = r.Data.Content;
                            reasoningVm.IsStreaming = false;
                        }
                        else
                        {
                            var msg = new ChatMessage
                            {
                                Role = "reasoning",
                                Content = r.Data.Content,
                                Author = "Thinking",
                                Timestamp = DateTimeOffset.Now,
                            };
                            chat.Messages.Add(msg);
                            var vm = new ChatMessageViewModel(msg);
                            _messages.Add(vm);
                            _transcriptBuilder.ProcessMessageToTranscript(vm);
                        }
                        reasoningContent.Clear();
                        reasoningVm = null;
                    });
                    break;

                case AssistantMessageDeltaEvent delta:
                    assistantContent.Append(delta.Data.DeltaContent);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_dataStore.Data.Settings.ShowStreamingUpdates)
                        {
                            StatusText = "Writing…";
                            if (_typingIndicator is not null) _typingIndicator.Label = "Writing…";
                            return;
                        }

                        if (assistantVm is null)
                        {
                            var msg = new ChatMessage
                            {
                                Role = "assistant",
                                Content = assistantContent.ToString(),
                                Author = "Rema",
                                IsStreaming = true,
                                Model = chat.LastModelUsed,
                                Timestamp = DateTimeOffset.Now,
                            };
                            assistantVm = new ChatMessageViewModel(msg);
                            _messages.Add(assistantVm);
                            _transcriptBuilder.ProcessMessageToTranscript(assistantVm);
                        }
                        else
                        {
                            assistantVm.Content = assistantContent.ToString();
                        }
                        StatusText = "Writing…";
                        if (_typingIndicator is not null) _typingIndicator.Label = "Writing…";
                        ScrollToEndRequested?.Invoke();
                    });
                    break;

                case AssistantMessageEvent am:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (assistantVm is not null)
                        {
                            assistantVm.Content = am.Data.Content;
                            assistantVm.IsStreaming = false;
                            assistantVm.Message.Content = am.Data.Content;
                            assistantVm.Message.IsStreaming = false;
                        }
                        else
                        {
                            var msg = new ChatMessage
                            {
                                Role = "assistant",
                                Content = am.Data.Content,
                                Author = "Rema",
                                Model = chat.LastModelUsed,
                                Timestamp = DateTimeOffset.Now,
                            };
                            assistantVm = new ChatMessageViewModel(msg);
                            _messages.Add(assistantVm);
                            _transcriptBuilder.ProcessMessageToTranscript(assistantVm);
                        }

                        chat.Messages.Add(assistantVm.Message);
                        assistantContent.Clear();
                        assistantVm = null;
                        ScrollToEndRequested?.Invoke();
                    });
                    break;

                case ToolExecutionStartEvent toolStart:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var (friendlyName, _) = ToolDisplayHelper.GetFriendlyToolDisplay(
                            toolStart.Data.ToolName,
                            null,
                            toolStart.Data.Arguments?.ToString());

                        StatusText = friendlyName;

                        var msg = new ChatMessage
                        {
                            Role = "tool",
                            ToolName = toolStart.Data.ToolName,
                            ToolCallId = toolStart.Data.ToolCallId,
                            ToolStatus = "InProgress",
                            Content = toolStart.Data.Arguments?.ToString() ?? "",
                            Author = friendlyName,
                            Timestamp = DateTimeOffset.Now,
                        };
                        var vm = new ChatMessageViewModel(msg);
                        _messages.Add(vm);
                        _transcriptBuilder.ProcessMessageToTranscript(vm);
                        ScrollToEndRequested?.Invoke();
                    });
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var status = toolComplete.Data.Success
                            ? StrataTheme.Controls.StrataAiToolCallStatus.Completed
                            : StrataTheme.Controls.StrataAiToolCallStatus.Failed;

                        _transcriptBuilder.UpdateToolStatus(
                            toolComplete.Data.ToolCallId,
                            status);

                        // Persist tool result — carry ToolName so transcript can rebuild correctly on reload
                        var startRecord = _messages
                            .Select(m => m.Message)
                            .FirstOrDefault(m => m.ToolCallId == toolComplete.Data.ToolCallId
                                                 && m.ToolName is not null);
                        var resultMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolComplete.Data.ToolCallId,
                            ToolName = startRecord?.ToolName,
                            Content = startRecord?.Content ?? "",
                            Author = startRecord?.Author,
                            ToolStatus = toolComplete.Data.Success ? "Completed" : "Failed",
                            Timestamp = DateTimeOffset.Now,
                        };
                        chat.Messages.Add(resultMsg);

                        StatusText = "Thinking…";
                    });
                    break;

                case SessionIdleEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsStreaming = false;
                        StatusText = "";
                    });
                    break;

                case AssistantUsageEvent usage:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var d = usage.Data;
                        var turnInput = (long)(d.InputTokens ?? 0);
                        TotalInputTokens += turnInput;
                        TotalOutputTokens += (long)(d.OutputTokens ?? 0);
                        if (turnInput > 0)
                            ContextCurrentTokens = turnInput;
                        chat.TotalInputTokens = TotalInputTokens;
                        chat.TotalOutputTokens = TotalOutputTokens;
                    });
                    break;

                case SessionUsageInfoEvent usageInfo:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var d = usageInfo.Data;
                        if (d.TokenLimit > 0)
                            ContextTokenLimit = (long)d.TokenLimit;
                        if (d.CurrentTokens > 0)
                            ContextCurrentTokens = (long)d.CurrentTokens;
                    });
                    break;
            }
        });
    }

    // ── Chat Loading ──

    public async Task LoadChatAsync(Chat chat)
    {
        CurrentChat = chat;
        _messages.Clear();
        _activeSession = null;

        TotalInputTokens = chat.TotalInputTokens;
        TotalOutputTokens = chat.TotalOutputTokens;
        ContextCurrentTokens = 0;
        ContextTokenLimit = 0;

        await _dataStore.LoadChatMessagesAsync(chat);

        foreach (var msg in chat.Messages)
            _messages.Add(new ChatMessageViewModel(msg));

        _transcriptBuilder.Rebuild(_messages);
        TranscriptRebuilt?.Invoke();
        ScrollToEndRequested?.Invoke();
    }

    [RelayCommand]
    public void NewChat()
    {
        CurrentChat = null;
        _messages.Clear();
        _activeSession = null;
        _transcriptBuilder.Reset();
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        ContextCurrentTokens = 0;
        ContextTokenLimit = 0;
        TranscriptRebuilt?.Invoke();
    }

    [RelayCommand]
    private Task SelectChat(Chat chat) => LoadChatAsync(chat);

    // ── Retry ──

    [RelayCommand]
    private async Task RetryLastMessage()
    {
        if (string.IsNullOrEmpty(_lastUserPrompt)) return;
        PromptText = _lastUserPrompt;
        await SendMessageAsync();
    }

    // ── MCP Servers ──

    private Dictionary<string, object>? BuildMcpServers()
    {
        var servers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _dataStore.Data.ServiceProjects)
        {
            AddMcpServer(project.McpServer, servers);
            foreach (var mcp in project.McpServers)
                AddMcpServer(mcp, servers);
        }
        return servers.Count > 0 ? servers : null;
    }

    private static void AddMcpServer(McpServerConfig? mcp, Dictionary<string, object> servers)
    {
        if (mcp is not { IsEnabled: true } || string.IsNullOrWhiteSpace(mcp.Name))
            return;

        var key = mcp.Name.Trim();
        if (mcp.ServerType == "local" || mcp.ServerType == "stdio")
        {
            // Use Dictionary instead of anonymous types — the Copilot SDK serializes
            // SessionConfig with its own source-generated JsonSerializerContext which
            // cannot handle anonymous types.
            servers[key] = new Dictionary<string, object>
            {
                ["type"] = "stdio",
                ["command"] = mcp.Command,
                ["args"] = mcp.Args,
                ["env"] = mcp.Env.Count > 0 ? mcp.Env : new Dictionary<string, string>(),
            };
        }
        else
        {
            servers[key] = new Dictionary<string, object>
            {
                ["type"] = "sse",
                ["url"] = mcp.Url,
                ["headers"] = mcp.Headers.Count > 0 ? mcp.Headers : new Dictionary<string, string>(),
            };
        }
    }

    // ── Helpers ──

    private void RemoveTypingIndicator()
    {
        if (_typingIndicator is not null)
        {
            _transcriptBuilder.RemoveTypingIndicator(_typingIndicator);
            _typingIndicator = null;
        }
    }

    private void AddErrorMessage(string message)
    {
        var msg = new ChatMessage
        {
            Role = "error",
            Content = message,
            Timestamp = DateTimeOffset.Now,
        };
        var vm = new ChatMessageViewModel(msg);
        _messages.Add(vm);
        // The transcript builder will create the ErrorMessageItem; wire retry afterward
        _transcriptBuilder.ProcessMessageToTranscript(vm, RetryLastMessageCommand);
    }

    private void DetachPersistedSession(Chat chat)
    {
        if (string.Equals(_activeSession?.SessionId, chat.CopilotSessionId, StringComparison.Ordinal))
            _activeSession = null;

        chat.CopilotSessionId = null;
    }

    private static bool IsSessionNotFoundError(Exception ex)
    {
        var message = FlattenExceptionMessages(ex);
        return message.Contains("Session not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var builder = new StringBuilder(ex.Message);
        var current = ex.InnerException;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                builder.Append(" ").Append(current.Message);
            current = current.InnerException;
        }

        return builder.ToString();
    }

    private static string BuildSessionRecoveryReplayPrompt(List<ChatMessage> retainedContext, string latestPrompt)
    {
        if (retainedContext.Count == 0)
            return latestPrompt;

        var lines = new List<string>
        {
            "The previous backend chat session is unavailable. Continue using ONLY the conversation context below.",
            "Treat the transcript as the complete conversation history so far.",
            "",
            "Conversation so far:"
        };

        foreach (var msg in retainedContext)
        {
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;

            var role = msg.Role switch
            {
                "assistant" => "Assistant",
                "system" => "System",
                "reasoning" => "Assistant reasoning",
                _ => "User"
            };

            if (msg.Role is "user" or "assistant" or "system" or "reasoning")
                lines.Add($"{role}: {msg.Content.Trim()}");
        }

        lines.Add("");
        lines.Add("Latest user message:");
        lines.Add(latestPrompt);

        return string.Join("\n", lines);
    }

    private static string? TruncateToolOutput(string? output)
    {
        if (output is null) return null;
        return output.Length > 500 ? output[..497] + "…" : output;
    }

    public static string? FormatModelDisplay(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        // "claude-sonnet-4" → "Sonnet 4"
        var parts = model.Split('-');
        if (parts.Length >= 2)
        {
            var family = parts.Length >= 2 ? parts[^2] : parts[^1];
            var version = parts[^1];
            return $"{char.ToUpper(family[0])}{family[1..]} {version}";
        }
        return model;
    }
}

// ── ChatMessageViewModel ──

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _toolStatus;

    public string Role => Message.Role;
    public string? Author => Message.Author;
    public string TimestampText => Message.Timestamp.ToString("HH:mm");
    public string? ToolName => Message.ToolName;

    public ChatMessageViewModel(ChatMessage message)
    {
        Message = message;
        _content = message.Content;
        _isStreaming = message.IsStreaming;
        _toolStatus = message.ToolStatus;
    }
}
