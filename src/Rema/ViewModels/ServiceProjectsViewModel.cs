using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public partial class ServiceProjectsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly DeploymentVersionDiscoveryService _deploymentVersionDiscoveryService;
    private readonly SafeFlyDiffService _safeFlyDiffService = new();

    [ObservableProperty] private ServiceProject? _selectedProject;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editRepoPath = "";
    [ObservableProperty] private string _editAdoOrgUrl = "";
    [ObservableProperty] private string _editAdoProjectName = "";
    [ObservableProperty] private string _editKustoCluster = "";
    [ObservableProperty] private string _editKustoDatabase = "";
    [ObservableProperty] private string _editInstructions = "";
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoveryStatus = "";
    [ObservableProperty] private string _safeFlyFromVersion = "";
    [ObservableProperty] private string _safeFlyToVersion = "HEAD";
    [ObservableProperty] private string _safeFlyOutputDirectory = "";
    [ObservableProperty] private string _safeFlyStatus = "";
    [ObservableProperty] private string _safeFlyVersionStatus = "";
    [ObservableProperty] private bool _isCreatingSafeFlyRequest;
    [ObservableProperty] private bool _isDiscoveringSafeFlyVersions;
    [ObservableProperty] private bool _hasSafeFlyVersionEvidence;

    public ObservableCollection<ServiceProject> Projects { get; } = [];
    public ObservableCollection<string> DiscoveryLog { get; } = [];
    public ObservableCollection<DeploymentVersionEvidenceDisplay> SafeFlyVersionEvidence { get; } = [];

    /// <summary>Raised when the user clicks Browse — the View handles the folder picker dialog.</summary>
    public event Action? BrowseRepoPathRequested;
    public event Action? BrowseSafeFlyOutputRequested;

    public ServiceProjectsViewModel(
        DataStore dataStore,
        CopilotService copilotService,
        AzureDevOpsService azureDevOpsService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _deploymentVersionDiscoveryService = new DeploymentVersionDiscoveryService(azureDevOpsService);
        Refresh();
    }

    public void Refresh()
    {
        Projects.Clear();
        foreach (var p in _dataStore.Data.ServiceProjects.OrderBy(p => p.Name))
            Projects.Add(p);
    }

    [RelayCommand]
    private void NewProject()
    {
        SelectedProject = null;
        ClearEditFields();
        IsEditing = true;
    }

    [RelayCommand]
    private void EditProject(ServiceProject project)
    {
        SelectedProject = project;
    }

    [RelayCommand]
    private void BrowseRepoPath() => BrowseRepoPathRequested?.Invoke();

    [RelayCommand]
    private void BrowseSafeFlyOutput() => BrowseSafeFlyOutputRequested?.Invoke();

    private void ClearEditFields()
    {
        EditName = "";
        EditRepoPath = "";
        EditAdoOrgUrl = "";
        EditAdoProjectName = "";
        EditKustoCluster = "";
        EditKustoDatabase = "";
        EditInstructions = "";
        SafeFlyFromVersion = "";
        SafeFlyToVersion = "HEAD";
        SafeFlyOutputDirectory = "";
        SafeFlyStatus = "";
        SafeFlyVersionStatus = "";
        SafeFlyVersionEvidence.Clear();
        HasSafeFlyVersionEvidence = false;
        DiscoveryStatus = "";
        DiscoveryLog.Clear();
        EditPipelines.Clear();
    }

    // ── Discovery ──

    private void LogStep(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DiscoveryLog.Add(message);
            DiscoveryStatus = message;
        });
    }

    [RelayCommand]
    private async Task DiscoverFromRepoAsync()
    {
        var repoPath = EditRepoPath.Trim();
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            DiscoveryStatus = "⚠ Directory not found";
            return;
        }

        IsDiscovering = true;
        DiscoveryLog.Clear();
        var details = new List<string>();

        // ── Step 1: Directory name ──
        LogStep("📂 Reading directory…");
        await Task.Delay(80); // yield for UI

        if (string.IsNullOrWhiteSpace(EditName))
        {
            EditName = Path.GetFileName(repoPath) ?? "";
            if (!string.IsNullOrWhiteSpace(EditName)) details.Add("name");
        }

        // ── Step 2: Git config ──
        LogStep("🔗 Scanning git config for ADO remote…");
        await Task.Delay(50);

        var gitConfigPath = Path.Combine(repoPath, ".git", "config");
        if (File.Exists(gitConfigPath))
        {
            try
            {
                var gitConfig = File.ReadAllText(gitConfigPath);
                var adoMatch = Regex.Match(
                    gitConfig,
                    @"url\s*=\s*https://(?:dev\.azure\.com|[^/]+\.visualstudio\.com)/([^/]+)/([^/]+?)(?:/_git/|\.git)",
                    RegexOptions.IgnoreCase);

                if (adoMatch.Success)
                {
                    if (string.IsNullOrWhiteSpace(EditAdoOrgUrl))
                    {
                        EditAdoOrgUrl = $"https://dev.azure.com/{adoMatch.Groups[1].Value}";
                        details.Add("ADO org");
                        LogStep($"  ✓ ADO org: {EditAdoOrgUrl}");
                    }
                    if (string.IsNullOrWhiteSpace(EditAdoProjectName))
                    {
                        EditAdoProjectName = adoMatch.Groups[2].Value;
                        details.Add("ADO project");
                        LogStep($"  ✓ ADO project: {EditAdoProjectName}");
                    }
                }
            }
            catch { }
        }

        // ── Step 2b: MCP config files ──
        LogStep("🔌 Scanning MCP configuration files…");
        await Task.Delay(50);

        var mcpInfo = ScanMcpConfigs(repoPath);
        if (mcpInfo.adoOrgUrl is not null && string.IsNullOrWhiteSpace(EditAdoOrgUrl))
        {
            EditAdoOrgUrl = mcpInfo.adoOrgUrl;
            details.Add("ADO org (MCP)");
            LogStep($"  ✓ ADO org from MCP: {mcpInfo.adoOrgUrl}");
        }
        if (mcpInfo.adoProject is not null && string.IsNullOrWhiteSpace(EditAdoProjectName))
        {
            EditAdoProjectName = mcpInfo.adoProject;
            details.Add("ADO project (MCP)");
            LogStep($"  ✓ ADO project from MCP: {mcpInfo.adoProject}");
        }
        if (mcpInfo.kustoCluster is not null && string.IsNullOrWhiteSpace(EditKustoCluster))
        {
            EditKustoCluster = mcpInfo.kustoCluster;
            details.Add("Kusto cluster (MCP)");
            LogStep($"  ✓ Kusto cluster from MCP: {mcpInfo.kustoCluster}");
        }
        if (mcpInfo.kustoDatabase is not null && string.IsNullOrWhiteSpace(EditKustoDatabase))
        {
            EditKustoDatabase = mcpInfo.kustoDatabase;
            details.Add("Kusto database (MCP)");
            LogStep($"  ✓ Kusto database from MCP: {mcpInfo.kustoDatabase}");
        }

        // ── Step 3: Kusto scanning ──
        LogStep("🔍 Scanning for Kusto references…");
        await Task.Delay(50);

        if (string.IsNullOrWhiteSpace(EditKustoCluster))
        {
            var kustoInfo = ScanForKusto(repoPath);
            if (kustoInfo.cluster is not null)
            {
                EditKustoCluster = kustoInfo.cluster;
                details.Add("Kusto cluster");
                LogStep($"  ✓ Kusto cluster: {kustoInfo.cluster}");
            }
            if (kustoInfo.database is not null && string.IsNullOrWhiteSpace(EditKustoDatabase))
            {
                EditKustoDatabase = kustoInfo.database;
                details.Add("Kusto database");
                LogStep($"  ✓ Kusto database: {kustoInfo.database}");
            }
        }

        // ── Step 4: Instructions gathering ──
        LogStep("📄 Gathering instructions & memory banks…");
        await Task.Delay(50);

        string gatheredContent = "";
        if (string.IsNullOrWhiteSpace(EditInstructions))
        {
            var instructions = GatherInstructions(repoPath);
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                EditInstructions = instructions;
                gatheredContent = instructions;
                details.Add("instructions");
                LogStep("  ✓ Found instruction/memory files");
            }
        }
        else
        {
            gatheredContent = EditInstructions;
        }

        // ── Step 5: Pipeline detection ──
        LogStep("🚀 Scanning for pipelines…");
        await Task.Delay(50);

        var pipelineInfo = ScanForPipelines(repoPath);
        if (pipelineInfo is not null)
        {
            details.Add("pipelines");
            LogStep($"  ✓ Pipelines: {pipelineInfo}");
        }

        // ── Step 6: LLM analysis of gathered content ──
        if (!string.IsNullOrWhiteSpace(gatheredContent) && _copilotService.IsConnected)
        {
            bool needsLlm = string.IsNullOrWhiteSpace(EditAdoOrgUrl)
                          || string.IsNullOrWhiteSpace(EditAdoProjectName)
                          || string.IsNullOrWhiteSpace(EditKustoCluster)
                          || string.IsNullOrWhiteSpace(EditKustoDatabase);

            if (needsLlm)
            {
                LogStep("🤖 Analyzing instructions with AI…");
                try
                {
                    var llmResult = await AnalyzeWithLlmAsync(gatheredContent);
                    if (llmResult is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(llmResult.AdoOrgUrl) && string.IsNullOrWhiteSpace(EditAdoOrgUrl))
                        {
                            EditAdoOrgUrl = llmResult.AdoOrgUrl;
                            details.Add("ADO org (AI)");
                            LogStep($"  ✓ AI found ADO org: {llmResult.AdoOrgUrl}");
                        }
                        if (!string.IsNullOrWhiteSpace(llmResult.AdoProject) && string.IsNullOrWhiteSpace(EditAdoProjectName))
                        {
                            EditAdoProjectName = llmResult.AdoProject;
                            details.Add("ADO project (AI)");
                            LogStep($"  ✓ AI found ADO project: {llmResult.AdoProject}");
                        }
                        if (!string.IsNullOrWhiteSpace(llmResult.KustoCluster) && string.IsNullOrWhiteSpace(EditKustoCluster))
                        {
                            EditKustoCluster = llmResult.KustoCluster;
                            details.Add("Kusto cluster (AI)");
                            LogStep($"  ✓ AI found Kusto cluster: {llmResult.KustoCluster}");
                        }
                        if (!string.IsNullOrWhiteSpace(llmResult.KustoDatabase) && string.IsNullOrWhiteSpace(EditKustoDatabase))
                        {
                            EditKustoDatabase = llmResult.KustoDatabase;
                            details.Add("Kusto database (AI)");
                            LogStep($"  ✓ AI found Kusto database: {llmResult.KustoDatabase}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogStep($"  ⚠ AI analysis failed: {ex.Message}");
                }
            }
        }

        // ── Done ──
        IsDiscovering = false;
        DiscoveryStatus = details.Count > 0
            ? $"✅ Discovered: {string.Join(", ", details)}"
            : "No additional info found";
        LogStep(DiscoveryStatus);
    }

    // ── LLM Analysis ──

    private record LlmDiscoveryResult(string? AdoOrgUrl, string? AdoProject, string? KustoCluster, string? KustoDatabase);

    private async Task<LlmDiscoveryResult?> AnalyzeWithLlmAsync(string content)
    {
        const string systemPrompt = """
            You are a project configuration extractor. Analyze the provided project documentation and extract:
            1. Azure DevOps organization URL (e.g., https://dev.azure.com/myorg)
            2. Azure DevOps project name
            3. Kusto/ADX cluster URL (e.g., https://mycluster.kusto.windows.net)
            4. Kusto/ADX database name

            Respond ONLY in this exact format, one per line. Use EMPTY for values not found:
            ADO_ORG=<value or EMPTY>
            ADO_PROJECT=<value or EMPTY>
            KUSTO_CLUSTER=<value or EMPTY>
            KUSTO_DATABASE=<value or EMPTY>

            Look for mentions of Azure DevOps, ADO, dev.azure.com, visualstudio.com, Kusto, ADX,
            Data Explorer, cluster URLs, database names in connection strings, config references,
            pipeline definitions, and any linked documentation.
            Do NOT explain your reasoning. Output ONLY the four lines.
            """;

        // Cap content to avoid token limits — keep it short for faster response
        if (content.Length > 4000)
            content = content[..4000] + "\n…(truncated)";

        LogStep("  ⏳ Waiting for AI response…");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var response = await _copilotService.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = systemPrompt,
                Streaming = false,
            },
            async (session, ct) =>
            {
                LogStep("  📡 Session created, sending prompt…");
                var result = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = content },
                    TimeSpan.FromMinutes(3),
                    ct).ConfigureAwait(false);

                var text = result?.Data?.Content?.Trim();
                LogStep(text is not null
                    ? $"  📨 Got response ({text.Length} chars)"
                    : "  ⚠ Empty response from AI");
                return text;
            },
            cts.Token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response)) return null;

        return ParseLlmResponse(response);
    }

    private static LlmDiscoveryResult? ParseLlmResponse(string response)
    {
        string? Extract(string key)
        {
            var match = Regex.Match(response, $@"{key}=(.+)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var val = match.Groups[1].Value.Trim();
            return string.Equals(val, "EMPTY", StringComparison.OrdinalIgnoreCase) ? null : val;
        }

        return new LlmDiscoveryResult(
            Extract("ADO_ORG"),
            Extract("ADO_PROJECT"),
            Extract("KUSTO_CLUSTER"),
            Extract("KUSTO_DATABASE"));
    }

    // ── File Scanning Helpers ──

    /// <summary>Scan MCP config files (.mcp.json, mcp.json, .vscode/mcp.json, etc.) for ADO and Kusto references.</summary>
    private static (string? adoOrgUrl, string? adoProject, string? kustoCluster, string? kustoDatabase) ScanMcpConfigs(string repoPath)
    {
        string? adoOrg = null, adoProject = null, kustoCluster = null, kustoDb = null;

        // Well-known MCP config locations
        string[] mcpPaths = [
            ".mcp.json",
            "mcp.json",
            ".vscode/mcp.json",
            ".cursor/mcp.json",
            ".ai/mcp.json",
            "mcp-servers.json",
        ];

        var adoOrgRx = new Regex(
            @"https://dev\.azure\.com/([a-zA-Z0-9\-_]+)",
            RegexOptions.IgnoreCase);
        var adoVstsRx = new Regex(
            @"https://([a-zA-Z0-9\-_]+)\.visualstudio\.com",
            RegexOptions.IgnoreCase);
        var adoProjectRx = new Regex(
            @"""(?:project|projectName|ado[_-]?project)""?\s*[:=]\s*""([^""]+)""",
            RegexOptions.IgnoreCase);
        var kustoClusterRx = new Regex(
            @"https://([a-zA-Z0-9\-]+)\.(?:kusto\.windows\.net|kustomfa\.windows\.net|kusto\.data\.microsoft\.com)",
            RegexOptions.IgnoreCase);
        var kustoDatabaseRx = new Regex(
            @"""(?:database|kustoDatabase|db)""?\s*[:=]\s*""([^""]+)""",
            RegexOptions.IgnoreCase);

        foreach (var rel in mcpPaths)
        {
            var fullPath = Path.Combine(repoPath, rel);
            if (!File.Exists(fullPath)) continue;

            try
            {
                var content = File.ReadAllText(fullPath);

                if (adoOrg is null)
                {
                    var m = adoOrgRx.Match(content);
                    if (m.Success) adoOrg = $"https://dev.azure.com/{m.Groups[1].Value}";
                    else
                    {
                        var mv = adoVstsRx.Match(content);
                        if (mv.Success) adoOrg = $"https://dev.azure.com/{mv.Groups[1].Value}";
                    }
                }
                if (adoProject is null)
                {
                    var m = adoProjectRx.Match(content);
                    if (m.Success) adoProject = m.Groups[1].Value;
                }
                if (kustoCluster is null)
                {
                    var m = kustoClusterRx.Match(content);
                    if (m.Success) kustoCluster = $"https://{m.Groups[1].Value}.kusto.windows.net";
                }
                if (kustoDb is null)
                {
                    var m = kustoDatabaseRx.Match(content);
                    if (m.Success) kustoDb = m.Groups[1].Value;
                }

                // Also scan args arrays and env vars for URLs
                // MCP configs often have: "args": ["--org", "https://dev.azure.com/myorg", "--project", "MyProject"]
                var argsOrgRx = new Regex(@"""--(?:org|organization)""\s*,\s*""(https://[^""]+)""", RegexOptions.IgnoreCase);
                var argsProjectRx = new Regex(@"""--(?:project|project-name)""\s*,\s*""([^""]+)""", RegexOptions.IgnoreCase);

                if (adoOrg is null)
                {
                    var m = argsOrgRx.Match(content);
                    if (m.Success) adoOrg = m.Groups[1].Value;
                }
                if (adoProject is null)
                {
                    var m = argsProjectRx.Match(content);
                    if (m.Success) adoProject = m.Groups[1].Value;
                }
            }
            catch { }
        }

        return (adoOrg, adoProject, kustoCluster, kustoDb);
    }

    private static (string? cluster, string? database) ScanForKusto(string repoPath)
    {
        string? cluster = null, database = null;

        string[] searchFiles = ["appsettings.json", "appsettings.*.json", "local.settings.json", "*.csproj", "*.config"];
        string[] knownPaths = [
            ".ai/instructions.md", ".ai/config.json",
            "memory-bank/productContext.md", "memory-bank/techContext.md",
            "AGENTS.md", "README.md",
        ];

        var filesToScan = new List<string>();
        foreach (var known in knownPaths)
        {
            var fullPath = Path.Combine(repoPath, known);
            if (File.Exists(fullPath)) filesToScan.Add(fullPath);
        }

        foreach (var pattern in searchFiles)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(repoPath, pattern, SearchOption.TopDirectoryOnly))
                    filesToScan.Add(f);
            }
            catch { }
        }

        var srcDir = Path.Combine(repoPath, "src");
        if (Directory.Exists(srcDir))
        {
            foreach (var pattern in new[] { "appsettings.json", "appsettings.*.json" })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(srcDir, pattern, SearchOption.AllDirectories).Take(20))
                        filesToScan.Add(f);
                }
                catch { }
            }
        }

        var clusterRx = new Regex(
            @"https://([a-zA-Z0-9\-]+)\.(?:kusto\.windows\.net|kustomfa\.windows\.net|kusto\.data\.microsoft\.com)",
            RegexOptions.IgnoreCase);
        var dbRx = new Regex(
            @"""?(?:Initial\s+Catalog|Database|database|kustoDatabase)""?\s*[:=]\s*""?([a-zA-Z0-9_\-]+)",
            RegexOptions.IgnoreCase);

        foreach (var file in filesToScan.Distinct().Take(50))
        {
            if (cluster is not null && database is not null) break;
            try
            {
                var text = File.ReadAllText(file);
                if (cluster is null) { var m = clusterRx.Match(text); if (m.Success) cluster = $"https://{m.Groups[1].Value}.kusto.windows.net"; }
                if (database is null) { var m = dbRx.Match(text); if (m.Success) database = m.Groups[1].Value; }
            }
            catch { }
        }

        return (cluster, database);
    }

    private static string GatherInstructions(string repoPath)
    {
        var sections = new List<string>();

        (string path, string label)[] sources = [
            ("AGENTS.md", "Agent Instructions"),
            (".ai/instructions.md", "AI Instructions"),
            (".github/copilot-instructions.md", "Copilot Instructions"),
            ("memory-bank/productContext.md", "Product Context"),
            ("memory-bank/techContext.md", "Tech Context"),
            ("memory-bank/activeContext.md", "Active Context"),
        ];

        foreach (var (relPath, label) in sources)
        {
            var fullPath = Path.Combine(repoPath, relPath);
            if (!File.Exists(fullPath)) continue;
            try
            {
                var content = File.ReadAllText(fullPath).Trim();
                if (content.Length > 0)
                {
                    if (content.Length > 1500) content = content[..1500] + "\n…";
                    sections.Add($"## {label}\n{content}");

                    // Follow markdown links within the file to discover more content
                    var linkedContent = FollowMarkdownLinks(repoPath, fullPath, content);
                    if (!string.IsNullOrWhiteSpace(linkedContent))
                        sections.Add(linkedContent);
                }
            }
            catch { }
        }

        foreach (var skillDir in new[] { ".ai/skills", "skills" })
        {
            var fullDir = Path.Combine(repoPath, skillDir);
            if (!Directory.Exists(fullDir)) continue;
            try
            {
                foreach (var sf in Directory.EnumerateFiles(fullDir, "*.md", SearchOption.TopDirectoryOnly).Take(5))
                {
                    var name = Path.GetFileNameWithoutExtension(sf);
                    var text = File.ReadAllText(sf).Trim();
                    if (text.Length > 500) text = text[..500] + "\n…";
                    sections.Add($"## Skill: {name}\n{text}");
                }
            }
            catch { }
        }

        var result = string.Join("\n\n", sections);
        if (result.Length > 6000) result = result[..6000] + "\n\n…(truncated)";
        return result;
    }

    /// <summary>Parse markdown links in file content and read linked local files.</summary>
    private static string FollowMarkdownLinks(string repoPath, string sourceFile, string content)
    {
        var sections = new List<string>();
        var linkRx = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        foreach (Match m in linkRx.Matches(content))
        {
            var linkPath = m.Groups[2].Value;
            // Skip URLs
            if (linkPath.StartsWith("http://") || linkPath.StartsWith("https://")) continue;
            // Skip anchors
            if (linkPath.StartsWith("#")) continue;

            // Resolve relative to source file's directory, then to repo root
            var baseDir = Path.GetDirectoryName(sourceFile) ?? repoPath;
            var resolved = Path.GetFullPath(Path.Combine(baseDir, linkPath));

            // Security: ensure resolved path is within the repo
            if (!resolved.StartsWith(repoPath, StringComparison.OrdinalIgnoreCase)) continue;

            if (File.Exists(resolved))
            {
                try
                {
                    var linked = File.ReadAllText(resolved).Trim();
                    if (linked.Length > 800) linked = linked[..800] + "\n…";
                    var label = m.Groups[1].Value;
                    sections.Add($"### Linked: {label}\n{linked}");
                }
                catch { }
            }
        }

        return string.Join("\n\n", sections.Take(5));
    }

    private static string? ScanForPipelines(string repoPath)
    {
        var found = new List<string>();
        string[] pipelinePaths = [
            "azure-pipelines.yml", "azure-pipelines.yaml",
            ".azure-pipelines/*.yml", ".azure-pipelines/*.yaml",
            "pipelines/*.yml", "pipelines/*.yaml",
            "build/*.yml", "build/*.yaml",
            ".github/workflows/*.yml", ".github/workflows/*.yaml",
        ];

        foreach (var pattern in pipelinePaths)
        {
            var dir = Path.GetDirectoryName(pattern) ?? "";
            var file = Path.GetFileName(pattern);
            var searchDir = Path.Combine(repoPath, dir);
            if (!Directory.Exists(searchDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(searchDir, file, SearchOption.TopDirectoryOnly).Take(10))
                    found.Add(Path.GetFileName(f));
            }
            catch { }
        }

        return found.Count > 0 ? string.Join(", ", found) : null;
    }

    // ── CRUD ──

    partial void OnSelectedProjectChanged(ServiceProject? value)
    {
        if (value is null)
        {
            if (!IsEditing) ClearEditFields();
            return;
        }
        EditName = value.Name;
        EditRepoPath = value.RepoPath;
        EditAdoOrgUrl = value.AdoOrgUrl;
        EditAdoProjectName = value.AdoProjectName;
        EditKustoCluster = value.KustoCluster ?? "";
        EditKustoDatabase = value.KustoDatabase ?? "";
        EditInstructions = value.Instructions ?? "";
        SafeFlyFromVersion = "";
        SafeFlyToVersion = "HEAD";
        SafeFlyOutputDirectory = string.IsNullOrWhiteSpace(value.RepoPath)
            ? ""
            : Path.Combine(value.RepoPath, "safefly-request");
        SafeFlyStatus = "";
        SafeFlyVersionStatus = "";
        SafeFlyVersionEvidence.Clear();
        HasSafeFlyVersionEvidence = false;
        DiscoveryLog.Clear();
        DiscoveryStatus = "";
        LoadPipelines(value);
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        bool isNew = SelectedProject is null;

        ServiceProject project;
        if (SelectedProject is not null)
            project = SelectedProject;
        else
        {
            project = new ServiceProject();
            _dataStore.Data.ServiceProjects.Add(project);
        }

        ApplyFieldsToProject(project);
        SavePipelines(project);
        _ = _dataStore.SaveAsync();
        Refresh();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);

        // Auto-discover on first save with a valid repo path
        if (isNew && !string.IsNullOrWhiteSpace(project.RepoPath) && Directory.Exists(project.RepoPath))
        {
            await DiscoverFromRepoAsync();
            ApplyFieldsToProject(project);
            SavePipelines(project);
            _ = _dataStore.SaveAsync();
        }
    }

    private void ApplyFieldsToProject(ServiceProject project)
    {
        project.Name = EditName.Trim();
        project.RepoPath = EditRepoPath.Trim();
        project.AdoOrgUrl = EditAdoOrgUrl.Trim();
        project.AdoProjectName = EditAdoProjectName.Trim();
        project.KustoCluster = string.IsNullOrWhiteSpace(EditKustoCluster) ? null : EditKustoCluster.Trim();
        project.KustoDatabase = string.IsNullOrWhiteSpace(EditKustoDatabase) ? null : EditKustoDatabase.Trim();
        project.Instructions = string.IsNullOrWhiteSpace(EditInstructions) ? null : EditInstructions.Trim();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        SelectedProject = null;
    }

    [RelayCommand]
    private void DeleteProject()
    {
        if (SelectedProject is null) return;
        _dataStore.Data.ServiceProjects.Remove(SelectedProject);
        _ = _dataStore.SaveAsync();
        SelectedProject = null;
        IsEditing = false;
        Refresh();
    }

    // ── Pipeline Management ──

    public ObservableCollection<PipelineEditItem> EditPipelines { get; } = [];

    [RelayCommand]
    private void AddPipeline()
    {
        EditPipelines.Add(new PipelineEditItem());
    }

    [RelayCommand]
    private void RemovePipeline(PipelineEditItem item)
    {
        EditPipelines.Remove(item);
    }

    private void LoadPipelines(ServiceProject project)
    {
        EditPipelines.Clear();
        foreach (var p in project.PipelineConfigs)
        {
            EditPipelines.Add(new PipelineEditItem
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                AdoUrl = p.AdoUrl,
                PipelineType = p.PipelineType,
                Description = p.Description,
            });
        }
    }

    private void SavePipelines(ServiceProject project)
    {
        project.PipelineConfigs.Clear();
        foreach (var item in EditPipelines)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayName) && string.IsNullOrWhiteSpace(item.AdoUrl))
                continue;
            project.PipelineConfigs.Add(new PipelineConfig
            {
                Id = item.Id,
                ServiceProjectId = project.Id,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? "" : item.DisplayName.Trim(),
                Name = item.DisplayName?.Trim() ?? "",
                AdoUrl = item.AdoUrl?.Trim() ?? "",
                AdoPipelineId = PipelineDefinitionIdResolver.Parse(item.AdoUrl),
                PipelineType = item.PipelineType ?? "yaml",
                Description = item.Description?.Trim() ?? "",
            });
        }
    }

    [RelayCommand]
    private async Task DiscoverSafeFlyVersionsAsync()
    {
        try
        {
            IsDiscoveringSafeFlyVersions = true;
            SafeFlyVersionStatus = "Discovering current deployed versions from ADO and telemetry configuration…";
            SafeFlyVersionEvidence.Clear();
            HasSafeFlyVersionEvidence = false;

            var evidence = await _deploymentVersionDiscoveryService.DiscoverAsync(_dataStore.Data.ServiceProjects);
            foreach (var item in evidence)
                SafeFlyVersionEvidence.Add(new DeploymentVersionEvidenceDisplay(item));

            HasSafeFlyVersionEvidence = SafeFlyVersionEvidence.Count > 0;
            SafeFlyVersionStatus = HasSafeFlyVersionEvidence
                ? $"Found {SafeFlyVersionEvidence.Count} deployed-version evidence item(s)."
                : "No ADO or telemetry version evidence was found. Add release pipelines or version telemetry queries to service projects.";

            if (string.IsNullOrWhiteSpace(SafeFlyFromVersion) && SelectedProject is not null)
            {
                var projectEvidence = evidence.FirstOrDefault(item =>
                    item.Source.Equals("ADO", StringComparison.OrdinalIgnoreCase)
                    && item.ServiceName.Equals(SelectedProject.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(item.Version));
                if (projectEvidence?.Version is not null)
                    SafeFlyFromVersion = projectEvidence.Version;
            }
        }
        catch (OperationCanceledException)
        {
            SafeFlyVersionStatus = "Version discovery was canceled.";
        }
        finally
        {
            IsDiscoveringSafeFlyVersions = false;
        }
    }

    [RelayCommand]
    private async Task CreateSafeFlyRequestAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            SafeFlyStatus = "Save or select a project before creating SafeFly request files.";
            return;
        }

        try
        {
            IsCreatingSafeFlyRequest = true;
            SafeFlyStatus = "Creating SafeFly request files…";

            var output = string.IsNullOrWhiteSpace(SafeFlyOutputDirectory)
                ? Path.Combine(project.RepoPath, "safefly-request")
                : SafeFlyOutputDirectory.Trim();

            var result = await _safeFlyDiffService.CreateRequestFilesAsync(
                project,
                SafeFlyFromVersion,
                SafeFlyToVersion,
                output,
                SafeFlyVersionEvidence.Select(e => e.Evidence).ToList());

            SafeFlyOutputDirectory = result.OutputDirectory;
            SafeFlyStatus = $"Created {result.Files.Count} SafeFly files for {result.ChangedFileCount} changed files.";
        }
        catch (InvalidOperationException ex)
        {
            SafeFlyStatus = ex.Message;
        }
        catch (IOException ex)
        {
            SafeFlyStatus = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeFlyStatus = ex.Message;
        }
        finally
        {
            IsCreatingSafeFlyRequest = false;
        }
    }

}

/// <summary>Editable pipeline row for the UI.</summary>
public partial class PipelineEditItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _adoUrl = "";
    [ObservableProperty] private string _pipelineType = "yaml";
    [ObservableProperty] private string _description = "";
}

public sealed class DeploymentVersionEvidenceDisplay
{
    public DeploymentVersionEvidenceDisplay(DeploymentVersionEvidence evidence)
    {
        Evidence = evidence;
    }

    public DeploymentVersionEvidence Evidence { get; }
    public string Source => Evidence.Source;
    public string ServiceName => Evidence.ServiceName;
    public string PipelineOrQueryName => Evidence.PipelineOrQueryName;
    public string Version => string.IsNullOrWhiteSpace(Evidence.Version) ? "Unknown version" : Evidence.Version!;
    public string Status => Evidence.Status;
    public string Details => Evidence.Details;
}
