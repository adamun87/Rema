using System;
using System.Collections.Generic;
using System.Linq;
using Rema.Models;

namespace Rema.Services;

/// <summary>
/// Analyses pipeline dependencies within a service project and evaluates
/// readiness based on currently tracked items in the active shift.
/// </summary>
public sealed class PipelineDependencyService
{
    private readonly DataStore _dataStore;

    public PipelineDependencyService(DataStore dataStore)
    {
        _dataStore = dataStore;
    }

    /// <summary>
    /// Returns the full dependency graph for a service project as a list of edges.
    /// Each edge maps a source pipeline (must complete first) to its dependent target.
    /// </summary>
    public List<DependencyEdge> GetDependencyGraph(ServiceProject project)
    {
        return project.Dependencies
            .Select(d =>
            {
                var source = project.PipelineConfigs.FirstOrDefault(p => p.Id == d.SourcePipelineId);
                var target = project.PipelineConfigs.FirstOrDefault(p => p.Id == d.TargetPipelineId);
                return new DependencyEdge(d, source, target);
            })
            .Where(e => e.Source is not null && e.Target is not null)
            .ToList();
    }

    /// <summary>
    /// Returns pipelines that block a given pipeline from starting.
    /// Only returns blocking pipelines whose tracked items have NOT yet succeeded.
    /// </summary>
    public List<BlockingPipeline> GetBlockingPipelines(Guid pipelineConfigId, Guid serviceProjectId)
    {
        var project = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == serviceProjectId);
        if (project is null) return [];

        var activeShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        var trackedItems = activeShift is not null
            ? _dataStore.Data.TrackedItems.Where(t => t.ShiftId == activeShift.Id).ToList()
            : [];

        var deps = project.Dependencies
            .Where(d => d.TargetPipelineId == pipelineConfigId)
            .ToList();

        var result = new List<BlockingPipeline>();
        foreach (var dep in deps)
        {
            var sourcePipeline = project.PipelineConfigs.FirstOrDefault(p => p.Id == dep.SourcePipelineId);
            if (sourcePipeline is null) continue;

            var sourceTrackedItems = trackedItems
                .Where(t => t.PipelineConfigId == dep.SourcePipelineId)
                .ToList();

            var anySucceeded = sourceTrackedItems.Any(t =>
                t.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
                || t.Status.Contains("completed", StringComparison.OrdinalIgnoreCase));

            if (!anySucceeded)
            {
                result.Add(new BlockingPipeline(
                    dep,
                    sourcePipeline,
                    sourceTrackedItems.FirstOrDefault()));
            }
        }

        return result;
    }

    /// <summary>
    /// Given a pipeline that just completed, returns pipelines that are now unblocked.
    /// A pipeline is considered unblocked when ALL its source dependencies have a succeeded tracked item.
    /// </summary>
    public List<UnblockedPipeline> GetUnblockedPipelines(Guid completedPipelineConfigId, Guid serviceProjectId)
    {
        var project = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == serviceProjectId);
        if (project is null) return [];

        var activeShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        var trackedItems = activeShift is not null
            ? _dataStore.Data.TrackedItems.Where(t => t.ShiftId == activeShift.Id).ToList()
            : [];

        // Find all pipelines that depend on the completed one
        var dependents = project.Dependencies
            .Where(d => d.SourcePipelineId == completedPipelineConfigId)
            .Select(d => d.TargetPipelineId)
            .Distinct()
            .ToList();

        var result = new List<UnblockedPipeline>();
        foreach (var targetId in dependents)
        {
            var allDeps = project.Dependencies
                .Where(d => d.TargetPipelineId == targetId)
                .ToList();

            var allMet = allDeps.All(dep =>
            {
                return trackedItems.Any(t =>
                    t.PipelineConfigId == dep.SourcePipelineId
                    && (t.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
                        || t.Status.Contains("completed", StringComparison.OrdinalIgnoreCase)));
            });

            if (allMet)
            {
                var pipeline = project.PipelineConfigs.FirstOrDefault(p => p.Id == targetId);
                if (pipeline is not null)
                    result.Add(new UnblockedPipeline(pipeline, allDeps));
            }
        }

        return result;
    }

    /// <summary>
    /// Validates that a proposed pipeline execution order respects all dependency constraints.
    /// Returns violations if any pipeline appears before its prerequisites.
    /// </summary>
    public List<string> ValidateDependencyOrder(ServiceProject project, List<Guid> pipelineIds)
    {
        var violations = new List<string>();
        var completed = new HashSet<Guid>();

        foreach (var id in pipelineIds)
        {
            var deps = project.Dependencies
                .Where(d => d.TargetPipelineId == id)
                .ToList();

            foreach (var dep in deps)
            {
                if (!completed.Contains(dep.SourcePipelineId))
                {
                    var source = project.PipelineConfigs.FirstOrDefault(p => p.Id == dep.SourcePipelineId);
                    var target = project.PipelineConfigs.FirstOrDefault(p => p.Id == id);
                    violations.Add(
                        $"'{target?.DisplayName ?? id.ToString()}' is scheduled before its dependency " +
                        $"'{source?.DisplayName ?? dep.SourcePipelineId.ToString()}' ({dep.DependencyType})");
                }
            }

            completed.Add(id);
        }

        return violations;
    }

    /// <summary>
    /// Returns a topologically sorted execution order for pipelines in a project,
    /// respecting all dependency constraints. Returns null if a cycle is detected.
    /// </summary>
    public List<PipelineConfig>? GetExecutionOrder(ServiceProject project)
    {
        var configs = project.PipelineConfigs.ToDictionary(p => p.Id);
        var inDegree = configs.Keys.ToDictionary(id => id, _ => 0);
        var adjacency = configs.Keys.ToDictionary(id => id, _ => new List<Guid>());

        foreach (var dep in project.Dependencies)
        {
            if (!inDegree.ContainsKey(dep.SourcePipelineId) || !inDegree.ContainsKey(dep.TargetPipelineId))
                continue;

            adjacency[dep.SourcePipelineId].Add(dep.TargetPipelineId);
            inDegree[dep.TargetPipelineId]++;
        }

        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<PipelineConfig>();

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (configs.TryGetValue(id, out var config))
                sorted.Add(config);

            foreach (var neighbor in adjacency[id])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        // Cycle detected if not all nodes processed
        return sorted.Count == configs.Count ? sorted : null;
    }

    /// <summary>
    /// Checks whether a specific pipeline has unmet dependencies in the current shift.
    /// Returns a readiness result with details.
    /// </summary>
    public DeploymentReadiness CheckDeploymentReadiness(Guid pipelineConfigId, Guid serviceProjectId)
    {
        var blocking = GetBlockingPipelines(pipelineConfigId, serviceProjectId);

        var project = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == serviceProjectId);
        var pipeline = project?.PipelineConfigs.FirstOrDefault(p => p.Id == pipelineConfigId);

        return new DeploymentReadiness(
            pipeline,
            blocking.Count == 0,
            blocking);
    }
}

// ── Result types ──

public sealed record DependencyEdge(
    PipelineDependency Dependency,
    PipelineConfig? Source,
    PipelineConfig? Target);

public sealed record BlockingPipeline(
    PipelineDependency Dependency,
    PipelineConfig SourcePipeline,
    TrackedItem? LatestTrackedItem);

public sealed record UnblockedPipeline(
    PipelineConfig Pipeline,
    List<PipelineDependency> SatisfiedDependencies);

public sealed record DeploymentReadiness(
    PipelineConfig? Pipeline,
    bool IsReady,
    List<BlockingPipeline> BlockingPipelines);
