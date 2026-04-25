#if DEBUG
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;
using LumiChatMessage = Lumi.Models.ChatMessage;

namespace Lumi;

public static class DebugAgentHarness
{
    private const string ExpectedStressOutput = "LUMI_CHAT_STRESS_OK";
    private const string ExpectedToolInput = "lumi-agent-harness";

    public static bool IsUiHarnessFlag(string arg)
        => string.Equals(arg, "--debug-agent-harness", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--debug-transcript-fixture", StringComparison.OrdinalIgnoreCase);

    public static bool IsChatStressFlag(string arg)
        => string.Equals(arg, "--test-chat-stress", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--stress-chat", StringComparison.OrdinalIgnoreCase);

    public static Chat CreateTranscriptFixtureChat(DataStore dataStore)
    {
        var root = EnsureFixtureDirectory();
        var attachmentPath = Path.Combine(root, "fixture-attachment.md");
        var editedPath = Path.Combine(root, "FixtureWidget.cs");
        var createdPath = Path.Combine(root, "generated-fixture-output.md");

        File.WriteAllText(attachmentPath, "# Debug fixture attachment\n\nThis file exists so attachment chips can resolve size and icon metadata.\n");
        File.WriteAllText(editedPath, "public class FixtureWidget\n{\n    public string State => \"before\";\n}\n");
        File.WriteAllText(createdPath, "# Generated fixture output\n\nThis file is announced by the debug transcript fixture.\n");

        var codingSkill = dataStore.Data.Skills.FirstOrDefault(s =>
            s.Name.Equals("Code Helper", StringComparison.OrdinalIgnoreCase))
            ?? new Skill
            {
                Name = "Code Helper",
                Description = "Writes, explains, and debugs code.",
                IconGlyph = "{}"
            };

        var skillRef = new SkillReference
        {
            Name = codingSkill.Name,
            Glyph = codingSkill.IconGlyph,
            Description = codingSkill.Description
        };

        var chat = new Chat
        {
            Title = "Debug transcript fixture (not saved)",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-12),
            UpdatedAt = DateTimeOffset.Now,
            LastModelUsed = dataStore.Data.Settings.PreferredModel,
            LastReasoningEffortUsed = dataStore.Data.Settings.ReasoningEffort,
            TotalInputTokens = 12345,
            TotalOutputTokens = 6789,
            PlanContent = """
                # Debug plan

                - Render every transcript item type.
                - Keep this chat out of persisted history.
                - Use this fixture when changing transcript UI.
                """
        };

        var userName = dataStore.Data.Settings.UserName ?? "You";
        var t = DateTimeOffset.Now.AddMinutes(-10);
        LumiChatMessage Message(string role, string content)
            => new()
            {
                Role = role,
                Content = content,
                Timestamp = t = t.AddSeconds(18)
            };

        var user = Message("user", """
            Debug fixture request:

            - Show a normal user bubble.
            - Show attachment chips.
            - Show an active skill chip.
            """);
        user.Author = userName;
        user.Attachments.Add(attachmentPath);
        user.ActiveSkills.Add(skillRef);
        chat.Messages.Add(user);

        chat.Messages.Add(Tool("view", JsonObject(
            JsonProperty("path", JsonString(attachmentPath))), "Completed", output: "1. # Debug fixture attachment"));

        var firstAssistant = Message("assistant", """
            ### Transcript fixture is active

            This assistant message verifies **markdown**, `inline code`, selectable plain text, and source chips.

            | Item | Expected |
            | --- | --- |
            | Markdown | rendered |
            | Sources | visible below |
            | Model label | visible after turn |
            """);
        firstAssistant.Author = "Lumi";
        firstAssistant.Model = "gpt-5.5";
        firstAssistant.ActiveSkills.Add(skillRef);
        firstAssistant.Sources.Add(new SearchSource
        {
            Title = "Lumi debug fixture",
            Snippet = "Synthetic source used by the Debug-only transcript fixture.",
            Url = "https://example.com/lumi-debug-fixture"
        });
        chat.Messages.Add(firstAssistant);

        var secondUser = Message("user", "Run the full debug transcript pass with tools, reasoning, a subagent, a question, and file changes.");
        secondUser.Author = userName;
        chat.Messages.Add(secondUser);

        chat.Messages.Add(Message("reasoning", """
            I need to exercise completed reasoning, grouped tools, terminal output, todo progress, subagent nesting, and generated artifacts.
            """));

        chat.Messages.Add(Tool("report_intent", JsonObject(
            JsonProperty("intent", JsonString("Exercising fixture"))), "Completed"));
        chat.Messages.Add(Tool("fetch_skill", JsonObject(
            JsonProperty("name", JsonString(skillRef.Name))), "Completed", output: $"Fetched skill: {skillRef.Name}"));
        chat.Messages.Add(Tool("powershell", JsonObject(
            JsonProperty("command", JsonString("Write-Output 'fixture terminal output'")),
            JsonProperty("description", JsonString("Emit fixture output"))), "Completed", output: "fixture terminal output\nexit code: 0"));
        var todoArgs = JsonObject(
            JsonProperty("todoList", JsonArray(
                JsonObject(
                    JsonProperty("id", "1"),
                    JsonProperty("title", JsonString("Render fixture chat")),
                    JsonProperty("status", JsonString("completed"))),
                JsonObject(
                    JsonProperty("id", "2"),
                    JsonProperty("title", JsonString("Validate tool grouping")),
                    JsonProperty("status", JsonString("completed"))),
                JsonObject(
                    JsonProperty("id", "3"),
                    JsonProperty("title", JsonString("Keep stress harness ready")),
                    JsonProperty("status", JsonString("in-progress"))))));
        chat.Messages.Add(Tool("manage_todo_list", todoArgs, "InProgress"));
        chat.Messages.Add(Tool("edit", JsonObject(
            JsonProperty("filePath", JsonString(editedPath)),
            JsonProperty("oldString", JsonString("public class FixtureWidget")),
            JsonProperty("newString", JsonString("public partial class FixtureWidget"))), "Completed", output: "Updated FixtureWidget.cs"));

        var subagentId = "debug-subagent-fixture";
        chat.Messages.Add(Tool("task", JsonObject(
            JsonProperty("description", JsonString("Inspect transcript fixture in a separate coding-agent card")),
            JsonProperty("agent_type", JsonString("explore")),
            JsonProperty("agentName", JsonString("explore")),
            JsonProperty("agentDisplayName", JsonString("Explore agent")),
            JsonProperty("agentDescription", JsonString("Fast codebase exploration agent used by coding agents.")),
            JsonProperty("mode", JsonString("background")),
            JsonProperty("model", JsonString("claude-haiku-4.5")),
            JsonProperty("reasoning", JsonString("The fixture should show nested activity under this subagent card.")),
            JsonProperty("transcript", JsonString("Found ChatView.axaml templates, transcript builders, and debug entry points."))), "Completed", toolCallId: subagentId, output: "Subagent completed"));
        chat.Messages.Add(Tool("powershell", JsonObject(
            JsonProperty("command", JsonString("dotnet build src\\Lumi\\Lumi.csproj --no-restore")),
            JsonProperty("description", JsonString("Build Lumi"))), "Completed", parentToolCallId: subagentId, output: "Build succeeded."));

        var question = Tool("ask_question", JsonObject(
            JsonProperty("question", JsonString("Which debug action should an agent try next?")),
            JsonProperty("options", JsonArray(
                JsonString("Run fixture"),
                JsonString("Run stress harness"),
                JsonString("Inspect UI map"))),
            JsonProperty("allowFreeText", "true"),
            JsonProperty("allowMultiSelect", "false")), "Completed", output: "User answered: Run stress harness");
        question.QuestionId = "debug-question-fixture";
        question.QuestionText = "Which debug action should an agent try next?";
        question.QuestionOptions = JsonSerializer.Serialize(
            new[] { "Run fixture", "Run stress harness", "Inspect UI map" },
            AppDataJsonContext.Default.StringArray);
        question.QuestionAllowFreeText = true;
        question.QuestionAllowMultiSelect = false;
        chat.Messages.Add(question);

        chat.Messages.Add(Tool("announce_file", JsonObject(
            JsonProperty("filePath", JsonString(createdPath))), "Completed", output: createdPath));

        var finalAssistant = Message("assistant", """
            The fixture turn includes:

            1. Grouped tool calls.
            2. Todo progress.
            3. A nested subagent card.
            4. A question card with a selected answer.
            5. Announced file chips and a file-change summary.
            """);
        finalAssistant.Author = "Lumi";
        finalAssistant.Model = "claude-sonnet-4.6";
        finalAssistant.Sources.Add(new SearchSource
        {
            Title = "Agent debug map",
            Snippet = "The debug map names stable controls and nav indices.",
            Url = "https://example.com/lumi-agent-debug-map"
        });
        chat.Messages.Add(finalAssistant);

        chat.Messages.Add(Message("error", "Debug fixture error bubble: simulated recoverable Copilot error with retry styling."));

        return chat;

        LumiChatMessage Tool(
            string name,
            string argsJson,
            string status,
            string? toolCallId = null,
            string? parentToolCallId = null,
            string? output = null)
        {
            var msg = Message("tool", argsJson);
            msg.ToolName = name;
            msg.ToolStatus = status;
            msg.ToolCallId = toolCallId ?? $"debug-{name}-{Guid.NewGuid():N}";
            msg.ParentToolCallId = parentToolCallId;
            msg.ToolOutput = output;
            return msg;
        }

        static string JsonObject(params string[] properties)
            => "{" + string.Join(",", properties) + "}";

        static string JsonArray(params string[] items)
            => "[" + string.Join(",", items) + "]";

        static string JsonProperty(string name, string valueJson)
            => $"{JsonString(name)}:{valueJson}";

        static string JsonString(string value)
            => $"\"{JsonEncodedText.Encode(value).ToString()}\"";
    }

    public static async Task<int> RunChatStressAsync(CopilotService copilotService, CancellationToken ct)
    {
        Console.WriteLine("Lumi chat stress harness");
        Console.WriteLine("Connecting to Copilot...");

        await copilotService.ConnectAsync(ct).ConfigureAwait(false);
        var model = await copilotService.GetFastestModelIdAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Model: {model ?? "(default)"}");

        var toolInputs = new List<string>();
        var toolStarted = 0;
        var toolCompleted = 0;
        var streamed = new StringBuilder();

        var echoTool = AIFunctionFactory.Create(
            ([Description("Echo payload. Must be exactly lumi-agent-harness for the stress test.")] string value) =>
            {
                toolInputs.Add(value);
                return $"debug_echo_result:{value}:ok";
            },
            "debug_echo",
            "Deterministic debug echo tool for Lumi chat stress tests.");

        string? finalContent = null;
        await copilotService.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = $"""
                    You are running a deterministic Lumi debug harness.
                    You must call debug_echo exactly once with value "{ExpectedToolInput}".
                    After the tool result, answer with a short sentence that contains "{ExpectedStressOutput}".
                    """,
                Model = model,
                Streaming = true,
                Tools = [echoTool]
            },
            async (session, innerCt) =>
            {
                using var sub = session.On(evt =>
                {
                    switch (evt)
                    {
                        case AssistantMessageDeltaEvent delta:
                            streamed.Append(delta.Data?.DeltaContent);
                            break;
                        case ToolExecutionStartEvent start when start.Data?.ToolName == "debug_echo":
                            Interlocked.Increment(ref toolStarted);
                            break;
                        case ToolExecutionCompleteEvent:
                            Interlocked.Increment(ref toolCompleted);
                            break;
                    }
                });

                var result = await session.SendAndWaitAsync(
                    new MessageOptions
                    {
                        Prompt = $"Run the stress contract. Call debug_echo with {ExpectedToolInput}, then include {ExpectedStressOutput} in the final answer."
                    },
                    TimeSpan.FromMinutes(2),
                    innerCt).ConfigureAwait(false);

                finalContent = result?.Data?.Content;
            },
            ct).ConfigureAwait(false);

