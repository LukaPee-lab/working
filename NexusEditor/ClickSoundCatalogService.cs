using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NexusEditor;

public sealed class ClickSoundEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FmodPath { get; set; } = "";
    public string RelativeBankPath { get; set; } = "";
    public int Subsong { get; set; }
    public long BankLength { get; set; }
    public long BankWriteTicks { get; set; }

    [JsonIgnore]
    public string BankPath { get; set; } = "";
}

public sealed record ClickSoundScanProgress(int Processed, int Total, string CurrentBank);
public sealed record ClickSoundCacheProgress(int Processed, int Total, string CurrentSound);

public sealed class ClickSoundCatalog
{
    public List<ClickSoundEntry> Sounds { get; } = [];
    public string Signature { get; set; } = "";
}

public sealed class ClickSoundCatalogService
{
    private const int FsbHeaderSize = 0x3c;
    private static readonly byte[] FsbSignature = "FSB5"u8.ToArray();
    private readonly string _devRoot;
    private readonly string _soundRoot;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _decodeLocks = new(StringComparer.OrdinalIgnoreCase);

    public ClickSoundCatalogService(string devRoot, string cacheRoot)
    {
        _devRoot = Path.GetFullPath(devRoot);
        _soundRoot = Path.Combine(_devRoot, "game", "Resources", "res", "sound");
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public string CacheRoot => _cacheRoot;
    public bool DecoderAvailable => File.Exists(FindDecoder());

    public async Task<ClickSoundCatalog> LoadCatalogAsync(
        IProgress<ClickSoundScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_soundRoot))
            throw new DirectoryNotFoundException($"사운드 폴더가 없습니다: {_soundRoot}");

        Directory.CreateDirectory(_cacheRoot);
        var indexPath = CatalogIndexPath();
        var oldIndex = LoadIndex(indexPath);
        var oldBanks = oldIndex?.Banks.ToDictionary(bank => bank.RelativePath, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, ClickSoundBankIndex>(StringComparer.OrdinalIgnoreCase);
        var bankFiles = Directory.EnumerateFiles(_soundRoot, "*.bank", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nextIndex = new ClickSoundCatalogIndex { DevRoot = _devRoot };

        for (var index = 0; index < bankFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = bankFiles[index];
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(_soundRoot, path);
            progress?.Report(new ClickSoundScanProgress(index, bankFiles.Count, relative));
            ClickSoundBankIndex bank;
            if (oldBanks.TryGetValue(relative, out var cached)
                && cached.Length == info.Length
                && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                bank = cached;
            }
            else
            {
                var sounds = await Task.Run(() => ReadBank(path), cancellationToken).ConfigureAwait(false);
                bank = new ClickSoundBankIndex
                {
                    RelativePath = relative,
                    Length = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Sounds = sounds
                };
            }
            nextIndex.Banks.Add(bank);
            progress?.Report(new ClickSoundScanProgress(index + 1, bankFiles.Count, relative));
        }

        ClickSoundPathResolver? resolver = null;
        if (nextIndex.Banks.SelectMany(bank => bank.Sounds).Any(sound => string.IsNullOrWhiteSpace(sound.FmodPath)))
            resolver = ClickSoundPathResolver.Load(_devRoot);

        var catalog = new ClickSoundCatalog();
        foreach (var bank in nextIndex.Banks)
        {
            var bankPath = Path.Combine(_soundRoot, bank.RelativePath);
            foreach (var indexed in bank.Sounds)
            {
                if (string.IsNullOrWhiteSpace(indexed.FmodPath))
                    indexed.FmodPath = resolver?.Resolve(indexed.Name, bank.RelativePath) ?? indexed.Name;
                catalog.Sounds.Add(new ClickSoundEntry
                {
                    Id = Hash($"{bank.RelativePath.ToLowerInvariant()}::{bank.Length}::{bank.LastWriteTicks}::{indexed.Subsong}"),
                    Name = indexed.Name,
                    FmodPath = indexed.FmodPath,
                    RelativeBankPath = bank.RelativePath,
                    BankPath = bankPath,
                    Subsong = indexed.Subsong,
                    BankLength = bank.Length,
                    BankWriteTicks = bank.LastWriteTicks
                });
            }
        }
        SaveIndex(indexPath, nextIndex);
        catalog.Sounds.Sort((left, right) => string.Compare(left.FmodPath, right.FmodPath, StringComparison.OrdinalIgnoreCase));
        catalog.Signature = Hash(string.Join("|", nextIndex.Banks.Select(bank => $"{bank.RelativePath}:{bank.Length}:{bank.LastWriteTicks}")));
        return catalog;
    }

    public string GetCachedWavPath(ClickSoundEntry sound)
        => Path.Combine(_cacheRoot, "wav", $"{sound.Id}.wav");

    public bool IsCached(ClickSoundEntry sound)
    {
        var path = GetCachedWavPath(sound);
        return File.Exists(path) && new FileInfo(path).Length > 44;
    }

    public async Task<string> EnsureDecodedAsync(ClickSoundEntry sound, CancellationToken cancellationToken)
    {
        var output = GetCachedWavPath(sound);
        if (IsCached(sound))
            return output;

        var gate = _decodeLocks.GetOrAdd(sound.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCached(sound))
                return output;

            var decoder = FindDecoder();
            if (!File.Exists(decoder))
                throw new FileNotFoundException("vgmstream-cli.exe를 찾을 수 없습니다.", decoder);

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temporary = output + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = decoder,
                    WorkingDirectory = Path.GetDirectoryName(decoder) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add("-s");
                startInfo.ArgumentList.Add(sound.Subsong.ToString());
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(temporary);
                startInfo.ArgumentList.Add(sound.BankPath);
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    throw new InvalidOperationException("vgmstream을 시작하지 못했습니다.");
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch
                {
                    TryKill(process);
                    throw;
                }
                var message = ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
                if (process.ExitCode != 0 || !File.Exists(temporary))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? $"vgmstream 종료 코드: {process.ExitCode}" : message);
                File.Move(temporary, output, overwrite: true);
                return output;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PrecacheAsync(
        IReadOnlyCollection<ClickSoundEntry> sounds,
        IProgress<ClickSoundCacheProgress>? progress,
        CancellationToken cancellationToken)
    {
        var missing = sounds.Where(sound => !IsCached(sound)).ToList();
        var processed = 0;
        await Parallel.ForEachAsync(missing, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = cancellationToken
        }, async (sound, token) =>
        {
            try
            {
                await EnsureDecodedAsync(sound, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One malformed stream must not block the rest of the startup cache.
            }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                progress?.Report(new ClickSoundCacheProgress(done, missing.Count, sound.FmodPath));
            }
        }).ConfigureAwait(false);
    }

