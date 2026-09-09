using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GTNHMigrator;

public partial class MainWindow : Window
{
    private readonly MigrationService service = new();
    private readonly HashSet<string> selectedChangeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> clientSelectedChangeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> clientSelectedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> serverSelectedChangeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> serverSelectedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private MigrationPlan? plan;
    private MigrationPlan? clientPlan;
    private MigrationPlan? serverPlan;
    private CancellationTokenSource? cancellation;
    private MigrationMode mode = MigrationMode.Client;
    private bool dualClientCompleted;
    private bool dualServerCompleted;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var pathBox in new[] { SourceBox, TargetBox, DualClientSourceBox, DualClientTargetBox, DualServerSourceBox, DualServerTargetBox })
            pathBox.TextWrapping = TextWrapping.Wrap;
        RefreshProfiles();
    }

    private void ChangeMode_Click(object sender, RoutedEventArgs e)
    {
        MainContentGrid.Visibility = Visibility.Collapsed;
        DualContentGrid.Visibility = Visibility.Collapsed;
        ModeSelectionView.Visibility = Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (cancellation is null) return;
        e.Cancel = !MigrationDialog.Confirm(this, "Operation In Progress", "A scan or migration is still running. Closing now may leave a partial target. Close anyway?");
        if (e.Cancel) return;
        cancellation.Cancel();
    }

    private void Client_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectMode(MigrationMode.Client);
    private void Server_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectMode(MigrationMode.Server);
    private void Dual_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => EnterDualMode();

    private void ModeCard_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)) return;
        if (sender is not Border card) return;

        var cardName = System.Windows.Automation.AutomationProperties.GetName(card);
        if (cardName == "Client migration") SelectMode(MigrationMode.Client);
        else if (cardName == "Server migration") SelectMode(MigrationMode.Server);
        else EnterDualMode();
        e.Handled = true;
    }

    private void SelectMode(MigrationMode selected)
    {
        mode = selected;
        ApplyModeToHeader();

        // Selection scope depends on mode, so any prior scan/plan is now stale.
        plan = null;
        selectedChangeIds.Clear();
        selectedModPaths.Clear();
        ItemsList.Items.Clear();
        ChangesSummaryText.Text = "Scan to review settings and additional mods.";
        ReviewChangesButton.IsEnabled = false;
        MigrateButton.IsEnabled = false;
        WarningText.Text = string.Empty;
        StatusText.Text = mode == MigrationMode.Server
            ? "Server migration selected. Choose the source and target server folders."
            : "Client migration selected. Choose source and target profiles.";

        ModeSelectionView.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
    }

    private void ApplyModeToHeader()
    {
        (TitleText.Text, ModeSubtitleText.Text) = mode == MigrationMode.Server
            ? ("SERVER TRANSFER", "SERVER DATA")
            : ("INSTANCE TRANSFER", "CLIENT DATA");
        var server = mode == MigrationMode.Server;
        EndpointTitleText.Text = server ? "01 / SERVER FOLDERS" : "01 / PRISM PROFILES";
        EndpointSubtitleText.Text = server ? "Transfer folders" : "Transfer endpoints";
        SourceSelectorLabel.Text = server ? "SOURCE FOLDER" : "SOURCE PROFILE";
        TargetSelectorLabel.Text = server ? "TARGET FOLDER" : "TARGET PROFILE";
        EndpointHintText.Text = server
            ? "Dedicated servers are selected directly from the file system."
            : "Profiles are discovered locally. Prism Launcher configuration is never modified.";
        SourceProfilePicker.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        TargetProfilePicker.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        ServerSourcePicker.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        ServerTargetPicker.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        RefreshProfilesButton.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnterDualMode()
    {
        dualClientCompleted = false;
        dualServerCompleted = false;
        SetDualRetry(null);
        clientPlan = null;
        serverPlan = null;
        clientSelectedChangeIds.Clear();
        clientSelectedModPaths.Clear();
        serverSelectedChangeIds.Clear();
        serverSelectedModPaths.Clear();
        DualItemsList.Items.Clear();
        DualChangesSummaryText.Text = "Scan both migrations to review settings and additional mods.";
        DualReviewClientChangesButton.IsEnabled = false;
        DualReviewServerChangesButton.IsEnabled = false;
        DualMigrateButton.IsEnabled = false;
        DualWarningText.Text = string.Empty;
        DualStatusText.Text = "Ready. Choose both instance pairs.";

        ModeSelectionView.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Collapsed;
        DualContentGrid.Visibility = Visibility.Visible;
    }

    private void RefreshProfiles_Click(object sender, RoutedEventArgs e) => RefreshProfiles();

    private void RefreshProfiles()
    {
        var profiles = service.FindPrismProfiles();
        SourceProfileBox.ItemsSource = profiles;
        TargetProfileBox.ItemsSource = profiles;
        DualClientSourceProfileBox.ItemsSource = profiles;
        DualClientTargetProfileBox.ItemsSource = profiles;
        var message = profiles.Count == 0
            ? "No Prism Launcher profiles found in the standard instance locations."
            : $"{profiles.Count} Prism Launcher profile{(profiles.Count == 1 ? "" : "s")} ready for selection.";
        StatusText.Text = message;
        DualStatusText.Text = message;
    }

    private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == SourceProfileBox && SourceProfileBox.SelectedItem is PrismProfile source)
        {
            SourceBox.Text = source.Path;
            SourceBox.ToolTip = source.Path;
            InvalidateSinglePlan();
        }
        if (sender == TargetProfileBox && TargetProfileBox.SelectedItem is PrismProfile target)
        {
            TargetBox.Text = target.Path;
            TargetBox.ToolTip = target.Path;
            InvalidateSinglePlan();
        }
        if (sender == DualClientSourceProfileBox && DualClientSourceProfileBox.SelectedItem is PrismProfile dcs)
        {
            DualClientSourceBox.Text = dcs.Path;
            DualClientSourceBox.ToolTip = dcs.Path;
            InvalidateDualPlans();
        }
        if (sender == DualClientTargetProfileBox && DualClientTargetProfileBox.SelectedItem is PrismProfile dct)
        {
            DualClientTargetBox.Text = dct.Path;
            DualClientTargetBox.ToolTip = dct.Path;
            InvalidateDualPlans();
        }
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => BrowseInto(SourceBox, "Select source instance", InvalidateSinglePlan);
    private void BrowseTarget_Click(object sender, RoutedEventArgs e) => BrowseInto(TargetBox, "Select target instance", InvalidateSinglePlan);
    private void BrowseDualClientSource_Click(object sender, RoutedEventArgs e) => BrowseInto(DualClientSourceBox, "Select client source instance", InvalidateDualPlans);
    private void BrowseDualClientTarget_Click(object sender, RoutedEventArgs e) => BrowseInto(DualClientTargetBox, "Select client target instance", InvalidateDualPlans);
    private void BrowseDualServerSource_Click(object sender, RoutedEventArgs e) => BrowseInto(DualServerSourceBox, "Select server source folder", InvalidateDualPlans);
    private void BrowseDualServerTarget_Click(object sender, RoutedEventArgs e) => BrowseInto(DualServerTargetBox, "Select server target folder", InvalidateDualPlans);

    private static void BrowseInto(TextBox destination, string title, Action invalidate)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true)
        {
            destination.Text = dialog.FolderName;
            destination.ToolTip = dialog.FolderName;
            invalidate();
        }
    }

    private void InvalidateSinglePlan()
    {
        plan = null;
        ReviewChangesButton.IsEnabled = false;
        MigrateButton.IsEnabled = false;
        ItemsList.Items.Clear();
        ChangesSummaryText.Text = "Scan to review settings and additional mods.";
        WarningText.Text = string.Empty;
    }

    private void InvalidateDualPlans()
    {
        clientPlan = null;
        serverPlan = null;
        DualReviewClientChangesButton.IsEnabled = false;
        DualReviewServerChangesButton.IsEnabled = false;
        DualMigrateButton.IsEnabled = false;
        DualItemsList.Items.Clear();
        DualChangesSummaryText.Text = "Scan both migrations to review settings and additional mods.";
        DualWarningText.Text = string.Empty;
    }

    private static IReadOnlyList<string> ValidateEndpoints(string source, string target, MigrationMode selectedMode)
    {
        var findings = new List<string>();
        if (string.IsNullOrWhiteSpace(source))
        {
            findings.Add("ERROR: Choose a source folder before scanning.");
            return findings;
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            findings.Add("ERROR: Choose a target folder before scanning.");
            return findings;
        }

        string normalizedSource;
        string normalizedTarget;
        try
        {
            normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Trim()));
            normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            findings.Add("ERROR: One of the selected paths is not valid.");
            return findings;
        }

        if (!Directory.Exists(normalizedSource))
            findings.Add("ERROR: The source folder does not exist.");

        var sourcePrefix = normalizedSource + Path.DirectorySeparatorChar;
        var targetPrefix = normalizedTarget + Path.DirectorySeparatorChar;
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
            normalizedSource.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            findings.Add("ERROR: Source and target must be separate, non-overlapping folders.");

        var hasServerMarker = File.Exists(Path.Combine(normalizedSource, "server.properties"));
        var hasClientMarker = File.Exists(Path.Combine(normalizedSource, "instance.cfg")) ||
                               File.Exists(Path.Combine(normalizedSource, "mmc-pack.json"));
        if (selectedMode == MigrationMode.Server && !hasServerMarker)
            findings.Add("WARNING: The source has no server.properties file and may not be a dedicated server.");
        if (selectedMode == MigrationMode.Client && hasServerMarker && !hasClientMarker)
            findings.Add("WARNING: The source looks like a dedicated server. Choose Server migration if that is intentional.");

        return findings;
    }

    private static bool ShowEndpointValidation(IReadOnlyList<string> findings, TextBlock warningText, TextBlock statusText)
    {
        warningText.Text = string.Join(Environment.NewLine, findings);
        var hasError = findings.Any(finding => finding.StartsWith("ERROR:", StringComparison.Ordinal));
        if (hasError)
            statusText.Text = "Choose valid source and target folders before scanning.";
        return !hasError;
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        var endpointFindings = ValidateEndpoints(SourceBox.Text, TargetBox.Text, mode);
        if (!ShowEndpointValidation(endpointFindings, WarningText, StatusText)) return;
        SetRecoveryActions(false, false);
        cancellation = new CancellationTokenSource();
        SetScanState(true);
        try
        {
            var source = SourceBox.Text;
            var target = TargetBox.Text;
            StatusText.Text = "Scanning files...";
            var scanToken = cancellation.Token;
            plan = await Task.Run(() =>
            {
                scanToken.ThrowIfCancellationRequested();
                var result = service.CreatePlan(source, target, mode);
                scanToken.ThrowIfCancellationRequested();
                return result;
            }, scanToken);

            ItemsList.Items.Clear();
            foreach (var item in plan.Items)
                ItemsList.Items.Add(FormatManifestItem(plan, item));

            selectedChangeIds.Clear();
            selectedModPaths.Clear();
            foreach (var status in plan.CommonChanges.Where(status => status.AppliedInSource))
                selectedChangeIds.Add(status.Change.Id);

            var appliedCount = plan.CommonChanges.Count(status => status.AppliedInSource);
            var additionalModCount = plan.ModChanges.Count(entry => entry.State == "SOURCE_ONLY");
            ChangesSummaryText.Text = $"{appliedCount} of {plan.CommonChanges.Count} settings found in the source. " +
                                       $"{additionalModCount} additional mod(s) detected.";
            ReviewChangesButton.IsEnabled = plan.CommonChanges.Count > 0 || additionalModCount > 0;

            var warnings = new List<string>();
            if (plan.LegacyDirectories.Count == 0)
                warnings.Add("The target GTNH installation, including its bundled mods, will be preserved.");
            else
                warnings.Add($"Warning: these target folders are affected: {string.Join(", ", plan.LegacyDirectories.Select(System.IO.Path.GetFileName))}");
            var collisionCount = CountTargetCollisions(plan);
            if (collisionCount > 0)
                warnings.Add($"{collisionCount} existing target file(s) will be backed up before replacement.");
            warnings.AddRange(plan.Preflight.Select(check => $"[{check.Severity}] {check.Detail}"));
            WarningText.Text = string.Join(Environment.NewLine, warnings);

            StatusText.Text = $"Scan complete. Required space: {FormatSize(plan.TotalBytes)}.";
            MigrateButton.IsEnabled = plan.Items.Count > 0 && !plan.Preflight.Any(check => check.Severity == "ERROR");

            if (ReviewChangesButton.IsEnabled) OpenChangesWindow();
        }
        catch (Exception ex)
        {
            MigrationLog.WriteError("Single migration scan failed", ex);
            StatusText.Text = ex.Message;
            SetRecoveryActions(false, true);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            SetScanState(false);
        }
    }

    private void ReviewChanges_Click(object sender, RoutedEventArgs e) => OpenChangesWindow();

    private void OpenChangesWindow()
    {
        if (plan is null) return;
        var window = new ChangesWindow(plan, selectedChangeIds, selectedModPaths) { Owner = this };
        window.ShowDialog();
        ChangesSummaryText.Text = $"{selectedChangeIds.Count} tweak(s) and {selectedModPaths.Count} additional mod(s) selected.";
    }

    private void SetScanState(bool scanning)
    {
        SourceProfileBox.IsEnabled = !scanning;
        TargetProfileBox.IsEnabled = !scanning;
        ServerSourcePicker.IsEnabled = !scanning;
        ServerTargetPicker.IsEnabled = !scanning;
        CancelButton.IsEnabled = scanning || cancellation is not null;
        CancelButton.Visibility = scanning || cancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = scanning;
        Progress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null) return;
        if (!MigrationDialog.Confirm(this, "Confirm Migration", "The target GTNH installation will be preserved. Only whitelisted data and selected source-only mods will be copied. Continue?")) return;

        MigrateButton.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Maximum = plan.CopyFileCount + selectedModPaths.Count;
        cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "Migration started. Preparing files...";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            var progress = new Progress<(string Path, int Completed, int Total)>(update =>
            {
                Progress.Maximum = update.Total;
                Progress.Value = update.Completed;
                StatusText.Text = $"Copying {update.Completed} of {update.Total}: {update.Path}";
            });

            var backupPath = await service.ExecuteAsync(plan, progress, cancellation.Token, selectedChangeIds, selectedModPaths);
            StatusText.Text = backupPath is null
                ? $"Migration complete. {plan.CopyFileCount + selectedModPaths.Count} files copied."
                : $"Migration complete. {plan.CopyFileCount + selectedModPaths.Count} files copied. Backup: {backupPath}";
            MigrationDialog.ShowInfo(this, "Migration Complete", "Migration completed successfully.");
            SetRecoveryActions(true, false);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Migration cancelled.";
            SetRecoveryActions(true, false);
        }
        catch (Exception ex)
        {
            MigrationLog.WriteError("Single migration failed", ex);
            StatusText.Text = $"Migration failed: {ex.Message} See {MigrationLog.FilePath} for details.";
            SetRecoveryActions(true, true);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            MigrateButton.IsEnabled = plan is not null && plan.Items.Count > 0 && !plan.Preflight.Any(check => check.Severity == "ERROR");
            CancelButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void ScanBoth_Click(object sender, RoutedEventArgs e)
    {
        var clientFindings = ValidateEndpoints(DualClientSourceBox.Text, DualClientTargetBox.Text, MigrationMode.Client);
        var serverFindings = ValidateEndpoints(DualServerSourceBox.Text, DualServerTargetBox.Text, MigrationMode.Server);
        var endpointFindings = clientFindings
            .Select(finding => $"[CLIENT] {finding}")
            .Concat(serverFindings.Select(finding => $"[SERVER] {finding}"))
            .ToArray();
        if (!ShowEndpointValidation(endpointFindings, DualWarningText, DualStatusText)) return;
        SetRecoveryActions(false, false);
        SetDualRetry(null);
        dualClientCompleted = false;
        dualServerCompleted = false;
        cancellation = new CancellationTokenSource();
        SetDualScanState(true);
        try
        {
            var clientSource = DualClientSourceBox.Text;
            var clientTarget = DualClientTargetBox.Text;
            var serverSource = DualServerSourceBox.Text;
            var serverTarget = DualServerTargetBox.Text;
            DualStatusText.Text = "Scanning files...";
            var scanToken = cancellation.Token;

            clientPlan = await Task.Run(() =>
            {
                scanToken.ThrowIfCancellationRequested();
                var result = service.CreatePlan(clientSource, clientTarget, MigrationMode.Client);
                scanToken.ThrowIfCancellationRequested();
                return result;
            }, scanToken);
            serverPlan = await Task.Run(() =>
            {
                scanToken.ThrowIfCancellationRequested();
                var result = service.CreatePlan(serverSource, serverTarget, MigrationMode.Server);
                scanToken.ThrowIfCancellationRequested();
                return result;
            }, scanToken);

            DualItemsList.Items.Clear();
            foreach (var item in clientPlan.Items)
                DualItemsList.Items.Add($"[CLIENT] {FormatManifestItem(clientPlan, item)}");
            foreach (var item in serverPlan.Items)
                DualItemsList.Items.Add($"[SERVER] {FormatManifestItem(serverPlan, item)}");

            clientSelectedChangeIds.Clear();
            clientSelectedModPaths.Clear();
            foreach (var status in clientPlan.CommonChanges.Where(status => status.AppliedInSource))
                clientSelectedChangeIds.Add(status.Change.Id);
            serverSelectedChangeIds.Clear();
            serverSelectedModPaths.Clear();
            foreach (var status in serverPlan.CommonChanges.Where(status => status.AppliedInSource))
                serverSelectedChangeIds.Add(status.Change.Id);

            var clientAdditionalModCount = clientPlan.ModChanges.Count(entry => entry.State == "SOURCE_ONLY");
            var serverAdditionalModCount = serverPlan.ModChanges.Count(entry => entry.State == "SOURCE_ONLY");
            DualChangesSummaryText.Text = $"Client: {clientSelectedChangeIds.Count} of {clientPlan.CommonChanges.Count} tweaks selected, {clientAdditionalModCount} additional mod(s). " +
                                           $"Server: {serverSelectedChangeIds.Count} of {serverPlan.CommonChanges.Count} tweaks selected, {serverAdditionalModCount} additional mod(s).";
            DualReviewClientChangesButton.IsEnabled = clientPlan.CommonChanges.Count > 0 || clientAdditionalModCount > 0;
            DualReviewServerChangesButton.IsEnabled = serverPlan.CommonChanges.Count > 0 || serverAdditionalModCount > 0;

            var warnings = new List<string>();
            var legacy = clientPlan.LegacyDirectories.Concat(serverPlan.LegacyDirectories).ToArray();
            warnings.Add(legacy.Length == 0
                ? "Both target GTNH installations, including their bundled mods, will be preserved."
                : $"Warning: these target folders are affected: {string.Join(", ", legacy.Select(System.IO.Path.GetFileName))}");
            var clientCollisions = CountTargetCollisions(clientPlan);
            var serverCollisions = CountTargetCollisions(serverPlan);
            if (clientCollisions + serverCollisions > 0)
                warnings.Add($"{clientCollisions + serverCollisions} existing target file(s) will be backed up before replacement.");
            warnings.AddRange(clientPlan.Preflight.Select(check => $"[CLIENT/{check.Severity}] {check.Detail}"));
            warnings.AddRange(serverPlan.Preflight.Select(check => $"[SERVER/{check.Severity}] {check.Detail}"));
            DualWarningText.Text = string.Join(Environment.NewLine, warnings);

            var totalBytes = clientPlan.TotalBytes + serverPlan.TotalBytes;
            DualStatusText.Text = $"Scan complete. Required space: {FormatSize(totalBytes)}.";
            var hasError = clientPlan.Preflight.Concat(serverPlan.Preflight).Any(check => check.Severity == "ERROR");
            DualMigrateButton.IsEnabled = (clientPlan.Items.Count > 0 || serverPlan.Items.Count > 0) && !hasError;
        }
        catch (Exception ex)
        {
            MigrationLog.WriteError("Dual migration scan failed", ex);
            DualStatusText.Text = ex.Message;
            SetRecoveryActions(false, true);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            SetDualScanState(false);
        }
    }

    private void ReviewDualClientChanges_Click(object sender, RoutedEventArgs e) => OpenDualChangesWindow(clientPlan, clientSelectedChangeIds);
    private void ReviewDualServerChanges_Click(object sender, RoutedEventArgs e) => OpenDualChangesWindow(serverPlan, serverSelectedChangeIds);

    private void OpenDualChangesWindow(MigrationPlan? changesPlan, HashSet<string> changeIds)
    {
        if (changesPlan is null) return;
        var modPaths = ReferenceEquals(changesPlan, clientPlan) ? clientSelectedModPaths : serverSelectedModPaths;
        var window = new ChangesWindow(changesPlan, changeIds, modPaths) { Owner = this };
        window.ShowDialog();
        DualChangesSummaryText.Text = $"Client: {clientSelectedChangeIds.Count} of {clientPlan?.CommonChanges.Count ?? 0} tweaks selected, {clientPlan?.ModChanges.Count ?? 0} mod change(s). " +
                                       $"Server: {serverSelectedChangeIds.Count} of {serverPlan?.CommonChanges.Count ?? 0} tweaks selected, {serverPlan?.ModChanges.Count ?? 0} mod change(s).";
    }

    private void SetDualScanState(bool scanning)
    {
        DualClientSourceProfileBox.IsEnabled = !scanning;
        DualClientTargetProfileBox.IsEnabled = !scanning;
        DualServerSourceBrowseButton.IsEnabled = !scanning;
        DualServerTargetBrowseButton.IsEnabled = !scanning;
        DualCancelButton.IsEnabled = scanning || cancellation is not null;
        DualCancelButton.Visibility = scanning || cancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        DualProgress.IsIndeterminate = scanning;
        DualProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DualMigrate_Click(object sender, RoutedEventArgs e)
    {
        if (clientPlan is null || serverPlan is null) return;
        if (!MigrationDialog.Confirm(this, "Confirm Both Migrations", "Both target GTNH installations will be preserved. Only whitelisted data and selected source-only mods will be copied. Continue?")) return;

        DualMigrateButton.IsEnabled = false;
        DualProgress.IsIndeterminate = false;
        DualProgress.Visibility = Visibility.Visible;
        var totalItems = clientPlan.CopyFileCount + clientSelectedModPaths.Count + serverPlan.CopyFileCount + serverSelectedModPaths.Count;
        DualProgress.Maximum = totalItems;
        cancellation = new CancellationTokenSource();
        DualCancelButton.IsEnabled = true;
        DualCancelButton.Visibility = Visibility.Visible;
        SetDualRetry(null);
        DualStatusText.Text = "Migration started. Preparing files...";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var clientCompleted = false;

        try
        {
            var clientProgress = new Progress<(string Path, int Completed, int Total)>(update =>
            {
                DualProgress.Value = update.Completed;
                DualStatusText.Text = $"[CLIENT] Copying {update.Completed} of {update.Total}: {update.Path}";
            });
            var clientBackupPath = await service.ExecuteAsync(clientPlan, clientProgress, cancellation.Token, clientSelectedChangeIds, clientSelectedModPaths);
            clientCompleted = true;
            dualClientCompleted = true;

            var serverProgress = new Progress<(string Path, int Completed, int Total)>(update =>
            {
                DualProgress.Value = clientPlan.CopyFileCount + clientSelectedModPaths.Count + update.Completed;
                DualStatusText.Text = $"[SERVER] Copying {update.Completed} of {update.Total}: {update.Path}";
            });
            var serverBackupPath = await service.ExecuteAsync(serverPlan, serverProgress, cancellation.Token, serverSelectedChangeIds, serverSelectedModPaths);
            dualServerCompleted = true;

            var totalBytes = clientPlan.TotalBytes + serverPlan.TotalBytes;
            var backups = new[] { clientBackupPath, serverBackupPath }.Where(path => path is not null).ToArray();
            DualStatusText.Text = backups.Length == 0
                ? $"Migration complete. {totalItems} files copied."
                : $"Migration complete. {totalItems} files copied. Backups: {string.Join("; ", backups)}";
            MigrationDialog.ShowInfo(this, "Migrations Complete", "Both migrations completed successfully.");
            SetRecoveryActions(true, false);
        }
        catch (OperationCanceledException)
        {
            DualStatusText.Text = clientCompleted
                ? "Client migration complete. Server migration cancelled."
                : "Client migration cancelled before the server migration started.";
            SetDualRetry(clientCompleted ? false : true);
            SetRecoveryActions(true, false);
        }
        catch (Exception ex)
        {
            MigrationLog.WriteError("Dual migration failed", ex);
            DualStatusText.Text = clientCompleted
                ? $"Client migration complete. Server migration failed: {ex.Message} See {MigrationLog.FilePath} for details."
                : $"Client migration failed: {ex.Message} See {MigrationLog.FilePath} for details.";
            SetDualRetry(clientCompleted ? false : true);
            SetRecoveryActions(true, true);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            DualMigrateButton.IsEnabled = true;
            DualCancelButton.IsEnabled = false;
            DualCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void RetryDualSide_Click(object sender, RoutedEventArgs e)
    {
        var retryClient = sender == DualRetryClientButton;
        var migrationPlan = retryClient ? clientPlan : serverPlan;
        if (migrationPlan is null) return;
        var side = retryClient ? "client" : "server";
        if (!MigrationDialog.Confirm(this, $"Retry {side} migration", $"Retry only the {side} migration? The completed side will not run again.")) return;

        SetDualRetry(null);
        DualMigrateButton.IsEnabled = false;
        DualProgress.IsIndeterminate = false;
        DualProgress.Visibility = Visibility.Visible;
        var selectedChanges = retryClient ? clientSelectedChangeIds : serverSelectedChangeIds;
        var selectedMods = retryClient ? clientSelectedModPaths : serverSelectedModPaths;
        DualProgress.Maximum = migrationPlan.CopyFileCount + selectedMods.Count;
        cancellation = new CancellationTokenSource();
        DualCancelButton.IsEnabled = true;
        DualCancelButton.Visibility = Visibility.Visible;
        DualStatusText.Text = $"Retrying {side} migration...";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            var progress = new Progress<(string Path, int Completed, int Total)>(update =>
            {
                DualProgress.Maximum = update.Total;
                DualProgress.Value = update.Completed;
                DualStatusText.Text = $"[{side.ToUpperInvariant()}] Copying {update.Completed} of {update.Total}: {update.Path}";
            });
            var backupPath = await service.ExecuteAsync(migrationPlan, progress, cancellation.Token, selectedChanges, selectedMods);
            if (retryClient) dualClientCompleted = true;
            else dualServerCompleted = true;
            var bothComplete = dualClientCompleted && dualServerCompleted;
            DualStatusText.Text = backupPath is null
                ? bothComplete ? "Both migrations complete." : $"{side.ToUpperInvariant()} migration complete. Retry the remaining side."
                : bothComplete ? $"Both migrations complete. Backup: {backupPath}" : $"{side.ToUpperInvariant()} migration complete. Backup: {backupPath}";
            SetDualRetry(bothComplete ? null : !dualClientCompleted);
            SetRecoveryActions(true, false);
        }
        catch (OperationCanceledException)
        {
            DualStatusText.Text = $"{side.ToUpperInvariant()} migration cancelled.";
            SetDualRetry(retryClient);
            SetRecoveryActions(true, false);
        }
        catch (Exception ex)
        {
            MigrationLog.WriteError($"Dual {side} retry failed", ex);
            DualStatusText.Text = $"{side.ToUpperInvariant()} migration failed: {ex.Message} See {MigrationLog.FilePath} for details.";
            SetDualRetry(retryClient);
            SetRecoveryActions(true, true);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            DualMigrateButton.IsEnabled = true;
            DualCancelButton.IsEnabled = false;
            DualCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SetRecoveryActions(bool showTarget, bool showLog)
    {
        var dual = DualContentGrid.Visibility == Visibility.Visible;
        var targetButton = dual ? DualOpenTargetButton : OpenTargetButton;
        var logButton = dual ? DualOpenLogButton : OpenLogButton;
        targetButton.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
        targetButton.IsEnabled = showTarget;
        logButton.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;
        logButton.IsEnabled = showLog;
    }

    private void SetDualRetry(bool? retryClient)
    {
        DualRetryClientButton.Visibility = retryClient == true ? Visibility.Visible : Visibility.Collapsed;
        DualRetryClientButton.IsEnabled = retryClient == true;
        DualRetryServerButton.Visibility = retryClient == false ? Visibility.Visible : Visibility.Collapsed;
        DualRetryServerButton.IsEnabled = retryClient == false;
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        var targets = DualContentGrid.Visibility == Visibility.Visible
            ? new[] { DualClientTargetBox.Text, DualServerTargetBox.Text }
            : new[] { TargetBox.Text };
        foreach (var target in targets.Where(Directory.Exists))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(MigrationLog.FilePath))
            File.WriteAllText(MigrationLog.FilePath, "GTNH Migrator log\r\n");
        Process.Start(new ProcessStartInfo(MigrationLog.FilePath) { UseShellExecute = true });
    }

    private static int CountTargetCollisions(MigrationPlan migrationPlan)
    {
        var count = 0;
        foreach (var item in migrationPlan.Items)
        {
            var sourcePath = Path.Combine(migrationPlan.Source, item.RelativePath);
            var targetPath = Path.Combine(migrationPlan.Target, item.RelativePath);
            if (item.Kind == "FILE")
            {
                if (File.Exists(targetPath)) count++;
                continue;
            }

            if (!Directory.Exists(sourcePath)) continue;
            count += Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Count(file => File.Exists(Path.Combine(targetPath, Path.GetRelativePath(sourcePath, file))));
        }
        return count;
    }

    private static string FormatManifestItem(MigrationPlan migrationPlan, MigrationItem item)
    {
        var targetPath = Path.Combine(migrationPlan.Target, item.RelativePath);
        var targetSummary = item.Kind == "FILE"
            ? File.Exists(targetPath) ? "TARGET: existing file, backup first" : "TARGET: new file"
            : Directory.Exists(targetPath)
                ? $"TARGET: {CountTargetFiles(migrationPlan.Source, migrationPlan.Target, item.RelativePath)} existing file(s), backup first"
                : "TARGET: new folder";
        return $"REQUIRED | {item.Kind,-9} | {item.RelativePath} | {FormatSize(item.Size)} | {targetSummary}";
    }

    private static int CountTargetFiles(string sourceRoot, string targetRoot, string relativeDirectory)
    {
        var sourceDirectory = Path.Combine(sourceRoot, relativeDirectory);
        var targetDirectory = Path.Combine(targetRoot, relativeDirectory);
        if (!Directory.Exists(sourceDirectory) || !Directory.Exists(targetDirectory)) return 0;
        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Count(file => File.Exists(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file))));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{bytes} B" : $"{value:0.00} {units[index]}";
    }
}
