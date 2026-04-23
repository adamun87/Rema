using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

public enum GlobalSearchCategory
{
    Chats,
    Projects,
    Skills,
    Lumis,
    Memories,
    McpServers,
    Settings
}

public enum GlobalSearchExecutionMode
{
    Fast,
    Full
}

public sealed class GlobalSearchMatch
{
    public GlobalSearchCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public int SettingsPageIndex { get; init; } = -1;
    public double Score { get; init; }
    public bool IsContentMatch { get; init; }
    public DateTimeOffset? SortTimestamp { get; init; }
}

public sealed class ChatSearchMessage
{
    public string Text { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class ChatSearchSnapshot
{
    public string Version { get; init; } = "";
    public IReadOnlyList<ChatSearchMessage> Messages { get; init; } = Array.Empty<ChatSearchMessage>();
}

public sealed class GlobalSearchService
{
    private readonly Func<AppData> _getData;
    private readonly Func<Chat, ChatSearchSnapshot> _chatSnapshotProvider;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly object _chatFieldCacheSync = new();
    private readonly Dictionary<Guid, CachedChatFields> _chatFieldCache = [];

    private static readonly SearchSettingEntry[] SettingsIndex =
    [
        new("Your Name", "Profile", 0),
        new("Language", "Profile", 0),
        new("Launch at Startup", "General", 1),
        new("Start Minimized", "General", 1),
        new("Minimize to Tray", "General", 1),
        new("Enable Notifications", "General", 1),
        new("Global Hotkey", "General", 1),
        new("Dark Mode", "Appearance", 2),
        new("Compact Density", "Appearance", 2),
        new("Font Size", "Appearance", 2),
        new("Show Animations", "Appearance", 2),
        new("Send with Enter", "Chat", 3),
        new("Show Timestamps", "Chat", 3),
        new("Show Tool Calls", "Chat", 3),
        new("Show Reasoning", "Chat", 3),
        new("Expand Reasoning While Streaming", "Chat", 3),
        new("Auto Generate Titles", "Chat", 3),
        new("GitHub Account", "AI & Models", 4),
        new("Default Model & Reasoning", "AI & Models", 4),
        new("Auto Save Memories", "Privacy & Data", 5),
        new("Auto Save Chats", "Privacy & Data", 5),
        new("Import Browser Cookies", "Privacy & Data", 5),
        new("Clear All Chats", "Privacy & Data", 5),
        new("Clear All Memories", "Privacy & Data", 5),
        new("Reset All Settings", "Privacy & Data", 5),
        new("Version", "About", 6)
    ];

    public GlobalSearchService(
        Func<AppData> getData,
        Func<Chat, ChatSearchSnapshot> chatSnapshotProvider,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _getData = getData ?? throw new ArgumentNullException(nameof(getData));
        _chatSnapshotProvider = chatSnapshotProvider ?? throw new ArgumentNullException(nameof(chatSnapshotProvider));
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, GlobalSearchExecutionMode.Full, cancellationToken);

    public Task<IReadOnlyList<GlobalSearchMatch>> SearchAsync(
        string query,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSnapshot(_getData());
        var trimmedQuery = query?.Trim() ?? "";

        if (string.IsNullOrEmpty(trimmedQuery))
            return Task.FromResult((IReadOnlyList<GlobalSearchMatch>)BuildDefaultResults(snapshot));

        var compiledQuery = CompiledQuery.Create(trimmedQuery);
        if (compiledQuery.Terms.Length == 0)
            return Task.FromResult((IReadOnlyList<GlobalSearchMatch>)BuildDefaultResults(snapshot));

        return Task.Run<IReadOnlyList<GlobalSearchMatch>>(
            () => SearchCore(snapshot, compiledQuery, executionMode, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<GlobalSearchMatch> SearchCore(
        SearchSnapshot snapshot,
        CompiledQuery query,
        GlobalSearchExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        var results = new List<GlobalSearchMatch>();

        SearchChats(snapshot, query, executionMode, results, cancellationToken);
        SearchProjects(snapshot, query, results, cancellationToken);
        SearchSkills(snapshot, query, results, cancellationToken);
        SearchAgents(snapshot, query, results, cancellationToken);
        SearchMemories(snapshot, query, results, cancellationToken);
        SearchMcpServers(snapshot, query, results, cancellationToken);
        SearchSettings(query, results, cancellationToken);

        results.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
                return scoreComparison;

            var rightTimestamp = right.SortTimestamp ?? DateTimeOffset.MinValue;
            var leftTimestamp = left.SortTimestamp ?? DateTimeOffset.MinValue;
            var timestampComparison = rightTimestamp.CompareTo(leftTimestamp);
            if (timestampComparison != 0)
                return timestampComparison;

            return StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
        });

        return results;
    }

    private void SearchChats(
        SearchSnapshot snapshot,
        CompiledQuery query,
        GlobalSearchExecutionMode executionMode,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var chat in snapshot.Chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(chat.Title, 3.8);
            var evaluation = EvaluateFields([titleField], query);

            if (!evaluation.IsCompleteMatch)
            {
                var contentFields = GetChatContentFields(chat, executionMode);
                if (contentFields.Count > 0)
                    evaluation = evaluation.Merge(EvaluateFields(contentFields, query));
            }

            if (!evaluation.IsCompleteMatch)
                continue;

            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);
            var contentMatch = !string.IsNullOrWhiteSpace(evaluation.BestContentSnippet);
            var score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(chat.UpdatedAt, multiplier: 1.6);

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, evaluation.BestContentSnippet),
                NavIndex = 0,
                Item = chat,
                Score = score,
                IsContentMatch = contentMatch,
                SortTimestamp = chat.UpdatedAt
            });
        }
    }

    private void SearchProjects(
        SearchSnapshot snapshot,
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(project.Name, 3.5);
            var instructionsField = new PreparedSearchField(project.Instructions, 1.0, isContent: true);
            var evaluation = EvaluateFields([titleField, instructionsField], query);

            if (!evaluation.IsCompleteMatch)
                continue;

            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;
            var contentMatch = !string.IsNullOrWhiteSpace(evaluation.BestContentSnippet);
            var defaultSubtitle = chatCount == 0
                ? "No chats"
                : $"{chatCount} chat{(chatCount == 1 ? "" : "s")} · {FormatRelativeTime(sortTimestamp)}";

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Projects,
                Title = project.Name,
                Subtitle = BuildSecondarySubtitle(defaultSubtitle, evaluation.BestContentSnippet),
                NavIndex = 1,
                Item = project,
                Score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(sortTimestamp, multiplier: 0.8),
                IsContentMatch = contentMatch,
                SortTimestamp = sortTimestamp
            });
        }
    }

    private void SearchSkills(
        SearchSnapshot snapshot,
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var skill in snapshot.Skills)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(skill.Name, 3.4);
            var descriptionField = new PreparedSearchField(skill.Description, 1.8);
            var contentField = new PreparedSearchField(skill.Content, 0.95, isContent: true);
            var evaluation = EvaluateFields([titleField, descriptionField, contentField], query);

            if (!evaluation.IsCompleteMatch)
                continue;

            var contentMatch = !string.IsNullOrWhiteSpace(evaluation.BestContentSnippet);

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = BuildSecondarySubtitle(skill.Description, evaluation.BestContentSnippet),
                NavIndex = 2,
                Item = skill,
                Score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                IsContentMatch = contentMatch,
                SortTimestamp = skill.CreatedAt
            });
        }
    }

    private void SearchAgents(
        SearchSnapshot snapshot,
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var agent in snapshot.Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(agent.Name, 3.4);
            var descriptionField = new PreparedSearchField(agent.Description, 1.8);
            var systemPromptField = new PreparedSearchField(agent.SystemPrompt, 0.95, isContent: true);
            var evaluation = EvaluateFields([titleField, descriptionField, systemPromptField], query);

            if (!evaluation.IsCompleteMatch)
                continue;

            var contentMatch = !string.IsNullOrWhiteSpace(evaluation.BestContentSnippet);

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = BuildSecondarySubtitle(agent.Description, evaluation.BestContentSnippet),
                NavIndex = 3,
                Item = agent,
                Score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                IsContentMatch = contentMatch,
                SortTimestamp = agent.CreatedAt
            });
        }
    }

    private void SearchMemories(
        SearchSnapshot snapshot,
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var memory in snapshot.Memories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(memory.Key, 3.3);
            var categoryField = new PreparedSearchField(memory.Category, 1.5);
            var contentField = new PreparedSearchField(memory.Content, 1.1, isContent: true);
            var evaluation = EvaluateFields([titleField, categoryField, contentField], query);

            if (!evaluation.IsCompleteMatch)
                continue;

            var contentMatch = !string.IsNullOrWhiteSpace(evaluation.BestContentSnippet);
            var defaultSubtitle = string.IsNullOrWhiteSpace(memory.Category)
                ? TrimForSubtitle(memory.Content)
                : $"[{memory.Category}] {TrimForSubtitle(memory.Content)}";

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Memories,
                Title = memory.Key,
                Subtitle = BuildSecondarySubtitle(defaultSubtitle, evaluation.BestContentSnippet),
                NavIndex = 4,
                Item = memory,
                Score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                IsContentMatch = contentMatch,
                SortTimestamp = memory.UpdatedAt
            });
        }
    }

    private void SearchMcpServers(
        SearchSnapshot snapshot,
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var server in snapshot.McpServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(server.Name, 3.2);
            var descriptionField = new PreparedSearchField(server.Description, 1.7);
            var commandField = new PreparedSearchField(server.Command ?? "", 1.0);
            var argsField = new PreparedSearchField(string.Join(' ', server.Args), 0.9);
            var urlField = new PreparedSearchField(server.Url ?? "", 0.8);
            var toolsField = new PreparedSearchField(string.Join(' ', server.Tools), 0.85);
            var evaluation = EvaluateFields(
                [titleField, descriptionField, commandField, argsField, urlField, toolsField],
                query);

            if (!evaluation.IsCompleteMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.McpServers,
                Title = server.Name,
                Subtitle = BuildSecondarySubtitle(server.Description, evaluation.BestContentSnippet),
                NavIndex = 5,
                Item = server,
                Score = evaluation.BaseScore
                        + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase)
                        + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
                IsContentMatch = false,
                SortTimestamp = server.CreatedAt
            });
        }
    }

    private static void SearchSettings(
        CompiledQuery query,
        ICollection<GlobalSearchMatch> results,
        CancellationToken cancellationToken)
    {
        foreach (var setting in SettingsIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var titleField = new PreparedSearchField(setting.Name, 3.2);
            var pageField = new PreparedSearchField(setting.Page, 1.6);
            var evaluation = EvaluateFields([titleField, pageField], query);

            if (!evaluation.IsCompleteMatch)
                continue;

            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Settings,
                Title = setting.Name,
                Subtitle = setting.Page,
                NavIndex = 6,
                Score = evaluation.BaseScore + GetTitleBonus(titleField.Prepared.Normalized, query.NormalizedPhrase),
                SettingsPageIndex = setting.PageIndex
            });
        }
    }

    private IReadOnlyList<GlobalSearchMatch> BuildDefaultResults(SearchSnapshot snapshot)
    {
        var results = new List<GlobalSearchMatch>();

        foreach (var chat in snapshot.Chats.OrderByDescending(static chat => chat.UpdatedAt).Take(6))
        {
            snapshot.ProjectNames.TryGetValue(chat.ProjectId ?? Guid.Empty, out var projectName);
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Chats,
                Title = chat.Title,
                Subtitle = BuildChatSubtitle(projectName, chat.UpdatedAt, snippet: null),
                NavIndex = 0,
                Item = chat,
                Score = 1_000 + GetRecencyBoost(chat.UpdatedAt, multiplier: 2.0),
                SortTimestamp = chat.UpdatedAt
            });
        }

        foreach (var project in snapshot.Projects
                     .OrderByDescending(project => snapshot.ProjectLastActivity.TryGetValue(project.Id, out var activity)
                         ? activity
                         : project.CreatedAt)
                     .Take(4))
        {
            snapshot.ProjectChatCounts.TryGetValue(project.Id, out var chatCount);
            snapshot.ProjectLastActivity.TryGetValue(project.Id, out var latestActivity);
            var sortTimestamp = latestActivity == default ? project.CreatedAt : latestActivity;
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Projects,
                Title = project.Name,
                Subtitle = chatCount == 0
                    ? "No chats"
                    : $"{chatCount} chat{(chatCount == 1 ? "" : "s")} · {FormatRelativeTime(sortTimestamp)}",
                NavIndex = 1,
                Item = project,
                Score = 900 + GetRecencyBoost(sortTimestamp, multiplier: 0.9),
                SortTimestamp = sortTimestamp
            });
        }

        foreach (var skill in snapshot.Skills.OrderByDescending(static skill => skill.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Skills,
                Title = skill.Name,
                Subtitle = skill.Description,
                NavIndex = 2,
                Item = skill,
                Score = 800 + GetRecencyBoost(skill.CreatedAt, multiplier: 0.45),
                SortTimestamp = skill.CreatedAt
            });
        }

        foreach (var agent in snapshot.Agents.OrderByDescending(static agent => agent.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Lumis,
                Title = agent.Name,
                Subtitle = agent.Description,
                NavIndex = 3,
                Item = agent,
                Score = 780 + GetRecencyBoost(agent.CreatedAt, multiplier: 0.45),
                SortTimestamp = agent.CreatedAt
            });
        }

        foreach (var memory in snapshot.Memories.OrderByDescending(static memory => memory.UpdatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.Memories,
                Title = memory.Key,
                Subtitle = string.IsNullOrWhiteSpace(memory.Category)
                    ? TrimForSubtitle(memory.Content)
                    : $"[{memory.Category}] {TrimForSubtitle(memory.Content)}",
                NavIndex = 4,
                Item = memory,
                Score = 760 + GetRecencyBoost(memory.UpdatedAt, multiplier: 0.7),
                SortTimestamp = memory.UpdatedAt
            });
        }

        foreach (var server in snapshot.McpServers.OrderByDescending(static server => server.CreatedAt).Take(4))
        {
            results.Add(new GlobalSearchMatch
            {
                Category = GlobalSearchCategory.McpServers,
                Title = server.Name,
                Subtitle = server.Description,
                NavIndex = 5,
                Item = server,
                Score = 740 + GetRecencyBoost(server.CreatedAt, multiplier: 0.4),
                SortTimestamp = server.CreatedAt
            });
        }

        results.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
                return scoreComparison;

            var rightTimestamp = right.SortTimestamp ?? DateTimeOffset.MinValue;
            var leftTimestamp = left.SortTimestamp ?? DateTimeOffset.MinValue;
            return rightTimestamp.CompareTo(leftTimestamp);
        });

        return results;
    }

    private IReadOnlyList<PreparedSearchField> GetChatContentFields(
        Chat chat,
        GlobalSearchExecutionMode executionMode)
    {
        if (chat.Messages.Count > 0)
            return GetChatContentFields(chat, _chatSnapshotProvider(chat));

        if (executionMode == GlobalSearchExecutionMode.Fast)
            return TryGetCachedChatContentFields(chat.Id, out var cachedFields) ? cachedFields : [];

        return GetChatContentFields(chat, _chatSnapshotProvider(chat));
    }

    private IReadOnlyList<PreparedSearchField> GetChatContentFields(Chat chat, ChatSearchSnapshot snapshot)
    {
        lock (_chatFieldCacheSync)
        {
            if (_chatFieldCache.TryGetValue(chat.Id, out var cached)
                && string.Equals(cached.Version, snapshot.Version, StringComparison.Ordinal))
            {
                return cached.Fields;
            }
        }

        var fields = snapshot.Messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(static message => new PreparedSearchField(message.Text, 1.0, isContent: true))
            .ToArray();

        lock (_chatFieldCacheSync)
        {
            _chatFieldCache[chat.Id] = new CachedChatFields(snapshot.Version, fields);
        }

        return fields;
    }

    private bool TryGetCachedChatContentFields(Guid chatId, out IReadOnlyList<PreparedSearchField> fields)
    {
        lock (_chatFieldCacheSync)
        {
            if (_chatFieldCache.TryGetValue(chatId, out var cached))
            {
                fields = cached.Fields;
                return true;
            }
        }

        fields = Array.Empty<PreparedSearchField>();
        return false;
    }

    private static SearchEvaluation EvaluateFields(
        IReadOnlyList<PreparedSearchField> fields,
        CompiledQuery query)
    {
        var termMatches = new SearchTermMatch[query.Terms.Length];

        foreach (var field in fields)
        {
            if (field.Prepared.Normalized.Length == 0)
                continue;

            for (var termIndex = 0; termIndex < query.Terms.Length; termIndex++)
            {
                var fieldMatch = ScoreTerm(field, query.Terms[termIndex]);
                if (fieldMatch.Score > termMatches[termIndex].Score)
                    termMatches[termIndex] = fieldMatch;
            }
        }

        return new SearchEvaluation(termMatches);
    }

    private static SearchTermMatch ScoreTerm(PreparedSearchField field, string term)
    {
        if (string.IsNullOrEmpty(term))
            return default;

        var bestScore = 0d;
        var normalized = field.Prepared.Normalized;

        if (string.Equals(normalized, term, StringComparison.Ordinal))
        {
            bestScore = 145;
        }
        else
        {
            bestScore = Math.Max(bestScore, ScoreTokens(field.Prepared.Tokens, term));
            bestScore = Math.Max(bestScore, ScoreNormalizedText(normalized, term));

            if (term.Length >= 3)
            {
                bestScore = Math.Max(bestScore, ScoreFuzzyTokens(field.Prepared.Tokens, term));
                bestScore = Math.Max(bestScore, ScoreEditDistance(normalized, term));
            }
        }

        if (bestScore <= 0)
            return default;

        return new SearchTermMatch(
            bestScore * field.Weight,
            field.IsContent,
            field.IsContent ? BuildSnippet(field.Original, term) : null);
    }

    private static double ScoreTokens(IReadOnlyList<string> tokens, string term)
    {
        var bestScore = 0d;

        foreach (var token in tokens)
        {
            if (string.Equals(token, term, StringComparison.Ordinal))
                bestScore = Math.Max(bestScore, 132);
            else if (token.StartsWith(term, StringComparison.Ordinal))
                bestScore = Math.Max(bestScore, 118 - Math.Min(18, token.Length - term.Length));
            else
            {
                var containsIndex = token.IndexOf(term, StringComparison.Ordinal);
                if (containsIndex >= 0)
                    bestScore = Math.Max(bestScore, 96 - Math.Min(24, containsIndex * 4));
            }
        }

        return bestScore;
    }

    private static double ScoreNormalizedText(string normalizedText, string term)
    {
        if (normalizedText.StartsWith(term, StringComparison.Ordinal))
            return 112 - Math.Min(16, normalizedText.Length - term.Length);

        var containsIndex = normalizedText.IndexOf(term, StringComparison.Ordinal);
        if (containsIndex >= 0)
            return 84 - Math.Min(26, containsIndex * 2);

        return 0;
    }

    private static double ScoreFuzzyTokens(IReadOnlyList<string> tokens, string term)
    {
        var bestScore = 0d;

        foreach (var token in tokens)
        {
            bestScore = Math.Max(bestScore, ScoreSubsequence(term, token));
            bestScore = Math.Max(bestScore, ScoreEditDistance(token, term));
        }

        return bestScore;
    }

    private static double ScoreSubsequence(string term, string token)
    {
        if (term.Length < 3 || token.Length < term.Length)
            return 0;

        var termIndex = 0;
        var firstMatchIndex = -1;
        var previousMatchIndex = -2;
        var score = 0d;

        for (var tokenIndex = 0; tokenIndex < token.Length && termIndex < term.Length; tokenIndex++)
        {
            if (token[tokenIndex] != term[termIndex])
                continue;

            if (firstMatchIndex < 0)
                firstMatchIndex = tokenIndex;

            score += 7;
            if (tokenIndex == 0)
                score += 10;
            if (tokenIndex == previousMatchIndex + 1)
                score += 11;

            previousMatchIndex = tokenIndex;
            termIndex++;
        }

        if (termIndex != term.Length)
            return 0;

        score += ((double)term.Length / token.Length) * 28;
        score -= Math.Max(0, firstMatchIndex) * 1.5;
        return Math.Clamp(score, 0, 88);
    }

    private static double ScoreEditDistance(string candidate, string term)
    {
        if (candidate.Length < 3
            || term.Length < 3
            || Math.Abs(candidate.Length - term.Length) > 2
            || candidate.Length > term.Length + 2)
        {
            return 0;
        }

        var maxDistance = term.Length <= 4 ? 1 : 2;
        var distance = DamerauLevenshteinDistance(candidate, term, maxDistance);
        if (distance > maxDistance)
            return 0;

        return distance switch
        {
            0 => 0,
            1 => 92,
            2 => 76,
            _ => 0
        };
    }

    private static int DamerauLevenshteinDistance(string source, string target, int maxDistance)
    {
        if (Math.Abs(source.Length - target.Length) > maxDistance)
            return maxDistance + 1;

        var rows = source.Length + 1;
        var columns = target.Length + 1;
        var distances = new int[rows, columns];

        for (var row = 0; row < rows; row++)
            distances[row, 0] = row;

        for (var column = 0; column < columns; column++)
            distances[0, column] = column;

        for (var row = 1; row < rows; row++)
        {
            var minInRow = int.MaxValue;

            for (var column = 1; column < columns; column++)
            {
                var substitutionCost = source[row - 1] == target[column - 1] ? 0 : 1;
                var deletion = distances[row - 1, column] + 1;
                var insertion = distances[row, column - 1] + 1;
                var substitution = distances[row - 1, column - 1] + substitutionCost;
                var value = Math.Min(Math.Min(deletion, insertion), substitution);

                if (row > 1
                    && column > 1
                    && source[row - 1] == target[column - 2]
                    && source[row - 2] == target[column - 1])
                {
                    value = Math.Min(value, distances[row - 2, column - 2] + 1);
                }

                distances[row, column] = value;
                if (value < minInRow)
                    minInRow = value;
            }

            if (minInRow > maxDistance)
                return maxDistance + 1;
        }

        return distances[source.Length, target.Length];
    }

    private double GetRecencyBoost(DateTimeOffset timestamp, double multiplier)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        var days = age.TotalDays;
        var baseBoost = 52d / (1d + (days / 7d));
        return baseBoost * multiplier;
    }

    private static double GetTitleBonus(string normalizedTitle, string normalizedQuery)
    {
        if (string.IsNullOrEmpty(normalizedTitle) || string.IsNullOrEmpty(normalizedQuery))
            return 0;

        if (string.Equals(normalizedTitle, normalizedQuery, StringComparison.Ordinal))
            return 260;

        if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 185;

        return normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal) ? 135 : 0;
    }

    private string BuildChatSubtitle(string? projectName, DateTimeOffset updatedAt, string? snippet)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectName))
            parts.Add(projectName);

        parts.Add(FormatRelativeTime(updatedAt));

        if (!string.IsNullOrWhiteSpace(snippet))
            parts.Add(snippet);

        return string.Join(" · ", parts);
    }

    private static string BuildSecondarySubtitle(string? primary, string? contentSnippet)
    {
        if (string.IsNullOrWhiteSpace(contentSnippet))
            return primary ?? "";

        if (string.IsNullOrWhiteSpace(primary))
            return contentSnippet;

        return $"{primary} · {contentSnippet}";
    }

    private string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var age = _nowProvider() - timestamp;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        if (age < TimeSpan.FromDays(7))
            return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        if (age < TimeSpan.FromDays(365))
            return timestamp.ToString("MMM d", CultureInfo.CurrentCulture);

        return timestamp.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static string BuildSnippet(string text, string term)
    {
        var flattened = CollapseWhitespace(text);
        if (flattened.Length == 0)
            return "";

        var snippetAnchor = flattened.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (snippetAnchor < 0 && term.Length > 3)
            snippetAnchor = flattened.IndexOf(term[..Math.Min(term.Length, 4)], StringComparison.OrdinalIgnoreCase);

        const int maxLength = 96;
        if (flattened.Length <= maxLength)
            return flattened;

        if (snippetAnchor < 0)
            return flattened[..maxLength] + "…";

        var start = Math.Max(0, snippetAnchor - 30);
        var length = Math.Min(maxLength, flattened.Length - start);
        var snippet = flattened.Substring(start, length).Trim();

        if (start > 0)
            snippet = "…" + snippet;
        if (start + length < flattened.Length)
            snippet += "…";

        return snippet;
    }

    private static string TrimForSubtitle(string? text)
    {
        var flattened = CollapseWhitespace(text);
        if (flattened.Length <= 80)
            return flattened;

        return flattened[..80] + "…";
    }

    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (previousWasWhitespace)
                    continue;

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static SearchSnapshot CaptureSnapshot(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var chats = data.Chats.ToArray();
        var projects = data.Projects.ToArray();
        var skills = data.Skills.ToArray();
        var agents = data.Agents.ToArray();
        var memories = data.Memories.ToArray();
        var servers = data.McpServers.ToArray();

        var projectNames = projects.ToDictionary(static project => project.Id, static project => project.Name);
        var projectChatCounts = chats
            .Where(static chat => chat.ProjectId.HasValue)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());

        var projectLastActivity = chats
            .Where(static chat => chat.ProjectId.HasValue)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Max(static chat => chat.UpdatedAt));

        return new SearchSnapshot(
            chats,
            projects,
            skills,
            agents,
            memories,
            servers,
            projectNames,
            projectChatCounts,
            projectLastActivity);
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = true;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string[] ExtractTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        var builder = new StringBuilder();
        var previousCharacter = '\0';

        void Flush()
        {
            if (builder.Length == 0)
                return;

            tokens.Add(builder.ToString());
            builder.Clear();
        }

        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                previousCharacter = '\0';
                continue;
            }

            var startsNewToken = builder.Length > 0
                                 && ((char.IsUpper(character) && char.IsLower(previousCharacter))
                                     || (char.IsDigit(character) != char.IsDigit(previousCharacter)));

            if (startsNewToken)
                Flush();

            builder.Append(char.ToLowerInvariant(character));
            previousCharacter = character;
        }

        Flush();
        return tokens
            .Where(static token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class SearchSnapshot(
        Chat[] chats,
        Project[] projects,
        Skill[] skills,
        LumiAgent[] agents,
        Memory[] memories,
        McpServer[] mcpServers,
        IReadOnlyDictionary<Guid, string> projectNames,
        IReadOnlyDictionary<Guid, int> projectChatCounts,
        IReadOnlyDictionary<Guid, DateTimeOffset> projectLastActivity)
    {
        public Chat[] Chats { get; } = chats;
        public Project[] Projects { get; } = projects;
        public Skill[] Skills { get; } = skills;
        public LumiAgent[] Agents { get; } = agents;
        public Memory[] Memories { get; } = memories;
        public McpServer[] McpServers { get; } = mcpServers;
        public IReadOnlyDictionary<Guid, string> ProjectNames { get; } = projectNames;
        public IReadOnlyDictionary<Guid, int> ProjectChatCounts { get; } = projectChatCounts;
        public IReadOnlyDictionary<Guid, DateTimeOffset> ProjectLastActivity { get; } = projectLastActivity;
    }

    private sealed class PreparedSearchField
    {
        public PreparedSearchField(string text, double weight, bool isContent = false)
        {
            Original = CollapseWhitespace(text);
            Weight = weight;
            IsContent = isContent;
            Prepared = new PreparedSearchText(Original);
        }

        public string Original { get; }
        public double Weight { get; }
        public bool IsContent { get; }
        public PreparedSearchText Prepared { get; }
    }

    private sealed class PreparedSearchText
    {
        public PreparedSearchText(string text)
        {
            Normalized = NormalizeText(text);
            Tokens = ExtractTokens(text);
        }

        public string Normalized { get; }
        public string[] Tokens { get; }
    }

    private readonly record struct CachedChatFields(string Version, IReadOnlyList<PreparedSearchField> Fields);
    private readonly record struct SearchTermMatch(double Score, bool IsContent, string? Snippet);
    private readonly record struct SearchSettingEntry(string Name, string Page, int PageIndex);

    private sealed class SearchEvaluation(SearchTermMatch[] termMatches)
    {
        public SearchTermMatch[] TermMatches { get; } = termMatches;

        public bool IsCompleteMatch => TermMatches.All(static match => match.Score > 0);

        public double BaseScore => TermMatches.Sum(static match => match.Score);

        public bool IsContentMatch => TermMatches.Any(static match => match.IsContent);

        public string? BestContentSnippet => TermMatches
            .Where(static match => match.IsContent && !string.IsNullOrWhiteSpace(match.Snippet))
            .OrderByDescending(static match => match.Score)
            .Select(static match => match.Snippet)
            .FirstOrDefault();

        public SearchEvaluation Merge(SearchEvaluation other)
        {
            if (TermMatches.Length != other.TermMatches.Length)
                throw new InvalidOperationException("Cannot merge search evaluations with different term counts.");

            var merged = new SearchTermMatch[TermMatches.Length];
            for (var index = 0; index < merged.Length; index++)
            {
                merged[index] = other.TermMatches[index].Score > TermMatches[index].Score
                    ? other.TermMatches[index]
                    : TermMatches[index];
            }

            return new SearchEvaluation(merged);
        }
    }

    private sealed class CompiledQuery(string raw, string normalizedPhrase, string[] terms)
    {
        public string Raw { get; } = raw;
        public string NormalizedPhrase { get; } = normalizedPhrase;
        public string[] Terms { get; } = terms;

        public static CompiledQuery Create(string query)
        {
            var normalizedPhrase = NormalizeText(query);
            var terms = normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new CompiledQuery(query, normalizedPhrase, terms);
        }
    }
}
