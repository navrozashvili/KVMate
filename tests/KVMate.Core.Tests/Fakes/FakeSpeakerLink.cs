using KVMate.Core.Speakers;

namespace KVMate.Core.Tests.Fakes;

/// <summary>
/// A speaker that answers instantly with scripted results and records when it was asked.
/// </summary>
internal sealed class FakeSpeakerLink(TimeProvider time) : ISpeakerLink
{
    private readonly DateTimeOffset _epoch = time.GetUtcNow();

    /// <summary>Whether the speaker is connected to this PC.</summary>
    public bool Connected { get; set; }

    /// <summary>
    /// Results for successive connect attempts. Once it is empty, <see cref="DefaultConnectResult"/>
    /// answers.
    /// </summary>
    public Queue<SpeakerResult> ConnectResults { get; } = new();

    public SpeakerResult DefaultConnectResult { get; set; } = SpeakerResult.Success;

    /// <summary>Results for successive disconnect attempts, then <see cref="DefaultDisconnectResult"/>.</summary>
    public Queue<SpeakerResult> DisconnectResults { get; } = new();

    public SpeakerResult DefaultDisconnectResult { get; set; } = SpeakerResult.Success;

    /// <summary>When each connect attempt was made, relative to the fake's creation.</summary>
    public List<TimeSpan> ConnectCalls { get; } = [];

    /// <summary>When each disconnect attempt was made, relative to the fake's creation.</summary>
    public List<TimeSpan> DisconnectCalls { get; } = [];

    public Task<IReadOnlyList<SpeakerInfo>> ListSpeakersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SpeakerInfo>>([new SpeakerInfo("speaker", "Speaker", Connected)]);

    public Task<bool> IsConnectedAsync(string speakerId, CancellationToken cancellationToken) =>
        Task.FromResult(Connected);

    public Task<SpeakerResult> ConnectAsync(string speakerId, CancellationToken cancellationToken)
    {
        ConnectCalls.Add(time.GetUtcNow() - _epoch);

        var result = ConnectResults.Count > 0 ? ConnectResults.Dequeue() : DefaultConnectResult;
        if (result == SpeakerResult.Success)
        {
            Connected = true;
        }

        return Task.FromResult(result);
    }

    public Task<SpeakerResult> DisconnectAsync(string speakerId, CancellationToken cancellationToken)
    {
        DisconnectCalls.Add(time.GetUtcNow() - _epoch);

        var result = DisconnectResults.Count > 0 ? DisconnectResults.Dequeue() : DefaultDisconnectResult;
        if (result == SpeakerResult.Success)
        {
            Connected = false;
        }

        return Task.FromResult(result);
    }
}
