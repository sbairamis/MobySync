using System.Text.RegularExpressions;
using Docker.DotNet.Models;

namespace MobySync.Helpers;

public class ImageTagResolver
{
    private readonly Dictionary<string, string> _stackTagOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _imageTagOverrides = new(StringComparer.OrdinalIgnoreCase);

    public ImageTagResolver(ILogger logger, string? overridesRaw = null)
    {
        var raw = overridesRaw ?? Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES") ?? string.Empty;

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');

            if (parts.Length == 3)
                _stackTagOverrides[$"{parts[0]}:{parts[1]}"] = parts[2];
            else if (parts.Length == 2)
                _imageTagOverrides[parts[0]] = parts[1];
        }

        if (_stackTagOverrides.Count > 0)
            logger.LogInformation("Stack tag overrides: {Overrides}", string.Join(", ", _stackTagOverrides.Select(kv => $"{kv.Key} → {kv.Value}")));

        if (_imageTagOverrides.Count > 0)
            logger.LogInformation("Image tag overrides: {Overrides}", string.Join(", ", _imageTagOverrides.Select(kv => $"{kv.Key} → {kv.Value}")));
    }

    public string ResolveTargetTag(ContainerListResponse container, string baseImageName)
    {
        if (container.Labels != null && container.Labels.TryGetValue("com.mobysync.target-tag", out var labelTag))
            return labelTag;

        if (container.Labels != null
            && container.Labels.TryGetValue("com.docker.compose.project", out var project)
            && container.Labels.TryGetValue("com.docker.compose.service", out var service)
            && _stackTagOverrides.TryGetValue($"{project}:{service}", out var stackTag))
            return stackTag;

        if (_imageTagOverrides.TryGetValue(baseImageName, out var imageTag))
            return imageTag;

        var detectedTag = ExtractTagFromImage(container.Image);
        if (detectedTag != null)
            return detectedTag;

        return "latest";
    }

    public static bool IsPinnedVersionTag(string tag) =>
        Regex.IsMatch(tag, @"^v?\d+(\.\d+)+$");

    public static string? ExtractTagFromImage(string imageFullPath)
    {
        var lastColonIndex = imageFullPath.LastIndexOf(':');
        var lastSlashIndex = imageFullPath.LastIndexOf('/');

        if (lastColonIndex > lastSlashIndex && lastColonIndex < imageFullPath.Length - 1)
            return imageFullPath.Substring(lastColonIndex + 1);

        return null;
    }

    public static string FetchBaseImageName(string imageFullPath)
    {
        var lastColonIndex = imageFullPath.LastIndexOf(':');
        var lastSlashIndex = imageFullPath.LastIndexOf('/');

        if (lastColonIndex > lastSlashIndex)
            return imageFullPath.Substring(0, lastColonIndex);

        return imageFullPath;
    }
}
