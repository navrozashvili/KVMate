using KVMate.Core.Handoff;
using KVMate.Core.Speakers;
using KVMate.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KVMate.Core.Tests.Handoff;

public sealed class HandoffEngineTests : IDisposable
{
    private const string Device = @"USB\VID_1234&PID_5678\1";
    private const string Speaker = "{00000000-0000-0000-0000-000000000001}";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeDeviceWatcher _watcher = new();
    private readonly FakeSpeakerLink _speaker;
    private readonly HandoffEngine _engine;
    private readonly List<HandoffStatus> _statuses = [];
    private int _failureNotices;

    public HandoffEngineTests()
    {
        _speaker = new FakeSpeakerLink(_time);
        _engine = new HandoffEngine(_watcher, _speaker, _time, NullLogger<HandoffEngine>.Instance);
        _engine.StatusChanged += (_, _) => _statuses.Add(_engine.Status);
        _engine.ConnectAttemptsExhausted += (_, _) => _failureNotices++;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _watcher.Dispose();
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>Let <paramref name="span"/> pass a millisecond at a time.</summary>
    /// <remarks>
    /// One large <c>Advance</c> moves the clock to its end before firing anything, so a timer the
    /// engine starts from inside a callback would be scheduled from the end of the jump. Stepping
    /// keeps every retry delay measured from the moment it was really started.
    /// </remarks>
    private void Elapse(TimeSpan span)
    {
        for (var step = TimeSpan.Zero; step < span; step += Ms(1))
        {
            _time.Advance(Ms(1));
        }
    }

    private Task ConfigureAsync() => _engine.ConfigureAsync(Device, Speaker, TestContext.Current.CancellationToken);

    // Not configured

    [Theory]
    [InlineData(null, Speaker)]
    [InlineData(Device, null)]
    [InlineData("", Speaker)]
    [InlineData(Device, " ")]
    public async Task Without_a_device_and_a_speaker_the_engine_is_idle(string? device, string? speaker)
    {
        _watcher.IsPresent = true;

        await _engine.ConfigureAsync(device, speaker, TestContext.Current.CancellationToken);
        _watcher.Arrive();
        Elapse(TimeSpan.FromSeconds(10));

        Assert.Equal(HandoffStatus.NotConfigured, _engine.Status);
        Assert.Equal(0, _watcher.StartCount);
        Assert.Empty(_speaker.ConnectCalls);
        Assert.Empty(_speaker.DisconnectCalls);
    }

    [Fact]
    public void A_new_engine_reads_as_not_configured() =>
        Assert.Equal(HandoffStatus.NotConfigured, _engine.Status);

    [Fact]
    public async Task Clearing_the_configuration_returns_to_idle_and_ignores_device_events()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);

        await _engine.ConfigureAsync(null, null, TestContext.Current.CancellationToken);
        _watcher.Remove();
        Elapse(TimeSpan.FromSeconds(10));

