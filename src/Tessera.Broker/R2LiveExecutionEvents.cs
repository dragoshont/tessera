using System.Collections.Concurrent;

namespace Tessera.Broker;

internal sealed record R2LiveExecutionEvent(long Sequence, string EventType, string DataJson);

/// <summary>Bounded, process-local token delivery. Canonical Chat state remains in SQLite.</summary>
internal sealed class R2LiveExecutionEvents(TimeProvider? timeProvider = null)
{
    private const int MaximumEvents = 4096;
    private const int MaximumCharacters = 16 * 1024;
    private const int MaximumStreams = 512;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AbandonedRetention = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<StreamKey, StreamState> _streams = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void PublishText(string owner, string conversationId, string executionId, string dataJson, int textCharacters)
    {
        if (textCharacters <= 0) return;
        Sweep();
        var key=new StreamKey(owner,conversationId,executionId);
        if (!_streams.TryGetValue(key, out var state))
        {
            if (_streams.Count >= MaximumStreams) throw new InvalidDataException("Live model stream capacity is exhausted.");
            state = _streams.GetOrAdd(key, _ => new(_timeProvider.GetUtcNow()));
        }
        lock (state)
        {
            if (state.Events.Count >= MaximumEvents || state.TextCharacters + textCharacters > MaximumCharacters)
                throw new InvalidDataException("Live model stream exceeded the product bound.");
            state.TextCharacters += textCharacters;
            state.LastTouched = _timeProvider.GetUtcNow();
            state.Events.Add(new(state.Events.Count + 1, "text", dataJson));
        }
    }

    public IReadOnlyList<R2LiveExecutionEvent> ListAfter(string owner, string conversationId, string executionId, long after)
    {
        Sweep();
        if (!_streams.TryGetValue(new(owner,conversationId,executionId), out var state)) return [];
        lock (state) return state.Events.Where(item => item.Sequence > after).ToArray();
    }

    public void MarkTerminal(string owner, string conversationId, string executionId)
    {
        if (!_streams.TryGetValue(new(owner,conversationId,executionId), out var state)) return;
        lock (state) state.TerminalAt = _timeProvider.GetUtcNow();
    }

    internal int StreamCount { get { Sweep(); return _streams.Count; } }

    private void Sweep()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (id, state) in _streams)
        {
            bool expired;
            lock (state) expired = state.TerminalAt is { } terminal
                ? now - terminal >= TerminalRetention
                : now - state.LastTouched >= AbandonedRetention;
            if (expired) _streams.TryRemove(id, out _);
        }
    }

    private sealed class StreamState(DateTimeOffset now)
    {
        public List<R2LiveExecutionEvent> Events { get; } = [];
        public int TextCharacters { get; set; }
        public DateTimeOffset LastTouched { get; set; } = now;
        public DateTimeOffset? TerminalAt { get; set; }
    }

    private sealed record StreamKey(string Owner,string ConversationId,string ExecutionId);
}
