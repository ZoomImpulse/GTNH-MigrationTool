using System.Windows;

namespace GTNHMigrator;

public partial class MigrationDialog : Window
{
    private MigrationDialog(string title, string message, bool confirmation, bool warning)
    {
        InitializeComponent();
        Title = $"GTNH Migrator // {title}";
        TitleText.Text = title;
        MessageText.Text = message;
        KindText.Text = warning ? "WARNING" : "INFORMATION";
        KindText.Foreground = FindResource(warning ? "DangerBrush" : "AccentBrightBrush") as System.Windows.Media.Brush;
        ConfirmButton.Content = confirmation ? "CONTINUE" : "OK";
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        new MigrationDialog(title, message, true, true) { Owner = owner }.ShowDialog() == true;

    public static void ShowInfo(Window owner, string title, string message) =>
        _ = new MigrationDialog(title, message, false, false) { Owner = owner }.ShowDialog();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}