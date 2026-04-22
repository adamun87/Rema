using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private const string ExternalSkillGlyph = "\u26A1";
    private const string ExternalAgentGlyph = "🤖";

    private CopilotCatalogSnapshot GetExternalCatalog()
        => GetExternalCatalog(GetEffectiveWorkingDirectory());

    private static CopilotCatalogSnapshot GetExternalCatalog(string workDir)
        => CopilotConfigCatalog.Discover(workDir);

    private CopilotSkillDefinition? FindExternalSkillByName(string name)
        => GetExternalCatalog().FindSkill(name);

    private CopilotAgentDefinition? FindExternalAgentByName(string name)
        => GetExternalCatalog().FindAgent(name);

    private static CopilotSkillDefinition? FindExternalSkillByName(CopilotCatalogSnapshot catalog, string? name)
        => catalog.FindSkill(name);

    private static CopilotAgentDefinition? FindExternalAgentByName(CopilotCatalogSnapshot catalog, string? name)
        => catalog.FindAgent(name);

    private List<SkillReference> BuildSkillReferences(IReadOnlyCollection<Guid> skillIds, IReadOnlyCollection<string> externalSkillNames)
    {
        var references = BuildSkillReferences(skillIds);
        if (externalSkillNames.Count == 0)
            return references;

        foreach (var skill in ResolveExternalSkills(GetExternalCatalog(), externalSkillNames))
        {
            references.Add(CreateExternalSkillReference(skill));
        }

        return references;
    }

    private static bool SkillNameListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static SkillReference CreateExternalSkillReference(CopilotSkillDefinition skill)
    {
        return new SkillReference
        {
            Name = skill.Name,
            Glyph = ExternalSkillGlyph,
            Description = skill.Description
        };
    }

    // File-based Copilot skills are not registered as persistent SDK session skills,
    // so selected ones must be resolved and reapplied from the catalog when needed.
    private static List<CopilotSkillDefinition> ResolveExternalSkills(
        CopilotCatalogSnapshot externalCatalog,
        IReadOnlyCollection<string> externalSkillNames)
    {
        var skills = new List<CopilotSkillDefinition>(externalSkillNames.Count);
        foreach (var externalSkillName in externalSkillNames)
        {
            var skill = FindExternalSkillByName(externalCatalog, externalSkillName);
            if (skill is not null)
                skills.Add(skill);
        }

        return skills;
    }

    private string AppendAvailableExternalSkillsToPrompt(string? systemPrompt, IReadOnlyList<CopilotSkillDefinition> externalSkills)
    {
        if (externalSkills.Count == 0)
            return systemPrompt ?? string.Empty;

        var internalSkillNames = _dataStore.Data.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var promptSkills = externalSkills
            .Where(skill => !internalSkillNames.Contains(skill.Name))
            .ToList();
        if (promptSkills.Count == 0)
            return systemPrompt ?? string.Empty;

        var activeSkillNames = _activeExternalSkillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder(systemPrompt ?? string.Empty);
        builder.Append("""


            --- Additional Available Skills ---
            These file-based Copilot skills are also available. Use `fetch_skill` to retrieve their full content when relevant.

            """);

        foreach (var skill in promptSkills)
        {
            var activeMarker = activeSkillNames.Contains(skill.Name) ? " ✓" : string.Empty;
            var description = string.IsNullOrWhiteSpace(skill.Description)
                ? "Available from Copilot config"
                : skill.Description;
            builder.Append("- **")
                .Append(skill.Name)
                .Append("**: ")
                .Append(description)
                .Append(activeMarker)
                .Append('\n');
        }

        return builder.ToString();
    }
}
