using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Rema.Services;

/// <summary>
/// Queries git repository information for display in the chat header.
/// </summary>
public sealed class GitStatusService
{
    /// <summary>Current branch name (e.g., "main", "feature/xyz").</summary>
    public string Branch { get; private set; } = "";

    /// <summary>Whether the repo is in worktree mode.</summary>
    public bool IsWorktree { get; private set; }

    /// <summary>Root path of the repo or worktree.</summary>
    public string? RepoRoot { get; private set; }

    /// <summary>Short status summary (e.g., "clean", "3 modified").</summary>
    public string StatusSummary { get; private set; } = "";

    public async Task RefreshAsync(string? workingDirectory = null)
    {
        var dir = workingDirectory ?? Directory.GetCurrentDirectory();

        Branch = await RunGitAsync("rev-parse --abbrev-ref HEAD", dir) ?? "unknown";
        RepoRoot = await RunGitAsync("rev-parse --show-toplevel", dir);

        // Detect worktree: if .git is a file (not directory), it's a worktree
        if (RepoRoot is not null)
        {
            var gitPath = Path.Combine(RepoRoot, ".git");
            IsWorktree = File.Exists(gitPath) && !Directory.Exists(gitPath);
        }

        // Get short status
        var status = await RunGitAsync("status --porcelain", dir);
        if (string.IsNullOrWhiteSpace(status))
        {
            StatusSummary = "clean";
        }
        else
        {
            var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var modified = 0;
            var untracked = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("??"))
                    untracked++;
                else
                    modified++;
            }

            var parts = new List<string>();
            if (modified > 0) parts.Add($"{modified} modified");
            if (untracked > 0) parts.Add($"{untracked} untracked");
            StatusSummary = string.Join(", ", parts);
        }
    }

    private static async Task<string?> RunGitAsync(string arguments, string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
