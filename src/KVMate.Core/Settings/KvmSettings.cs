using System.Text.Json.Serialization;

namespace KVMate.Core.Settings;

/// <summary>What the user chose in the settings window.</summary>
/// <remarks>
/// The names are kept only for display. They let the settings window and the tray describe a
/// device that is not plugged in right now, which on the inactive PC is the normal state.
/// </remarks>
public sealed record KvmSettings
{
    /// <summary>Defaults: nothing chosen.</summary>
    public static KvmSettings Default { get; } = new();

    /// <summary>The watched USB device's instance id.</summary>
    public string? DeviceInstanceId { get; init; }

    /// <summary>The watched device's name when it was chosen.</summary>
    public string? DeviceName { get; init; }

    /// <summary>The speaker's id, as <see cref="Speakers.SpeakerInfo.Id"/>.</summary>
    public string? SpeakerId { get; init; }

    /// <summary>The speaker's name when it was chosen.</summary>
    public string? SpeakerName { get; init; }

    /// <summary>Whether KVMate starts at logon.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Whether both a device and a speaker are chosen, without which the engine is idle.</summary>
    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(DeviceInstanceId) && !string.IsNullOrWhiteSpace(SpeakerId);
}
