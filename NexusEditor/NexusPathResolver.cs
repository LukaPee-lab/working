using System.IO;
using System.Text.Json;

namespace NexusEditor;

public static class NexusPathResolver
{
    private const string ProductFolderName = "NexusEditor";
    private static readonly string LegacyProductFolderName = "Dimension" + "EventEditor";
    public const string PlayerFolderName = "dimension_event_player_windows_release";
    public const string PlayerExeName = "Dimension Exploration Event Player.exe";
    private const string BundledEventWorkbookName = "nexus_event 차원 탐사 이벤트.xlsx";

    private const string RelativeEventPath = "design\\DB\\alpha\\nexus_event 차원 탐사 이벤트.xlsx";
    private const string RelativeBackgroundImagePath = "game\\Resources\\res\\nexus";

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SuperCreative", ProductFolderName, "settings.json");

    private static string LegacySettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SuperCreative", LegacyProductFolderName, "settings.json");

    public static string DefaultSoundCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SuperCreative", ProductFolderName, "SoundCache");

    private static string LegacyDefaultSoundCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SuperCreative", LegacyProductFolderName, "SoundCache");

    public static AppSettings LoadSettings()
    {
        try
        {
            var sourcePath = File.Exists(SettingsPath)
                ? SettingsPath
                : File.Exists(LegacySettingsPath)
                    ? LegacySettingsPath
                    : null;
            if (sourcePath is null)
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath)) ?? new AppSettings();
            var migratedSettings = string.Equals(sourcePath, LegacySettingsPath, StringComparison.OrdinalIgnoreCase);
            if (UsesLegacyDefaultSoundCache(settings.SoundCacheRoot) && MigrateLegacySoundCache())
            {
                settings.SoundCacheRoot = DefaultSoundCacheRoot;
                migratedSettings = true;
            }

            if (migratedSettings)
                TrySaveSettings(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? ResolveDefaultEventWorkbook()
    {
        var settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.EventWorkbookPath) && File.Exists(settings.EventWorkbookPath))
            return settings.EventWorkbookPath;

        if (!string.IsNullOrWhiteSpace(settings.ReposRoot))
        {
            var fromSettingsRoot = Path.Combine(settings.ReposRoot, RelativeEventPath);
            if (File.Exists(fromSettingsRoot))
                return fromSettingsRoot;
        }

        var dRepos = Path.Combine("D:\\repos", RelativeEventPath);
        if (File.Exists(dRepos))
            return dRepos;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var possibleRepos = Path.Combine(profile, "repos", RelativeEventPath);
        if (File.Exists(possibleRepos))
            return possibleRepos;

