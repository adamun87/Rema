using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rema.Models;

namespace Rema.Services;

public sealed record RepoCapabilityDiscoveryResult(
    List<CapabilityDefinition> Capabilities,
    List<McpServerConfig> McpServers);

public sealed record CapabilityMergeResult(int Added, int Updated);

public static class RepoCapabilityDiscoveryService
{
    private static readonly string[] CapabilityJsonPaths =
    [
        "rema-capabilities.json",
        "capabilities.json",
        ".ai/capabilities.json",
        ".github/capabilities.json",
    ];

    private static readonly string[] McpConfigPaths =
    [
        ".mcp.json",
        "mcp.json",
        ".vscode/mcp.json",
        ".cursor/mcp.json",
        ".ai/mcp.json",
        "mcp-servers.json",
    ];

    private static readonly (string Directory, string Pattern, string Kind)[] MarkdownCapabilityPaths =
    [
        (".ai/skills", "*.md", "Skill"),
        ("skills", "*.md", "Skill"),
        (".github/skills", "*.md", "Skill"),
        (".github/prompts", "*.prompt.md", "Skill"),
        ("prompts", "*.prompt.md", "Skill"),
        (".ai/agents", "*.md", "Agent"),
        ("agents", "*.md", "Agent"),
        (".github/agents", "*.md", "Agent"),
        (".github", "*.agent.md", "Agent"),
        (".vscode", "*.agent.md", "Agent"),
    ];

    public static RepoCapabilityDiscoveryResult Discover(string repoPath, string serviceProjectName, Guid? serviceProjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        var capabilities = new List<CapabilityDefinition>();
        var mcpServers = new List<McpServerConfig>();

        foreach (var relPath in CapabilityJsonPaths)
        {
            var path = Path.Combine(repoPath, relPath);
            if (!File.Exists(path))
                continue;

            try
            {
                capabilities.AddRange(ParseCapabilityJson(path, repoPath, serviceProjectName, serviceProjectId));
            }
            catch
            {
                // Invalid repo capability files should not block project onboarding.
            }
        }

        foreach (var relPath in McpConfigPaths)
        {
            var path = Path.Combine(repoPath, relPath);
            if (!File.Exists(path))
                continue;

            try
            {
                var discoveredServers = ParseMcpConfig(path);
                foreach (var server in discoveredServers)
                {
                    mcpServers.Add(server);
                    capabilities.Add(CreateMcpCapability(server, path, repoPath, serviceProjectName, serviceProjectId));
                }
            }
            catch
            {
                // Invalid MCP config files are ignored; the regular discovery log stays actionable.
            }
        }

        foreach (var (directory, pattern, kind) in MarkdownCapabilityPaths)
        {
            var fullDirectory = Path.Combine(repoPath, directory);
            if (!Directory.Exists(fullDirectory))
                continue;

            foreach (var file in EnumerateFilesSafely(fullDirectory, pattern, SearchOption.AllDirectories).Take(50))
            {
                try
                {
                    var capability = ParseMarkdownCapability(file, repoPath, serviceProjectName, serviceProjectId, kind);
                    if (capability is not null)
                        capabilities.Add(capability);
                }
                catch { }
            }
        }

        var agentsFile = Path.Combine(repoPath, "AGENTS.md");
        if (File.Exists(agentsFile))
        {
            var capability = ParseMarkdownCapability(agentsFile, repoPath, serviceProjectName, serviceProjectId, "Agent", "Repository Agent Instructions");
            if (capability is not null)
                capabilities.Add(capability);
        }

        return new RepoCapabilityDiscoveryResult(Deduplicate(capabilities), DeduplicateMcpServers(mcpServers));
    }

    public static CapabilityMergeResult MergeInto(RemaAppData data, IEnumerable<CapabilityDefinition> discovered)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Capabilities ??= [];

        var added = 0;
        var updated = 0;

        foreach (var capability in discovered.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var existing = data.Capabilities.FirstOrDefault(existing => IsSameCapability(existing, capability));
            if (existing is null)
            {
                data.Capabilities.Add(capability);
                added++;
                continue;
            }

            if (existing.IsBuiltIn)
                continue;

            existing.Kind = NormalizeKind(capability.Kind);
            existing.Name = capability.Name.Trim();
            existing.Description = capability.Description;
            existing.Content = capability.Content;
            existing.Source = capability.Source;
            existing.DeepLink = capability.DeepLink;
            existing.ServiceProjectId = capability.ServiceProjectId;
            existing.SourcePath = capability.SourcePath;
            existing.InvocationHint = capability.InvocationHint;
            existing.Tags = capability.Tags;
            existing.IsEnabled = capability.IsEnabled;
            existing.IsWorkflow = capability.IsWorkflow;
            updated++;
        }

