namespace KVMate.Core.Speakers;

/// <summary>A paired Bluetooth audio device.</summary>
/// <param name="Id">
/// Stable identifier: the device's container id in registry form, <c>{...}</c>. It survives
/// reboots and re-pairing on the same PC does not change it.
/// </param>
/// <param name="Name">The name Windows shows for the device.</param>
/// <param name="IsConnected">Whether it is connected to this PC right now.</param>
public sealed record SpeakerInfo(string Id, string Name, bool IsConnected);

/// <summary>How a connect or disconnect request ended.</summary>
public enum SpeakerResult
{
    /// <summary>The speaker reached the requested state.</summary>
    Success = 0,

    /// <summary>
    /// The driver accepted the request but the speaker did not come. Normally means another host
    /// still holds it.
    /// </summary>
    Rejected = 1,

    /// <summary>No paired Bluetooth audio device has that id.</summary>
    NotFound = 2,

    /// <summary>A Windows call failed.</summary>
    Failed = 3,
}

/// <summary>Connects and disconnects one already-paired Bluetooth speaker.</summary>
/// <remarks>
/// No member throws to the caller, except <see cref="OperationCanceledException"/> when the token
/// is cancelled. A failure comes back as a result, because the caller is a policy loop that has to
/// keep going.
/// </remarks>
public interface ISpeakerLink
{
    /// <summary>Every paired Bluetooth audio device with a playback endpoint.</summary>
    Task<IReadOnlyList<SpeakerInfo>> ListSpeakersAsync(CancellationToken cancellationToken);

    /// <summary>Whether the speaker is connected to this PC. False if it cannot be found.</summary>
    Task<bool> IsConnectedAsync(string speakerId, CancellationToken cancellationToken);

    /// <summary>Ask the speaker to connect to this PC and wait to see whether it does.</summary>
    Task<SpeakerResult> ConnectAsync(string speakerId, CancellationToken cancellationToken);

    /// <summary>Ask the speaker to disconnect from this PC and wait to see whether it does.</summary>
    Task<SpeakerResult> DisconnectAsync(string speakerId, CancellationToken cancellationToken);
}
