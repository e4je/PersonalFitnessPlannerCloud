namespace PersonalFitnessPlanner.Infrastructure;

/// <summary>
/// Resolves all writable application locations. .NET paths are Unicode, so an
/// explicitly selected directory such as D:\训练记录 is supported without any
/// ANSI conversion.
/// </summary>
public sealed class AppPaths
{
    public const string ApplicationFolderName = "PersonalFitnessPlanner";

    public AppPaths(string? dataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationFolderName)
            : Environment.ExpandEnvironmentVariables(dataDirectory.Trim());

        DataDirectory = Path.GetFullPath(root);
        DatabasePath = Path.Combine(DataDirectory, "fitness.db");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        BackupsDirectory = Path.Combine(DataDirectory, "backups");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        TokenPath = Path.Combine(DataDirectory, "auth.dat");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string CacheDirectory { get; }
    public string BackupsDirectory { get; }
    public string SettingsPath { get; }
    public string TokenPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
