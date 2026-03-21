using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelLeakTests
{
    [Fact]
    public void ReleaseInactiveChatState_ClearsDetachedChatResources()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var inactiveChat = new Chat { Title = "inactive" };
        inactiveChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(inactiveChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[inactiveChat.Id] = new ChatRuntimeState
        {
            Chat = inactiveChat,
            HasUsedBrowser = true
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[inactiveChat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[inactiveChat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[inactiveChat.Id] =
            new ChatMessage { Role = "assistant", Content = "streaming" };
        GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats").Add(inactiveChat.Id);
        GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat")[inactiveChat.Id] = Guid.NewGuid();
        GetField<Dictionary<Guid, BrowserService>>(vm, "_chatBrowserServices")[inactiveChat.Id] = new BrowserService();

        InvokePrivate(vm, "ReleaseInactiveChatState", inactiveChat, true);

        Assert.Empty(inactiveChat.Messages);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(inactiveChat.Id));
        Assert.DoesNotContain(inactiveChat.Id, GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats"));
        Assert.DoesNotContain(inactiveChat.Id, GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat").Keys);
        Assert.False(GetField<Dictionary<Guid, BrowserService>>(vm, "_chatBrowserServices").ContainsKey(inactiveChat.Id));
    }

    [Fact]
    public void ReleaseInactiveChatState_LeavesBusyChatAttached()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var busyChat = new Chat { Title = "busy" };
        busyChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(busyChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[busyChat.Id] = new ChatRuntimeState
        {
            Chat = busyChat,
            IsBusy = true
        };
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[busyChat.Id] = subscription;

        InvokePrivate(vm, "ReleaseInactiveChatState", busyChat, true);

        Assert.Single(busyChat.Messages);
        Assert.Equal(0, subscription.DisposeCount);
        Assert.True(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(busyChat.Id));
        Assert.True(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(busyChat.Id));
    }

    [Fact]
    public void ReleaseInactiveChatState_DoesNotCreateRuntimeStateForUnknownChat()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var detachedChat = new Chat { Title = "detached" };
        detachedChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(detachedChat);
        vm.CurrentChat = activeChat;

        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat, true);

        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
    }

    [Fact]
    public void CancelPendingQuestions_RemovesTrackedQuestionTasks()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "question-chat" };
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-1" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-2" });

        var pendingQuestions = GetField<Dictionary<string, TaskCompletionSource<string>>>(vm, "_pendingQuestions");
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingQuestions["q-1"] = first;
        pendingQuestions["q-2"] = second;

        InvokePrivate(vm, "CancelPendingQuestions", chat);

        Assert.True(first.Task.IsCanceled);
        Assert.True(second.Task.IsCanceled);
        Assert.Empty(pendingQuestions);
    }

    [Fact]
    public void ResetAfterCopilotReconnect_ClearsTransientRuntimeState()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "recoverable-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            StatusText = "busy"
        };

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[chat.Id] =
            new ChatMessage { Role = "assistant", Content = "partial" };

        InvokePrivate(vm, "ResetAfterCopilotReconnect");

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void DetachSessionAfterRemoteShutdown_PreservesPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "recoverable-chat",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;

        InvokePrivate(vm, "DetachSessionAfterRemoteShutdown", chat, true);

        Assert.Equal("session-123", chat.CopilotSessionId);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void InvalidateCurrentSession_ClearsPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "fresh-session",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        InvokePrivate(vm, "InvalidateCurrentSession");

        Assert.Null(chat.CopilotSessionId);
    }

    [Fact]
    public void IsCopilotTransportError_DetectsJsonRpcDisconnect()
    {
        var ex = new Exception(
            "Communication error with Copilot CLI",
            new IOException("The JSON-RPC connection with the remote party was lost before the request could complete."));

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.True(result);
    }

    [Fact]
    public void IsCopilotTransportError_IgnoresUnrelatedExceptions()
    {
        var ex = new InvalidOperationException("Session not found");

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerIsMissingLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.False(analysis.UserMessageObserved);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerAlreadyRecordedLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first"),
            CreateUserMessageEvent("second")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.True(analysis.UserMessageObserved);
    }

    [Fact]
    public void GetRecoveredAssistantMessages_ReturnsOnlyMissingTopLevelMessages()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("continue"),
            CreateAssistantMessageEvent("msg-1", "First reply"),
            CreateAssistantMessageEvent("tool-1", "Tool transcript", parentToolCallId: "call-1"),
            CreateAssistantMessageEvent("msg-2", "Second reply")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);
        var result = analysis.AssistantMessages.Skip(1).ToList();

        Assert.Single(result);
        Assert.Equal("Second reply", result[0].Content);
    }

    [Fact]
    public void AllowCreateSessionForSend_WhenChatWasCreatedThisTurn_ReturnsTrue()
    {
        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "AllowCreateSessionForSend", true);

        Assert.True(result);
    }

    [Fact]
    public void AllowCreateSessionForSend_WhenChatAlreadyExisted_ReturnsFalse()
    {
        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "AllowCreateSessionForSend", false);

        Assert.False(result);
    }

    private static DataStore CreateDataStore()
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        });

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args);
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object[] args)
        => (T)(type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args)
            ?? throw new InvalidOperationException($"Static method {name} was not found."));

    private static UserMessageEvent CreateUserMessageEvent(string content)
        => new()
        {
            Data = new UserMessageData
            {
                Content = content
            }
        };

    private static AssistantMessageEvent CreateAssistantMessageEvent(
        string messageId,
        string content,
        string? parentToolCallId = null)
        => new()
        {
            Data = new AssistantMessageData
            {
                MessageId = messageId,
                Content = content,
                ParentToolCallId = parentToolCallId
            }
        };

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
