using System.IO;
using System.Text.Json;

namespace NexusEditor;

public sealed class ResearchEditorSettings
{
    public string? OutSystemWorkbookPath { get; set; }
    public string? EffectWorkbookPath { get; set; }
    public string? LastCategory { get; set; }
    public int PermissionFilter { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double CategoryPaneWidth { get; set; } = 250;
    public double RightPaneWidth { get; set; } = 700;
    public double NavigatorPaneWidth { get; set; } = 220;
    public double HierarchyPaneWidth { get; set; } = 220;
    public double ConsoleHeight { get; set; } = 200;
    public string LastExportId { get; set; } = ResearchWorkbookService.DefaultResearchExportId;
}

public sealed record ResearchWorkbookPaths(string OutSystemPath, string EffectPath)
{
    public string DbRoot => Path.GetDirectoryName(OutSystemPath) ?? "";
}

public static class ResearchPathResolver
{
    public const string OutSystemWorkbookName = "nexus_out_system 차원_탐사_아웃시스템.xlsx";
    public const string EffectWorkbookName = "nexus_effect 차원 탐사 효과.xlsx";

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SuperCreative", "NexusEditor", "research-settings.json");

    public static ResearchEditorSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ResearchEditorSettings>(File.ReadAllText(SettingsPath))
                  ?? new ResearchEditorSettings()
                : new ResearchEditorSettings();
        }
        catch
        {
            return new ResearchEditorSettings();
        }
    }

    public static void SaveSettings(ResearchEditorSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static ResearchWorkbookPaths? ResolveDefault()
    {
        var research = LoadSettings();
        if (IsValidPair(research.OutSystemWorkbookPath, research.EffectWorkbookPath))
            return new ResearchWorkbookPaths(
                Path.GetFullPath(research.OutSystemWorkbookPath!),
                Path.GetFullPath(research.EffectWorkbookPath!));

        var eventSettings = NexusPathResolver.LoadSettings();
        var candidates = new List<string>();
        AddCandidate(candidates, Path.GetDirectoryName(eventSettings.EventWorkbookPath));
        if (!string.IsNullOrWhiteSpace(eventSettings.ReposRoot))
            AddCandidate(candidates, Path.Combine(eventSettings.ReposRoot, "design", "DB", "alpha"));
        AddCandidate(candidates, @"D:\repos\design\DB\alpha");
        AddCandidate(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "repos", "design", "DB", "alpha"));

        foreach (var root in candidates)
        {
            var pair = ResolveFromFolder(root);
            if (pair is not null)
                return pair;
        }
        return null;
    }

    public static ResearchWorkbookPaths? ResolveSelection(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        try
        {
            var path = Path.GetFullPath(selectedPath.Trim().Trim('"'));
            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;
            return ResolveFromFolder(path);
        }
        catch
        {
            return null;
        }
    }

    public static ResearchWorkbookPaths? ResolveFromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;

        var outSystem = Path.Combine(folder, OutSystemWorkbookName);
        var effect = Path.Combine(folder, EffectWorkbookName);
        return IsValidPair(outSystem, effect)
            ? new ResearchWorkbookPaths(Path.GetFullPath(outSystem), Path.GetFullPath(effect))
            : null;
    }

    public static void Remember(ResearchWorkbookPaths paths)
    {
        var settings = LoadSettings();
        settings.OutSystemWorkbookPath = paths.OutSystemPath;
        settings.EffectWorkbookPath = paths.EffectPath;
        SaveSettings(settings);
    }

    private static bool IsValidPair(string? outSystem, string? effect) =>
        !string.IsNullOrWhiteSpace(outSystem)
        && !string.IsNullOrWhiteSpace(effect)
        && File.Exists(outSystem)
        && File.Exists(effect);

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            candidates.Add(path);
    }
}
