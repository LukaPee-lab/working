using System.IO;
using System.Text;
using System.Windows;

namespace DimensionEventEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--qa", StringComparison.OrdinalIgnoreCase))
        {
            RunQaMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-probe", StringComparison.OrdinalIgnoreCase))
        {
            RunDevProbeMode();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-run-event", StringComparison.OrdinalIgnoreCase))
        {
            RunDevEventMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--dev-read-console", StringComparison.OrdinalIgnoreCase))
        {
            RunDevConsoleReadMode(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--sound-probe", StringComparison.OrdinalIgnoreCase))
        {
            RunSoundProbeMode(e.Args);
            Shutdown();
            return;
        }

        var splash = new SplashWindow("Opening editor...");
        splash.Show();
        _ = StartEditorAsync(splash);
    }

    private async Task StartEditorAsync(SplashWindow splash)
    {
        try
        {
            await Task.Delay(850);
            await PrepareClickSoundCacheAsync(splash);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            splash.Close();
            window.BeginInitialLoad();
        }
        catch (Exception ex)
        {
            splash.Close();
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "DimensionEventEditor_startup_error.txt"), ex.ToString());
            }
            catch
            {
                // Ignore diagnostic logging failures.
            }
            ThemedMessageBox.Show(ex.ToString(), "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void RunQaMode(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // WinExe builds may run without an attached console.
        }
        var source = args.Length > 1 ? args[1] : NexusPathResolver.ResolveDefaultEventWorkbook();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            Console.Error.WriteLine("QA failed: nexus_event workbook not found.");
            Environment.ExitCode = 2;
            return;
        }

        var output = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetDirectoryName(source) ?? Environment.CurrentDirectory, "nexus_event_qa_export.xlsx");
        var workbook = EventWorkbookService.Load(source);
        var issues = EventWorkbookService.Validate(workbook);
        foreach (var issue in issues)
            Console.WriteLine($"{issue.Severity}: {issue.Message}");

        var errorCount = issues.Count(i => i.Severity == ValidationSeverity.Error);
        if (errorCount > 0)
        {
            Console.Error.WriteLine($"QA failed: {errorCount} validation errors.");
            Environment.ExitCode = 3;
            return;
        }

        var diff = EventWorkbookService.BuildDiff(workbook);
        Console.WriteLine($"QA diff entries: {diff.Count}");
        foreach (var group in diff.GroupBy(d => d.Sheet).OrderBy(g => g.Key))
            Console.WriteLine($"QA diff {group.Key}: {group.Count()}");

        EventWorkbookService.SaveAs(workbook, output, createBackup: false);
        var reloaded = EventWorkbookService.Load(output);
        var reloadIssues = EventWorkbookService.Validate(reloaded);
        var reloadErrorCount = reloadIssues.Count(i => i.Severity == ValidationSeverity.Error);

        Console.WriteLine($"QA loaded: {workbook.Events.Count} events, {workbook.Groups.Count} groups, {workbook.Choices.Count} choices.");
        Console.WriteLine($"QA exported: {output}");
        Console.WriteLine($"QA reload errors: {reloadErrorCount}");
        Environment.ExitCode = reloadErrorCount == 0 ? 0 : 4;
    }

    private static async Task PrepareClickSoundCacheAsync(SplashWindow splash)
    {
        var settings = NexusPathResolver.LoadSettings();
        var devRoot = NexusPathResolver.ResolveDefaultDevRoot(settings);
        if (devRoot is null)
            return;

        var cacheRoot = NexusPathResolver.ResolveSoundCacheRoot(settings);
        settings.SoundCacheRoot = cacheRoot;
        var service = new ClickSoundCatalogService(devRoot, cacheRoot);
        try
        {
            splash.SetStatus("Indexing FMOD sound banks...");
            var scanProgress = new Progress<ClickSoundScanProgress>(item =>
            {
                splash.SetStatus($"Indexing FMOD banks {item.Processed}/{item.Total}: {item.CurrentBank}");
                splash.SetProgress(item.Processed, item.Total);
            });
            var catalog = await service.LoadCatalogAsync(scanProgress, CancellationToken.None);
            if (catalog.Sounds.Count == 0)
                return;
            var missing = catalog.Sounds.Where(sound => !service.IsCached(sound)).ToList();
            if (missing.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(settings.SoundCacheMode))
            {
                var answer = ThemedMessageBox.Show(
                    $"FMOD 사운드 {catalog.Sounds.Count:N0}개를 미리 WAV 캐시로 추출할까요?\n\n" +
                    "예: 지금 로딩 화면에서 모두 추출한 뒤 에디터를 엽니다. 디스크 용량과 시간이 많이 필요할 수 있습니다.\n" +
                    "아니요: 목록은 모두 표시하고, 재생한 음원만 그때 캐시합니다.\n\n" +
                    $"캐시 위치: {cacheRoot}\nDEV/SVN 폴더에는 파일을 만들지 않습니다.",
                    "Click Sound Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
                settings.SoundCacheMode = answer == MessageBoxResult.Yes ? "precache" : "lazy";
                NexusPathResolver.SaveSettings(settings);
            }

            if (!string.Equals(settings.SoundCacheMode, "precache", StringComparison.OrdinalIgnoreCase))
                return;

            splash.SetStatus($"Preparing sound cache 0/{missing.Count}...");
            splash.SetProgress(0, missing.Count);
            var cacheProgress = new Progress<ClickSoundCacheProgress>(item =>
            {
                splash.SetStatus($"Preparing sound cache {item.Processed}/{item.Total}: {item.CurrentSound}");
                splash.SetProgress(item.Processed, item.Total);
            });
            await service.PrecacheAsync(missing, cacheProgress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            splash.SetStatus($"Sound cache skipped: {ex.Message}");
            await Task.Delay(1200);
        }
        finally
        {
            NexusPathResolver.SaveSettings(settings);
            splash.SetProgress(0, 0);
        }
    }

    private static void RunDevProbeMode()
    {
        TryEnableUtf8Console();
        var instances = EpicSevenDevClientService.FindInstances();
        foreach (var instance in instances)
        {
            Console.WriteLine($"PID={instance.ProcessId} title={instance.GameWindowTitle} console={instance.HasConsole} input=0x{instance.ConsoleInputHandle.ToInt64():X} output=0x{instance.ConsoleOutputHandle.ToInt64():X} path={instance.ExecutablePath}");
        }
        Console.WriteLine($"DEV instances: {instances.Count}");
        Environment.ExitCode = instances.Any(instance => instance.HasConsole) ? 0 : 5;
    }

    private static void RunDevEventMode(string[] args)
    {
        TryEnableUtf8Console();
        if (args.Length < 3 || !int.TryParse(args[1], out var processId))
        {
            Console.Error.WriteLine("Usage: --dev-run-event <pid> <event-id>");
            Environment.ExitCode = 6;
            return;
        }

        var instance = EpicSevenDevClientService.FindInstances().FirstOrDefault(candidate => candidate.ProcessId == processId);
        if (instance is null)
        {
            Console.Error.WriteLine($"DEV PID {processId} not found.");
            Environment.ExitCode = 7;
            return;
        }

        var result = EpicSevenDevClientService.SendRunEvent(instance, args[2]);
        Console.WriteLine(result.Message);
        Environment.ExitCode = result.Success ? 0 : 8;
    }

    private static void RunDevConsoleReadMode(string[] args)
    {
        TryEnableUtf8Console();
        if (args.Length < 2 || !int.TryParse(args[1], out var processId))
        {
            Console.Error.WriteLine("Usage: --dev-read-console <pid>");
            Environment.ExitCode = 9;
            return;
        }

        var instance = EpicSevenDevClientService.FindInstances().FirstOrDefault(candidate => candidate.ProcessId == processId);
        if (instance is null)
        {
            Console.Error.WriteLine($"DEV PID {processId} not found.");
            Environment.ExitCode = 10;
            return;
        }

        var text = EpicSevenDevClientService.ReadConsoleText(instance);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
            Console.WriteLine(line);
        Console.WriteLine($"DEV console chars: {text.Length}");
        Environment.ExitCode = text.Length > 0 ? 0 : 11;
    }

    private static void RunSoundProbeMode(string[] args)
    {
        TryEnableUtf8Console();
        var settings = NexusPathResolver.LoadSettings();
        var devRoot = args.Length > 1 ? NexusPathResolver.FindDevRoot(args[1]) : NexusPathResolver.ResolveDefaultDevRoot(settings);
        if (devRoot is null)
        {
            Console.Error.WriteLine("Sound probe failed: DEV root not found.");
            Environment.ExitCode = 12;
            return;
        }
        var cacheRoot = args.Length > 2 ? Path.GetFullPath(args[2]) : NexusPathResolver.ResolveSoundCacheRoot(settings);
        try
        {
            var service = new ClickSoundCatalogService(devRoot, cacheRoot);
            var catalog = service.LoadCatalogAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"Sound probe: {catalog.Sounds.Count} sounds / signature={catalog.Signature}");
            Console.WriteLine($"Sound cache: {cacheRoot}");
            Console.WriteLine($"Decoder ready: {service.DecoderAvailable}");
            foreach (var sound in catalog.Sounds.Take(3))
                Console.WriteLine($"{sound.FmodPath}\t{sound.RelativeBankPath}\tsubsong={sound.Subsong}");
            if (args.Skip(3).Any(value => string.Equals(value, "--decode-first", StringComparison.OrdinalIgnoreCase)))
            {
                var first = catalog.Sounds.First();
                var wav = service.EnsureDecodedAsync(first, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Decoded: {first.FmodPath} -> {wav} ({new FileInfo(wav).Length:N0} bytes)");
            }
            Environment.ExitCode = catalog.Sounds.Count > 0 && service.DecoderAvailable ? 0 : 13;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sound probe failed: {ex}");
            Environment.ExitCode = 14;
        }
    }

    private static void TryEnableUtf8Console()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }
    }
}

