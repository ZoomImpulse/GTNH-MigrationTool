# GTNH Migrator

GTNH Migrator is a Windows WPF utility for moving supported player data from an older GregTech: New Horizons installation into a newer instance. It targets .NET 8 and supports Prism/MultiMC-style client instances and dedicated servers.

## Features

- Client migration for Prism Launcher, MultiMC, or a `.minecraft` instance.
- Dedicated server migration with server-specific retained files.
- Combined client and server migration with independent retry handling.
- Whitelist-based copying so the target modpack and unrelated files are preserved.
- Backup of existing target files before replacement.
- Reviewable common configuration tweaks and optional source-only mod copies.
- Server world-directory detection from `server.properties` and startup-script comparison.
- Preflight checks for source type, target disk space, running Minecraft/Prism processes, and incomplete previous runs.
- JSON migration state written to the target while an operation is running or has failed/cancelled.

## Requirements

- Windows.
- .NET 8 SDK.
- A source and target directory on separate, non-overlapping paths.

The application does not modify Prism Launcher configuration. Profiles are discovered from the standard Prism Launcher application-data locations when available, as well as local `instances` folders beside the application or current working directory.

## Build and Test

From the repository root:

```powershell
dotnet build .\GTNHMigrator.csproj --configuration Release
dotnet test .\GTNHMigrator.Tests\GTNHMigrator.Tests.csproj --configuration Release
```

To run the application after a Debug build:

```powershell
dotnet run --project .\GTNHMigrator.csproj --configuration Debug
```

The test suite uses xUnit and creates isolated temporary source and target directories for filesystem tests.

## Migration Behavior

### Client mode

Client migrations retain the documented player data, including saves, screenshots, schematics, resource packs, shader packs, JourneyMap data, selected options, servers, and other GTNH-specific files. Common GTNH configuration tweaks can be selected during review. Manual reminders are shown for changes that require an in-game menu, deletion, or JSON editing.

### Server mode

Server migrations retain server administration files, `server.properties`, the `serverutilities` folder, JourneyMap server data, and the world named by `level-name` in the source `server.properties` file. Startup scripts are compared for RAM, Java arguments, and restart behavior.

### Mods and configuration changes

Source-only mods in `mods` and `coremods` are shown for optional selection. Mod identities are read from `mcmod.info` when available, with a filename-based fallback for unreadable or non-standard archives.

Selected configuration changes update the target file while preserving an existing Forge type prefix such as `B:`. Missing configuration files and directories are created as needed.

## Backups and Recovery State

Before replacing an existing target file, the application copies it to a sibling backup directory named like:

```text
<target-name>.gtnh-backup-YYYYMMDD-HHMMSS
```

Each target also receives `.gtnh-migrator-state.json` during execution. The state records the source, target, mode, operation counts, completed paths, backup path, timestamps, and terminal status:

- `RUNNING`: migration is currently in progress.
- `COMPLETED`: migration finished successfully.
- `CANCELLED`: the user cancelled the operation.
- `FAILED`: an operation raised an error; the error message is retained.

A later scan warns when the target contains a non-completed state file. The current implementation records recovery information and supports the existing in-session retry flow, but it does not automatically resume individual unfinished files.

## Safety Notes

- Source and target paths must be different and must not contain one another.
- Only whitelisted data and explicitly selected source-only mods are copied.
- Existing target files are backed up before replacement.
- Close Minecraft, the launcher, and the dedicated server before migrating.
- Review warnings and the transfer manifest before executing a migration.

## Repository Layout

- `MigrationService.cs`: planning, preflight, inventory, configuration updates, backup, copying, and recovery state.
- `MainWindow.xaml` and `MainWindow.xaml.cs`: client, server, and dual-migration workflows.
- `ChangesWindow.xaml` and `ChangesWindow.xaml.cs`: common tweak and mod selection.
- `MigrationLog.cs`: application error log handling.
- `GTNHMigrator.Tests/`: focused service tests using temporary filesystem fixtures.
- `Resources/Fonts/`: bundled Monocraft font and its license.
