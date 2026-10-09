using System.Text;
using System.Text.Json;

namespace KVMate.Core.Settings;

/// <summary>Reads and writes <see cref="KvmSettings"/> as one JSON file.</summary>
/// <remarks>
/// <para>
/// A file that is missing, unreadable or not valid JSON yields <see cref="KvmSettings.Default"/>,
/// so the worst a damaged file can do is send the user back to the settings window.
/// </para>
/// <para>
/// A save writes a temporary file beside the real one and then swaps it in. A reader, or a crash
/// halfway through, sees either the whole old file or the whole new one.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="path">The settings file, normally <see cref="StoragePaths.SettingsFile"/>.</param>
    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>The settings file.</summary>
    public string Path { get; }

    /// <summary>The saved settings, or defaults if there are none or they cannot be read.</summary>
    public KvmSettings Load()
    {
        try
        {
            var text = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<KvmSettings>(text, Json) ?? KvmSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return KvmSettings.Default;
        }
    }

    /// <summary>Replace the saved settings.</summary>
    /// <exception cref="IOException">The file could not be written; the previous one is intact.</exception>
    /// <exception cref="UnauthorizedAccessException">As above, for a permission failure.</exception>
    public void Save(KvmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Same folder as the destination, so the swap stays on one volume and is atomic.
        var temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(JsonSerializer.Serialize(settings, Json));
                writer.Flush();

                // On disk before the swap, so a power cut cannot leave the name pointing at a file
                // whose contents never got there.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                File.Replace(temporary, Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, Path);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort. The failure the caller needs to see is the one already in flight.
        }
    }
}