        var combined = string.Join("\n", finalContent, streamed.ToString());
        var hasExpectedOutput = combined.Contains(ExpectedStressOutput, StringComparison.Ordinal);
        var hasExpectedToolInput = toolInputs.Contains(ExpectedToolInput, StringComparer.Ordinal);
        var hasToolLifecycle = toolStarted > 0 && toolCompleted > 0;

        Console.WriteLine($"Tool started: {toolStarted}");
        Console.WriteLine($"Tool completed: {toolCompleted}");
        Console.WriteLine($"Tool inputs: {string.Join(", ", toolInputs)}");
        Console.WriteLine($"Final content: {finalContent}");

        if (hasExpectedOutput && hasExpectedToolInput && hasToolLifecycle)
        {
            Console.WriteLine("PASS: real Copilot stress check completed.");
            return 0;
        }

        Console.Error.WriteLine("FAIL: Copilot stress check did not satisfy the contract.");
        if (!hasToolLifecycle)
            Console.Error.WriteLine("- debug_echo tool lifecycle events were not observed.");
        if (!hasExpectedToolInput)
            Console.Error.WriteLine($"- debug_echo was not called with {ExpectedToolInput}.");
        if (!hasExpectedOutput)
            Console.Error.WriteLine($"- final response did not contain {ExpectedStressOutput}.");
        return 1;
    }

    private static string EnsureFixtureDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lumi",
            "debug-fixtures");
        Directory.CreateDirectory(path);
        return path;
    }
}
#endif
