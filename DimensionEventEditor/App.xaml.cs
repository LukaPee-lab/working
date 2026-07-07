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

        var splash = new SplashWindow("Opening editor...");
        splash.Show();
        _ = StartEditorAsync(splash);
    }

    private async Task StartEditorAsync(SplashWindow splash)
    {
        try
        {
            await Task.Delay(850);
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
            MessageBox.Show(ex.ToString(), "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
}
