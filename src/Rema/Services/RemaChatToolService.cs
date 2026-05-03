using System;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rema.Models;

namespace Rema.Services;

public static class RemaChatToolService
{
    public static List<AIFunction> CreateTools(DataStore dataStore, AzureDevOpsService azureDevOpsService,
        CopilotService? copilotService = null, Func<Guid?>? getCurrentChatId = null)
    {
        var safeFlyDiffService = new SafeFlyDiffService();
        var deploymentVersionDiscoveryService = new DeploymentVersionDiscoveryService(azureDevOpsService);

        List<AIFunction> tools =
        [
            AIFunctionFactory.Create(
                ([Description("Optional capability kind to filter by: Skill, Mcp, Tool, or Agent.")] string? kind = null) =>
                {
                    var query = dataStore.Data.Capabilities
                        .Where(c => c.IsEnabled);
                    if (!string.IsNullOrWhiteSpace(kind))
                        query = query.Where(c => c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

                    var result = query
                        .OrderBy(c => c.Kind)
                        .ThenBy(c => c.Name)
                        .Select(c => new
                        {
                            c.Kind,
                            c.Name,
                            c.Description,
                            c.Source,
                            c.ServiceProjectId,
                            c.SourcePath,
                            c.InvocationHint,
                            c.IsWorkflow,
                            Tags = c.Tags,
                        })
                        .ToList();

                    return JsonSerializer.Serialize(result);
                },
                "rema_list_capabilities",
                "List enabled Rema skills, MCP servers, tools, and agents, including repo-discovered capabilities."),

            AIFunctionFactory.Create(
                async (
                    [Description("Name of the skill, MCP server, tool, or agent to use.")] string capabilityName,
                    [Description("Optional user goal for this invocation or long-running workflow.")] string? goal = null) =>
                {
                    var capability = dataStore.Data.Capabilities.FirstOrDefault(c =>
                        c.IsEnabled && c.Name.Equals(capabilityName, StringComparison.OrdinalIgnoreCase));
                    if (capability is null)
                        throw new InvalidOperationException($"Capability '{capabilityName}' was not found or is disabled.");

                    WorkflowExecution? execution = null;
                    if (capability.IsWorkflow || capability.Kind.Equals("Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        execution = new WorkflowExecution
                        {
                            CapabilityId = capability.Id,
                            OriginatingChatId = getCurrentChatId?.Invoke(),
                            CapabilityName = capability.Name,
                            CapabilityKind = capability.Kind,
                            Goal = goal ?? "",
                            Status = "Running",
                            StartedAt = DateTimeOffset.Now,
                            UpdatedAt = DateTimeOffset.Now,
                        };
                        dataStore.Data.WorkflowExecutions.Add(execution);
                        await dataStore.SaveAsync();
                    }

                    var project = capability.ServiceProjectId is Guid projectId
                        ? dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == projectId)
                        : null;

                    return JsonSerializer.Serialize(new
                    {
                        capability.Kind,
                        capability.Name,
                        capability.Description,
                        Instructions = capability.Content,
                        capability.InvocationHint,
                        capability.Source,
                        capability.SourcePath,
                        ServiceProject = project?.Name,
                        Tags = capability.Tags,
                        WorkflowExecution = execution is null ? null : new
                        {
                            execution.Id,
                            execution.Status,
                            execution.StartedAt,
                        },
                    });
                },
                "rema_invoke_capability",
                "Retrieve the full prompt/invocation details for an enabled Rema capability and start a tracked workflow when it represents an agent or long-running workflow."),

            // ── Operation Tracking Tools ──

            AIFunctionFactory.Create(
                async (
                    [Description("Short description of what this operation does (e.g. 'Deploy ServiceX v2.1 to canary').")] string goal,
                    [Description("Optional name of the capability or workflow being executed.")] string? capabilityName = null,
                    [Description("Optional kind: Deployment, Build, Investigation, Workflow.")] string? kind = null) =>
                {
                    var execution = new WorkflowExecution
                    {
                        OriginatingChatId = getCurrentChatId?.Invoke(),
                        CapabilityName = capabilityName ?? "",
                        CapabilityKind = kind ?? "Workflow",
                        Goal = goal,
                        Status = "Running",
                        StartedAt = DateTimeOffset.Now,
                        UpdatedAt = DateTimeOffset.Now,
                    };
                    dataStore.Data.WorkflowExecutions.Add(execution);
                    await dataStore.SaveAsync();

                    return JsonSerializer.Serialize(new
                    {
                        execution.Id,
                        execution.Goal,
                        execution.Status,
                        execution.StartedAt,
                        message = "Operation registered on dashboard.",
                    });
                },
                "rema_register_operation",
                "Register a long-running operation on the Rema dashboard so the user can track progress. Call this when starting a multi-step workflow (build, deploy, investigate). Returns the operation ID for subsequent updates."),

            AIFunctionFactory.Create(
                async (
                    [Description("ID of the operation returned by rema_register_operation.")] string operationId,
                    [Description("New status: Running, Completed, Failed, or Canceled.")] string? status = null,
                    [Description("Progress percentage (0–100).")] int? progressPercent = null,
                    [Description("Name of the current step being executed.")] string? currentStep = null,
                    [Description("Log message describing what just happened.")] string? logMessage = null,
                    [Description("Final result summary (set when status is Completed).")] string? result = null,
                    [Description("Error details (set when status is Failed).")] string? error = null) =>
                {
                    if (!Guid.TryParse(operationId, out var id))
                        throw new InvalidOperationException("Invalid operation ID.");

                    var execution = dataStore.Data.WorkflowExecutions.FirstOrDefault(w => w.Id == id);
                    if (execution is null)
                        throw new InvalidOperationException($"Operation '{operationId}' not found.");

                    if (status is not null) execution.Status = status;
                    if (progressPercent.HasValue) execution.ProgressPercent = progressPercent.Value;
                    if (currentStep is not null) execution.CurrentStep = currentStep;
                    if (logMessage is not null) execution.LogMessages.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {logMessage}");
                    if (result is not null) execution.Result = result;
                    if (error is not null) execution.Error = error;
                    execution.UpdatedAt = DateTimeOffset.Now;

                    if (status is "Completed" or "Failed" or "Canceled")
                        execution.CompletedAt = DateTimeOffset.Now;

                    await dataStore.SaveAsync();

                    return JsonSerializer.Serialize(new
                    {
                        execution.Id,
                        execution.Status,
                        execution.ProgressPercent,
                        execution.CurrentStep,
                        execution.UpdatedAt,
                    });
                },
                "rema_update_operation",
                "Update the status, progress, or log of a tracked dashboard operation. Call this as you complete each step of a long-running workflow."),

            AIFunctionFactory.Create(
                (
                    [Description("Name of the service project being deployed.")] string serviceProjectName,
                    [Description("Build version or source branch being deployed.")] string buildVersion,
                    [Description("Ordered list of deployment stages (e.g. ['Build', 'Deploy Canary', 'Validate Canary', 'Deploy Prod']).")] List<string> stages,
                    [Description("List of target clusters or environments (e.g. ['canary-westus2', 'prod-eastus']).")] List<string> targetClusters,
                    [Description("List of clusters or stages explicitly excluded from this deployment.")] List<string>? excludedTargets = null,
                    [Description("Rollback strategy if a stage fails.")] string? rollbackStrategy = null,
                    [Description("Any additional notes about this deployment.")] string? notes = null) =>
                {
                    var project = dataStore.Data.ServiceProjects.FirstOrDefault(p =>
                        p.Name.Equals(serviceProjectName, StringComparison.OrdinalIgnoreCase));

                    var plan = new
                    {
                        ServiceProject = serviceProjectName,
                        BuildVersion = buildVersion,
                        DeploymentFlow = stages.Select((stage, i) => new
                        {
                            Step = i + 1,
                            Stage = stage,
                        }).ToList(),
                        TargetClusters = targetClusters,
                        ExcludedTargets = excludedTargets ?? [],
                        RollbackStrategy = rollbackStrategy ?? "Stop and alert on failure. No automatic rollback.",
                        Notes = notes,
                        ConfiguredPipelines = project?.PipelineConfigs.Select(p => new
                        {
                            p.DisplayName,
                            p.DeploymentStages,
                        }).ToList(),
                        Warning = "⚠️ Review the target clusters carefully. Confirm this plan before proceeding.",
                    };

                    return JsonSerializer.Serialize(plan);
                },
                "rema_propose_deployment_plan",
                "Present a structured deployment plan to the user for review BEFORE executing a multi-stage deployment. Shows stages, target clusters, exclusions, and rollback strategy. Always ask the user to confirm the plan before proceeding."),

            // ── ADO Pipeline Trigger & Branch Verification Tools ──

            AIFunctionFactory.Create(
                async (
                    [Description("Name of the saved Rema service project.")] string serviceProjectName,
                    [Description("Name or display name of the pipeline to trigger.")] string pipelineName,
                    [Description("Source branch to build (e.g. 'main', 'users/adam/my-fix', 'feature/xyz'). Do NOT omit — always confirm the branch with the user.")] string sourceBranch) =>
                {
                    var project = dataStore.Data.ServiceProjects.FirstOrDefault(p =>
                        p.Name.Equals(serviceProjectName, StringComparison.OrdinalIgnoreCase));
                    if (project is null)
                        throw new InvalidOperationException($"Service project '{serviceProjectName}' was not found.");

                    var pipeline = project.PipelineConfigs.FirstOrDefault(p =>
                        p.DisplayName.Equals(pipelineName, StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals(pipelineName, StringComparison.OrdinalIgnoreCase));
                    if (pipeline is null)
                        throw new InvalidOperationException(
                            $"Pipeline '{pipelineName}' was not found in project '{serviceProjectName}'. " +
                            $"Available: {string.Join(", ", project.PipelineConfigs.Select(p => p.DisplayName))}");

                    // Queue the build on the exact branch.
                    var snapshot = await azureDevOpsService.QueueBuildAsync(project, pipeline, sourceBranch);

                    // Verify ADO actually queued it on the requested branch.
                    var actualBranch = snapshot.SourceBranch ?? "";
                    var expectedRef = sourceBranch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)
                        ? sourceBranch
                        : $"refs/heads/{sourceBranch}";
                    var branchMatch = actualBranch.Equals(expectedRef, StringComparison.OrdinalIgnoreCase);

                    // Auto-track the build in the active shift so polling picks it up.
                    var activeShift = dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
                    TrackedItem? trackedItem = null;
                    if (activeShift is not null)
                    {
                        trackedItem = new TrackedItem
                        {
                            ShiftId = activeShift.Id,
                            ServiceProjectId = project.Id,
                            PipelineConfigId = pipeline.Id,
                        };
                        AzureDevOpsService.ApplySnapshot(trackedItem, snapshot);
                        dataStore.Data.TrackedItems.Add(trackedItem);
                        await dataStore.SaveAsync();
                    }

                    return JsonSerializer.Serialize(new
                    {
                        Success = true,
                        BuildId = snapshot.BuildId,
                        BuildNumber = snapshot.BuildNumber,
                        RequestedBranch = expectedRef,
                        ActualBranch = actualBranch,
                        BranchConfirmed = branchMatch,
                        BranchWarning = branchMatch
                            ? (string?)null
                            : $"⚠️ BRANCH MISMATCH: Requested '{expectedRef}' but ADO queued on '{actualBranch}'. The wrong code may be built!",
                        snapshot.Status,
                        snapshot.WebUrl,
                        TrackedItemId = trackedItem?.Id,
                        Message = branchMatch
                            ? $"✅ Build #{snapshot.BuildNumber} queued on {actualBranch}. Build ID {snapshot.BuildId} is now tracked."
                            : $"⚠️ Build #{snapshot.BuildNumber} queued but branch mismatch detected! Expected {expectedRef}, got {actualBranch}.",
                    });
                },
                "ado_trigger_pipeline",
                "Trigger a new Azure DevOps pipeline run on a SPECIFIC branch. Always confirm the source branch with the user before calling. Returns the build ID and verifies the branch matches. The build is automatically tracked in the active shift."),

            AIFunctionFactory.Create(
                async (
                    [Description("Name of the saved Rema service project.")] string serviceProjectName,
                    [Description("Name or display name of the pipeline.")] string pipelineName,
                    [Description("Branch to filter by (e.g. 'refs/heads/main', 'users/adam/my-fix'). Partial match is supported.")] string sourceBranch,
                    [Description("Only return successful builds.")] bool successfulOnly = true) =>
                {
                    var project = dataStore.Data.ServiceProjects.FirstOrDefault(p =>
                        p.Name.Equals(serviceProjectName, StringComparison.OrdinalIgnoreCase));
                    if (project is null)
                        throw new InvalidOperationException($"Service project '{serviceProjectName}' was not found.");

                    var pipeline = project.PipelineConfigs.FirstOrDefault(p =>
                        p.DisplayName.Equals(pipelineName, StringComparison.OrdinalIgnoreCase)
                        || p.Name.Equals(pipelineName, StringComparison.OrdinalIgnoreCase));
                    if (pipeline is null)
                        throw new InvalidOperationException(
                            $"Pipeline '{pipelineName}' was not found in project '{serviceProjectName}'.");

                    var branchRef = sourceBranch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)
                        ? sourceBranch
                        : $"refs/heads/{sourceBranch}";

                    var allBuilds = await azureDevOpsService.GetRecentBuildsAsync(project, pipeline, 50);
                    var filtered = allBuilds
                        .Where(b => (b.SourceBranch ?? "").Equals(branchRef, StringComparison.OrdinalIgnoreCase))
                        .Where(b => !successfulOnly || b.Result.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
                        .Take(10)
                        .Select(b => new
                        {
                            b.BuildId,
                            b.BuildNumber,
                            b.SourceBranch,
                            b.Status,
                            b.Result,
                            b.QueueTime,
                            b.FinishTime,
                            b.WebUrl,
                        })
                        .ToList();

                    return JsonSerializer.Serialize(new
                    {
                        RequestedBranch = branchRef,
                        MatchCount = filtered.Count,
                        Builds = filtered,
                        Hint = filtered.Count == 0
                            ? $"No {(successfulOnly ? "successful " : "")}builds found for branch '{branchRef}'. Check the branch name or trigger a new build first."
                            : $"Found {filtered.Count} build(s). Use the BuildId to verify this is the correct artifact before deploying.",
                    });
                },
                "ado_get_build_by_branch",
                "Find recent builds for a specific branch. Use this to verify the correct build artifact exists before deploying. Returns build IDs, versions, and status filtered by the exact branch."),

            AIFunctionFactory.Create(
                () =>
                {
                    var activeShift = dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
                    if (activeShift is null)
                        return "No active shift.";

                    var result = dataStore.Data.TrackedItems
                        .Where(t => t.ShiftId == activeShift.Id)
                        .Select(item =>
                        {
                            var project = dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
                            var pipeline = project?.PipelineConfigs.FirstOrDefault(p => p.Id == item.PipelineConfigId);
                            return new
                            {
                                Shift = activeShift.Name,
                                Project = project?.Name ?? "Unknown project",
                                Pipeline = pipeline?.DisplayName ?? "Unknown pipeline",
                                item.AdoRunId,
                                item.BuildVersion,
                                item.Status,
                                item.CurrentStage,
                                Steps = $"{item.SucceededSteps} succeeded / {item.FailedSteps} failed / {item.SkippedSteps} skipped / {item.PendingSteps} pending",
                                item.ExpectedNextStep,
                                item.RequiresAction,
                                item.ActionReason,
                                item.AdoWebUrl,
                            };
                        })
                        .ToList();

                    return JsonSerializer.Serialize(result);
                },
                "rema_list_tracked_runs",
                "List the active shift's tracked ADO runs with status, step counts, next steps, and ADO deep links."),

            AIFunctionFactory.Create(
                async () =>
                {
                    var evidence = await deploymentVersionDiscoveryService.DiscoverAsync(dataStore.Data.ServiceProjects);
                    return JsonSerializer.Serialize(evidence);
                },
                "rema_discover_deployed_versions",
                "Discover current deployed application-version evidence from ADO release/build pipelines and configured telemetry queries across all service projects."),

            AIFunctionFactory.Create(
                async (
                    [Description("Name of the saved Rema service project.")] string serviceProjectName,
                    [Description("Source git ref/application version for the diff.")] string fromVersion,
                    [Description("Target git ref/application version for the diff.")] string toVersion,
                    [Description("Output directory where SafeFly request files should be created.")] string outputDirectory) =>
                {
                    var project = dataStore.Data.ServiceProjects.FirstOrDefault(p =>
                        p.Name.Equals(serviceProjectName, StringComparison.OrdinalIgnoreCase));
                    if (project is null)
                        throw new InvalidOperationException($"Service project '{serviceProjectName}' was not found.");

                    var evidence = await deploymentVersionDiscoveryService.DiscoverAsync([project]);
                    var result = await safeFlyDiffService.CreateRequestFilesAsync(
                        project,
                        fromVersion,
                        toVersion,
                        outputDirectory,
                        evidence);

                    return JsonSerializer.Serialize(new
                    {
                        result.OutputDirectory,
                        result.Files,
                        result.ChangedFileCount,
                    });
                },
                "safefly_create_request_files",
                "Create SafeFly request markdown, changed-files inventory, and application diff patch for a saved service project."),

            // ── Memory Tools ──

            AIFunctionFactory.Create(
                async (
                    [Description("Short key/title for the memory (e.g. 'preferred-model', 'team-name').")] string key,
                    [Description("Full content of the memory.")] string content,
                    [Description("Category for organization (e.g. 'General', 'Personal', 'Work', 'Technical').")] string? category = "General") =>
                {
                    var existing = dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        existing.Content = content;
                        existing.Category = category ?? existing.Category;
                        existing.UpdatedAt = DateTimeOffset.Now;
                        await dataStore.SaveAsync();
                        return JsonSerializer.Serialize(new { existing.Id, existing.Key, action = "updated" });
                    }

                    var memory = new Memory
                    {
                        Key = key,
                        Content = content,
                        Category = category ?? "General",
                        Source = "chat",
                    };
                    dataStore.Data.Memories.Add(memory);
                    await dataStore.SaveAsync();
                    return JsonSerializer.Serialize(new { memory.Id, memory.Key, action = "created" });
                },
                "memory_save",
                "Save or update a persistent memory. If a memory with the same key exists, it is updated. Memories are included in the system prompt across all future sessions."),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to retrieve full content for.")] string key) =>
                {
                    var memory = dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (memory is null)
                        return JsonSerializer.Serialize(new { found = false, key });

                    return JsonSerializer.Serialize(new
                    {
                        found = true,
                        memory.Key,
                        memory.Content,
                        memory.Category,
                        memory.Source,
                        memory.CreatedAt,
                        memory.UpdatedAt,
                    });
                },
                "memory_recall",
                "Retrieve the full content of a specific memory by its key."),

            AIFunctionFactory.Create(
                async ([Description("Key of the memory to delete.")] string key) =>
                {
                    var memory = dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (memory is null)
                        return JsonSerializer.Serialize(new { deleted = false, reason = "not found" });

                    dataStore.Data.Memories.Remove(memory);
                    await dataStore.SaveAsync();
                    return JsonSerializer.Serialize(new { deleted = true, key });
                },
                "memory_delete",
                "Delete a persistent memory by its key."),

            AIFunctionFactory.Create(
                ([Description("Optional category to filter by.")] string? category = null) =>
                {
                    var query = dataStore.Data.Memories.AsEnumerable();
                    if (!string.IsNullOrWhiteSpace(category))
                        query = query.Where(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

                    var result = query
                        .OrderBy(m => m.Category)
                        .ThenBy(m => m.Key)
                        .Select(m => new { m.Key, m.Category, Preview = m.Content.Length > 80 ? m.Content[..80] + "…" : m.Content })
                        .ToList();

                    return JsonSerializer.Serialize(result);
                },
                "memory_list",
                "List all persistent memories, optionally filtered by category. Returns key, category, and a short preview."),

            // ── Web Fetch Tool ──

            // web_fetch is provided as a built-in SDK tool — no need to define it here.

            // ── File Announcement Tool ──

            AIFunctionFactory.Create(
                ([Description("Absolute path of the file to announce to the user.")] string filePath) =>
                {
                    return JsonSerializer.Serialize(new
                    {
                        announced = true,
                        filePath,
                        fileName = System.IO.Path.GetFileName(filePath),
                    });
                },
                "announce_file",
                "Show a clickable file attachment chip in the chat for a file you created or produced. Call once per deliverable file."),

            // ── Coding Tools (use lightweight LLM sessions) ──
        ];

        if (copilotService is not null)
            tools.AddRange(CreateCodingTools(copilotService));

        if (OperatingSystem.IsWindows())
            tools.AddRange(CreateUIAutomationTools());

        tools.AddRange(CreateBrowserTools());

        return tools;
    }