    private string CatalogIndexPath()
        => Path.Combine(_cacheRoot, $"catalog-{Hash(_devRoot.ToLowerInvariant())}.json");

    private static List<ClickSoundIndexedEntry> ReadBank(string bankPath)
    {
        using var stream = new FileStream(bankPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offsets = FindFsbOffsets(stream);
        var sounds = new List<ClickSoundIndexedEntry>();
        foreach (var offset in offsets)
        {
            try
            {
                ReadFsbChunk(stream, offset, sounds);
            }
            catch
            {
                // A bank may contain non-audio FSB-like bytes. Keep valid chunks.
            }
        }
        return sounds;
    }

    private static void ReadFsbChunk(FileStream stream, long offset, List<ClickSoundIndexedEntry> sounds)
    {
        Span<byte> header = stackalloc byte[FsbHeaderSize];
        stream.Position = offset;
        stream.ReadExactly(header);
        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        var sampleCount = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        var sampleHeadersSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        var nameTableSize = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        if (version <= 0 || sampleCount <= 0 || nameTableSize < sampleCount * 4)
            throw new InvalidDataException("지원하지 않는 FSB5 헤더입니다.");
        var tableOffset = offset + FsbHeaderSize + sampleHeadersSize;
        if (tableOffset < 0 || tableOffset + nameTableSize > stream.Length)
            throw new InvalidDataException("FSB5 이름 테이블 범위가 잘못되었습니다.");
        var table = new byte[nameTableSize];
        stream.Position = tableOffset;
        stream.ReadExactly(table);
        var subsongOffset = sounds.Count;
        for (var index = 0; index < sampleCount; index++)
        {
            var name = ReadFsbName(table, index, sampleCount);
            if (string.IsNullOrWhiteSpace(name))
                name = $"{Path.GetFileNameWithoutExtension(stream.Name)}_{subsongOffset + index + 1:0000}";
            sounds.Add(new ClickSoundIndexedEntry { Name = name, Subsong = subsongOffset + index + 1 });
        }
    }

    private static List<long> FindFsbOffsets(FileStream stream)
    {
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize + FsbSignature.Length - 1];
        var overlap = 0;
        long position = 0;
        var result = new List<long>();
        stream.Position = 0;
        while (true)
        {
            var read = stream.Read(buffer, overlap, chunkSize);
            if (read == 0)
                return result;
            var length = overlap + read;
            for (var index = 0; index <= length - FsbSignature.Length; index++)
            {
                if (buffer[index] == FsbSignature[0]
                    && buffer[index + 1] == FsbSignature[1]
                    && buffer[index + 2] == FsbSignature[2]
                    && buffer[index + 3] == FsbSignature[3])
                    result.Add(position - overlap + index);
            }
            var nextOverlap = Math.Min(FsbSignature.Length - 1, length);
            Array.Copy(buffer, length - nextOverlap, buffer, 0, nextOverlap);
            position += read;
            overlap = nextOverlap;
        }
    }

