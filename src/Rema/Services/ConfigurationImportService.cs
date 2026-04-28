using System;
using System.Collections.Generic;
using System.Linq;
using Rema.Models;

namespace Rema.Services;

public sealed record ConfigurationImportResult(
    bool SettingsUpdated,
    int ServiceProjectsAdded,
    int ServiceProjectsUpdated,
    int CapabilitiesAdded,
    int CapabilitiesUpdated,
    int ScriptTemplatesAdded,
    int ScriptTemplatesUpdated,
    int MemoriesAdded,
    int MemoriesUpdated)
{
    public int TotalAdded => ServiceProjectsAdded + CapabilitiesAdded + ScriptTemplatesAdded + MemoriesAdded;
    public int TotalUpdated => ServiceProjectsUpdated + CapabilitiesUpdated + ScriptTemplatesUpdated + MemoriesUpdated;

    public string ToStatusMessage()
    {
        var parts = new List<string>();

        if (SettingsUpdated)
            parts.Add("settings refreshed");
        AddPart(parts, ServiceProjectsAdded, ServiceProjectsUpdated, "service project");
        AddPart(parts, CapabilitiesAdded, CapabilitiesUpdated, "capability", "capabilities");
        AddPart(parts, ScriptTemplatesAdded, ScriptTemplatesUpdated, "script template");
        AddPart(parts, MemoriesAdded, MemoriesUpdated, "memory", "memories");

        return parts.Count == 0
            ? "Imported configuration. No new items were found."
            : $"Imported configuration: {string.Join("; ", parts)}.";
    }

    private static void AddPart(List<string> parts, int added, int updated, string singular, string? plural = null)
    {
        if (added == 0 && updated == 0)
            return;

        var name = added + updated == 1 ? singular : plural ?? singular + "s";
        if (added > 0 && updated > 0)
            parts.Add($"{added} added / {updated} updated {name}");
        else if (added > 0)
            parts.Add($"{added} added {name}");
        else
            parts.Add($"{updated} updated {name}");
    }
}

public static class ConfigurationImportService
{
    public const int SupportedSchemaVersion = 1;

    public static ConfigurationImportResult ImportInto(RemaAppData target, RemaConfigurationExport import)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(import);

        if (import.SchemaVersion > SupportedSchemaVersion)
            throw new NotSupportedException($"Configuration schema version {import.SchemaVersion} is newer than this Rema version supports.");

        target.Settings ??= new RemaSettings();
        target.ServiceProjects ??= [];
        target.Capabilities ??= [];
        target.ScriptTemplates ??= [];
        target.Memories ??= [];
        target.WorkflowExecutions ??= [];

        var settingsUpdated = import.Settings is not null && CopyShareableSettings(target.Settings, import.Settings);
        var (projectsAdded, projectsUpdated) = MergeServiceProjects(target.ServiceProjects, import.ServiceProjects);
        var (capabilitiesAdded, capabilitiesUpdated) = MergeCapabilities(target.Capabilities, import.Capabilities);
        var (templatesAdded, templatesUpdated) = MergeScriptTemplates(target.ScriptTemplates, import.ScriptTemplates);
        var (memoriesAdded, memoriesUpdated) = MergeMemories(target.Memories, import.Memories);

        BuiltInCapabilityCatalog.EnsureBuiltIns(target);

