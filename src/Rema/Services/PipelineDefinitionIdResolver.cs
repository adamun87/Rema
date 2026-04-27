using System;
using System.Text.RegularExpressions;
using Rema.Models;

namespace Rema.Services;

public static class PipelineDefinitionIdResolver
{
    public static int Resolve(PipelineConfig pipeline)
    {
        if (pipeline.AdoPipelineId > 0)
            return pipeline.AdoPipelineId;

        return Parse(pipeline.AdoUrl);
    }

    public static int Parse(string? adoUrl)
    {
        if (string.IsNullOrWhiteSpace(adoUrl)) return 0;

        if (!Uri.TryCreate(adoUrl.Trim(), UriKind.Absolute, out var uri))
            return ParseFromText(adoUrl);

        var definitionId = ParseQueryValue(uri.Query, "definitionId");
        if (definitionId > 0)
            return definitionId;

        return ParseFromText(adoUrl);
    }

    public static bool Normalize(ServiceProject project)
    {
        var changed = false;
        foreach (var pipeline in project.PipelineConfigs)
        {
            var id = Resolve(pipeline);
            if (id > 0 && pipeline.AdoPipelineId != id)
            {
                pipeline.AdoPipelineId = id;
                changed = true;
            }
        }

        return changed;
    }

    private static int ParseQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2) continue;
            if (!string.Equals(Uri.UnescapeDataString(pieces[0]), key, StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(Uri.UnescapeDataString(pieces[1]), out var value)
                ? value
                : 0;
        }

        return 0;
    }

    private static int ParseFromText(string text)
    {
        var definitionMatch = Regex.Match(text, @"[?&]definitionId=(\d+)", RegexOptions.IgnoreCase);
        if (definitionMatch.Success && int.TryParse(definitionMatch.Groups[1].Value, out var definitionId))
            return definitionId;

        var buildDefinitionMatch = Regex.Match(text, @"/_build/(?:results)?\?[^#]*definitionId=(\d+)", RegexOptions.IgnoreCase);
        if (buildDefinitionMatch.Success && int.TryParse(buildDefinitionMatch.Groups[1].Value, out definitionId))
            return definitionId;

        var pipelineMatch = Regex.Match(text, @"/pipelines/(\d+)", RegexOptions.IgnoreCase);
        if (pipelineMatch.Success && int.TryParse(pipelineMatch.Groups[1].Value, out var pipelineId))
            return pipelineId;

        return 0;
    }
}
