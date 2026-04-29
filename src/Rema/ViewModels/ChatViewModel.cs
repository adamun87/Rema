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
    private int _questionCounter;

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

    // Post-turn suggestion chips on the composer
    [ObservableProperty] private string _suggestionA = "";
    [ObservableProperty] private string _suggestionB = "";
    [ObservableProperty] private string _suggestionC = "";
    [ObservableProperty] private bool _isSuggestionsGenerating;

    // ── Chat Search (Ctrl+F) ──
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchResultText = "";

    // ── Voice Input ──
    [ObservableProperty] private bool _isRecording;
    private VoiceInputService? _voiceService;

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
        _transcriptBuilder.OnUserEditConfirmed = OnUserEditConfirmedAsync;

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
        SuggestionA = "";
        SuggestionB = "";
        SuggestionC = "";
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
            var tools = RemaChatToolService.CreateTools(_dataStore, _azureDevOpsService, _copilotService);

            _activeSessionWasRecreated = false;
            UserInputHandler userInputHandler = HandleUserInputAsync;

            if (chat.CopilotSessionId is not null)
            {
                // Resume existing session
                try
                {
                    var resumeConfig = SessionConfigBuilder.BuildForResume(
                        systemPrompt, model, reasoningEffort, tools, mcpServers, null, null,
                        userInputHandler);
                    var session = await client.ResumeSessionAsync(
                        chat.CopilotSessionId, resumeConfig, ct);
                    _activeSession = session;
                }
                catch (Exception ex) when (IsSessionNotFoundError(ex))
                {
                    DetachPersistedSession(chat);
                    var config = SessionConfigBuilder.Build(
                        systemPrompt, model, reasoningEffort, tools, mcpServers, null, null,
                        userInputHandler);
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
                    systemPrompt, model, reasoningEffort, tools, mcpServers, null, null,
                    userInputHandler);
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
                        // Persist tool result — carry ToolName so transcript can rebuild correctly on reload
                        var startRecord = _messages
                            .Select(m => m.Message)
                            .FirstOrDefault(m => m.ToolCallId == toolComplete.Data.ToolCallId
                                                 && m.ToolName is not null);

                        // Track file changes for the summary card
                        if (startRecord?.ToolName is "edit" or "edit_file" or "create" or "create_file"
                            && toolComplete.Data.Success)
                        {
                            var filePath = ToolDisplayHelper.ExtractJsonField(
                                startRecord.Content, "path")
                                ?? ToolDisplayHelper.ExtractJsonField(
                                    startRecord.Content, "file_path");
                            if (filePath is not null)
                            {
                                var isCreate = startRecord.ToolName is "create" or "create_file";
                                var oldText = ToolDisplayHelper.ExtractJsonField(
                                    startRecord.Content, "old_str");
                                var newText = ToolDisplayHelper.ExtractJsonField(
                                    startRecord.Content, "new_str")
                                    ?? ToolDisplayHelper.ExtractJsonField(
                                        startRecord.Content, "file_text");
                                _transcriptBuilder.AddFileChange(filePath, isCreate, oldText, newText);
                            }
                        }
                        else
                        {
                            // Non-file tool → close any pending file changes summary
                            _transcriptBuilder.CloseCurrentFileChanges();
                        }

                        var status = toolComplete.Data.Success
                            ? StrataTheme.Controls.StrataAiToolCallStatus.Completed
                            : StrataTheme.Controls.StrataAiToolCallStatus.Failed;

                        _transcriptBuilder.UpdateToolStatus(
                            toolComplete.Data.ToolCallId,
                            status);

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
                        _ = GenerateSuggestionsAsync();
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

                // ── Sub-agent lifecycle ──

                case SubagentStartedEvent subStart:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var d = subStart.Data;
                        var item = new SubagentToolCallItem($"subagent-{d.ToolCallId}")
                        {
                            DisplayName = d.AgentDisplayName ?? d.AgentName ?? "Sub-agent",
                            TaskDescription = d.AgentDescription,
                            Status = StrataTheme.Controls.StrataAiToolCallStatus.InProgress,
                            IsExpanded = true,
                        };
                        _transcriptBuilder.AddSubagent(d.ToolCallId, item);
                        ScrollToEndRequested?.Invoke();
                    });
                    break;

                case SubagentCompletedEvent subComplete:
                    Dispatcher.UIThread.Post(() =>
                    {
                        _transcriptBuilder.UpdateSubagentStatus(
                            subComplete.Data.ToolCallId,
                            StrataTheme.Controls.StrataAiToolCallStatus.Completed);
                    });
                    break;

                case SubagentFailedEvent subFailed:
                    Dispatcher.UIThread.Post(() =>
                    {
                        _transcriptBuilder.UpdateSubagentStatus(
                            subFailed.Data.ToolCallId,
                            StrataTheme.Controls.StrataAiToolCallStatus.Failed,
                            subFailed.Data.Error);
                    });
                    break;

                // UserInputRequestedEvent is NOT used — we register a
                // handler via session.RegisterUserInputHandler() instead
                // so the SDK gets a response back.
            }
        });
    }

    private Task<UserInputResponse> HandleUserInputAsync(
        UserInputRequest request, UserInputInvocation _)
    {
        var tcs = new TaskCompletionSource<UserInputResponse>();
        Dispatcher.UIThread.Post(() =>
        {
            var questionItem = new QuestionItem(
                request.Question,
                request.Question,
                request.Choices?.ToList(),
                request.AllowFreeform ?? true,
                (__, answer) => tcs.TrySetResult(new UserInputResponse
                {
                    Answer = answer,
                }),
                $"question-{_questionCounter++}");
            _transcriptBuilder.AddDirectItem(questionItem);
            ScrollToEndRequested?.Invoke();
        });
        return tcs.Task;
    }

    // ── Post-Turn Suggestions ──

    private async Task GenerateSuggestionsAsync()
    {
        if (CurrentChat is null) return;

        SuggestionA = "";
        SuggestionB = "";
        SuggestionC = "";
        IsSuggestionsGenerating = true;

        try
        {
            var lastMessages = CurrentChat.Messages.TakeLast(4)
                .Select(m => $"{m.Role}: {(m.Content?.Length > 200 ? m.Content[..200] : m.Content)}")
                .ToList();
            var context = string.Join("\n", lastMessages);

            var fastModel = await _copilotService.GetFastestModelIdAsync();
            var result = await _copilotService.UseLightweightSessionAsync(
                new LightweightSessionOptions
                {
                    SystemPrompt = "Generate exactly 3 short follow-up suggestions (max 8 words each) for a chat conversation. Return ONLY 3 lines, one suggestion per line. No numbering, no bullets, no quotes.",
                    Model = fastModel,
                    Streaming = false,
                },
                async (session, ct) =>
                {
                    var response = await session.SendAndWaitAsync(
                        new MessageOptions { Prompt = context },
                        TimeSpan.FromSeconds(15), ct);
                    return response?.Data?.Content ?? "";
                });

            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Dispatcher.UIThread.Post(() =>
            {
                SuggestionA = lines.Length > 0 ? lines[0] : "";
                SuggestionB = lines.Length > 1 ? lines[1] : "";
                SuggestionC = lines.Length > 2 ? lines[2] : "";
                IsSuggestionsGenerating = false;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() => IsSuggestionsGenerating = false);
        }
    }

    // ── Chat Loading ──

    public async Task LoadChatAsync(Chat chat)
    {
        CurrentChat = chat;
        _messages.Clear();
        _activeSession = null;
        _questionCounter = 0;

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
        _questionCounter = 0;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        ContextCurrentTokens = 0;
        ContextTokenLimit = 0;
        SuggestionA = "";
        SuggestionB = "";
        SuggestionC = "";
        TranscriptRebuilt?.Invoke();
    }

    [RelayCommand]
    private Task SelectChat(Chat chat) => LoadChatAsync(chat);

    // ── Edit + Resend ──

    private async void OnUserEditConfirmedAsync(ChatMessage originalMsg, string editedText)
    {
        if (CurrentChat is null || string.IsNullOrWhiteSpace(editedText)) return;

        // Truncate chat messages after (and including) the edited message
        var idx = CurrentChat.Messages.IndexOf(originalMsg);
        if (idx < 0) return;

        // Remove all messages from the edited one onward
        while (CurrentChat.Messages.Count > idx)
            CurrentChat.Messages.RemoveAt(CurrentChat.Messages.Count - 1);

        // Detach the session so a new one will be created with fresh context
        DetachPersistedSession(CurrentChat);

        // Rebuild internal message list to match
        _messages.Clear();
        foreach (var msg in CurrentChat.Messages)
            _messages.Add(new ChatMessageViewModel(msg));

        _transcriptBuilder.Rebuild(_messages);
        TranscriptRebuilt?.Invoke();

        // Send the edited text as a new message
        PromptText = editedText;
        await SendMessageAsync();
    }

    // ── Retry ──

    [RelayCommand]
    private async Task RetryLastMessage()
    {
        if (string.IsNullOrEmpty(_lastUserPrompt)) return;
        PromptText = _lastUserPrompt;
        await SendMessageAsync();
    }

    // ── Chat Search (Ctrl+F) ──

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen)
        {
            SearchQuery = "";
            SearchResultText = "";
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SearchResultText = "";
            return;
        }

        var count = 0;
        foreach (var turn in MountedTranscriptTurns)
        {
            foreach (var item in turn.Items)
            {
                var text = item switch
                {
                    UserMessageItem u => u.Content,
                    AssistantMessageItem a => a.Content,
                    ErrorMessageItem e => e.Content,
                    ReasoningItem r => r.Content,
                    _ => null
                };
                if (text is not null)
                    count += CountOccurrences(text, value);
            }
        }
        SearchResultText = count > 0 ? $"{count} match{(count == 1 ? "" : "es")}" : "No matches";
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    // ── Voice Input ──

    [RelayCommand]
    private void ToggleVoice()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (IsRecording)
        {
            _voiceService?.StopListening();
            IsRecording = false;
        }
        else
        {
            _voiceService ??= new VoiceInputService();
            _voiceService.TextRecognized += text =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PromptText += (string.IsNullOrEmpty(PromptText) ? "" : " ") + text;
                });
            };
            _voiceService.StartListening();
            IsRecording = true;
        }
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