        return null;
    }

    public static string? ResolveEventWorkbookSelection(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        var path = selectedPath.Trim().Trim('"');
        if (LooksLikeEventWorkbook(path))
            return Path.GetFullPath(path);
        if (!Directory.Exists(path))
            return null;

        var exact = Path.Combine(path, BundledEventWorkbookName);
        if (LooksLikeEventWorkbook(exact))
            return Path.GetFullPath(exact);

        try
        {
            return Directory.EnumerateFiles(path, "nexus_event*.xlsx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(LooksLikeEventWorkbook);
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveDefaultPlayerExe()
    {
        var settings = LoadSettings();
        if (LooksLikePlayerExe(settings.PlayerExePath))
            return settings.PlayerExePath;

        foreach (var candidate in PlayerExeCandidates(settings))
        {
            if (LooksLikePlayerExe(candidate))
                return candidate;
        }

        return null;
    }

    public static string? ResolveDefaultDevRoot(AppSettings settings)
    {
        foreach (var candidate in new[] { settings.DevRoot, settings.BackgroundImageRoot })
        {
            var resolved = FindDevRoot(candidate);
            if (resolved is not null)
                return resolved;
        }

        try
        {
            foreach (var instance in EpicSevenDevClientService.FindInstances())
            {
                var resolved = FindDevRoot(instance.ExecutablePath);
                if (resolved is not null)
                    return resolved;
            }
        }
        catch
        {
            // Running clients are only one optional discovery source.
        }

        foreach (var candidate in new[]
                 {
                     settings.ReposRoot,
                     "D:\\repos",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "repos")
                 })
        {
            var resolved = FindDevRoot(candidate);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    public static string? FindDevRoot(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        try
        {
            var path = Path.GetFullPath(selectedPath.Trim().Trim('"'));
            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;

            var directDev = Path.Combine(path, "dev");
            if (LooksLikeDevRoot(directDev))
                return directDev;

            var current = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.GetParent(path);
            while (current is not null)
            {
                if (LooksLikeDevRoot(current.FullName))
                    return current.FullName;
                current = current.Parent;
            }
        }
        catch
        {
            // Invalid or inaccessible candidates are ignored.
        }

        return null;
    }

    public static bool LooksLikeDevRoot(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && Directory.Exists(path)
           && Directory.Exists(GetBackgroundImageRoot(path));

    public static string GetBackgroundImageRoot(string devRoot)
        => Path.Combine(devRoot, RelativeBackgroundImagePath);

    public static string ResolveSoundCacheRoot(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SoundCacheRoot))
        {
            try
            {
                var configured = Path.GetFullPath(settings.SoundCacheRoot.Trim().Trim('"'));
                if (!UsesLegacyDefaultSoundCache(configured))
                    return configured;

                if (MigrateLegacySoundCache())
                {
                    settings.SoundCacheRoot = DefaultSoundCacheRoot;
                    TrySaveSettings(settings);
                    return DefaultSoundCacheRoot;
                }
                return configured;
            }
            catch { }
        }
        return MigrateLegacySoundCache() ? DefaultSoundCacheRoot : LegacyDefaultSoundCacheRoot;
    }

    private static bool UsesLegacyDefaultSoundCache(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Directory.Exists(LegacyDefaultSoundCacheRoot);

        try
        {
            return string.Equals(Path.GetFullPath(path.Trim().Trim('"')), LegacyDefaultSoundCacheRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool MigrateLegacySoundCache()
    {
        if (Directory.Exists(DefaultSoundCacheRoot) || !Directory.Exists(LegacyDefaultSoundCacheRoot))
            return true;

        try
        {
            var parent = Path.GetDirectoryName(DefaultSoundCacheRoot);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            Directory.Move(LegacyDefaultSoundCacheRoot, DefaultSoundCacheRoot);
            return Directory.Exists(DefaultSoundCacheRoot);
        }
        catch
        {
            // If the cache is locked, keep startup usable. The next launch retries the one-time move.
            return false;
        }
    }

    private static void TrySaveSettings(AppSettings settings)
    {
        try { SaveSettings(settings); }
        catch { }
    }

    private static IEnumerable<string> PlayerExeCandidates(AppSettings settings)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var appParent = Directory.GetParent(appDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(appParent))
            yield return Path.Combine(appParent, PlayerFolderName, PlayerExeName);

        var appGrandParent = !string.IsNullOrWhiteSpace(appParent) ? Directory.GetParent(appParent)?.FullName : null;
        if (!string.IsNullOrWhiteSpace(appGrandParent))
            yield return Path.Combine(appGrandParent, PlayerFolderName, PlayerExeName);

        if (!string.IsNullOrWhiteSpace(settings.EventWorkbookPath))
        {
            var dir = Path.GetDirectoryName(settings.EventWorkbookPath);
            while (!string.IsNullOrWhiteSpace(dir))
            {
                yield return Path.Combine(dir, PlayerFolderName, PlayerExeName);
                dir = Path.GetDirectoryName(dir);
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile,
            "OneDrive - Super Creative",
            "EpicSeven - 문서",
            "기획실",
            "2_코어시스템팀",
            "1. 이병연",
            "1_작업중",
            "175126 260917 신규 PVE 전투",
            PlayerFolderName,
            PlayerExeName);
    }

    public static bool LooksLikePlayerExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var dir = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(dir)
               && File.Exists(Path.Combine(dir, "Dimension Exploration Event Player.pck"));
    }

    public static bool LooksLikeEventWorkbook(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var workbook = EventWorkbookService.OpenWorkbookSnapshot(path);
            return workbook.Worksheets.Any(ws => ws.Name == EventWorkbookService.BaseSheetName)
                && workbook.Worksheets.Any(ws => ws.Name == EventWorkbookService.GroupSheetName)
                && workbook.Worksheets.Any(ws => ws.Name == EventWorkbookService.ChoiceSheetName)
                && workbook.Worksheets.Any(ws => ws.Name == EventWorkbookService.TextSheetName);
        }
        catch
        {
            return false;
        }
    }
}
