using System.Text.Json;

namespace Rema.Services;

public static class ToolDisplayHelper
{
    /// <summary>Returns a user-friendly (Name, Info) pair for a tool call.</summary>
    public static (string Name, string? Info) GetFriendlyToolDisplay(
        string toolName, string? author, string? argsJson)
    {
        var name = author ?? toolName;
        string? info = null;

        switch (toolName)
        {
            // ── File system ──
            case "read_file" or "view":
                info = ExtractJsonField(argsJson, "path") ?? ExtractJsonField(argsJson, "file_path");
                name = "Read file";
                break;
            case "edit" or "edit_file":
                info = ExtractJsonField(argsJson, "path") ?? ExtractJsonField(argsJson, "file_path");
                name = "Edit file";
                break;
            case "create" or "create_file":
                info = ExtractJsonField(argsJson, "path") ?? ExtractJsonField(argsJson, "file_path");
                name = "Create file";
                break;

            // ── Search & navigation ──
            case "grep" or "search":
                info = ExtractJsonField(argsJson, "pattern");
                name = "Search";
                break;
            case "glob" or "find_files":
                info = ExtractJsonField(argsJson, "pattern");
                name = "Find files";
                break;

            // ── Shell ──
            case "powershell" or "bash" or "shell":
                info = ExtractJsonField(argsJson, "command");
                name = "Run command";
                break;

            // ── Web ──
            case "web_fetch" or "fetch_url":
                info = ExtractJsonField(argsJson, "url");
                name = "Fetch URL";
                break;

            // ── Kusto / ADO (Rema-specific) ──
            case "kusto_query" or "execute_query":
                info = ExtractJsonField(argsJson, "query");
                name = "Kusto query";
                break;
            case "kusto_command" or "execute_command":
                name = "Kusto command";
                break;
            case "ado_trigger_pipeline":
                info = ExtractJsonField(argsJson, "pipelineName") ?? ExtractJsonField(argsJson, "pipeline_name");
                var branch = ExtractJsonField(argsJson, "sourceBranch") ?? ExtractJsonField(argsJson, "source_branch");
                if (!string.IsNullOrWhiteSpace(branch))
                    info = $"{info} on {branch}";
                name = "Trigger pipeline";
                break;
            case "ado_get_build_by_branch":
                info = ExtractJsonField(argsJson, "sourceBranch") ?? ExtractJsonField(argsJson, "source_branch");
                name = "Find build by branch";
                break;
            case "ado_list_builds":
                info = ExtractJsonField(argsJson, "definitionId") ?? ExtractJsonField(argsJson, "pipeline_name");
                name = "List ADO builds";
                break;
            case "ado_get_build_status":
                info = ExtractJsonField(argsJson, "buildId") ?? ExtractJsonField(argsJson, "runId");
                name = "ADO build status";
                break;
            case "open_ado_deep_link":
                name = "Open ADO link";
                break;
            case "safefly_create_request_files":
                info = ExtractJsonField(argsJson, "outputFolder") ?? ExtractJsonField(argsJson, "output_folder");
                name = "Create SafeFly request files";
                break;
            case "rema_list_capabilities":
                info = ExtractJsonField(argsJson, "kind");
                name = "List Rema capabilities";
                break;
            case "rema_list_tracked_runs":
                name = "List tracked runs";
                break;
            case "rema_discover_deployed_versions":
                name = "Discover deployed versions";
                break;
            case "rema_register_operation":
                info = ExtractJsonField(argsJson, "goal");
                name = "Register operation";
                break;
            case "rema_update_operation":
                info = ExtractJsonField(argsJson, "currentStep") ?? ExtractJsonField(argsJson, "status");
                name = "Update operation";
                break;
            case "rema_propose_deployment_plan":
                info = ExtractJsonField(argsJson, "serviceProjectName");
                name = "Propose deployment plan";
                break;
            case "rema_invoke_capability":
                info = ExtractJsonField(argsJson, "capabilityName");
                name = "Invoke capability";
                break;

            // ── Memory ──
            case "memory_save":
                info = ExtractJsonField(argsJson, "key");
                name = "Save memory";
                break;
            case "memory_recall":
                info = ExtractJsonField(argsJson, "key");
                name = "Recall memory";
                break;
            case "memory_delete":
                info = ExtractJsonField(argsJson, "key");
                name = "Delete memory";
                break;
            case "memory_list":
                name = "List memories";
                break;

            // ── File ──
            case "announce_file":
                info = ExtractJsonField(argsJson, "filePath");
                name = "Announce file";
                break;

            // ── Coding ──
            case "code_review":
                name = "Code review";
                break;
            case "generate_tests":
                name = "Generate tests";
                break;
            case "explain_code":
                info = ExtractJsonField(argsJson, "focus");
                name = "Explain code";
                break;

            // ── UI Automation ──
            case "ui_list_windows":
                name = "List windows";
                break;
            case "ui_inspect":
                info = ExtractJsonField(argsJson, "title");
                name = "Inspect window";
                break;
            case "ui_find":
                info = ExtractJsonField(argsJson, "query");
                name = "Find UI element";
                break;
            case "ui_click":
                info = ExtractJsonField(argsJson, "elementName");
                name = "Click element";
                break;
            case "ui_type":
                info = ExtractJsonField(argsJson, "elementName");
                name = "Type text";
                break;
            case "ui_read":
                info = ExtractJsonField(argsJson, "elementName");
                name = "Read element";
                break;

            // ── Browser Automation ──
            case "browser_navigate":
                info = ExtractJsonField(argsJson, "url");
                name = "Navigate browser";
                break;
            case "browser_get_content":
                name = "Get page content";
                break;
            case "browser_get_info":
                name = "Get page info";
                break;
            case "browser_click":
                info = ExtractJsonField(argsJson, "selector");
                name = "Click element";
                break;
            case "browser_type":
                info = ExtractJsonField(argsJson, "selector");
                name = "Type in browser";
                break;
            case "browser_list_tabs":
                name = "List tabs";
                break;

            // ── GitHub ──
            case "github-search_code":
                info = ExtractJsonField(argsJson, "query");
                name = "Search code";
                break;
            case "github-list_issues" or "github-search_issues":
                name = "Search issues";
                break;
            case "github-issue_read":
                info = ExtractJsonField(argsJson, "issue_number");
                name = "Read issue";
                break;

            // ── Thinking ──
            case "think" or "report_intent":
                name = "Thinking";
                break;

            // ── Agent delegation ──
            case "task":
                info = ExtractJsonField(argsJson, "description");
                name = "Delegate task";
                break;

            default:
                // Use author if available (SDK may set friendly names)
                name = author ?? FormatToolNameFallback(toolName);
                break;
        }

        // Truncate long info strings
        if (info is not null && info.Length > 80)
            info = info[..77] + "…";

        return (name, info);
    }

