using System.IO;

namespace PersonalFitnessPlanner.App.Services;

internal static class DataDirectoryPointer
{
    private const string PointerFileName = "data-location.txt";

    public static string ResolveDefault()
    {
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PersonalFitnessPlanner");
        var pointer = Path.Combine(defaultRoot, PointerFileName);
        try
        {
            if (File.Exists(pointer))
            {
                var configured = File.ReadAllText(pointer).Trim();
                if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            }
        }
        catch
        {
            // A damaged pointer must not stop startup; the default root remains safe.
        }
        return defaultRoot;
    }

    public static void Save(string dataDirectory)
    {
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PersonalFitnessPlanner");
        Directory.CreateDirectory(defaultRoot);
        var pointer = Path.Combine(defaultRoot, PointerFileName);
        var temporary = pointer + ".tmp";
        File.WriteAllText(temporary, Path.GetFullPath(dataDirectory));
        File.Move(temporary, pointer, true);
    }
}