    private static string ReadFsbName(byte[] table, int index, int sampleCount)
    {
        var position = index * 4;
        if (position + 4 > table.Length)
            return "";
        var offset = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(position, 4));
        if (offset < sampleCount * 4 || offset >= table.Length)
            return "";
        var end = offset;
        while (end < table.Length && table[end] != 0)
            end++;
        return end > offset ? Encoding.UTF8.GetString(table.AsSpan(offset, end - offset)) : "";
    }

    private static ClickSoundCatalogIndex? LoadIndex(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ClickSoundCatalogIndex>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveIndex(string path, ClickSoundCatalogIndex index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(index));
        File.Move(temporary, path, overwrite: true);
    }

    private static string FindDecoder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "vgmstream", "vgmstream-cli.exe"),
            Path.Combine(Environment.CurrentDirectory, "Tools", "vgmstream", "vgmstream-cli.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 12).ToLowerInvariant();

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public sealed class ClickSoundCatalogIndex
{
    public string DevRoot { get; set; } = "";
    public List<ClickSoundBankIndex> Banks { get; set; } = [];
}

public sealed class ClickSoundBankIndex
{
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteTicks { get; set; }
    public List<ClickSoundIndexedEntry> Sounds { get; set; } = [];
}

public sealed class ClickSoundIndexedEntry
{
    public string Name { get; set; } = "";
    public int Subsong { get; set; }
    public string FmodPath { get; set; } = "";
}

internal sealed class ClickSoundPathResolver
{
    private readonly Dictionary<string, string> _exactPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _characterEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _characterFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousNames = new(StringComparer.OrdinalIgnoreCase);

    public static ClickSoundPathResolver Load(string devRoot)
    {
        var resolver = new ClickSoundPathResolver();
        resolver.LoadMasterStrings(devRoot);
        resolver.LoadGameDataPaths(devRoot);
        resolver.LoadStoryVoicePaths(devRoot);
        resolver.LoadCharacterEvents(devRoot);
        resolver.LoadCharacterFolders(devRoot);
        return resolver;
    }

    public string Resolve(string streamName, string relativeBankPath)
    {
        if (string.IsNullOrWhiteSpace(streamName))
            return "";
        if (!_ambiguousNames.Contains(streamName) && _exactPaths.TryGetValue(streamName, out var exact))
            return exact;
        var bank = relativeBankPath.Replace('\\', '/').ToLowerInvariant();
        if (streamName.StartsWith("voc_", StringComparison.OrdinalIgnoreCase))
            return ResolveVoicePath(streamName);
        if (bank.Contains("voc", StringComparison.Ordinal))
            return streamName.StartsWith("illust_", StringComparison.OrdinalIgnoreCase)
                ? $"voc/sound_illust/{streamName}"
                : $"voc/npc/{streamName}";
        if (bank.Contains("/ui", StringComparison.Ordinal) || bank.StartsWith("ui", StringComparison.Ordinal))
            return $"ui/{streamName}";
        if (bank.Contains("/sfx", StringComparison.Ordinal) || bank.StartsWith("sfx", StringComparison.Ordinal))
            return $"fx/{streamName}";
        if (bank.Contains("/bgm", StringComparison.Ordinal) || bank.StartsWith("bgm", StringComparison.Ordinal))
            return $"bgm/{streamName}";
        return streamName;
    }

    private string ResolveVoicePath(string streamName)
    {
        var characterMatch = Regex.Match(streamName, @"^voc_(c\d+)_", RegexOptions.IgnoreCase);
        if (!characterMatch.Success)
            return $"voc/{streamName}";

        var characterId = characterMatch.Groups[1].Value;
        var eventCode = _characterEvents.TryGetValue(characterId, out var foundEvent) ? foundEvent : "";
        var folder = _characterFolders.TryGetValue(characterId, out var foundFolder) ? foundFolder : "";
        var storyMatch = Regex.Match(streamName, @"^voc_c\d+_story_([a-z])_", RegexOptions.IgnoreCase);
        if (storyMatch.Success && eventCode.Length > 0)
            return $"voc/story/{eventCode}/story_{storyMatch.Groups[1].Value.ToLowerInvariant()}/{streamName}";
        if (eventCode.Length > 0 && folder.Length > 0)
            return $"voc/story/{eventCode}/{folder}/{streamName}";
        if (folder.Length > 0)
            return $"voc/{folder}/{streamName}";
        return eventCode.Length > 0 ? $"voc/story/{eventCode}/{streamName}" : $"voc/{streamName}";
    }

    private void LoadMasterStrings(string devRoot)
    {
        var path = Path.Combine(devRoot, "game", "Resources", "res", "sound", "master.strings.bank");
        if (!File.Exists(path))
            return;
        try
        {
            var text = ExtractAsciiText(File.ReadAllBytes(path));
            foreach (Match match in Regex.Matches(text, @"event:/([A-Za-z0-9_./-]+)", RegexOptions.IgnoreCase))
                AddExactPath(match.Groups[1].Value);
        }
        catch
        {
            // FMOD string hints are optional.
        }
    }

    private void LoadGameDataPaths(string devRoot)
    {
        var roots = new[]
        {
            Path.Combine(devRoot, "game", "Resources", "tool", "res", "db"),
            Path.Combine(devRoot, "game", "Resources", "server", "res", "db"),
            Path.Combine(devRoot, "game", "Resources", "res", "db")
        };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var file in EnumerateFilesSafe(root, "*.dat").Concat(EnumerateFilesSafe(root, "*.tsv")))
        {
            try
            {
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                foreach (Match match in Regex.Matches(line,
                             @"(?<![A-Za-z0-9_./-])((?:voc|bgm|ui|fx|sfx)/[A-Za-z0-9_./-]+)",
                             RegexOptions.IgnoreCase))
                    AddExactPath(match.Groups[1].Value);
            }
            catch { }
        }
    }

    private void LoadStoryVoicePaths(string devRoot)
    {
        var roots = new[]
        {
            Path.Combine(devRoot, "game", "epic7-script-story", "epic7_story_csd", "cocosstudio", "story_layouts"),
            Path.Combine(devRoot, "game", "Resources", "res", "story"),
            Path.Combine(devRoot, "game", "Resources", "pre", "story")
        };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var file in EnumerateFilesSafe(root, "*.csd"))
        {
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                foreach (Match match in Regex.Matches(text, @"VOICE\(([^)]+)\)", RegexOptions.IgnoreCase))
                    AddExactPath($"voc/{match.Groups[1].Value.Trim().Trim('\"').Replace('\\', '/')}");
            }
            catch { }
        }
    }

    private void LoadCharacterEvents(string devRoot)
    {
        var roots = new[]
        {
            Path.Combine(devRoot, "game", "Resources", "tool", "res", "text"),
            Path.Combine(devRoot, "game", "Resources", "res", "text"),
            Path.Combine(devRoot, "game", "Resources", "server", "res", "text")
        };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var file in EnumerateFilesSafe(root, "*.dat").Concat(EnumerateFilesSafe(root, "*.tsv")))
        {
            try
            {
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                foreach (Match match in Regex.Matches(line, @"\b(c\d+)_(v[a-z0-9]+)", RegexOptions.IgnoreCase))
                {
                    var characterId = match.Groups[1].Value;
                    if (!_characterEvents.ContainsKey(characterId))
                        _characterEvents[characterId] = NormalizeEventCode(match.Groups[2].Value);
                }
            }
            catch { }
        }
    }

    private void LoadCharacterFolders(string devRoot)
    {
        var path = Path.Combine(devRoot, "game", "Resources", "res", "sound", "master.strings.bank");
        if (!File.Exists(path))
            return;
        try
        {
            var text = ExtractAsciiText(File.ReadAllBytes(path));
            foreach (Match match in Regex.Matches(text, @"([a-z0-9_]+)/voc_(c\d+)_(?!story_[a-z]_)", RegexOptions.IgnoreCase))
                _characterFolders.TryAdd(match.Groups[2].Value, match.Groups[1].Value);
        }
        catch { }
    }

    private void AddExactPath(string value)
    {
        var path = value.Trim().Replace('\\', '/');
        var name = path.Split('/').LastOrDefault() ?? "";
        if (name.Length == 0 || _ambiguousNames.Contains(name))
            return;
        if (!_exactPaths.TryGetValue(name, out var existing))
        {
            _exactPaths[name] = path;
            return;
        }
        if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
        {
            _exactPaths.Remove(name);
            _ambiguousNames.Add(name);
        }
    }

    private static string ExtractAsciiText(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
            builder.Append((value >= 'a' && value <= 'z') ||
                           (value >= 'A' && value <= 'Z') ||
                           (value >= '0' && value <= '9') ||
                           value is (byte)'_' or (byte)'/' or (byte)'-' or (byte)'.' or (byte)':'
                ? (char)value
                : '|');
        return builder.ToString();
    }

    private static string NormalizeEventCode(string value)
    {
        var match = Regex.Match(value, @"^(v[a-z]+\d+[a-z]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : value.ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }
}
