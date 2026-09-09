using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace GTNHMigrator;

// Client: Prism/MultiMC modpack instance (including LAN/integrated hosting). Server: dedicated server only.
public enum MigrationMode { Client, Server }

public sealed record MigrationItem(string RelativePath, string Kind, long Size);
public sealed record PreflightFinding(string Severity, string Detail);
public sealed record PrismProfile(string DisplayName, string Path);

// A GTNH-specific config tweak, sourced from the official wiki's "Installing and Migrating" / "Server Setup" pages.
public sealed record CommonChange(string Id, string RelativePath, char Separator, string Key, string Value, string Description);
public sealed record CommonChangeStatus(CommonChange Change, bool AppliedInSource, bool AppliedInTarget);
public sealed record ModInventoryEntry(string Folder, string FileName, string State);
public sealed record StartupScriptDifference(string FileName, string Category, string SourceValue, string TargetValue);
public sealed record MigrationExecutionState(
    string Status,
    string Source,
    string Target,
    string Mode,
    string? BackupPath,
    int TotalOperations,
    int CompletedOperations,
    IReadOnlyList<string> CompletedPaths,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record MigrationPlan(
    string Source,
    string Target,
    string Mode,
    IReadOnlyList<MigrationItem> Items,
    long TotalBytes,
    int CopyFileCount,
    IReadOnlyList<string> LegacyDirectories,
    IReadOnlyList<CommonChangeStatus> CommonChanges,
    IReadOnlyList<ModInventoryEntry> ModChanges,
    IReadOnlyList<StartupScriptDifference> StartupScriptDifferences,
    IReadOnlyList<string> ManualReminders,
    IReadOnlyList<PreflightFinding> Preflight);

public sealed class MigrationService
{
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // File/folder list from the wiki's "Installing and Migrating" page, Method 1 step 2
    // (https://wiki.gtnewhorizons.com/wiki/Installing_and_Migrating#Method_1:_Migrating_to_a_New_Instance_(Recommended)).
    private static readonly string[] RetainDirectories = ["saves", "screenshots", "schematics", "resourcepacks", "shaderpacks", "journeymap", "visualprospecting", "TCNodeTracker", "ESM", Path.Combine("config", "vendingmachine", "favourites")];
    private static readonly string[] RetainFiles = ["options.txt", "optionsof.txt", "optionsnf.txt", "servers.dat", "BotaniaVars.dat", Path.Combine("config", "shaders.properties"), "localconfig.cfg"];
    // ServerUtilities plus the JourneyMapServer world UUID folder called out in the wiki's "Server Setup" page
    // (https://wiki.gtnewhorizons.com/wiki/Server_Setup#Server_Update) as required to preserve map data across updates.
    private static readonly string[] ServerOnly = ["ops.json", "whitelist.json", "banned-players.json", "banned-ips.json", "server.properties", "server-icon.png", "serverutilities", Path.Combine("config", "JourneyMapServer")];
    private static readonly string[] Markers = ["instance.cfg", "mmc-pack.json", "server.properties"];

    // GTNH client tweaks documented on the official wiki's "Installing and Migrating" page
    // (https://wiki.gtnewhorizons.com/wiki/Installing_and_Migrating, "Method 1" optional customization list).
    private static readonly CommonChange[] ClientCatalog =
    [
        new("disable-pollution", Path.Combine("config", "GregTech", "Pollution.cfg"), '=', "Activate Pollution", "false", "Disable GregTech pollution simulation"),
        new("disable-space-elevator-anim", Path.Combine("config", "gtnhintergalactic.cfg"), '=', "isCableRenderingEnabled", "false", "Disable Space Elevator cable rendering animation (performance)"),
        new("enable-borderless", Path.Combine("config", "lwjgl3ify.cfg"), '=', "borderless", "true", "Enable borderless fullscreen mode"),
        new("disable-tesla-anim", Path.Combine("config", "tectech.cfg"), '=', "TESLA_VISUAL_EFFECT", "false", "Disable Tesla Tower visual effect animation"),
    ];

    // Reminders from the same wiki page that require in-game mod GUIs, deleting files, or editing JSON
    // rather than a single documented cfg key, so they are surfaced as manual checklist items instead of being auto-applied.
    private static readonly string[] ClientManualReminders =
    [
        "Delete config/txloader/load/minecraft/sounds/music/menu to remove the main menu music.",
        "Remove the \"music.menu\" section from config/txloader/load/minecraft/sounds.json to disable main menu music.",
        "Disable or remove the DefaultServerList mod if you don't want it to repopulate servers.dat.",
        "Video Settings (Advanced): disable \"compact vertex format\" to remove stray white dots.",
        "GTNH Lib settings (Number Formatting): switch fluid units from mB to Liters.",
        "Botania mod settings: disable shader usage to remove poor mana pool reflections.",
        "Thaumcraft mod settings: enable Wuss Mode to disable Warp events.",
        "Draconic Evolution mod settings: disable the item dislocator sound.",
        "Adventure Backpacks mod settings (Gameplay): disable tool cycling.",
        "StructureLib mod settings (Common): set autoPlaceBudget to 200 and autoPlaceInterval to 1 for faster multiblock building.",
        "Only copy the serverutilities folder if you want its ServerUtilities permissions (ranks, homes, chunk loading) carried over.",
    ];

    // GTNH server tweaks from the ServerUtilities section of the wiki's "Server Setup" page
    // (https://wiki.gtnewhorizons.com/wiki/Server_Setup#Chunk_Loading), stored in serverutilities/serverutilities.cfg.
    private static readonly CommonChange[] ServerCatalog =
    [
        new("chunk-claiming", Path.Combine("serverutilities", "serverutilities.cfg"), '=', "chunk_claiming", "true", "Allow players to claim chunks via ServerUtilities"),
        new("chunk-loading", Path.Combine("serverutilities", "serverutilities.cfg"), '=', "chunk_loading", "true", "Keep claimed chunks loaded via ServerUtilities"),
        new("ranks-enabled", Path.Combine("serverutilities", "serverutilities.cfg"), '=', "enabled", "true", "Enable rank-based chunk claim limits (ServerUtilities ranks)"),
    ];

    public IReadOnlyList<PrismProfile> FindPrismProfiles()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) roots.Add(Path.Combine(appData, "PrismLauncher", "instances"));
        if (!string.IsNullOrWhiteSpace(localAppData)) roots.Add(Path.Combine(localAppData, "PrismLauncher", "instances"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "instances"));
        roots.Add(Path.Combine(Directory.GetCurrentDirectory(), "instances"));

        return roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateDirectories(root))
            .Where(directory => Markers.Any(marker => File.Exists(Path.Combine(directory, marker))))
            .Select(directory => new PrismProfile(Path.GetFileName(directory), NormalizeProfilePath(directory)))
            .GroupBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeProfilePath(string directory)
    {
        var minecraft = Path.Combine(directory, ".minecraft");
        return Directory.Exists(minecraft) ? minecraft : directory;
    }

    public string? DetectInstance()
    {
        var directory = Directory.GetCurrentDirectory();
        for (var level = 0; level < 4; level++)
        {
            if (Markers.Any(marker => File.Exists(Path.Combine(directory, marker))))
            {
                var minecraft = Path.Combine(directory, ".minecraft");
                return Directory.Exists(minecraft) ? minecraft : directory;
            }
            var parent = Directory.GetParent(directory)?.FullName;
            if (parent is null || parent == directory) break;
            directory = parent;
        }
        return null;
    }

    public MigrationPlan CreatePlan(string source, string target, MigrationMode mode)
    {
        source = Path.GetFullPath(source.Trim());
        target = Path.GetFullPath(target.Trim());
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The source directory does not exist.");
        EnsureSeparateDirectories(source, target);
        Directory.CreateDirectory(target);

        var server = mode == MigrationMode.Server;
        var paths = RetainDirectories.Concat(RetainFiles).Concat(server ? ServerOnly : []).ToList();
        if (server && GetWorldDirectory(source) is { } worldDirectory && !paths.Contains(worldDirectory, StringComparer.OrdinalIgnoreCase))
            paths.Add(worldDirectory);
        var items = new List<MigrationItem>();
        foreach (var relative in paths)
        {
            var full = Path.Combine(source, relative);
            if (!File.Exists(full) && !Directory.Exists(full)) continue;
            var isDirectory = Directory.Exists(full);
            items.Add(new MigrationItem(relative, isDirectory ? "DIRECTORY" : "FILE", isDirectory ? DirectorySize(full) : new FileInfo(full).Length));
        }

        var total = items.Sum(item => item.Size);
        var copyFileCount = items.Sum(item => item.Kind == "DIRECTORY"
            ? Directory.EnumerateFiles(Path.Combine(source, item.RelativePath), "*", SearchOption.AllDirectories).Count()
            : 1);
        var catalog = server ? ServerCatalog : ClientCatalog;
        var commonChanges = catalog.Select(change => new CommonChangeStatus(
            change,
            IsSet(Path.Combine(source, change.RelativePath), change),
            IsSet(Path.Combine(target, change.RelativePath), change))).ToArray();
        var modChanges = CollectModInventory(source, target);
        var startupScriptDifferences = server ? CompareStartupScripts(source, target) : Array.Empty<StartupScriptDifference>();
        IReadOnlyList<string> manualReminders = server ? Array.Empty<string>() : ClientManualReminders;
        var affectedTargetDirectories = CollectAffectedTargetDirectories(target, items);

        return new MigrationPlan(source, target, server ? "SERVER" : "CLIENT", items, total, copyFileCount, affectedTargetDirectories, commonChanges, modChanges, startupScriptDifferences, manualReminders, CollectPreflight(source, target, total, server));
    }

    private static IReadOnlyList<string> CollectAffectedTargetDirectories(string target, IEnumerable<MigrationItem> items)
    {
        return items
            .Select(item => item.Kind == "DIRECTORY"
                ? item.RelativePath
                : Path.GetDirectoryName(item.RelativePath))
            .Where(relative => !string.IsNullOrWhiteSpace(relative) && Directory.Exists(Path.Combine(target, relative!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static string? GetWorldDirectory(string source)
    {
        var levelName = ReadServerProperty(Path.Combine(source, "server.properties"), "level-name");
        if (string.IsNullOrWhiteSpace(levelName)) return null;

        var worldPath = Path.GetFullPath(Path.Combine(source, levelName.Trim().Trim('"')));
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var sourcePrefix = normalizedSource + Path.DirectorySeparatorChar;
        if (!worldPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(worldPath)) return null;
        return Path.GetRelativePath(source, worldPath);
    }

    private static string? ReadServerProperty(string path, string key)
    {
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator <= 0 || !string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            return trimmed[(separator + 1)..].Trim();
        }
        return null;
    }

    private static void EnsureSeparateDirectories(string source, string target)
    {
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var sourcePrefix = normalizedSource + Path.DirectorySeparatorChar;
        var targetPrefix = normalizedTarget + Path.DirectorySeparatorChar;

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
            normalizedSource.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and target must be separate, non-overlapping directories to keep the source instance unchanged.");
    }

    private static IReadOnlyList<PreflightFinding> CollectPreflight(string source, string target, long requiredBytes, bool server)
    {
        var findings = new List<PreflightFinding>();
        var root = Path.GetPathRoot(target);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < requiredBytes) findings.Add(new PreflightFinding("ERROR", $"Target drive has {FormatBytes(free)} free, but migration needs {FormatBytes(requiredBytes)}."));
            else findings.Add(new PreflightFinding("OK", $"Target drive has {FormatBytes(free)} free."));
        }
        if (!File.Exists(Path.Combine(source, "instance.cfg")) && !File.Exists(Path.Combine(source, "mmc-pack.json")) && !File.Exists(Path.Combine(source, "server.properties")))
            findings.Add(new PreflightFinding("WARNING", "Source does not contain a recognized Prism or server marker."));
        var hasServerMarker = File.Exists(Path.Combine(source, "server.properties"));
        if (server && !hasServerMarker)
            findings.Add(new PreflightFinding("WARNING", "Server migration selected, but the source has no server.properties file."));
        else if (!server && hasServerMarker)
            findings.Add(new PreflightFinding("WARNING", "Client migration selected, but the source has a server.properties file (looks like a server instance)."));
        if (ReadState(target) is { } previousState && !string.Equals(previousState.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            findings.Add(new PreflightFinding("WARNING", $"Target has an incomplete previous migration ({previousState.Status}); review {Path.GetFileName(GetStatePath(target))} before continuing."));
        if (Process.GetProcessesByName("java").Length > 0 || Process.GetProcessesByName("prismlauncher").Length > 0)
            findings.Add(new PreflightFinding("WARNING", "Minecraft or Prism Launcher appears to be running. Close it before migrating."));
        else findings.Add(new PreflightFinding("OK", "Minecraft and Prism Launcher are not running."));
        return findings;
    }

    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.00} KiB";

    private static IReadOnlyList<StartupScriptDifference> CompareStartupScripts(string source, string target)
    {
        var names = Directory.EnumerateFiles(source, "*.bat").Concat(Directory.EnumerateFiles(source, "*.sh"))
            .Concat(Directory.EnumerateFiles(target, "*.bat")).Concat(Directory.EnumerateFiles(target, "*.sh"))
            .Select(Path.GetFileName).Where(name => name is not null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        var differences = new List<StartupScriptDifference>();
        foreach (var name in names)
        {
            var sourcePath = Path.Combine(source, name!);
            var targetPath = Path.Combine(target, name!);
            if (!File.Exists(sourcePath) || !File.Exists(targetPath))
            {
                differences.Add(new StartupScriptDifference(name!, "FILE", File.Exists(sourcePath) ? "Present" : "Missing", File.Exists(targetPath) ? "Present" : "Missing"));
                continue;
            }

            var sourceText = File.ReadAllText(sourcePath);
            var targetText = File.ReadAllText(targetPath);
            AddDifference(differences, name!, "RAM", ExtractRam(sourceText), ExtractRam(targetText));
            AddDifference(differences, name!, "JAVA ARGUMENTS", ExtractJavaArguments(sourceText), ExtractJavaArguments(targetText));
            AddDifference(differences, name!, "RESTART", DescribeRestart(sourceText), DescribeRestart(targetText));
        }
        return differences;
    }

    private static void AddDifference(List<StartupScriptDifference> differences, string fileName, string category, string sourceValue, string targetValue)
    {
        if (!string.Equals(sourceValue, targetValue, StringComparison.OrdinalIgnoreCase))
            differences.Add(new StartupScriptDifference(fileName, category, sourceValue, targetValue));
    }

    private static string ExtractRam(string text)
    {
        var match = Regex.Match(text, @"-Xms(?<min>\S+)\s+-Xmx(?<max>\S+)", RegexOptions.IgnoreCase);
        return match.Success ? $"Xms {match.Groups["min"].Value}, Xmx {match.Groups["max"].Value}" : "Not specified";
    }

    private static string ExtractJavaArguments(string text)
    {
        var line = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).FirstOrDefault(line => Regex.IsMatch(line, @"\bjava(?:\.exe)?\b", RegexOptions.IgnoreCase));
        if (line is null) return "No Java command";
        return Regex.Replace(line.Trim(), @"-Xms\S+\s+-Xmx\S+\s*", "", RegexOptions.IgnoreCase).Trim();
    }

    private static string DescribeRestart(string text)
    {
        if (Regex.IsMatch(text, @"while\s+true", RegexOptions.IgnoreCase)) return "Automatic restart loop";
        if (Regex.IsMatch(text, @"(^|\s)pause(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)) return "Waits for input before exit";
        return "No automatic restart";
    }

    // Compares mod IDs from mcmod.info when available, so a differently named newer target JAR is not offered as an add-on.
    private static IReadOnlyList<ModInventoryEntry> CollectModInventory(string source, string target)
    {
        var findings = new List<ModInventoryEntry>();
        foreach (var folder in new[] { "mods", "coremods" })
        {
            var sourceFiles = InventoryFiles(Path.Combine(source, folder));
            var targetFiles = InventoryFiles(Path.Combine(target, folder));
            var targetModIds = targetFiles
                .SelectMany(file => ModIdentities(Path.Combine(target, folder, file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in sourceFiles.Union(targetFiles, StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var inSource = sourceFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
                var inTarget = targetFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (inSource && !inTarget)
                {
                    var sourceModIds = ModIdentities(Path.Combine(source, folder, name));
                    var state = sourceModIds.Any(targetModIds.Contains) ? "TARGET_VERSION" : "SOURCE_ONLY";
                    findings.Add(new ModInventoryEntry(folder, name, state));
                }
                else if (!inSource && inTarget) findings.Add(new ModInventoryEntry(folder, name, "TARGET_ONLY"));
            }
        }
        return findings;
    }

    private static IReadOnlyList<string> ModIdentities(string path)
    {
        var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var metadata = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("mcmod.info", StringComparison.OrdinalIgnoreCase));
                if (metadata is not null)
                {
                    using var reader = new StreamReader(metadata.Open());
                    foreach (Match match in Regex.Matches(reader.ReadToEnd(), "\\\"modid\\\"\\s*:\\s*\\\"(?<id>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                        modIds.Add(match.Groups["id"].Value.Trim());
                }
            }
            catch (InvalidDataException)
            {
                // Non-JAR archives are compared using their stable filename portion below.
            }
            catch (IOException)
            {
                // A locked or unreadable archive is compared using its stable filename portion below.
            }
        }

        if (modIds.Count == 0)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var normalized = Regex.Replace(name, @"[-_ ]v?\d+(?:[.\-_][a-z0-9]+)*$", "", RegexOptions.IgnoreCase).Trim();
            modIds.Add(string.IsNullOrEmpty(normalized) ? name : normalized);
        }
        return modIds.ToArray();
    }

    private static IReadOnlyList<string> InventoryFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(directory, file)).ToArray()
            : [];

    private static bool IsSet(string path, CommonChange change) =>
        string.Equals(ReadValue(path, change), change.Value, StringComparison.OrdinalIgnoreCase);

    // Forge's Configuration format prefixes keys with a single letter and colon (e.g. "B:someKey=true").
    private static string StripForgeTypePrefix(string key) =>
        key.Length > 2 && key[1] == ':' && char.IsLetter(key[0]) ? key[2..].Trim() : key.Trim();

    private static string? ReadValue(string path, CommonChange change)
    {
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var separatorIndex = line.IndexOf(change.Separator);
            if (separatorIndex <= 0) continue;
            var key = StripForgeTypePrefix(line[..separatorIndex]);
            if (!string.Equals(key, change.Key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separatorIndex + 1)..].Trim();
        }
        return null;
    }

    private static void UpsertValue(string path, CommonChange change)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var updated = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var separatorIndex = lines[index].IndexOf(change.Separator);
            if (separatorIndex <= 0) continue;
            var rawKey = lines[index][..separatorIndex];
            if (!string.Equals(StripForgeTypePrefix(rawKey), change.Key, StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve any existing Forge type prefix (e.g. "B:") when rewriting the line.
            var prefix = rawKey.Trim().Length != StripForgeTypePrefix(rawKey).Length ? rawKey.Trim()[..2] : "";
            lines[index] = $"{prefix}{change.Key}{change.Separator}{change.Value}";
            updated = true;
            break;
        }
        if (!updated)
        {
            var prefix = Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase) ? "B:" : "";
            lines.Add($"{prefix}{change.Key}{change.Separator}{change.Value}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllLines(path, lines);
    }

    public async Task<string?> ExecuteAsync(MigrationPlan plan, IProgress<(string Path, int Completed, int Total)> progress, CancellationToken token, ISet<string>? selectedChangeIds = null, ISet<string>? selectedModPaths = null)
    {
        EnsureSeparateDirectories(plan.Source, plan.Target);
        var selectedModCount = plan.ModChanges.Count(entry => entry.State == "SOURCE_ONLY" && selectedModPaths?.Contains(ModPath(entry)) == true);
        var statePath = GetStatePath(plan.Target);
        var startedAt = DateTimeOffset.UtcNow;
        var completedPaths = new List<string>();
        var state = new MigrationExecutionState("RUNNING", plan.Source, plan.Target, plan.Mode, null, plan.CopyFileCount + selectedModCount, 0, completedPaths, null, startedAt, startedAt);
        WriteState(statePath, state);

        void UpdateState(string status, string? backupPath = null, string? error = null, int completed = 0)
        {
            state = state with
            {
                Status = status,
                BackupPath = backupPath ?? state.BackupPath,
                CompletedOperations = completed,
                CompletedPaths = completedPaths.ToArray(),
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            WriteState(statePath, state);
        }

        var trackedProgress = new SynchronousProgress<(string Path, int Completed, int Total)>(update =>
        {
            if (!update.Path.StartsWith("BACKUP ", StringComparison.Ordinal))
                completedPaths.Add(update.Path);
            UpdateState("RUNNING", completed: update.Completed);
            progress.Report(update);
        });

        try
        {
            var backupPath = await ExecuteCoreAsync(plan, trackedProgress, token, selectedChangeIds, selectedModPaths);
            UpdateState("COMPLETED", backupPath, completed: state.TotalOperations);
            return backupPath;
        }
        catch (OperationCanceledException)
        {
            UpdateState("CANCELLED", error: "Migration was cancelled.", completed: state.CompletedOperations);
            throw;
        }
        catch (Exception exception)
        {
            UpdateState("FAILED", error: exception.Message, completed: state.CompletedOperations);
            throw;
        }
    }

    private async Task<string?> ExecuteCoreAsync(MigrationPlan plan, IProgress<(string Path, int Completed, int Total)> progress, CancellationToken token, ISet<string>? selectedChangeIds, ISet<string>? selectedModPaths)
    {
        EnsureSeparateDirectories(plan.Source, plan.Target);

        var selectedMods = plan.ModChanges
            .Where(entry => entry.State == "SOURCE_ONLY" && selectedModPaths?.Contains(ModPath(entry)) == true)
            .ToArray();
        var total = plan.CopyFileCount + selectedMods.Length;
        var completed = 0;
        string? backupRoot = null;
        var backedUpFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task PrepareTargetAsync(string target)
        {
            if (!File.Exists(target) || !backedUpFiles.Add(target)) return;
            backupRoot ??= Path.Combine(
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(plan.Target))!,
                $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Target))}.gtnh-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            var relativePath = Path.GetRelativePath(plan.Target, target);
            var backupPath = Path.Combine(backupRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            progress.Report(($"BACKUP {relativePath}", completed, total));
            await CopyFileContentsAsync(target, backupPath, token);
        }

        foreach (var item in plan.Items)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(plan.Source, item.RelativePath);
            var target = Path.Combine(plan.Target, item.RelativePath);
            if (item.Kind == "DIRECTORY")
                await CopyDirectoryAsync(source, target, token, relativePath =>
                {
                    var targetFile = Path.Combine(target, relativePath);
                    return PrepareTargetAsync(targetFile);
                }, relativePath =>
                {
                    progress.Report((Path.Combine(item.RelativePath, relativePath), ++completed, total));
                    return Task.CompletedTask;
                });
            else
            {
                await PrepareTargetAsync(target);
                await CopyFileAsync(source, target, token);
                progress.Report((item.RelativePath, ++completed, total));
            }
        }

        for (var index = 0; index < selectedMods.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var mod = selectedMods[index];
            var relativePath = ModPath(mod);
            var target = Path.Combine(plan.Target, relativePath);
            await PrepareTargetAsync(target);
            await CopyFileAsync(Path.Combine(plan.Source, relativePath), target, token);
            progress.Report((relativePath, ++completed, total));
        }

        if (selectedChangeIds is { Count: > 0 })
        {
            foreach (var status in plan.CommonChanges.Where(status => selectedChangeIds.Contains(status.Change.Id)))
            {
                var target = Path.Combine(plan.Target, status.Change.RelativePath);
                await PrepareTargetAsync(target);
                UpsertValue(target, status.Change);
            }
        }

        return backupRoot;
    }

    private static long DirectorySize(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static string ModPath(ModInventoryEntry entry) => Path.Combine(entry.Folder, entry.FileName);

    public static string GetStatePath(string target) => Path.Combine(target, ".gtnh-migrator-state.json");

    private static void WriteState(string path, MigrationExecutionState state)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, true);
    }

    private static MigrationExecutionState? ReadState(string target)
    {
        var path = GetStatePath(target);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationExecutionState>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, 1024 * 1024, token);
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken token, Func<string, Task> beforeCopy, Func<string, Task> afterCopy)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            await beforeCopy(relativePath);
            await CopyFileAsync(file, Path.Combine(target, relativePath), token);
            await afterCopy(relativePath);
        }
    }

    private static async Task CopyFileContentsAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, 1024 * 1024, token);
    }
}
