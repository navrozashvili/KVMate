using System.Runtime.InteropServices;
using KVMate.Core.Interop;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;

namespace KVMate.Core.Speakers;

/// <summary>
/// Drives a Bluetooth speaker through its audio driver's one-shot reconnect and disconnect
/// properties, the same path the Connect button in Sound settings takes.
/// </summary>
/// <remarks>
/// <para>
/// The walk follows ToothTray: enumerate the playback endpoints, follow each one's topology to the
/// device on the other side of its connector, and keep those whose id marks them as a Bluetooth
/// audio filter (<c>{2}.\\?\bth...</c>, either the A2DP or the hands-free enumerator). That filter
/// answers <c>IKsControl</c>, and the request goes to every filter the speaker has.
/// </para>
/// <para>
/// Nothing is cached between calls. A disconnected speaker's filters can be torn down and rebuilt by
/// the driver, so a handle kept from an earlier walk could point at nothing.
/// </para>
/// <para>
/// Every walk runs on the thread pool, which is a multithreaded apartment, so the audio objects are
/// never created on the UI thread and never block it.
/// </para>
/// </remarks>
public sealed class KsSpeakerLink : ISpeakerLink
{
    /// <summary>How long to wait for the speaker to reach the requested state after asking.</summary>
    /// <remarks>
    /// The driver acknowledges at once and the radio takes a moment. A speaker held by the other PC
    /// never comes, and this is how long it takes to conclude that.
    /// </remarks>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ConfirmPollInterval = TimeSpan.FromMilliseconds(200);

    private const string BluetoothFilterPrefix = @"{2}.\\?\bth";

    private readonly ILogger<KsSpeakerLink> _logger;

    public KsSpeakerLink(ILogger<KsSpeakerLink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeakerInfo>> ListSpeakersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var speakers = await Task.Run(() => Walk(command: null, speakerId: null), cancellationToken);

            var result = new List<SpeakerInfo>(speakers.Count);
            foreach (var speaker in speakers)
            {
                var name = await ContainerNameAsync(speaker.ContainerId) ?? speaker.EndpointName;
                result.Add(new SpeakerInfo(FormatId(speaker.ContainerId), name, speaker.IsConnected));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate Bluetooth audio devices.");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsConnectedAsync(string speakerId, CancellationToken cancellationToken)
    {
        if (!TryParseId(speakerId, out var container))
        {
            return false;
        }

        try
        {
            var speakers = await Task.Run(() => Walk(command: null, container), cancellationToken);
            return speakers.Any(speaker => speaker.IsConnected);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the state of speaker {SpeakerId}.", speakerId);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<SpeakerResult> ConnectAsync(string speakerId, CancellationToken cancellationToken) =>
        SendAndConfirmAsync(speakerId, BtAudioProperties.OneShotReconnect, wantConnected: true, cancellationToken);

    /// <inheritdoc />
    public Task<SpeakerResult> DisconnectAsync(string speakerId, CancellationToken cancellationToken) =>
        SendAndConfirmAsync(speakerId, BtAudioProperties.OneShotDisconnect, wantConnected: false, cancellationToken);

    private async Task<SpeakerResult> SendAndConfirmAsync(
        string speakerId,
        uint command,
        bool wantConnected,
        CancellationToken cancellationToken)
    {
        var verb = wantConnected ? "connect" : "disconnect";

        if (!TryParseId(speakerId, out var container))
        {
            _logger.LogWarning("Cannot {Verb} {SpeakerId}: not a speaker id.", verb, speakerId);
            return SpeakerResult.NotFound;
        }

        try
        {
            var speakers = await Task.Run(() => Walk(command, container), cancellationToken);
            if (speakers.Count == 0)
            {
                _logger.LogWarning("Cannot {Verb} {SpeakerId}: no paired Bluetooth audio device has that id.", verb, speakerId);
                return SpeakerResult.NotFound;
            }

            if (!speakers[0].CommandSent)
            {
                _logger.LogWarning("Cannot {Verb} {SpeakerId}: the driver refused the request on every filter.", verb, speakerId);
                return SpeakerResult.Failed;
            }

            // The driver answers before the radio has done anything, so the outcome is read from
            // the endpoint state.
            var deadline = DateTimeOffset.UtcNow + ConfirmTimeout;
            while (true)
            {
                var connected = await Task.Run(() => Walk(command: null, container), cancellationToken);
                if (connected.Any(speaker => speaker.IsConnected) == wantConnected)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Speaker {SpeakerId}: {Verb} confirmed.", speakerId, verb);
                    }

                    return SpeakerResult.Success;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Speaker {SpeakerId}: {Verb} sent but not confirmed within {Timeout}.", speakerId, verb, ConfirmTimeout);
                    }

                    return SpeakerResult.Rejected;
                }

                await Task.Delay(ConfirmPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not {Verb} speaker {SpeakerId}.", verb, speakerId);
            return SpeakerResult.Failed;
        }
    }

    /// <summary>
    /// Walk every playback endpoint and group the Bluetooth ones by physical device, optionally
    /// sending <paramref name="command"/> to each filter of the device named.
    /// </summary>
    /// <param name="command">The one-shot property to send, or null to only read.</param>
    /// <param name="speakerId">Only this device, or null for every one.</param>
    private List<WalkedSpeaker> Walk(uint? command, Guid? speakerId)
    {
        using var scope = new ComScope();
        var speakers = new Dictionary<Guid, WalkedSpeaker>();

        var enumerator = scope.Own(ComInterop.CreateDeviceEnumerator());
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.All, out var collection));
        scope.Own(collection);
        Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var endpoint) < 0)
            {
                continue;
            }

