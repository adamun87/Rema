using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lumi.Benchmark;

internal sealed class VisualTreeProfile
{
    public required int TotalVisuals { get; init; }
    public required int HiddenVisuals { get; init; }
    public required int ZeroAreaVisuals { get; init; }
    public required IReadOnlyList<VisualTreeTypeProfile> TopTypes { get; init; }
    public required IReadOnlyList<VisualTreeSubtreeProfile> TopSubtrees { get; init; }

    public void WriteTo(BenchmarkOutput output)
    {
        output.WriteLine("  Visual tree profile:");
        output.WriteLine($"    total={TotalVisuals}, hidden={HiddenVisuals}, zero-area={ZeroAreaVisuals}");

        if (TopTypes.Count > 0)
        {
            output.WriteLine("    Top types:");
            foreach (var type in TopTypes)
                output.WriteLine($"      {type.TypeName}: count={type.Count}, hidden={type.HiddenCount}, zero-area={type.ZeroAreaCount}");
        }

        if (TopSubtrees.Count > 0)
        {
            output.WriteLine("    Top subtrees:");
            foreach (var subtree in TopSubtrees)
                output.WriteLine($"      {subtree.DisplayName}: descendants={subtree.DescendantCount}, bounds={subtree.BoundsSummary}, hidden={subtree.IsHidden}, zero-area={subtree.IsZeroArea}");
        }
    }
}

internal readonly record struct VisualTreeTypeProfile(string TypeName, int Count, int HiddenCount, int ZeroAreaCount);

internal readonly record struct VisualTreeSubtreeProfile(
    string DisplayName,
    int DescendantCount,
    bool IsHidden,
    bool IsZeroArea,
    string BoundsSummary);

internal static class VisualTreeProfiler
{
    public static VisualTreeProfile Capture(Visual root, int maxTypes = 12, int maxSubtrees = 12)
    {
        var typeStats = new Dictionary<string, MutableTypeProfile>(StringComparer.Ordinal);
        var subtreeStats = new List<VisualTreeSubtreeProfile>();
        var totals = new MutableTotals();

        Traverse(root, isRoot: true, typeStats, subtreeStats, totals);

        return new VisualTreeProfile
        {
            TotalVisuals = totals.Total,
            HiddenVisuals = totals.Hidden,
            ZeroAreaVisuals = totals.ZeroArea,
            TopTypes = typeStats
                .Select(static pair => new VisualTreeTypeProfile(pair.Key, pair.Value.Count, pair.Value.HiddenCount, pair.Value.ZeroAreaCount))
                .OrderByDescending(static profile => profile.Count)
                .ThenBy(static profile => profile.TypeName, StringComparer.Ordinal)
                .Take(maxTypes)
                .ToArray(),
            TopSubtrees = subtreeStats
                .OrderByDescending(static profile => profile.DescendantCount)
                .ThenBy(static profile => profile.DisplayName, StringComparer.Ordinal)
                .Take(maxSubtrees)
                .ToArray(),
        };
    }

    private static int Traverse(
        Visual visual,
        bool isRoot,
        Dictionary<string, MutableTypeProfile> typeStats,
        List<VisualTreeSubtreeProfile> subtreeStats,
        MutableTotals totals)
    {
        totals.Total++;

        var typeName = visual.GetType().Name;
        if (!typeStats.TryGetValue(typeName, out var typeProfile))
        {
            typeProfile = new MutableTypeProfile();
            typeStats[typeName] = typeProfile;
        }

        typeProfile.Count++;

        var isHidden = visual is Visual visualControl && !visualControl.IsVisible;
        if (isHidden)
        {
            totals.Hidden++;
            typeProfile.HiddenCount++;
        }

        var bounds = visual.Bounds;
        var isZeroArea = bounds.Width <= 0 || bounds.Height <= 0;
        if (isZeroArea)
        {
            totals.ZeroArea++;
            typeProfile.ZeroAreaCount++;
        }

        var descendantCount = 0;
        foreach (var child in visual.GetVisualChildren())
            descendantCount += 1 + Traverse(child, isRoot: false, typeStats, subtreeStats, totals);

        if (!isRoot && descendantCount >= 4)
        {
            subtreeStats.Add(new VisualTreeSubtreeProfile(
                GetDisplayName(visual),
                descendantCount,
                isHidden,
                isZeroArea,
                $"{bounds.Width:F0}x{bounds.Height:F0}"));
        }

        return descendantCount;
    }

    private static string GetDisplayName(Visual visual)
    {
        if (visual is Control { Name.Length: > 0 } control)
            return $"{control.GetType().Name}#{control.Name}";

        return visual.GetType().Name;
    }

    private sealed class MutableTotals
    {
        public int Total;
        public int Hidden;
        public int ZeroArea;
    }

    private sealed class MutableTypeProfile
    {
        public int Count;
        public int HiddenCount;
        public int ZeroAreaCount;
    }
}