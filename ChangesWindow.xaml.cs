using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GTNHMigrator;

public partial class ChangesWindow : Window
{
    private readonly MigrationPlan plan;
    private readonly List<(CheckBox Box, CommonChangeStatus Status)> rows = [];
    private readonly List<(CheckBox Box, ModInventoryEntry Entry)> modRows = [];
    public HashSet<string> SelectedChangeIds { get; }
    public HashSet<string> SelectedModPaths { get; }

    public ChangesWindow(MigrationPlan plan, HashSet<string> selectedChangeIds, HashSet<string> selectedModPaths)
    {
        InitializeComponent();
        this.plan = plan;
        SelectedChangeIds = selectedChangeIds;
        SelectedModPaths = selectedModPaths;

        foreach (var status in plan.CommonChanges)
        {
            var (container, box) = BuildRow(status);
            ChangesPanel.Children.Add(container);
            rows.Add((box, status));
        }

        var additionalMods = plan.ModChanges.Where(entry => entry.State == "SOURCE_ONLY").ToArray();
        if (additionalMods.Length == 0)
            ModChangesList.Items.Add("No additional source mods detected.");
        else
            foreach (var entry in additionalMods)
                ModChangesList.Items.Add(BuildModRow(entry));

        ManualSectionTitle.Text = plan.StartupScriptDifferences.Count > 0 ? "SERVER START SCRIPTS" : "MANUAL CHANGES";
        foreach (var difference in plan.StartupScriptDifferences)
            ManualRemindersList.Items.Add($"{difference.FileName} | {difference.Category}: Source = {difference.SourceValue}; Target = {difference.TargetValue}");

        if (plan.StartupScriptDifferences.Count == 0 && plan.ManualReminders.Count == 0)
            ManualRemindersList.Items.Add("No changes.");
        else
            foreach (var reminder in plan.ManualReminders)
                ManualRemindersList.Items.Add(reminder);

        UpdateSelectionSummary();
    }

    private (Border Container, CheckBox Box) BuildRow(CommonChangeStatus status)
    {
        var box = new CheckBox
        {
            IsChecked = status.AppliedInSource,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        box.Checked += SelectionChanged;
        box.Unchecked += SelectionChanged;

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = status.Change.Description,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("InkBrush")
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{status.Change.RelativePath}   {status.Change.Key}{status.Change.Separator}{status.Change.Value}",
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        var state = new TextBlock
        {
            Text = StateLabel(status),
            FontSize = 10,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            Foreground = (Brush)FindResource(status.AppliedInTarget ? "SuccessBrush" : "MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(state, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(state);
        row.Children.Add(text);

        var border = new Border
        {
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 12),
            Child = row
        };

        return (border, box);
    }

    private CheckBox BuildModRow(ModInventoryEntry entry)
    {
        var path = Path.Combine(entry.Folder, entry.FileName);
        var box = new CheckBox
        {
            Content = path,
            IsChecked = SelectedModPaths.Contains(path),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            Foreground = (Brush)FindResource("InkBrush"),
            Margin = new Thickness(4)
        };
        box.Checked += SelectionChanged;
        box.Unchecked += SelectionChanged;
        modRows.Add((box, entry));
        return box;
    }

    private void SelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        var selectedChanges = rows.Count(row => row.Box.IsChecked == true);
        var selectedMods = modRows.Count(row => row.Box.IsChecked == true);
        var availableMods = plan.ModChanges.Count(entry => entry.State == "SOURCE_ONLY");
        SummaryText.Text = $"{selectedChanges} of {plan.CommonChanges.Count} tweak(s) selected. " +
                           $"{selectedMods} of {availableMods} additional source mod(s) selected.";
    }

    private static string StateLabel(CommonChangeStatus status) =>
        status.AppliedInTarget ? "ALREADY IN TARGET" : status.AppliedInSource ? "SET IN SOURCE" : "NOT SET";

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedChangeIds.Clear();
        foreach (var (box, status) in rows)
            if (box.IsChecked == true) SelectedChangeIds.Add(status.Change.Id);
        SelectedModPaths.Clear();
        foreach (var (box, entry) in modRows)
            if (box.IsChecked == true) SelectedModPaths.Add(Path.Combine(entry.Folder, entry.FileName));
        DialogResult = true;
        Close();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, _) in rows)
            box.IsChecked = true;
        foreach (var (box, _) in modRows)
            box.IsChecked = true;
    }

    private void ClearSelections_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, _) in rows)
            box.IsChecked = false;
        foreach (var (box, _) in modRows)
            box.IsChecked = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
