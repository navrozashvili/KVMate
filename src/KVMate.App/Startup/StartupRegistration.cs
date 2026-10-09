using System.Globalization;
using Microsoft.Win32;

namespace KVMate.App.Startup;

/// <summary>Start with Windows, as one value under <c>HKCU\...\Run</c>.</summary>
/// <remarks>
/// Per user only. A machine-wide entry would need an elevated process, and nothing in this app
/// asks for elevation.
/// </remarks>
internal sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KVMate";

    private readonly string _executablePath;

    /// <param name="executablePath">
    /// The apphost to launch at logon. The managed dll would not bring the Windows App SDK
    /// bootstrapper with it.
    /// </param>
    public StartupRegistration(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <summary>The running process's own executable.</summary>
    public static StartupRegistration ForThisProcess() =>
        new(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]);

    /// <summary>
    /// Whether the entry launches this executable. A registry that cannot be read reads as no, so
    /// the settings window still opens.
    /// </summary>
    public bool IsEnabledForThisCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var recorded = (key?.GetValue(ValueName) as string)?.Trim().Trim('"');
            return string.Equals(recorded, _executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Point the entry at this executable, or remove it.</summary>
    /// <exception cref="Exception">
    /// The registry could not be written. Surfaced, because a checkbox that silently does not stick
    /// is worse than one that says why.
    /// </exception>
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($@"Could not open HKCU\{RunKeyPath} for writing.");

        if (enabled)
        {
            // Quoted, because an unquoted Run value is cut at the first space.
            key.SetValue(
                ValueName,
                string.Create(CultureInfo.InvariantCulture, $"\"{_executablePath}\""),
                RegistryValueKind.String);
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
