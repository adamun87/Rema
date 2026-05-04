using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Rema.Models;

namespace Rema.Services;

public sealed class MemoryAgentService
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, string> _lastProcessedByChat = new();

    private static readonly string[] DurableSignals =
    [
        "my name is", "call me", "i prefer", "i like", "i love", "i dislike",
        "birthday", "born on", "live in", "i'm from", "works as", "work at",
        "my hobby", "favorite", "preferred", "always use", "never use",
        "team name", "manager is", "reports to", "timezone",
    ];

    private static readonly string[] EphemeralSignals =
    [
        "currently", "right now", "today", "yesterday", "this week",
        "for now", "temporary", "working on", "active branch",
    ];

    public MemoryAgentService(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
    }

    /// <summary>
    /// Called after each completed assistant turn. Checks if the user's messages
    /// contain durable personal facts worth remembering.
    /// </summary>
    public async Task ProcessTurnAsync(Chat chat, CancellationToken ct = default)
    {
        if (!_dataStore.Data.Settings.NotificationsEnabled) return;
        if (chat.Messages.Count < 2) return;

        var recentUserMessages = chat.Messages
            .Where(m => m.Role == "user")
            .TakeLast(3)
            .Select(m => m.Content)
            .ToList();

        if (recentUserMessages.Count == 0) return;

        var combinedText = string.Join(" ", recentUserMessages).ToLowerInvariant();

        var hasDurableSignal = DurableSignals.Any(s => combinedText.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (!hasDurableSignal) return;

        var ephemeralCount = EphemeralSignals.Count(s => combinedText.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (ephemeralCount > 2) return;

        var signature = combinedText.Length > 200 ? combinedText[..200] : combinedText;
        lock (_lastProcessedByChat)
        {
            if (_lastProcessedByChat.TryGetValue(chat.Id, out var last) && last == signature)
                return;
            _lastProcessedByChat[chat.Id] = signature;
        }

        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await ExtractMemoriesAsync(chat, recentUserMessages, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryAgent] Extraction failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExtractMemoriesAsync(Chat chat, List<string> userMessages, CancellationToken ct)
    {
        if (!_copilotService.IsConnected) return;

        var fastModel = await _copilotService.GetFastestModelIdAsync(ct);
        if (fastModel is null) return;

        var existingMemories = _dataStore.Data.Memories
            .Select(m => $"- {m.Key}: {m.Content}")
            .Take(50);
        var existingContext = string.Join("\n", existingMemories);

        var prompt = BuildExtractionPrompt(userMessages, existingContext);

        try
        {
            var client = _copilotService.Client;
            if (client is null) return;

            var config = SessionConfigBuilder.BuildLightweight(new LightweightSessionOptions
            {
                Model = fastModel,
                SystemPrompt = "You extract durable personal facts from conversations and save them as memories. Be selective — only save facts that are genuinely personal, stable, and worth remembering across sessions. Never save ephemeral task context, code snippets, or debugging details.",
            });

            var session = await client.CreateSessionAsync(config, ct);
            try
            {
                await session.SendAsync(new MessageOptions { Prompt = prompt }, ct);
            }
            finally
            {
                try { await client.DeleteSessionAsync(session.SessionId, ct); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryAgent] Session failed: {ex.Message}");
        }
    }

    private static string BuildExtractionPrompt(List<string> userMessages, string existingMemories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze these recent user messages for durable personal facts worth remembering:");
        sb.AppendLine();
        foreach (var msg in userMessages)
            sb.AppendLine($"> {msg}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(existingMemories))
        {
            sb.AppendLine("Already known memories (avoid duplicates):");
            sb.AppendLine(existingMemories);
            sb.AppendLine();
        }

        sb.AppendLine("""
            If you find NEW durable facts (preferences, personal info, work details, habits), respond with JSON:
            [{"key": "brief-label", "content": "full detail", "category": "Personal|Preferences|Work|Technical"}]
            
            If nothing worth saving, respond with: []
            
            Rules:
            - Only save genuinely personal/stable facts, not ephemeral task context
            - Skip code paths, file references, debugging details
            - Skip things already in existing memories
            - Keep keys short and descriptive
            - Keep content concise but complete
            """);

        return sb.ToString();
    }
}
