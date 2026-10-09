namespace KVMate.Core.Handoff;

/// <summary>What the engine last did or is doing, for the tray to show.</summary>
public enum HandoffStatus
{
    /// <summary>No device or no speaker is chosen, so nothing happens.</summary>
    NotConfigured = 0,

    /// <summary>The connect loop is running.</summary>
    Connecting = 1,

    /// <summary>The speaker is connected to this PC.</summary>
    Connected = 2,

    /// <summary>Every connect attempt failed.</summary>
    CouldNotConnect = 3,

    /// <summary>A disconnect is in progress.</summary>
    Disconnecting = 4,

    /// <summary>The watched device is gone, so the other PC is active, and the speaker is not here.</summary>
    OtherPcActive = 5,

    /// <summary>Disconnected by hand while this PC has the device.</summary>
    Disconnected = 6,
}