        return new ConfigurationImportResult(
            settingsUpdated,
            projectsAdded,
            projectsUpdated,
            capabilitiesAdded,
            capabilitiesUpdated,
            templatesAdded,
            templatesUpdated,
            memoriesAdded,
            memoriesUpdated);
    }

    private static bool CopyShareableSettings(RemaSettings target, RemaSettings import)
    {
        var changed = false;

        SetIfChanged(ref changed, target.Language, import.Language, value => target.Language = value, requireValue: true);
        SetIfChanged(ref changed, target.IsDarkTheme, import.IsDarkTheme, value => target.IsDarkTheme = value);
        SetIfChanged(ref changed, target.IsCompactDensity, import.IsCompactDensity, value => target.IsCompactDensity = value);
        SetIfChanged(ref changed, target.FontSize, import.FontSize, value => target.FontSize = value);
        SetIfChanged(ref changed, target.SendWithEnter, import.SendWithEnter, value => target.SendWithEnter = value);
        SetIfChanged(ref changed, target.ShowToolCalls, import.ShowToolCalls, value => target.ShowToolCalls = value);
        SetIfChanged(ref changed, target.ShowTimestamps, import.ShowTimestamps, value => target.ShowTimestamps = value);
        SetIfChanged(ref changed, target.ShowReasoning, import.ShowReasoning, value => target.ShowReasoning = value);
        SetIfChanged(ref changed, target.ExpandReasoningWhileStreaming, import.ExpandReasoningWhileStreaming, value => target.ExpandReasoningWhileStreaming = value);
        SetIfChanged(ref changed, target.ShowStreamingUpdates, import.ShowStreamingUpdates, value => target.ShowStreamingUpdates = value);
        SetIfChanged(ref changed, target.AutoGenerateTitles, import.AutoGenerateTitles, value => target.AutoGenerateTitles = value);
        SetIfChanged(ref changed, target.PreferredModel, import.PreferredModel, value => target.PreferredModel = value, requireValue: true);
        SetIfChanged(ref changed, target.ReasoningEffort, import.ReasoningEffort, value => target.ReasoningEffort = value, requireValue: true);
        SetIfChanged(ref changed, target.PollingIntervalSeconds, import.PollingIntervalSeconds, value => target.PollingIntervalSeconds = value);
        SetIfChanged(ref changed, target.IsPollingEnabled, import.IsPollingEnabled, value => target.IsPollingEnabled = value);
        SetIfChanged(ref changed, target.NotificationsEnabled, import.NotificationsEnabled, value => target.NotificationsEnabled = value);

        return changed;
    }

    private static (int Added, int Updated) MergeServiceProjects(List<ServiceProject> target, IEnumerable<ServiceProject>? imports)
    {
        var added = 0;
        var updated = 0;

        foreach (var import in imports?.OfType<ServiceProject>() ?? Enumerable.Empty<ServiceProject>())
        {
            if (string.IsNullOrWhiteSpace(import.Name))
                continue;

            var existing = FindServiceProject(target, import);
            if (existing is null)
            {
                var clone = CloneServiceProject(import);
                PipelineDefinitionIdResolver.Normalize(clone);
                target.Add(clone);
                added++;
            }
            else
            {
                ApplyServiceProject(existing, import);
                PipelineDefinitionIdResolver.Normalize(existing);
                updated++;
            }
        }

        return (added, updated);
    }

    private static ServiceProject? FindServiceProject(IEnumerable<ServiceProject> target, ServiceProject import)
    {
        var byId = target.FirstOrDefault(project => import.Id != Guid.Empty && project.Id == import.Id);
        if (byId is not null)
            return byId;

        var importKey = ServiceProjectKey(import);
        return string.IsNullOrWhiteSpace(importKey)
            ? null
            : target.FirstOrDefault(project => ServiceProjectKey(project).Equals(importKey, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyServiceProject(ServiceProject target, ServiceProject import)
    {
        target.Name = import.Name.Trim();
        if (string.IsNullOrWhiteSpace(target.RepoPath) && !string.IsNullOrWhiteSpace(import.RepoPath))
            target.RepoPath = import.RepoPath;
        target.AdoOrgUrl = import.AdoOrgUrl ?? "";
        target.AdoProjectName = import.AdoProjectName ?? "";
        target.KustoCluster = import.KustoCluster;
        target.KustoDatabase = import.KustoDatabase;
        target.DiscoveredAgentPath = import.DiscoveredAgentPath;
        target.Instructions = import.Instructions;
        target.McpServer = CloneMcpServer(import.McpServer);
        target.McpServers = import.McpServers?.Select(CloneMcpServer).OfType<McpServerConfig>().ToList() ?? [];
        target.PipelineConfigs ??= [];
        target.HealthQueries ??= [];

        MergePipelines(target, import.PipelineConfigs);
        MergeHealthQueries(target, import.HealthQueries);
    }

    private static ServiceProject CloneServiceProject(ServiceProject import)
    {
        var id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id;
        var clone = new ServiceProject
        {
            Id = id,
            Name = import.Name.Trim(),
            RepoPath = import.RepoPath ?? "",
            AdoOrgUrl = import.AdoOrgUrl ?? "",
            AdoProjectName = import.AdoProjectName ?? "",
            KustoCluster = import.KustoCluster,
            KustoDatabase = import.KustoDatabase,
            DiscoveredAgentPath = import.DiscoveredAgentPath,
            Instructions = import.Instructions,
            CreatedAt = import.CreatedAt,
            McpServer = CloneMcpServer(import.McpServer),
            McpServers = import.McpServers?.Select(CloneMcpServer).OfType<McpServerConfig>().ToList() ?? [],
        };

        foreach (var pipeline in import.PipelineConfigs?.OfType<PipelineConfig>() ?? Enumerable.Empty<PipelineConfig>())
            clone.PipelineConfigs.Add(ClonePipeline(pipeline, clone.Id));
        foreach (var query in import.HealthQueries?.OfType<HealthQuery>() ?? Enumerable.Empty<HealthQuery>())
            clone.HealthQueries.Add(CloneHealthQuery(query, clone.Id));

        return clone;
    }

    private static void MergePipelines(ServiceProject target, IEnumerable<PipelineConfig>? imports)
    {
        foreach (var import in imports?.OfType<PipelineConfig>() ?? Enumerable.Empty<PipelineConfig>())
        {
            var existing = FindPipeline(target.PipelineConfigs, import);
            if (existing is null)
                target.PipelineConfigs.Add(ClonePipeline(import, target.Id));
            else
                ApplyPipeline(existing, import, target.Id);
        }
    }

    private static PipelineConfig? FindPipeline(IEnumerable<PipelineConfig> target, PipelineConfig import)
    {
        var byId = target.FirstOrDefault(pipeline => import.Id != Guid.Empty && pipeline.Id == import.Id);
        if (byId is not null)
            return byId;

        if (import.AdoPipelineId != 0)
        {
            var byPipelineId = target.FirstOrDefault(pipeline => pipeline.AdoPipelineId == import.AdoPipelineId);
            if (byPipelineId is not null)
                return byPipelineId;
        }

        var importKey = PipelineKey(import);
        return string.IsNullOrWhiteSpace(importKey)
            ? null
            : target.FirstOrDefault(pipeline => PipelineKey(pipeline).Equals(importKey, StringComparison.OrdinalIgnoreCase));
    }

    private static PipelineConfig ClonePipeline(PipelineConfig import, Guid serviceProjectId)
    {
        var clone = new PipelineConfig { Id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id };
        ApplyPipeline(clone, import, serviceProjectId);
        return clone;
    }

    private static void ApplyPipeline(PipelineConfig target, PipelineConfig import, Guid serviceProjectId)
    {
        target.ServiceProjectId = serviceProjectId;
        target.AdoPipelineId = import.AdoPipelineId;
        target.PipelineType = string.IsNullOrWhiteSpace(import.PipelineType) ? "yaml" : import.PipelineType;
        target.Name = import.Name ?? "";
        target.DisplayName = import.DisplayName ?? "";
        target.AdoUrl = import.AdoUrl ?? "";
        target.Description = import.Description ?? "";
        target.DeploymentStages = import.DeploymentStages?.Where(stage => !string.IsNullOrWhiteSpace(stage)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        target.ApprovalRequired = import.ApprovalRequired is null
            ? []
            : new Dictionary<string, bool>(import.ApprovalRequired, StringComparer.OrdinalIgnoreCase);
        target.HealthCheckEnabled = import.HealthCheckEnabled;
    }

    private static void MergeHealthQueries(ServiceProject target, IEnumerable<HealthQuery>? imports)
    {
        foreach (var import in imports?.OfType<HealthQuery>() ?? Enumerable.Empty<HealthQuery>())
        {
            if (string.IsNullOrWhiteSpace(import.Name))
                continue;

            var existing = FindHealthQuery(target.HealthQueries, import);
            if (existing is null)
                target.HealthQueries.Add(CloneHealthQuery(import, target.Id));
            else
                ApplyHealthQuery(existing, import, target.Id);
        }
    }

    private static HealthQuery? FindHealthQuery(IEnumerable<HealthQuery> target, HealthQuery import)
    {
        var byId = target.FirstOrDefault(query => import.Id != Guid.Empty && query.Id == import.Id);
        return byId ?? target.FirstOrDefault(query => query.Name.Equals(import.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static HealthQuery CloneHealthQuery(HealthQuery import, Guid serviceProjectId)
    {
        var clone = new HealthQuery { Id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id };
        ApplyHealthQuery(clone, import, serviceProjectId);
        return clone;
    }

    private static void ApplyHealthQuery(HealthQuery target, HealthQuery import, Guid serviceProjectId)
    {
        target.ServiceProjectId = serviceProjectId;
        target.Name = import.Name.Trim();
        target.Query = import.Query ?? "";
        target.ThresholdType = string.IsNullOrWhiteSpace(import.ThresholdType) ? "GreaterThan" : import.ThresholdType;
        target.ThresholdValue = import.ThresholdValue;
        target.Severity = string.IsNullOrWhiteSpace(import.Severity) ? "Warning" : import.Severity;
    }

    private static McpServerConfig? CloneMcpServer(McpServerConfig? import)
    {
        if (import is null)
            return null;

        return new McpServerConfig
        {
            Name = import.Name ?? "",
            ServerType = string.IsNullOrWhiteSpace(import.ServerType) ? "local" : import.ServerType,
            Command = import.Command ?? "",
            Args = import.Args?.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToList() ?? [],
            Env = import.Env is null ? [] : new Dictionary<string, string>(import.Env, StringComparer.OrdinalIgnoreCase),
            Url = import.Url ?? "",
            Headers = import.Headers is null ? [] : new Dictionary<string, string>(import.Headers, StringComparer.OrdinalIgnoreCase),
            IsEnabled = import.IsEnabled,
        };
    }

    private static (int Added, int Updated) MergeCapabilities(List<CapabilityDefinition> target, IEnumerable<CapabilityDefinition>? imports)
    {
        var added = 0;
        var updated = 0;

        foreach (var import in imports?.OfType<CapabilityDefinition>() ?? Enumerable.Empty<CapabilityDefinition>())
        {
            if (string.IsNullOrWhiteSpace(import.Name))
                continue;

            var existing = FindCapability(target, import);
            if (existing is null)
            {
                target.Add(CloneCapability(import));
                added++;
            }
            else
            {
                ApplyCapability(existing, import);
                updated++;
            }
        }

        return (added, updated);
    }

    private static CapabilityDefinition? FindCapability(IEnumerable<CapabilityDefinition> target, CapabilityDefinition import)
    {
        var byId = target.FirstOrDefault(capability => import.Id != Guid.Empty && capability.Id == import.Id);
        if (byId is not null)
            return byId;

        var importKey = CapabilityKey(import);
        return target.FirstOrDefault(capability => CapabilityKey(capability).Equals(importKey, StringComparison.OrdinalIgnoreCase));
    }

    private static CapabilityDefinition CloneCapability(CapabilityDefinition import)
    {
        var clone = new CapabilityDefinition { Id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id };
        ApplyCapability(clone, import);
        return clone;
    }

    private static void ApplyCapability(CapabilityDefinition target, CapabilityDefinition import)
    {
        var targetIsBuiltIn = target.IsBuiltIn;

        target.Kind = string.IsNullOrWhiteSpace(import.Kind) ? "Tool" : import.Kind;
        target.Name = import.Name.Trim();
        target.IsEnabled = import.IsEnabled;
        target.ServiceProjectId = import.ServiceProjectId;
        target.SourcePath = import.SourcePath;
        target.InvocationHint = import.InvocationHint;
        target.IsWorkflow = import.IsWorkflow;

        if (!targetIsBuiltIn)
        {
            target.Description = import.Description ?? "";
            target.Content = import.Content ?? "";
            target.Source = string.IsNullOrWhiteSpace(import.Source) ? "imported" : import.Source;
            target.DeepLink = import.DeepLink;
            target.Tags = import.Tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            target.IsBuiltIn = import.IsBuiltIn;
            target.CreatedAt = import.CreatedAt;
        }
    }

    private static (int Added, int Updated) MergeScriptTemplates(List<ScriptTemplate> target, IEnumerable<ScriptTemplate>? imports)
    {
        var added = 0;
        var updated = 0;

        foreach (var import in imports?.OfType<ScriptTemplate>() ?? Enumerable.Empty<ScriptTemplate>())
        {
            if (string.IsNullOrWhiteSpace(import.Name))
                continue;

            var existing = FindScriptTemplate(target, import);
            if (existing is null)
            {
                target.Add(CloneScriptTemplate(import));
                added++;
            }
            else
            {
                ApplyScriptTemplate(existing, import);
                updated++;
            }
        }

        return (added, updated);
    }

    private static ScriptTemplate? FindScriptTemplate(IEnumerable<ScriptTemplate> target, ScriptTemplate import)
    {
        var byId = target.FirstOrDefault(template => import.Id != Guid.Empty && template.Id == import.Id);
        if (byId is not null)
            return byId;

        var importKey = ScriptTemplateKey(import);
        return target.FirstOrDefault(template => ScriptTemplateKey(template).Equals(importKey, StringComparison.OrdinalIgnoreCase));
    }

    private static ScriptTemplate CloneScriptTemplate(ScriptTemplate import)
    {
        var clone = new ScriptTemplate { Id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id };
        ApplyScriptTemplate(clone, import);
        return clone;
    }

    private static void ApplyScriptTemplate(ScriptTemplate target, ScriptTemplate import)
    {
        target.Name = import.Name.Trim();
        target.Description = import.Description ?? "";
        target.ScriptType = string.IsNullOrWhiteSpace(import.ScriptType) ? "PowerShell" : import.ScriptType;
        target.Content = import.Content ?? "";
        target.Parameters = import.Parameters?.Where(parameter => !string.IsNullOrWhiteSpace(parameter)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        target.IsBuiltIn = import.IsBuiltIn;
    }

    private static (int Added, int Updated) MergeMemories(List<Memory> target, IEnumerable<Memory>? imports)
    {
        var added = 0;
        var updated = 0;

        foreach (var import in imports?.OfType<Memory>() ?? Enumerable.Empty<Memory>())
        {
            if (string.IsNullOrWhiteSpace(import.Key) || string.IsNullOrWhiteSpace(import.Content))
                continue;

            var existing = FindMemory(target, import);
            if (existing is null)
            {
                target.Add(CloneMemory(import));
                added++;
            }
            else
            {
                ApplyMemory(existing, import);
                updated++;
            }
        }

        return (added, updated);
    }

    private static Memory? FindMemory(IEnumerable<Memory> target, Memory import)
    {
        var byId = target.FirstOrDefault(memory => import.Id != Guid.Empty && memory.Id == import.Id);
        if (byId is not null)
            return byId;

        var importKey = MemoryKey(import);
        return target.FirstOrDefault(memory => MemoryKey(memory).Equals(importKey, StringComparison.OrdinalIgnoreCase));
    }

    private static Memory CloneMemory(Memory import)
    {
        var clone = new Memory { Id = import.Id == Guid.Empty ? Guid.NewGuid() : import.Id };
        ApplyMemory(clone, import);
        clone.CreatedAt = import.CreatedAt;
        return clone;
    }

    private static void ApplyMemory(Memory target, Memory import)
    {
        target.Key = import.Key.Trim();
        target.Content = import.Content.Trim();
        target.Category = string.IsNullOrWhiteSpace(import.Category) ? "General" : import.Category.Trim();
        target.Source = string.IsNullOrWhiteSpace(import.Source) ? "imported" : import.Source;
        target.UpdatedAt = import.UpdatedAt;
    }

    private static string ServiceProjectKey(ServiceProject project)
        => NormalizeKey(project.Name, project.AdoOrgUrl, project.AdoProjectName);

    private static string PipelineKey(PipelineConfig pipeline)
        => NormalizeKey(pipeline.PipelineType, pipeline.DisplayName, pipeline.Name, pipeline.AdoUrl);

    private static string CapabilityKey(CapabilityDefinition capability)
        => NormalizeKey(capability.Kind, capability.Name);

    private static string ScriptTemplateKey(ScriptTemplate template)
        => NormalizeKey(template.ScriptType, template.Name);

    private static string MemoryKey(Memory memory)
        => NormalizeKey(memory.Category, memory.Key);

    private static string NormalizeKey(params string?[] parts)
        => string.Join("|", parts.Select(part => (part ?? "").Trim().TrimEnd('/')).Where(part => part.Length > 0));

    private static void SetIfChanged<T>(ref bool changed, T current, T value, Action<T> apply, bool requireValue = false)
    {
        if (requireValue && value is null)
            return;

        if (requireValue && value is string text && string.IsNullOrWhiteSpace(text))
            return;

        if (EqualityComparer<T>.Default.Equals(current, value))
            return;

        apply(value);
        changed = true;
    }
}