        Assert.Equal(HandoffStatus.NotConfigured, _engine.Status);
        Assert.Empty(_speaker.DisconnectCalls);
    }

    // Startup reconciliation

    [Fact]
    public async Task Present_at_startup_connects_once_the_debounce_has_passed()
    {
        _watcher.IsPresent = true;

        await ConfigureAsync();
        Assert.Equal(Device, _watcher.WatchedInstanceId);

        _time.Advance(Debounce - Ms(1));
        Assert.Empty(_speaker.ConnectCalls);

        _time.Advance(Ms(1));
        Assert.Equal([Debounce], _speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    [Fact]
    public async Task Absent_at_startup_disconnects_a_speaker_connected_here()
    {
        _watcher.IsPresent = false;
        _speaker.Connected = true;

        await ConfigureAsync();
        _time.Advance(Debounce);

        Assert.Equal([Debounce], _speaker.DisconnectCalls);
        Assert.Empty(_speaker.ConnectCalls);
        Assert.False(_speaker.Connected);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    [Fact]
    public async Task Absent_at_startup_with_the_speaker_elsewhere_does_nothing_but_report()
    {
        _watcher.IsPresent = false;
        _speaker.Connected = false;

        await ConfigureAsync();
        _time.Advance(Debounce);

        Assert.Empty(_speaker.DisconnectCalls);
        Assert.Empty(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    [Fact]
    public async Task Already_connected_here_short_circuits_without_a_connect_attempt()
    {
        _watcher.IsPresent = true;
        _speaker.Connected = true;

        await ConfigureAsync();
        _time.Advance(Debounce);

        Assert.Empty(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    // Resume reconciliation

    [Fact]
    public async Task Resume_with_the_device_present_reconnects_a_speaker_that_left()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);
        _speaker.ConnectCalls.Clear();

        // Asleep, the speaker dropped the link.
        _speaker.Connected = false;
        _engine.NotifyResumed();
        _time.Advance(Debounce);

        Assert.Single(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    [Fact]
    public async Task Resume_with_the_device_absent_disconnects_a_speaker_that_came_here()
    {
        _watcher.IsPresent = false;
        await ConfigureAsync();
        _time.Advance(Debounce);

        // The speaker powered on and joined this PC, its last host, by itself.
        _speaker.Connected = true;
        _engine.NotifyResumed();
        _time.Advance(Debounce);

        Assert.Single(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    // Debounce

    [Fact]
    public async Task An_arrival_acts_only_after_300_ms_of_stability()
    {
        _watcher.IsPresent = false;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _watcher.Arrive();
        _time.Advance(Ms(299));
        Assert.Empty(_speaker.ConnectCalls);

        _time.Advance(Ms(1));
        Assert.Single(_speaker.ConnectCalls);
    }

    [Fact]
    public async Task A_flicker_restarts_the_debounce_and_only_the_final_state_counts()
    {
        _watcher.IsPresent = true;
        _speaker.Connected = true;
        await ConfigureAsync();
        _time.Advance(Debounce);

        // Remove and come back inside the window: the speaker must not be dropped.
        _watcher.Remove();
        _time.Advance(Ms(200));
        _watcher.Arrive();
        _time.Advance(Ms(200));
        _watcher.Remove();
        _time.Advance(Ms(100));
        _watcher.Arrive();
        Elapse(TimeSpan.FromSeconds(5));

        Assert.Empty(_speaker.DisconnectCalls);
        Assert.Empty(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    // Connect retries

    [Fact]
    public async Task Rejected_connects_retry_after_half_a_second_then_one_then_two_four_attempts_in_all()
    {
        _watcher.IsPresent = true;
        _speaker.DefaultConnectResult = SpeakerResult.Rejected;

        await ConfigureAsync();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal(
            [Debounce, Debounce + Ms(500), Debounce + Ms(1500), Debounce + Ms(3500)],
            _speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.CouldNotConnect, _engine.Status);
        Assert.Equal(1, _failureNotices);
    }

    [Fact]
    public async Task Each_retry_waits_its_full_delay()
    {
        _watcher.IsPresent = true;
        _speaker.DefaultConnectResult = SpeakerResult.Rejected;
        await ConfigureAsync();

        _time.Advance(Debounce);
        Assert.Single(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connecting, _engine.Status);

        _time.Advance(Ms(499));
        Assert.Single(_speaker.ConnectCalls);
        _time.Advance(Ms(1));
        Assert.Equal(2, _speaker.ConnectCalls.Count);

        _time.Advance(Ms(999));
        Assert.Equal(2, _speaker.ConnectCalls.Count);
        _time.Advance(Ms(1));
        Assert.Equal(3, _speaker.ConnectCalls.Count);

        _time.Advance(Ms(1999));
        Assert.Equal(3, _speaker.ConnectCalls.Count);
        Assert.Equal(0, _failureNotices);
        _time.Advance(Ms(1));
        Assert.Equal(4, _speaker.ConnectCalls.Count);
        Assert.Equal(1, _failureNotices);
    }

    [Fact]
    public async Task A_connect_that_succeeds_on_a_retry_stops_retrying()
    {
        _watcher.IsPresent = true;
        _speaker.ConnectResults.Enqueue(SpeakerResult.Rejected);
        _speaker.ConnectResults.Enqueue(SpeakerResult.Failed);
        _speaker.ConnectResults.Enqueue(SpeakerResult.Success);

        await ConfigureAsync();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal(3, _speaker.ConnectCalls.Count);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
        Assert.Equal(0, _failureNotices);
    }

    [Fact]
    public async Task A_removal_mid_retry_cancels_the_connect_loop()
    {
        _watcher.IsPresent = true;
        _speaker.DefaultConnectResult = SpeakerResult.Rejected;
        await ConfigureAsync();
        _time.Advance(Debounce);
        _time.Advance(Ms(500));
        Assert.Equal(2, _speaker.ConnectCalls.Count);

        _watcher.Remove();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal(2, _speaker.ConnectCalls.Count);
        Assert.Empty(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
        Assert.Equal(0, _failureNotices);
    }

    [Fact]
    public async Task A_device_gone_before_a_retry_stops_the_loop_without_changing_status()
    {
        _watcher.IsPresent = true;
        _speaker.DefaultConnectResult = SpeakerResult.Rejected;
        await ConfigureAsync();
        _time.Advance(Debounce);
        Assert.Equal(HandoffStatus.Connecting, _engine.Status);

        // Gone, but the removal event has not been delivered yet.
        _watcher.IsPresent = false;
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Single(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connecting, _engine.Status);
        Assert.Equal(0, _failureNotices);
    }

    // Disconnect

    [Fact]
    public async Task A_removal_disconnects_the_speaker()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _watcher.Remove();
        _time.Advance(Debounce);

        Assert.Single(_speaker.DisconnectCalls);
        Assert.False(_speaker.Connected);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    [Fact]
    public async Task A_failed_disconnect_is_retried_once_after_a_second_then_given_up()
    {
        _watcher.IsPresent = false;
        _speaker.Connected = true;
        _speaker.DefaultDisconnectResult = SpeakerResult.Failed;

        await ConfigureAsync();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal([Debounce, Debounce + TimeSpan.FromSeconds(1)], _speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    [Fact]
    public async Task A_disconnect_that_succeeds_on_the_retry_stops_there()
    {
        _watcher.IsPresent = false;
        _speaker.Connected = true;
        _speaker.DisconnectResults.Enqueue(SpeakerResult.Failed);

        await ConfigureAsync();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal(2, _speaker.DisconnectCalls.Count);
        Assert.False(_speaker.Connected);
    }

    [Fact]
    public async Task An_arrival_cancels_a_pending_disconnect_retry()
    {
        _watcher.IsPresent = false;
        _speaker.Connected = true;
        _speaker.DefaultDisconnectResult = SpeakerResult.Failed;
        await ConfigureAsync();
        _time.Advance(Debounce);
        Assert.Single(_speaker.DisconnectCalls);

        _watcher.Arrive();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Single(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    // Manual actions

    [Fact]
    public async Task Connect_now_is_not_debounced_and_ignores_presence()
    {
        _watcher.IsPresent = false;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _engine.ConnectNow();

        Assert.Equal([Debounce], _speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    [Fact]
    public async Task Connect_now_retries_on_the_full_schedule_even_with_the_device_absent()
    {
        _watcher.IsPresent = false;
        _speaker.DefaultConnectResult = SpeakerResult.Rejected;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _engine.ConnectNow();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal(
            [Debounce, Debounce + Ms(500), Debounce + Ms(1500), Debounce + Ms(3500)],
            _speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.CouldNotConnect, _engine.Status);
        Assert.Equal(1, _failureNotices);
    }

    [Fact]
    public async Task Connect_now_holds_until_the_next_device_event()
    {
        _watcher.IsPresent = false;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _engine.ConnectNow();
        Elapse(TimeSpan.FromMinutes(5));
        Assert.True(_speaker.Connected);
        Assert.Empty(_speaker.DisconnectCalls);

        // A real event resumes automatic behaviour.
        _watcher.Arrive();
        _watcher.Remove();
        _time.Advance(Debounce);

        Assert.Single(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.OtherPcActive, _engine.Status);
    }

    [Fact]
    public async Task Disconnect_now_disconnects_once_and_holds_until_the_next_device_event()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);
        _speaker.ConnectCalls.Clear();

        _engine.DisconnectNow();
        Assert.Single(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.Disconnected, _engine.Status);

        Elapse(TimeSpan.FromMinutes(5));
        Assert.Empty(_speaker.ConnectCalls);

        _watcher.Remove();
        _watcher.Arrive();
        _time.Advance(Debounce);

        Assert.Single(_speaker.ConnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    [Fact]
    public async Task A_failed_disconnect_now_is_not_retried()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);
        _speaker.DefaultDisconnectResult = SpeakerResult.Failed;

        _engine.DisconnectNow();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Single(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.Connected, _engine.Status);
    }

    [Fact]
    public async Task Connect_now_cancels_a_pending_automatic_action()
    {
        _watcher.IsPresent = true;
        await ConfigureAsync();
        _time.Advance(Debounce);

        _watcher.Remove();
        _engine.ConnectNow();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Empty(_speaker.DisconnectCalls);
        Assert.True(_speaker.Connected);
    }

    [Fact]
    public async Task Manual_actions_do_nothing_when_not_configured()
    {
        await _engine.ConfigureAsync(null, null, TestContext.Current.CancellationToken);

        _engine.ConnectNow();
        _engine.DisconnectNow();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Empty(_speaker.ConnectCalls);
        Assert.Empty(_speaker.DisconnectCalls);
        Assert.Equal(HandoffStatus.NotConfigured, _engine.Status);
    }

    // Status reporting

    [Fact]
    public async Task Status_changes_are_announced_in_order()
    {
        _watcher.IsPresent = true;
        _speaker.ConnectResults.Enqueue(SpeakerResult.Rejected);

        await ConfigureAsync();
        Elapse(TimeSpan.FromSeconds(30));

        Assert.Equal([HandoffStatus.Connecting, HandoffStatus.Connected], _statuses);
    }
}