        return new CapabilityMergeResult(added, updated);
    }

    public static List<McpServerConfig> MergeMcpServers(
        IEnumerable<McpServerConfig>? existing,
        IEnumerable<McpServerConfig>? discovered)
    {
        var merged = new List<McpServerConfig>();

        foreach (var server in discovered?.Where(s => !string.IsNullOrWhiteSpace(s.Name)) ?? [])
        {
            if (merged.Any(existingServer => existingServer.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            merged.Add(server);
        }

        foreach (var server in existing?.Where(s => !string.IsNullOrWhiteSpace(s.Name)) ?? [])
        {
            if (merged.Any(existingServer => existingServer.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            merged.Add(server);
        }

        return merged;
    }

    private static IEnumerable<CapabilityDefinition> ParseCapabilityJson(
        string path,
        string repoPath,
        string serviceProjectName,
        Guid? serviceProjectId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array => caps.EnumerateArray(),
            JsonValueKind.Object => [root],
            _ => [],
        };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(item, "name") ?? GetString(item, "id");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var kind = NormalizeKind(GetString(item, "kind") ?? GetString(item, "type") ?? "Skill");
            var tags = GetStringArray(item, "tags");
            yield return new CapabilityDefinition
            {
                Kind = kind,
                Name = name.Trim(),
                Description = GetString(item, "description") ?? $"Discovered {kind.ToLowerInvariant()} from {serviceProjectName}.",
                Content = GetString(item, "content")
                    ?? GetString(item, "instructions")
                    ?? GetString(item, "prompt")
                    ?? item.GetRawText(),
                Source = $"discovered from {serviceProjectName}",
                SourcePath = ToRelativePath(repoPath, path),
                DeepLink = path,
                ServiceProjectId = serviceProjectId,
                InvocationHint = GetString(item, "invocationHint") ?? GetString(item, "usage"),
                Tags = tags.Count > 0 ? tags : ["repo", "discovered", kind.ToLowerInvariant()],
                IsEnabled = GetBool(item, "isEnabled") ?? true,
                IsWorkflow = GetBool(item, "isWorkflow") ?? LooksLikeWorkflow(name, GetString(item, "content") ?? item.GetRawText(), tags),
            };
        }
    }

    private static List<McpServerConfig> ParseMcpConfig(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        JsonElement serversElement;
        if (root.TryGetProperty("servers", out var servers))
            serversElement = servers;
        else if (root.TryGetProperty("mcpServers", out var mcpServers))
            serversElement = mcpServers;
        else
            serversElement = root;

        if (serversElement.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<McpServerConfig>();
        foreach (var serverProperty in serversElement.EnumerateObject())
        {
            if (serverProperty.Value.ValueKind != JsonValueKind.Object)
                continue;

            var server = serverProperty.Value;
            var name = GetString(server, "name") ?? serverProperty.Name;
            var url = GetString(server, "url");
            var command = GetString(server, "command");
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(command))
                continue;

            var serverType = NormalizeMcpServerType(GetString(server, "type"), command, url);
            result.Add(new McpServerConfig
            {
                Name = name.Trim(),
                ServerType = serverType,
                Command = command ?? "",
                Args = GetStringArray(server, "args"),
                Env = GetStringDictionary(server, "env"),
                Url = url ?? "",
                Headers = GetStringDictionary(server, "headers"),
                IsEnabled = GetBool(server, "isEnabled") ?? GetBool(server, "enabled") ?? true,
            });
        }

        return result;
    }

    private static CapabilityDefinition CreateMcpCapability(
        McpServerConfig server,
        string path,
        string repoPath,
        string serviceProjectName,
        Guid? serviceProjectId)
    {
        var description = string.IsNullOrWhiteSpace(server.Url)
            ? $"Repo-discovered MCP server using `{server.Command}`."
            : $"Repo-discovered MCP server at {server.Url}.";

        return new CapabilityDefinition
        {
            Kind = "Mcp",
            Name = server.Name,
            Description = description,
            Content = JsonSerializer.Serialize(server, AppDataJsonContext.Default.McpServerConfig),
            Source = $"discovered from {serviceProjectName}",
            SourcePath = ToRelativePath(repoPath, path),
            DeepLink = path,
            ServiceProjectId = serviceProjectId,
            InvocationHint = "When a chat session is created, enabled repo MCP servers are attached so their tools can be used directly.",
            Tags = ["repo", "mcp", "discovered"],
            IsEnabled = server.IsEnabled,
        };
    }

    private static CapabilityDefinition? ParseMarkdownCapability(
        string path,
        string repoPath,
        string serviceProjectName,
        Guid? serviceProjectId,
        string kind,
        string? fallbackName = null)
    {
        var content = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var metadata = ParseFrontMatter(content);
        var name = metadata.TryGetValue("name", out var metadataName) && !string.IsNullOrWhiteSpace(metadataName)
            ? metadataName
            : fallbackName ?? Path.GetFileNameWithoutExtension(path).Replace(".agent", "").Replace(".prompt", "");
        var description = metadata.TryGetValue("description", out var metadataDescription) && !string.IsNullOrWhiteSpace(metadataDescription)
            ? metadataDescription
            : FirstContentLine(content) ?? $"Discovered {kind.ToLowerInvariant()} from {serviceProjectName}.";
        var tags = metadata.TryGetValue("tags", out var rawTags)
            ? SplitTags(rawTags)
            : [];

        if (tags.Count == 0)
            tags = ["repo", "discovered", kind.ToLowerInvariant()];

        return new CapabilityDefinition
        {
            Kind = NormalizeKind(kind),
            Name = name.Trim(),
            Description = description.Trim(),
            Content = TrimCapabilityContent(content),
            Source = $"discovered from {serviceProjectName}",
            SourcePath = ToRelativePath(repoPath, path),
            DeepLink = path,
            ServiceProjectId = serviceProjectId,
            Tags = tags,
            IsEnabled = true,
            IsWorkflow = LooksLikeWorkflow(name, content, tags),
            InvocationHint = metadata.TryGetValue("invocation", out var invocation) ? invocation : null,
        };
    }

    private static List<CapabilityDefinition> Deduplicate(IEnumerable<CapabilityDefinition> capabilities)
    {
        var result = new List<CapabilityDefinition>();
        foreach (var capability in capabilities)
        {
            if (result.Any(existing => IsSameCapability(existing, capability)))
                continue;
            result.Add(capability);
        }
        return result;
    }

    private static List<McpServerConfig> DeduplicateMcpServers(IEnumerable<McpServerConfig> servers)
    {
        var result = new List<McpServerConfig>();
        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            if (result.Any(existing => existing.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(server);
        }
        return result;
    }

    private static bool IsSameCapability(CapabilityDefinition left, CapabilityDefinition right)
    {
        if (left.Id != Guid.Empty && right.Id != Guid.Empty && left.Id == right.Id)
            return true;

        if (left.ServiceProjectId.HasValue
            && right.ServiceProjectId.HasValue
            && left.ServiceProjectId == right.ServiceProjectId
            && left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase)
            && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        return left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase)
               && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseFrontMatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return result;

        using var reader = new StringReader(content);
        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---")
                break;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            result[key] = value;
        }

        return result;
    }

    private static string? FirstContentLine(string content)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "---" || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (trimmed.Contains(':') && trimmed.Length < 120)
                continue;
            return trimmed.Length > 180 ? trimmed[..177] + "..." : trimmed;
        }

        return null;
    }

    private static string TrimCapabilityContent(string content)
        => content.Length > 16000 ? content[..16000] + "\n\n...(truncated)" : content;

    private static bool LooksLikeWorkflow(string name, string content, IReadOnlyCollection<string> tags)
        => name.Contains("workflow", StringComparison.OrdinalIgnoreCase)
           || tags.Any(tag => tag.Contains("workflow", StringComparison.OrdinalIgnoreCase))
           || content.Contains("long-running", StringComparison.OrdinalIgnoreCase)
           || content.Contains("workflow", StringComparison.OrdinalIgnoreCase)
           || content.Contains("steps:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKind(string? kind)
    {
        return kind?.Trim().ToLowerInvariant() switch
        {
            "skill" or "skills" => "Skill",
            "mcp" or "mcpserver" or "mcp server" => "Mcp",
            "agent" or "agents" => "Agent",
            "tool" or "tools" => "Tool",
            _ => "Skill",
        };
    }

    private static string NormalizeMcpServerType(string? type, string? command, string? url)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            var normalized = type.Trim().ToLowerInvariant();
            if (normalized is "stdio" or "local")
                return "stdio";
            if (normalized is "sse" or "http" or "https")
                return "sse";
        }

        return string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(url)
            ? "sse"
            : "stdio";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => null,
        };
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }

        return value.ValueKind == JsonValueKind.String
            ? SplitTags(value.GetString())
            : [];
    }

    private static Dictionary<string, string> GetStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return [];

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            var propValue = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.GetRawText();
            if (!string.IsNullOrWhiteSpace(propValue))
                result[prop.Name] = propValue!;
        }

        return result;
    }

    private static List<string> SplitTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Trim('[', ']')
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim('"', '\''))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateFilesSafely(string path, string pattern, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, searchOption);
        }
        catch
        {
            return [];
        }
    }

    private static string ToRelativePath(string repoPath, string path)
    {
        try
        {
            return Path.GetRelativePath(repoPath, path);
        }
        catch
        {
            return path;
        }
    }
}
