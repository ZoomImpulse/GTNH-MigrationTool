using System.IO;

namespace GTNHMigrator;

public static class MigrationLog
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "gtnh-migrator.log");

    public static void WriteError(string operation, Exception exception)
    {
        try
        {
            File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:O}] {operation}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must not hide the original migration error.
        }
    }
}