            scope.Own(endpoint);

            try
            {
                WalkEndpoint(scope, enumerator, endpoint, command, speakerId, speakers);
            }
            catch (Exception ex)
            {
                // One endpoint that cannot be read (a driver mid-teardown, typically) must not hide
                // the others.
                _logger.LogDebug(ex, "Skipped an audio endpoint that could not be read.");
            }
        }

        return [.. speakers.Values];
    }

    private void WalkEndpoint(
        ComScope scope,
        IMMDeviceEnumerator enumerator,
        IMMDevice endpoint,
        uint? command,
        Guid? speakerId,
        Dictionary<Guid, WalkedSpeaker> speakers)
    {
        Marshal.ThrowExceptionForHR(endpoint.GetState(out var state));
        Marshal.ThrowExceptionForHR(endpoint.OpenPropertyStore(StorageAccessMode.Read, out var store));
        scope.Own(store);

        var container = ReadGuid(store, PropertyKeys.DeviceContainerId);
        if (container is null || (speakerId is not null && container != speakerId))
        {
            return;
        }

        var endpointName = ReadString(store, PropertyKeys.DeviceFriendlyName) ?? "Unnamed speaker";

        var topologyIid = typeof(IDeviceTopology).GUID;
        Marshal.ThrowExceptionForHR(endpoint.Activate(in topologyIid, ComInterop.ClsCtxAll, IntPtr.Zero, out var topologyPointer));
        var topology = scope.Own(ComInterop.Wrap<IDeviceTopology>(topologyPointer));

        Marshal.ThrowExceptionForHR(topology.GetConnectorCount(out var connectors));

        for (uint c = 0; c < connectors; c++)
        {
            if (topology.GetConnector(c, out var connector) < 0)
            {
                continue;
            }

            scope.Own(connector);

            if (connector.GetDeviceIdConnectedTo(out var otherIdPointer) < 0)
            {
                continue;
            }

            var otherId = ComInterop.ReadCoTaskMemString(otherIdPointer);
            if (otherId is null || !otherId.StartsWith(BluetoothFilterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!speakers.TryGetValue(container.Value, out var speaker))
            {
                speaker = new WalkedSpeaker(container.Value, endpointName);
                speakers.Add(container.Value, speaker);
            }

            // A speaker has an endpoint per profile; any one of them active means it is here.
            speaker.IsConnected |= state == DeviceState.Active;

            if (command is { } property)
            {
                speaker.CommandSent |= Send(scope, enumerator, otherId, property);
            }
        }
    }

    private bool Send(ComScope scope, IMMDeviceEnumerator enumerator, string filterId, uint property)
    {
        if (enumerator.GetDevice(filterId, out var filter) < 0 || filter is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Bluetooth filter {FilterId} could not be opened.", filterId);
            }

            return false;
        }

        scope.Own(filter);

        var ksIid = typeof(IKsControl).GUID;
        var hr = filter.Activate(in ksIid, ComInterop.ClsCtxAll, IntPtr.Zero, out var ksPointer);
        if (hr < 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Bluetooth filter {FilterId} has no IKsControl (0x{Hr:X8}).", filterId, hr);
            }

            return false;
        }

        var control = scope.Own(ComInterop.Wrap<IKsControl>(ksPointer));

        var request = new KsIdentifier
        {
            Set = BtAudioProperties.PropertySet,
            Id = property,
            Flags = BtAudioProperties.TypeGet,
        };

        hr = control.KsProperty(in request, (uint)Marshal.SizeOf<KsIdentifier>(), IntPtr.Zero, 0, out _);
        // Information rather than debug: this result is what the hardware check on a new speaker reads.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Property {Property} on {FilterId}: 0x{Hr:X8}.", property, filterId, hr);
        }


        return hr >= 0;
    }

    private static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(in key, out var value) < 0)
        {
            return null;
        }

        try
        {
            return value.AsGuid();
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(in key, out var value) < 0)
        {
            return null;
        }

        try
        {
            return value.AsString();
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    /// <summary>The name of the physical device, which is what Bluetooth settings shows.</summary>
    /// <remarks>
    /// The endpoint's own name is "Headphones (Speaker Name)" or similar, and differs per profile.
    /// </remarks>
    private async Task<string?> ContainerNameAsync(Guid container)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(
                FormatId(container),
                [],
                DeviceInformationKind.DeviceContainer);

            return string.IsNullOrWhiteSpace(info?.Name) ? null : info.Name;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "No name for device container {Container}.", container);
            }

            return null;
        }
    }

    private static string FormatId(Guid container) => container.ToString("B");

    private static bool TryParseId(string? speakerId, out Guid container) =>
        Guid.TryParse(speakerId, out container);

    private sealed class WalkedSpeaker(Guid containerId, string endpointName)
    {
        public Guid ContainerId { get; } = containerId;

        public string EndpointName { get; } = endpointName;

        public bool IsConnected { get; set; }

        public bool CommandSent { get; set; }
    }
}
