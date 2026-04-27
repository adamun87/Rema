using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rema.Models;

namespace Rema.Services;

public sealed record SafeFlyRequestResult(string OutputDirectory, IReadOnlyList<string> Files, int ChangedFileCount);

public sealed class SafeFlyDiffService
{
    public async Task<SafeFlyRequestResult> CreateRequestFilesAsync(
        ServiceProject project,
        string fromVersion,
        string toVersion,
        string outputDirectory,
        IReadOnlyList<DeploymentVersionEvidence>? deployedVersionEvidence = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project.RepoPath) || !Directory.Exists(project.RepoPath))
            throw new InvalidOperationException("The service project must have a valid repository path before creating SafeFly files.");

        if (string.IsNullOrWhiteSpace(fromVersion))
            throw new InvalidOperationException("Enter the source application version or git ref for the SafeFly diff.");

        if (string.IsNullOrWhiteSpace(toVersion))
            throw new InvalidOperationException("Enter the target application version or git ref for the SafeFly diff.");

        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("Choose an output folder for the SafeFly request files.");

        Directory.CreateDirectory(outputDirectory);

        var from = fromVersion.Trim();
        var to = toVersion.Trim();
        var nameStatus = await RunGitAsync(project.RepoPath, $"diff --name-status {Quote(from)} {Quote(to)}", cancellationToken).ConfigureAwait(false);
        var stat = await RunGitAsync(project.RepoPath, $"diff --stat {Quote(from)} {Quote(to)}", cancellationToken).ConfigureAwait(false);
        var patch = await RunGitAsync(project.RepoPath, $"diff --unified=3 {Quote(from)} {Quote(to)}", cancellationToken).ConfigureAwait(false);
        var log = await RunGitAsync(project.RepoPath, $"log --oneline {Quote(from)}..{Quote(to)}", cancellationToken).ConfigureAwait(false);

        var changedFilesPath = Path.Combine(outputDirectory, "changed-files.txt");
        var patchPath = Path.Combine(outputDirectory, "application-diff.patch");
        var requestPath = Path.Combine(outputDirectory, "safefly-request.md");

        await File.WriteAllTextAsync(changedFilesPath, nameStatus, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(patchPath, patch, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(requestPath, BuildRequest(project, from, to, nameStatus, stat, log, deployedVersionEvidence), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return new SafeFlyRequestResult(
            outputDirectory,
            [requestPath, changedFilesPath, patchPath],
            CountChangedFiles(nameStatus));
    }

    private static string BuildRequest(
        ServiceProject project,
        string from,
        string to,
        string nameStatus,
        string stat,
        string log,
        IReadOnlyList<DeploymentVersionEvidence>? deployedVersionEvidence)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("# SafeFly request");
        sb.AppendLine();
        sb.AppendLine($"- Service: {project.Name}");
        sb.AppendLine($"- Repository: `{project.RepoPath}`");
        if (!string.IsNullOrWhiteSpace(project.AdoOrgUrl) || !string.IsNullOrWhiteSpace(project.AdoProjectName))
            sb.AppendLine($"- Azure DevOps: {project.AdoOrgUrl}/{project.AdoProjectName}");
        sb.AppendLine($"- From version/ref: `{from}`");
        sb.AppendLine($"- To version/ref: `{to}`");
        sb.AppendLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        sb.AppendLine("## Change summary");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(stat) ? "_No diff stat returned._" : $"```text\n{stat.Trim()}\n```");
        sb.AppendLine();

        if (deployedVersionEvidence is { Count: > 0 })
        {
            sb.AppendLine("## Current deployed version evidence");
            sb.AppendLine();
            sb.AppendLine("| Source | Service | Pipeline/query | Version | Status | Details |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var item in deployedVersionEvidence)
            {
                sb.Append("| ");
                sb.Append(EscapeTable(item.Source));
                sb.Append(" | ");
                sb.Append(EscapeTable(item.ServiceName));
                sb.Append(" | ");
                var name = string.IsNullOrWhiteSpace(item.Link)
                    ? item.PipelineOrQueryName
                    : $"[{item.PipelineOrQueryName}]({item.Link})";
                sb.Append(EscapeTable(name, preserveMarkdownLink: true));
                sb.Append(" | ");
                sb.Append(EscapeTable(item.Version ?? "Unknown"));
                sb.Append(" | ");
                sb.Append(EscapeTable(item.Status));
                sb.Append(" | ");
                sb.Append(EscapeTable(item.Details));
                sb.AppendLine(" |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Commits");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(log) ? "_No commits found in the requested range._" : $"```text\n{log.Trim()}\n```");
        sb.AppendLine();

        sb.AppendLine("## Changed files");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(nameStatus) ? "_No changed files found._" : $"```text\n{nameStatus.Trim()}\n```");
        sb.AppendLine();

        sb.AppendLine("## Risk checklist");
        sb.AppendLine();
        sb.AppendLine("- [ ] Deployment steps and ownership reviewed");
        sb.AppendLine("- [ ] Config, schema, permissions, and feature flag changes identified");
        sb.AppendLine("- [ ] Rollback or mitigation path documented");
        sb.AppendLine("- [ ] Post-deployment health checks identified");
        sb.AppendLine("- [ ] Linked ADO build/release run validated");
        sb.AppendLine();

        sb.AppendLine("## Rema analysis notes");
        sb.AppendLine();
        sb.AppendLine("Use Rema chat with the attached `application-diff.patch` and `changed-files.txt` to classify risk and finalize the SafeFly request narrative.");
        return sb.ToString();
    }

    private static int CountChangedFiles(string nameStatus)
    {
        if (string.IsNullOrWhiteSpace(nameStatus)) return 0;

        var count = 0;
        using var reader = new StringReader(nameStatus);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        }

        return count;
    }

    private static string EscapeTable(string value, bool preserveMarkdownLink = false)
    {
        var escaped = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

        return preserveMarkdownLink ? escaped : escaped.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--no-pager " + arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");

        return stdout;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
