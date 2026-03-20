using System.Collections.Generic;
using GitHub.Copilot.SDK;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class PendingTurnRecoveryAnalyzerTests
{
    [Fact]
    public void Analyze_ReturnsUserMessageNotObserved_WhenTurnWasNeverRecorded()
    {
        var events = new SessionEvent[]
        {
            UserMessage("first"),
            AssistantMessage("done")
        };

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.False(analysis.UserMessageObserved);
        Assert.Equal(PendingTurnTerminalState.None, analysis.TerminalState);
        Assert.Empty(analysis.AssistantMessages);
    }

    [Fact]
    public void Analyze_TracksRunningAndCompletedToolsAcrossEventTypes()
    {
        var events = new SessionEvent[]
        {
            UserMessage("continue"),
            ToolStart("tool-a", "powershell"),
            ExternalToolRequested("req-1", "tool-b", "browser"),
            ToolComplete("tool-a", success: true),
            ExternalToolCompleted("req-1"),
            ToolStart("tool-c", "read_powershell")
        };

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);

        Assert.True(analysis.UserMessageObserved);
        Assert.Equal(1, analysis.ActiveToolCount);
        Assert.Contains("tool-a", analysis.CompletedToolCallIds);
        Assert.Contains("tool-b", analysis.CompletedToolCallIds);
        Assert.DoesNotContain("tool-c", analysis.CompletedToolCallIds);
    }

    [Fact]
    public void Analyze_CapturesAssistantMessagesAndTerminalErrors()
    {
        var events = new SessionEvent[]
        {
            UserMessage("continue"),
            AssistantMessage(""),
            AssistantMessage("Final answer"),
            SessionError("backend died")
        };

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);

        Assert.True(analysis.UserMessageObserved);
        Assert.Single(analysis.AssistantMessages);
        Assert.Equal("Final answer", analysis.AssistantMessages[0].Content);
        Assert.Equal(PendingTurnTerminalState.Error, analysis.TerminalState);
        Assert.Equal("backend died", analysis.ErrorMessage);
    }

    [Fact]
    public void Analyze_RecognizesIdleTerminalState()
    {
        var events = new SessionEvent[]
        {
            UserMessage("continue"),
            AssistantMessage("All done"),
            new SessionIdleEvent { Data = new SessionIdleData() }
        };

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);

        Assert.True(analysis.UserMessageObserved);
        Assert.Equal(PendingTurnTerminalState.Idle, analysis.TerminalState);
        Assert.Equal(0, analysis.ActiveToolCount);
    }

    [Fact]
    public void AnalyzePersistedLog_CapturesShutdownMissingFromLiveStream()
    {
        var lines = new[]
        {
            "{\"type\":\"user.message\",\"data\":{\"content\":\"continue\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"\",\"toolRequests\":[{\"toolCallId\":\"tool-1\"}]}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolCallId\":\"tool-1\",\"toolName\":\"view\"}}",
            "{\"type\":\"tool.execution_complete\",\"data\":{\"toolCallId\":\"tool-1\",\"success\":false}}",
            "{\"type\":\"assistant.turn_end\",\"data\":{\"turnId\":\"0\"}}",
            "{\"type\":\"assistant.turn_start\",\"data\":{\"turnId\":\"1\"}}",
            "{\"type\":\"session.shutdown\",\"data\":{\"shutdownType\":\"routine\"}}"
        };

        var analysis = PendingTurnRecoveryAnalyzer.AnalyzePersistedLog(lines, expectedSessionUserMessageCount: 1);

        Assert.True(analysis.UserMessageObserved);
        Assert.Equal(PendingTurnTerminalState.Shutdown, analysis.TerminalState);
        Assert.Equal(0, analysis.ActiveToolCount);
        Assert.Contains("tool-1", analysis.FailedToolCallIds);
    }

    [Fact]
    public void Merge_PrefersPersistedTerminalState()
    {
        var liveAnalysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            AssistantMessages = [new RecoveredAssistantMessage("Recovered answer")]
        };
        var persistedAnalysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            TerminalState = PendingTurnTerminalState.Shutdown
        };

        var analysis = PendingTurnRecoveryAnalyzer.Merge(liveAnalysis, persistedAnalysis);

        Assert.True(analysis.UserMessageObserved);
        Assert.Equal(PendingTurnTerminalState.Shutdown, analysis.TerminalState);
        Assert.Equal("Recovered answer", Assert.Single(analysis.AssistantMessages).Content);
    }

    [Fact]
    public void CountPersistedLogUserMessages_UsesSessionLocalHistory()
    {
        var lines = new[]
        {
            "{\"type\":\"user.message\",\"data\":{\"content\":\"first\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"reply\"}}",
            "{\"type\":\"user.message\",\"data\":{\"content\":\"second\"}}"
        };

        var count = PendingTurnRecoveryAnalyzer.CountPersistedLogUserMessages(lines);

        Assert.Equal(2, count);
    }

    [Fact]
    public void AnalyzePersistedLog_RequiresSessionLocalOrdinal_NotLocalChatOrdinal()
    {
        var lines = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            lines.Add($@"{{""type"":""user.message"",""data"":{{""content"":""turn-{i}""}}}}");
            lines.Add(@"{""type"":""assistant.turn_start"",""data"":{}}");
        }

        lines.Add(@"{""type"":""session.shutdown"",""data"":{""shutdownType"":""routine""}}");

        var sessionLocalAnalysis = PendingTurnRecoveryAnalyzer.AnalyzePersistedLog(
            lines,
            expectedSessionUserMessageCount: 8);
        var localChatCountAnalysis = PendingTurnRecoveryAnalyzer.AnalyzePersistedLog(
            lines,
            expectedSessionUserMessageCount: 27);

        Assert.True(sessionLocalAnalysis.UserMessageObserved);
        Assert.Equal(PendingTurnTerminalState.Shutdown, sessionLocalAnalysis.TerminalState);
        Assert.False(localChatCountAnalysis.UserMessageObserved);
    }

    private static UserMessageEvent UserMessage(string content)
        => new()
        {
            Data = new UserMessageData
            {
                Content = content
            }
        };

    private static AssistantMessageEvent AssistantMessage(string content)
        => new()
        {
            Data = new AssistantMessageData
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Content = content
            }
        };

    private static ToolExecutionStartEvent ToolStart(string toolCallId, string toolName)
        => new()
        {
            Data = new ToolExecutionStartData
            {
                ToolCallId = toolCallId,
                ToolName = toolName
            }
        };

    private static ToolExecutionCompleteEvent ToolComplete(string toolCallId, bool success)
        => new()
        {
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = toolCallId,
                Success = success
            }
        };

    private static ExternalToolRequestedEvent ExternalToolRequested(string requestId, string toolCallId, string toolName)
        => new()
        {
            Data = new ExternalToolRequestedData
            {
                RequestId = requestId,
                SessionId = Guid.NewGuid().ToString("N"),
                ToolCallId = toolCallId,
                ToolName = toolName
            }
        };

    private static ExternalToolCompletedEvent ExternalToolCompleted(string requestId)
        => new()
        {
            Data = new ExternalToolCompletedData
            {
                RequestId = requestId
            }
        };

    private static SessionErrorEvent SessionError(string message)
        => new()
        {
            Data = new SessionErrorData
            {
                ErrorType = "fatal",
                Message = message
            }
        };
}
