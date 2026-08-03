using NexusEditor;

var devRoot = @"D:\repos\dev";
var releaseRoot = Path.Combine(devRoot, "game", "bin", "release", "x64");
var cases = new Dictionary<string, bool>
{
    [Path.Combine(releaseRoot, "ur.exe")] = true,
    [Path.Combine(releaseRoot, "EpicSeven.exe")] = true,
    [Path.Combine(releaseRoot, "ur_sound.exe")] = false,
    [Path.Combine(releaseRoot, "renamed_dev_client.exe")] = true,
    [@"C:\Program Files\Epic Seven\EpicSeven.exe"] = false
};

var failures = 0;
foreach (var (path, expected) in cases)
{
    var actual = EpicSevenDevClientService.LooksLikeDevClientExecutable(path, devRoot);
    Console.WriteLine($"{Path.GetFileName(path)} expected={expected} actual={actual} path={path}");
    if (actual != expected)
        failures++;
}

Environment.ExitCode = failures == 0 ? 0 : 1;