    private static List<AIFunction> CreateCodingTools(CopilotService copilotService)
    {
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description("The source code to review.")] string code,
                    [Description("The programming language (e.g. csharp, python, typescript).")] string language,
                    [Description("Context: what the code does, specific concerns.")] string? context = null) =>
                {
                    var prompt = $"""
                        Review this {language} code for bugs, security vulnerabilities, performance issues, and best practice violations.
                        Return structured feedback with severity levels (critical/warning/info) and fix suggestions.
                        Only surface issues that genuinely matter — no style or formatting nits.

                        {(context is not null ? $"Context: {context}\n" : "")}
                        ```{language}
                        {code}
                        ```
                        """;

                    return await RunLightweightAsync(copilotService, prompt);
                },
                "code_review",
                "Analyze code for bugs, security vulnerabilities, performance issues, and best practice violations. Returns structured feedback with severity and fixes."),

            AIFunctionFactory.Create(
                async (
                    [Description("The source code to generate tests for.")] string code,
                    [Description("The programming language (e.g. csharp, python, typescript).")] string language,
                    [Description("The test framework to use (e.g. xunit, pytest, jest).")] string framework,
                    [Description("Context: what to test, edge cases, conventions.")] string? context = null) =>
                {
                    var prompt = $"""
                        Generate comprehensive unit tests for this {language} code using {framework}.
                        Cover happy paths, edge cases, error handling, and boundary conditions.
                        Return ready-to-run test code.

                        {(context is not null ? $"Context: {context}\n" : "")}
                        ```{language}
                        {code}
                        ```
                        """;

                    return await RunLightweightAsync(copilotService, prompt);
                },
                "generate_tests",
                "Generate comprehensive unit tests for source code. Covers happy paths, edge cases, error handling. Returns ready-to-run test code."),

            AIFunctionFactory.Create(
                async (
                    [Description("The source code to explain.")] string code,
                    [Description("The programming language (e.g. csharp, python, typescript).")] string language,
                    [Description("Depth: 'overview', 'detailed', or 'teaching'.")] string depth = "detailed",
                    [Description("Optional aspect to focus on (e.g. 'the async pattern', 'error handling').")] string? focus = null) =>
                {
                    var prompt = $"""
                        Explain this {language} code at a {depth} level.
                        Break it into understandable parts with call flow and data flow.
                        {(focus is not null ? $"Focus on: {focus}" : "")}

                        ```{language}
                        {code}
                        ```
                        """;

                    return await RunLightweightAsync(copilotService, prompt);
                },
                "explain_code",
                "Deep code explanation: break down code into understandable parts with call flow, data flow, and pattern identification."),
        ];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<AIFunction> CreateUIAutomationTools()
    {
        return
        [
            AIFunctionFactory.Create(
                () => UIAutomationService.ListWindows(),
                "ui_list_windows",
                "List all visible windows on the desktop. Returns window titles, process names, and PIDs."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match) to inspect.")] string title,
                 [Description("How deep to walk the UI tree (1-5, default 3).")] int depth = 3) =>
                    UIAutomationService.InspectWindow(title, Math.Clamp(depth, 1, 5)),
                "ui_inspect",
                "Get the UI element tree of a window. Returns element names, types, automation IDs, and nesting."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match).")] string title,
                 [Description("Search query — matches against name, automation ID, control type.")] string query) =>
                    UIAutomationService.FindElement(title, query),
                "ui_find",
                "Search for UI elements in a window by name, automation ID, or control type."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match).")] string title,
                 [Description("Name or AutomationId of the element to click.")] string elementName) =>
                    UIAutomationService.ClickElement(title, elementName),
                "ui_click",
                "Click a UI element by name or automation ID. Uses Invoke, Toggle, or Select pattern."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match).")] string title,
                 [Description("Name or AutomationId of the text input element.")] string elementName,
                 [Description("Text to type or set in the element.")] string text) =>
                    UIAutomationService.TypeText(title, elementName, text),
                "ui_type",
                "Type or set text in a UI element by name or automation ID."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match).")] string title,
                 [Description("Name or AutomationId of the element to read.")] string elementName) =>
                    UIAutomationService.ReadElement(title, elementName),
                "ui_read",
                "Read detailed info about a UI element: name, value, type, enabled state, bounds."),
        ];
    }

    private static List<AIFunction> CreateBrowserTools()
    {
        var browserService = new BrowserAutomationService();

        return
        [
            AIFunctionFactory.Create(
                async ([Description("The URL to navigate to.")] string url) =>
                    await browserService.NavigateAsync(url),
                "browser_navigate",
                "Navigate the browser to a URL. Launches Edge/Chrome with remote debugging if needed."),

            AIFunctionFactory.Create(
                async () => await browserService.GetPageContentAsync(),
                "browser_get_content",
                "Get the text content of the current browser page (up to 8K characters)."),

            AIFunctionFactory.Create(
                async () => await browserService.GetPageInfoAsync(),
                "browser_get_info",
                "Get page title, URL, links, and interactive elements (inputs, buttons) from the current page."),

            AIFunctionFactory.Create(
                async ([Description("CSS selector of the element to click (e.g. '#submit', '.btn', 'a[href]').")] string selector) =>
                    await browserService.ClickElementAsync(selector),
                "browser_click",
                "Click an element on the current page by CSS selector."),

            AIFunctionFactory.Create(
                async (
                    [Description("CSS selector of the input element.")] string selector,
                    [Description("Text to type into the element.")] string text) =>
                    await browserService.TypeTextAsync(selector, text),
                "browser_type",
                "Type text into an input element on the current page by CSS selector."),

            AIFunctionFactory.Create(
                async () => await browserService.ListTabsAsync(),
                "browser_list_tabs",
                "List all open browser tabs with their titles and URLs."),
        ];
    }

    private static async Task<string> RunLightweightAsync(CopilotService copilotService, string prompt)
    {
        try
        {
            var fastModel = await copilotService.GetFastestModelIdAsync();
            return await copilotService.UseLightweightSessionAsync(
                new LightweightSessionOptions
                {
                    SystemPrompt = "You are a code analysis expert. Be concise and actionable.",
                    Model = fastModel,
                    Streaming = false,
                },
                async (session, ct) =>
                {
                    var response = await session.SendAndWaitAsync(
                        new GitHub.Copilot.SDK.MessageOptions { Prompt = prompt },
                        TimeSpan.FromSeconds(60), ct);
                    return response?.Data?.Content ?? "No response from model.";
                });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string StripHtml(string html)
    {
        // Remove script/style blocks
        html = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        // Remove tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Decode common entities
        html = html.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
        // Collapse whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();
        return html;
    }
}
