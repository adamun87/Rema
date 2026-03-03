using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Skills, MCP servers, agents, projects, and attachment management.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Whether the agent can still be changed (only before the first message is sent).</summary>
    public bool CanChangeAgent => CurrentChat is null || CurrentChat.Messages.Count == 0;

    public void SetActiveAgent(LumiAgent? agent)
    {
        // Don't allow switching agents once a chat has messages
        if (!CanChangeAgent) return;

        ActiveAgent = agent;
        if (CurrentChat is not null)
        {
            CurrentChat.AgentId = agent?.Id;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    /// <summary>Assigns a project to the current (or next) chat. Called when a project filter is active.</summary>
    public void SetProjectId(Guid projectId)
    {
        if (CurrentChat is not null)
        {
            var changed = CurrentChat.ProjectId != projectId;
            CurrentChat.ProjectId = projectId;
            QueueSaveChat(CurrentChat, saveIndex: true);
            if (changed)
                OnPropertyChanged(nameof(CurrentChat));

            // If project context changed on an existing chat, force a fresh Copilot session
            // so the next turn uses the updated project system prompt.
            if (changed && CurrentChat.CopilotSessionId is not null)
            {
                InvalidateCurrentSession();
                _pendingSkillInjections.Clear();
            }
        }
        else
        {
            // Will be applied when the chat is created in SendMessage
            _pendingProjectId = projectId;
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    private Guid? _pendingProjectId;

    /// <summary>
    /// Current project filter from the shell sidebar. Used as a fallback when creating a new chat
    /// to avoid losing project context due UI timing or unchanged filter selections.
    /// </summary>
    private Guid? _activeProjectFilterId;
    public Guid? ActiveProjectFilterId
    {
        get => _activeProjectFilterId;
        set
        {
            if (_activeProjectFilterId == value)
                return;

            _activeProjectFilterId = value;
            SyncComposerProjectSelectionFromState();
            RefreshProjectBadge();
        }
    }

    /// <summary>Removes the project assignment from the current chat.</summary>
    public void ClearProjectId()
    {
        if (CurrentChat is not null)
        {
            var changed = CurrentChat.ProjectId is not null;
            CurrentChat.ProjectId = null;
            QueueSaveChat(CurrentChat, saveIndex: true);
            if (changed)
                OnPropertyChanged(nameof(CurrentChat));

            if (changed && CurrentChat.CopilotSessionId is not null)
            {
                InvalidateCurrentSession();
                _pendingSkillInjections.Clear();
            }
        }
        else
        {
            _pendingProjectId = null;
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    public void AddSkill(Skill skill)
    {
        if (ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
        // If added to an existing chat with a session, inject via next message instead of system prompt
        if (CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    /// <summary>Registers a skill ID without adding a chip (composer already added it).</summary>
    public void RegisterSkillIdByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is null || ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        // If added to an existing chat with a session, inject via next message
        if (CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    private void SyncActiveSkillsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(ActiveSkillIds);
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    public void RemoveSkillByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is null) return;
        ActiveSkillIds.Remove(skill.Id);
        var chip = ActiveSkillChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveSkillChips.Remove(chip);
        SyncActiveSkillsToChat();
    }

    public void AddMcpServer(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        var server = _dataStore.Data.McpServers.FirstOrDefault(s => s.Name == name);
        if (server is null) return;
        ActiveMcpServerNames.Add(name);
        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name));
        SyncActiveMcpsToChat();
    }

    /// <summary>Registers an MCP server name without adding a chip (composer already added it).</summary>
    public void RegisterMcpByName(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        var server = _dataStore.Data.McpServers.FirstOrDefault(s => s.Name == name);
        if (server is null) return;
        ActiveMcpServerNames.Add(name);
        SyncActiveMcpsToChat();
    }

    public void RemoveMcpByName(string name)
    {
        ActiveMcpServerNames.Remove(name);
        var chip = ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveMcpChips.Remove(chip);
        SyncActiveMcpsToChat();
    }

    public void SyncActiveMcpsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveMcpServerNames = new List<string>(ActiveMcpServerNames);
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    /// <summary>Populate ActiveMcpChips and ActiveMcpServerNames with all enabled MCP servers (default state).</summary>
    public void PopulateDefaultMcps()
    {
        IsLoadingChat = true;
        try
        {
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            foreach (var server in _dataStore.Data.McpServers.Where(s => s.IsEnabled))
            {
                ActiveMcpServerNames.Add(server.Name);
                ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(server.Name));
            }
        }
        finally
        {
            IsLoadingChat = false;
        }
    }

    /// <summary>Returns StrataComposerChip items for all agents (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetAgentChips()
    {
        return _dataStore.Data.Agents
            .Select(a => new StrataTheme.Controls.StrataComposerChip(a.Name, a.IconGlyph))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all skills (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetSkillChips()
    {
        return _dataStore.Data.Skills
            .Select(s => new StrataTheme.Controls.StrataComposerChip(s.Name, s.IconGlyph))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all enabled MCP servers (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetMcpChips()
    {
        return _dataStore.Data.McpServers
            .Where(s => s.IsEnabled)
            .Select(s => new StrataTheme.Controls.StrataComposerChip(s.Name))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all projects (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetProjectChips()
    {
        return _dataStore.Data.Projects
            .Select(p => new StrataTheme.Controls.StrataComposerChip(p.Name, "📁"))
            .ToList();
    }

    /// <summary>Selects a project by name (called from composer autocomplete).</summary>
    public void SelectProjectByName(string name)
    {
        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Name == name);
        if (project is not null)
            SetProjectId(project.Id);
    }

    /// <summary>Returns the display name of the current project, or null.</summary>
    public string? GetCurrentProjectName()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        if (!pid.HasValue) return null;
        return _dataStore.Data.Projects.FirstOrDefault(p => p.Id == pid.Value)?.Name;
    }

    /// <summary>Selects an agent by name (called from composer autocomplete).</summary>
    public void SelectAgentByName(string name)
    {
        var agent = _dataStore.Data.Agents.FirstOrDefault(a => a.Name == name);
        SetActiveAgent(agent);
    }

    /// <summary>Adds a skill by name (called from composer autocomplete).</summary>
    public void AddSkillByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is not null) AddSkill(skill);
    }

    /// <summary>Finds a skill by name for display purposes (e.g. fetching icon glyph).</summary>
    public Skill? FindSkillByName(string name)
    {
        return _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void AddAttachment(string filePath)
    {
        if (PendingAttachments.Contains(filePath))
            return;

        PendingAttachments.Add(filePath);
        PendingAttachmentItems.Add(new FileAttachmentItem(filePath, isRemovable: true, removeAction: RemoveAttachment));
    }

    public void RemoveAttachment(string filePath)
    {
        PendingAttachments.Remove(filePath);

        var pendingItem = PendingAttachmentItems.FirstOrDefault(item =>
            string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (pendingItem is not null)
            PendingAttachmentItems.Remove(pendingItem);
    }

    private readonly FileSearchService _fileSearchService = new();

    /// <summary>
    /// Searches for files in the current working directory matching the query.
    /// Returns StrataComposerChip items where Name is the relative display path
    /// and Glyph stores the full absolute path (for selection).
    /// </summary>
    public List<StrataTheme.Controls.StrataComposerChip> SearchFiles(string query, int maxResults = 20)
    {
        var workDir = GetEffectiveWorkingDirectory();
        var isProjectDir = workDir != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Require at least 1 character of query for user home (too many files otherwise)
        if (!isProjectDir && string.IsNullOrEmpty(query))
            return [];

        var maxDepth = isProjectDir ? 10 : 4;
        return _fileSearchService.Search(workDir, query, maxResults, maxDepth)
            .ConvertAll(r => new StrataTheme.Controls.StrataComposerChip(r.RelativePath, r.FullPath));
    }

    /// <summary>
    /// Resolves the effective working directory, checking pending/active project
    /// even before a chat is created (when CurrentChat is still null).
    /// </summary>
    private string GetEffectiveWorkingDirectory()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        if (pid.HasValue)
        {
            var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == pid.Value);
            if (project is { WorkingDirectory: { Length: > 0 } dir } && Directory.Exists(dir))
                return dir;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private List<UserMessageDataAttachmentsItemFile>? TakePendingAttachments()
    {
        if (PendingAttachments.Count == 0) return null;
        var items = PendingAttachments.Select(fp => new UserMessageDataAttachmentsItemFile
        {
            Path = fp,
            DisplayName = Path.GetFileName(fp)
        }).ToList();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();
        return items;
    }
}
