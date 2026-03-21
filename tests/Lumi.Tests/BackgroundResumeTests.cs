using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundResumeTests
{
    // ── Model tests ──

    [Fact]
    public void Chat_HasUnreadMessages_DefaultsFalse()
    {
        var chat = new Chat();
        Assert.False(chat.HasUnreadMessages);
    }

    [Fact]
    public void Chat_HasUnreadMessages_NotifiesPropertyChanged()
    {
        var chat = new Chat();
        var raised = false;
        chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Chat.HasUnreadMessages))
                raised = true;
        };

        chat.HasUnreadMessages = true;
        Assert.True(raised);
        Assert.True(chat.HasUnreadMessages);
    }

    [Fact]
    public void Chat_HasUnreadMessages_DoesNotRaiseWhenValueUnchanged()
    {
        var chat = new Chat { HasUnreadMessages = true };
        var raised = false;
        chat.PropertyChanged += (_, _) => raised = true;

        chat.HasUnreadMessages = true; // Same value
        Assert.False(raised);
    }

    [Fact]
    public void UserSettings_AutoResumeBackgroundTasks_DefaultsTrue()
    {
        var settings = new UserSettings();
        Assert.True(settings.AutoResumeBackgroundTasks);
    }

    [Fact]
    public void ExtractBackgroundShellId_PrefersArgsJsonShellId()
    {
        var shellId = ChatViewModel.ExtractBackgroundShellId(
            "{\"mode\":\"async\",\"shellId\":\"task15\"}",
            "Agent started in background with shellId: generated");

        Assert.Equal("task15", shellId);
    }

    [Fact]
    public void ExtractBackgroundShellId_FallsBackToToolOutput()
    {
        var shellId = ChatViewModel.ExtractBackgroundShellId(
            argsJson: null,
            toolOutput: "<command started in background with shellId: task42>");

        Assert.Equal("task42", shellId);
    }

    [Fact]
    public void ExtractBackgroundAgentId_ReadsAgentIdFromToolOutput()
    {
        var agentId = ChatViewModel.ExtractBackgroundAgentId(
            "Agent started in background with agent_id: agent-7. You can use read_agent.");

        Assert.Equal("agent-7", agentId);
    }

    [Fact]
    public void IsBackgroundPowershellReadComplete_DetectsExitMarker()
    {
        Assert.True(ChatViewModel.IsBackgroundPowershellReadComplete(
            "task10",
            "1861087324\n<command with id: task10 exited with exit code 0>"));
        Assert.False(ChatViewModel.IsBackgroundPowershellReadComplete(
            "task10",
            "1861087324"));
    }

    [Fact]
    public void IsBackgroundAgentReadComplete_DetectsFinalStatuses()
    {
        Assert.True(ChatViewModel.IsBackgroundAgentReadComplete(
            "agent-3",
            "Agent completed. agent_id: agent-3, agent_type: task, status: completed, description: Test"));
        Assert.True(ChatViewModel.IsBackgroundAgentReadComplete(
            "agent-3",
            "Agent completed. agent_id: agent-3, agent_type: task, status: failed, description: Test"));
        Assert.False(ChatViewModel.IsBackgroundAgentReadComplete(
            "agent-3",
            "Agent running. agent_id: agent-3, agent_type: task, status: running"));
    }

    // ── Transcript builder tests ──

    [Fact]
    public void TranscriptBuilder_BackgroundSubagentMode_IsTracked()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm(
            "bg-task-1", "task", "InProgress",
            "{\"description\":\"Process files\",\"agent_type\":\"general-purpose\",\"mode\":\"background\"}");

        builder.ProcessMessageToTranscript(toolVm);

        var turn = Assert.Single(liveTurns);
        var subagent = Assert.IsType<SubagentToolCallItem>(Assert.Single(turn.Items));
        Assert.Equal("Background", subagent.ModeLabel);
        Assert.Equal("Process files", subagent.Title);
        Assert.True(subagent.IsActive);
    }

    [Fact]
    public void TranscriptBuilder_BackgroundSubagent_CompletionUpdatesStatus()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm(
            "bg-task-1", "task", "InProgress",
            "{\"description\":\"Process files\",\"agent_type\":\"general-purpose\",\"mode\":\"background\"}");

        builder.ProcessMessageToTranscript(toolVm);

        var turn = Assert.Single(liveTurns);
        var subagent = Assert.IsType<SubagentToolCallItem>(Assert.Single(turn.Items));
        Assert.True(subagent.IsActive);

        // Simulate completion
        toolVm.Message.ToolStatus = "Completed";
        toolVm.NotifyToolStatusChanged();

        Assert.True(subagent.IsCompleted);
        Assert.False(subagent.IsActive);
        Assert.False(subagent.IsExpanded); // Auto-collapses on completion
    }

    // ── ToolDisplayHelper mode detection ──

    [Fact]
    public void ToolDisplayHelper_ExtractJsonField_ReturnsMode()
    {
        var json = "{\"description\":\"Run tests\",\"agent_type\":\"task\",\"mode\":\"background\"}";
        var mode = ToolDisplayHelper.ExtractJsonField(json, "mode");
        Assert.Equal("background", mode);
    }

    [Fact]
    public void ToolDisplayHelper_ExtractJsonField_ReturnsNullForMissingField()
    {
        var json = "{\"description\":\"Run tests\"}";
        var mode = ToolDisplayHelper.ExtractJsonField(json, "mode");
        Assert.Null(mode);
    }

    [Fact]
    public void ToolDisplayHelper_GetSubagentModeLabel_FormatsBackground()
    {
        var json = "{\"mode\":\"background\"}";
        var label = ToolDisplayHelper.GetSubagentModeLabel(json);
        Assert.Equal("Background", label);
    }

    [Fact]
    public void ToolDisplayHelper_GetSubagentModeLabel_FormatsSync()
    {
        var json = "{\"mode\":\"sync\"}";
        var label = ToolDisplayHelper.GetSubagentModeLabel(json);
        Assert.Equal("Sync", label);
    }

    // ── Helpers ──

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, (_, _) => Task.CompletedTask, () => null);

    private static ChatMessageViewModel CreateToolVm(
        string toolCallId,
        string toolName,
        string toolStatus,
        string content,
        string? parentToolCallId = null)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateAssistantVm(string content)
        => new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Author = "Lumi",
            Timestamp = DateTimeOffset.Now,
        });

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, new AppData());
        return store;
    }
}
