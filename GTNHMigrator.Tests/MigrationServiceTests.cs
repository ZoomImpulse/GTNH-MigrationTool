using GTNHMigrator;
using System.Text.Json;
using Xunit;

namespace GTNHMigrator.Tests;

public sealed class MigrationServiceTests
{
    [Fact]
    public void CreatePlan_ReportsExistingRetainedTargetDirectories()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource(Path.Combine("saves", "world", "level.dat"), "source");
        fixture.WriteTarget(Path.Combine("saves", "world", "level.dat"), "target");

        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);

        Assert.Contains("saves", plan.LegacyDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Items, item => item.RelativePath == "saves" && item.Kind == "DIRECTORY");
    }

    [Fact]
    public void CreatePlan_RejectsSameOrNestedDirectories()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource("instance.cfg", "source");

        Assert.Throws<InvalidOperationException>(() =>
            new MigrationService().CreatePlan(fixture.Source, fixture.Source, MigrationMode.Client));
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationService().CreatePlan(fixture.Source, Path.Combine(fixture.Source, "nested"), MigrationMode.Client));
    }

    [Fact]
    public async Task ExecuteAsync_BackupsExistingFilesBeforeReplacingThem()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource(Path.Combine("saves", "world", "level.dat"), "new content");
        fixture.WriteTarget(Path.Combine("saves", "world", "level.dat"), "old content");
        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);

        var backupPath = await new MigrationService().ExecuteAsync(
            plan,
            new Progress<(string Path, int Completed, int Total)>(),
            CancellationToken.None);

        Assert.NotNull(backupPath);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(fixture.Target, "saves", "world", "level.dat")));
        Assert.Equal("old content", File.ReadAllText(Path.Combine(backupPath!, "saves", "world", "level.dat")));

        var state = JsonSerializer.Deserialize<MigrationExecutionState>(File.ReadAllText(MigrationService.GetStatePath(fixture.Target)));
        Assert.NotNull(state);
        Assert.Equal("COMPLETED", state.Status);
        Assert.Equal(fixture.Target, state.Target);
        Assert.Contains(Path.Combine("saves", "world", "level.dat"), state.CompletedPaths);
    }

    [Fact]
    public void CreatePlan_ServerMigrationIncludesConfiguredWorldDirectory()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource("server.properties", "level-name=custom-world\n");
        fixture.WriteSource(Path.Combine("custom-world", "level.dat"), "world");

        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Server);

        Assert.Contains(plan.Items, item => item.RelativePath == "custom-world" && item.Kind == "DIRECTORY");
    }

    [Fact]
    public void CreatePlan_WarnsAboutIncompletePreviousMigration()
    {
        using var fixture = new MigrationFixture();
        var now = DateTimeOffset.UtcNow;
        var state = new MigrationExecutionState(
            "FAILED",
            fixture.Source,
            fixture.Target,
            "CLIENT",
            null,
            1,
            0,
            [],
            "copy failed",
            now,
            now);
        File.WriteAllText(MigrationService.GetStatePath(fixture.Target), JsonSerializer.Serialize(state));

        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);

        Assert.Contains(plan.Preflight, finding => finding.Severity == "WARNING" && finding.Detail.Contains("incomplete previous migration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_AppliesSelectedConfigChangeAndCreatesMissingFile()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource(Path.Combine("config", "lwjgl3ify.cfg"), "B:borderless=false\n");
        var service = new MigrationService();
        var plan = service.CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);
        var selectedChanges = plan.CommonChanges
            .Where(status => status.Change.Id == "enable-borderless")
            .Select(status => status.Change.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await service.ExecuteAsync(
            plan,
            new Progress<(string Path, int Completed, int Total)>(),
            CancellationToken.None,
            selectedChanges,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("B:borderless=true", File.ReadAllText(Path.Combine(fixture.Target, "config", "lwjgl3ify.cfg")).Trim());
    }

    [Fact]
    public async Task ExecuteAsync_PersistsCancelledState()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource(Path.Combine("saves", "world", "level.dat"), "source");
        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new MigrationService().ExecuteAsync(
            plan,
            new Progress<(string Path, int Completed, int Total)>(),
            cancellation.Token));

        var state = JsonSerializer.Deserialize<MigrationExecutionState>(File.ReadAllText(MigrationService.GetStatePath(fixture.Target)));
        Assert.NotNull(state);
        Assert.Equal("CANCELLED", state.Status);
        Assert.Empty(state.CompletedPaths);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsFailureStateWhenSourceFileDisappears()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteSource("options.txt", "source");
        var plan = new MigrationService().CreatePlan(fixture.Source, fixture.Target, MigrationMode.Client);
        File.Delete(Path.Combine(fixture.Source, "options.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() => new MigrationService().ExecuteAsync(
            plan,
            new Progress<(string Path, int Completed, int Total)>(),
            CancellationToken.None));

        var state = JsonSerializer.Deserialize<MigrationExecutionState>(File.ReadAllText(MigrationService.GetStatePath(fixture.Target)));
        Assert.NotNull(state);
        Assert.Equal("FAILED", state.Status);
        Assert.Contains("options.txt", state.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MigrationFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gtnh-migrator-tests", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public string Target => Path.Combine(Root, "target");

        public MigrationFixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Target);
        }

        public void WriteSource(string relativePath, string content) => Write(Source, relativePath, content);
        public void WriteTarget(string relativePath, string content) => Write(Target, relativePath, content);

        private static void Write(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