    public static string GetToolGlyph(string toolName) => toolName switch
    {
        "read_file" or "view" => "📄",
        "edit" or "edit_file" => "✏️",
        "create" or "create_file" => "📝",
        "grep" or "search" => "🔍",
        "glob" or "find_files" => "📂",
        "powershell" or "bash" or "shell" => "💻",
        "web_fetch" or "fetch_url" => "🌐",
        "kusto_query" or "execute_query" => "📊",
        "kusto_command" or "execute_command" => "⚡",
        "ado_list_builds" or "ado_get_build_status" or "ado_get_build_by_branch" => "🔄",
        "ado_trigger_pipeline" => "🚀",
        "open_ado_deep_link" => "🔗",
        "safefly_create_request_files" => "🛡️",
        "rema_list_capabilities" => "🧩",
        "rema_list_tracked_runs" => "📋",
        "rema_discover_deployed_versions" => "🧭",
        "rema_register_operation" or "rema_update_operation" => "📌",
        "rema_propose_deployment_plan" => "📋",
        "rema_invoke_capability" => "🧩",
        "memory_save" or "memory_recall" or "memory_delete" or "memory_list" => "🧠",
        "announce_file" => "📎",
        "code_review" or "generate_tests" or "explain_code" => "🧑‍💻",
        "ui_list_windows" or "ui_inspect" or "ui_find" or "ui_click" or "ui_type" or "ui_read" => "🖥️",
        "browser_navigate" or "browser_get_content" or "browser_get_info" or "browser_click" or "browser_type" or "browser_list_tabs" => "🌐",
        "think" or "report_intent" => "💭",
        "task" => "🤖",
        _ => "⚙️",
    };

    public static bool IsCompactEligible(string toolName) => toolName is
        "think" or "report_intent" or "sql" or "glob" or "grep" or "find_files"
        or "read_file" or "view" or "web_fetch" or "fetch_url"
        or "memory_save" or "memory_recall" or "memory_delete" or "memory_list"
        or "announce_file";

    public static string? FormatToolArgsFriendly(string toolName, string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var parts = new List<string>();

            foreach (var prop in root.EnumerateObject())
            {
                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => prop.Value.GetRawText(),
                };
                if (val is not null && val.Length > 120)
                    val = val[..117] + "…";
                parts.Add($"**{prop.Name}**: {val}");
            }

            return string.Join("  \n", parts);
        }
        catch
        {
            return argsJson.Length > 200 ? argsJson[..197] + "…" : argsJson;
        }
    }

    public static string? ExtractJsonField(string? json, string fieldName)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(fieldName, out var val))
                return val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText();
        }
        catch { }
        return null;
    }

    private static string FormatToolNameFallback(string toolName)
    {
        // "github-search_code" → "Search code"
        var name = toolName;
        var dashIdx = name.IndexOf('-');
        if (dashIdx >= 0 && dashIdx < name.Length - 1)
            name = name[(dashIdx + 1)..];

        return name.Replace('_', ' ') switch
        {
            var s when s.Length > 0 => char.ToUpper(s[0]) + s[1..],
            var s => s,
        };
    }
}
