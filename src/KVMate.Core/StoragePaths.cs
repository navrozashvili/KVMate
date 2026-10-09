namespace KVMate.Core;

/// <summary>Where KVMate keeps its files: <c>%LOCALAPPDATA%\KVMate</c>.</summary>
/// <remarks>
/// Local rather than roaming on purpose. The chosen USB device and speaker are ids that only mean
/// something on this PC, so a roaming profile carrying them to another machine would be wrong.
/// </remarks>
public static class StoragePaths
{
    /// <summary>The folder everything lives under.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KVMate");

    /// <summary>The settings file.</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>The log folder.</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
}
