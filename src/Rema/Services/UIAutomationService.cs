using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Automation;

namespace Rema.Services;

[SupportedOSPlatform("windows")]
public static class UIAutomationService
{
    public static string ListWindows()
    {
        var root = AutomationElement.RootElement;
        var windows = root.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        var result = new List<object>();
        foreach (AutomationElement win in windows)
        {
            try
            {
                var name = win.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new
                {
                    Title = name,
                    ProcessId = win.Current.ProcessId,
                    ProcessName = GetProcessName(win.Current.ProcessId),
                    ClassName = win.Current.ClassName,
                    BoundingRectangle = win.Current.BoundingRectangle.ToString(),
                });
            }
            catch { }
        }
        return JsonSerializer.Serialize(result);
    }

    public static string InspectWindow(string windowTitle, int maxDepth = 3)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
            return JsonSerializer.Serialize(new { error = $"Window '{windowTitle}' not found" });

        var tree = BuildTree(window, 0, maxDepth);
        return JsonSerializer.Serialize(tree);
    }

    public static string FindElement(string windowTitle, string query)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
            return JsonSerializer.Serialize(new { error = $"Window '{windowTitle}' not found" });

        var results = new List<object>();
        SearchTree(window, query.ToLowerInvariant(), results, 0, 5, 0);
        return JsonSerializer.Serialize(results);
    }

    public static string ClickElement(string windowTitle, string elementName)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
            return JsonSerializer.Serialize(new { error = $"Window '{windowTitle}' not found" });

        var element = FindElementByNameOrId(window, elementName);
        if (element is null)
            return JsonSerializer.Serialize(new { error = $"Element '{elementName}' not found" });

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj))
        {
            ((InvokePattern)invokeObj).Invoke();
            return JsonSerializer.Serialize(new { clicked = true, method = "Invoke" });
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObj))
        {
            ((TogglePattern)toggleObj).Toggle();
            return JsonSerializer.Serialize(new { clicked = true, method = "Toggle" });
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectObj))
        {
            ((SelectionItemPattern)selectObj).Select();
            return JsonSerializer.Serialize(new { clicked = true, method = "Select" });
        }

        return JsonSerializer.Serialize(new { clicked = false, reason = "No clickable pattern found" });
    }

    public static string TypeText(string windowTitle, string elementName, string text)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
            return JsonSerializer.Serialize(new { error = $"Window '{windowTitle}' not found" });

        var element = FindElementByNameOrId(window, elementName);
        if (element is null)
            return JsonSerializer.Serialize(new { error = $"Element '{elementName}' not found" });

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj))
        {
            ((ValuePattern)valueObj).SetValue(text);
            return JsonSerializer.Serialize(new { typed = true, method = "ValuePattern" });
        }

        return JsonSerializer.Serialize(new { typed = false, reason = "Element does not accept text input" });
    }

    public static string ReadElement(string windowTitle, string elementName)
    {
        var window = FindWindow(windowTitle);
        if (window is null)
            return JsonSerializer.Serialize(new { error = $"Window '{windowTitle}' not found" });

        var element = FindElementByNameOrId(window, elementName);
        if (element is null)
            return JsonSerializer.Serialize(new { error = $"Element '{elementName}' not found" });

        string? value = null;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj))
            value = ((ValuePattern)valueObj).Current.Value;

        return JsonSerializer.Serialize(new
        {
            Name = element.Current.Name,
            ControlType = element.Current.ControlType.ProgrammaticName,
            AutomationId = element.Current.AutomationId,
            Value = value,
            IsEnabled = element.Current.IsEnabled,
            BoundingRectangle = element.Current.BoundingRectangle.ToString(),
        });
    }

    // ── Helpers ──

    private static AutomationElement? FindWindow(string title)
    {
        var root = AutomationElement.RootElement;
        var windows = root.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        foreach (AutomationElement win in windows)
        {
            try
            {
                if (win.Current.Name.Contains(title, StringComparison.OrdinalIgnoreCase))
                    return win;
            }
            catch { }
        }
        return null;
    }

    private static AutomationElement? FindElementByNameOrId(AutomationElement root, string nameOrId)
    {
        // Try by AutomationId first
        try
        {
            var byId = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, nameOrId));
            if (byId is not null) return byId;
        }
        catch { }

        // Try by Name (partial match via tree walk)
        var walker = TreeWalker.ControlViewWalker;
        return FindByName(root, nameOrId.ToLowerInvariant(), walker, 0, 8);
    }

    private static AutomationElement? FindByName(AutomationElement parent, string query,
        TreeWalker walker, int depth, int maxDepth)
    {
        if (depth > maxDepth) return null;

        var child = walker.GetFirstChild(parent);
        while (child is not null)
        {
            try
            {
                if (child.Current.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return child;

                var found = FindByName(child, query, walker, depth + 1, maxDepth);
                if (found is not null) return found;
            }
            catch { }
            child = walker.GetNextSibling(child);
        }
        return null;
    }

    private static object BuildTree(AutomationElement element, int depth, int maxDepth)
    {
        var info = new Dictionary<string, object?>
        {
            ["Name"] = element.Current.Name,
            ["ControlType"] = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            ["AutomationId"] = string.IsNullOrEmpty(element.Current.AutomationId) ? null : element.Current.AutomationId,
        };

        if (depth < maxDepth)
        {
            var children = new List<object>();
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(element);
            int count = 0;
            while (child is not null && count < 50)
            {
                try { children.Add(BuildTree(child, depth + 1, maxDepth)); }
                catch { }
                child = walker.GetNextSibling(child);
                count++;
            }
            if (children.Count > 0)
                info["Children"] = children;
        }

        return info;
    }

    private static void SearchTree(AutomationElement element, string query,
        List<object> results, int depth, int maxDepth, int index)
    {
        if (depth > maxDepth || results.Count >= 20) return;

        try
        {
            var name = element.Current.Name ?? "";
            var automationId = element.Current.AutomationId ?? "";
            var controlType = element.Current.ControlType.ProgrammaticName ?? "";

            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || automationId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || controlType.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new
                {
                    Name = name,
                    ControlType = controlType.Replace("ControlType.", ""),
                    AutomationId = automationId,
                    IsEnabled = element.Current.IsEnabled,
                });
            }
        }
        catch { return; }

        var walker = TreeWalker.ControlViewWalker;
        var child = walker.GetFirstChild(element);
        int childIndex = 0;
        while (child is not null && results.Count < 20)
        {
            SearchTree(child, query, results, depth + 1, maxDepth, childIndex++);
            child = walker.GetNextSibling(child);
        }
    }

    private static string GetProcessName(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch { return ""; }
    }
}
