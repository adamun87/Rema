#if DEBUG
using System.Collections.ObjectModel;
using StrataTheme.Controls;

namespace Rema.ViewModels;

/// <summary>
/// Populates a TranscriptBuilder with synthetic items exercising every
/// transcript item type. Used by --debug-agent-harness.
/// </summary>
internal static class DebugFixture
{
    public static void Populate(ObservableCollection<TranscriptTurn> turns)
    {
        int item = 0;
        string Id() => $"fixture-{item++}";

        // ── Turn 1: User message ──
        var turn1 = new TranscriptTurn("fixture-turn-0");
        turn1.Items.Add(new UserMessageItem(Id())
        {
            Content = "Can you refactor the auth module and show me the changes?",
            Author = "Adam",
            TimestampText = "11:00 AM",
        });
        turns.Add(turn1);

        // ── Turn 2: Assistant response with reasoning + model label ──
        var turn2 = new TranscriptTurn("fixture-turn-1");

        turn2.Items.Add(new TurnModelItem(Id()) { ModelName = "claude-sonnet-4" });

        turn2.Items.Add(new ReasoningItem(Id())
        {
            Content = "The user wants me to refactor the authentication module. I'll need to:\n1. Read the current auth code\n2. Identify improvements\n3. Make the changes\n4. Show a summary of what changed",
            IsActive = false,
            IsExpanded = true,
        });

        // ── Tool group with multiple tools ──
        var group = new ToolGroupItem(Id())
        {
            Label = "⚙️ Read file +2 more",
            Meta = "3 done",
            IsActive = false,
            IsExpanded = true,
        };
        group.ToolCalls.Add(new ToolCallItem(Id())
        {
            ToolName = "Read file",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 120,
            InputParameters = "src/auth/login.ts",
        });
        group.ToolCalls.Add(new ToolCallItem(Id())
        {
            ToolName = "Read file",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 85,
            InputParameters = "src/auth/token.ts",
        });
        group.ToolCalls.Add(new ToolCallItem(Id())
        {
            ToolName = "Search code",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 340,
            InputParameters = "pattern: validateToken",
            MoreInfo = "Found 3 matches in 2 files",
        });
        turn2.Items.Add(group);

        // ── Sub-agent with nested activities (including TerminalPreviewItem) ──
        var subagent = new SubagentToolCallItem(Id())
        {
            DisplayName = "code-review",
            TaskDescription = "Review auth module refactoring changes",
            ModeLabel = "background",
            ModelDisplayName = "Claude Haiku 4.5",
            Meta = "✓",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 4200,
            IsExpanded = true,
        };
        subagent.Activities.Add(new ToolCallItem(Id())
        {
            ToolName = "Analyze diff",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 800,
        });
        subagent.Activities.Add(new ToolCallItem(Id())
        {
            ToolName = "Check types",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 450,
        });
        // TerminalPreviewItem extends ToolCallItemBase so it fits in Activities
        subagent.Activities.Add(new TerminalPreviewItem(Id())
        {
            ToolName = "Run tests",
            Command = "npm run test -- --filter auth",
            Output = "PASS src/auth/__tests__/login.test.ts\nPASS src/auth/__tests__/token.test.ts\n\nTest Suites: 2 passed, 2 total\nTests:       8 passed, 8 total",
            Status = StrataAiToolCallStatus.Completed,
            DurationMs = 1540,
            IsExpanded = true,
        });
        turn2.Items.Add(subagent);

        // ── File Changes Summary ──
        var fileChanges = new FileChangesSummaryItem(Id())
        {
            Label = "3 files changed",
            TotalStatsAdded = "+47",
            TotalStatsRemoved = "−12",
            HasTotalRemovals = true,
        };
        var fc1 = new FileChangeItem("src/auth/login.ts", false)
        {
            LinesAdded = 22,
            LinesRemoved = 8,
        };
        var fc2 = new FileChangeItem("src/auth/token.ts", false)
        {
            LinesAdded = 15,
            LinesRemoved = 4,
        };
        var fc3 = new FileChangeItem("src/auth/types.ts", true)
        {
            LinesAdded = 10,
            LinesRemoved = 0,
        };
        fileChanges.FileChanges.Add(fc1);
        fileChanges.FileChanges.Add(fc2);
        fileChanges.FileChanges.Add(fc3);
        turn2.Items.Add(fileChanges);

        // ── Assistant message ──
        turn2.Items.Add(new AssistantMessageItem(Id())
        {
            Content = "I've refactored the auth module. Here's what I changed:\n\n- **login.ts**: Extracted token validation into a separate function, added proper error handling\n- **token.ts**: Simplified the refresh logic, removed redundant checks\n- **types.ts**: New file with shared TypeScript interfaces\n\nAll 8 tests pass. Want me to create a PR?",
            Author = "Rema",
            TimestampText = "11:01 AM",
        });

        // ── File Attachment ──
        turn2.Items.Add(new FileAttachmentItem(Id())
        {
            FileName = "auth-refactor-summary.md",
            FilePath = @"C:\Users\adau\Documents\auth-refactor-summary.md",
        });

        // ── Source Citations ──
        var sources = new SourcesListItem(Id()) { IsExpanded = true };
        sources.Sources.Add(new SourceItem("JWT Best Practices", "https://auth0.com/blog/a-look-at-the-latest-draft-for-jwt-bcp/"));
        sources.Sources.Add(new SourceItem("Token Refresh Patterns", "https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-auth-code-flow"));
        sources.Sources.Add(new SourceItem("Node.js Auth Guide", "https://nodejs.org/en/learn/getting-started/security-best-practices"));
        turn2.Items.Add(sources);

        turns.Add(turn2);

        // ── Turn 3: Question Card ──
        var turn3 = new TranscriptTurn("fixture-turn-2");

        turn3.Items.Add(new QuestionItem(
            questionId: "q1",
            question: "Which PR strategy should I use?",
            options: new List<string>
            {
                "Single PR with all changes (Recommended)",
                "Separate PRs per file",
                "Draft PR for review first",
            },
            allowFreeText: true,
            submitAction: null,
            stableId: Id()));

        turns.Add(turn3);

        // ── Turn 4: Error message ──
        var turn4 = new TranscriptTurn("fixture-turn-3");
        turn4.Items.Add(new ErrorMessageItem(Id())
        {
            Content = "Failed to create PR: authentication token expired. Please re-authenticate.",
            TimestampText = "11:02 AM",
        });
        turns.Add(turn4);
    }
}
#endif
