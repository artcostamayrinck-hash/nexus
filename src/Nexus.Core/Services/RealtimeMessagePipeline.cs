using System.Collections.Concurrent;
using Nexus.Contracts.Common;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;

namespace Nexus.Core.Services;

/// <summary>
/// Filters duplicate MessageIds within a bounded rolling window and reorders sequenced frames
/// per (SourceDeviceId, StreamKey) without dropping or scrambling messages on unstable networks.
/// </summary>
public sealed class RealtimeMessagePipeline : IMessageOrderingPipeline
{
    private const int MaxTrackedMessageIds = 4096;
    private const int MaxOutOfOrderBufferSize = 128;
    private static readonly TimeSpan DeduplicationTtl = TimeSpan.FromMinutes(15);

    private sealed class StreamState
    {
        public long NextExpectedSequence { get; set; } = 1;
        public SortedDictionary<long, RealtimeMessageEnvelope> Buffer { get; } = new();
    }

    private readonly IClock _clock;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seenMessageIds = new();
    private readonly Dictionary<(Guid SourceId, string StreamKey), StreamState> _inboundStreams = new();
    private readonly ConcurrentDictionary<(Guid SourceId, string StreamKey), long> _outboundSequences = new();

    private long _duplicatesDropped;
    private long _outOfOrderReordered;

    public RealtimeMessagePipeline(IClock clock)
    {
        _clock = clock;
    }

    public long DuplicatesDroppedCount => Interlocked.Read(ref _duplicatesDropped);
    public long OutOfOrderReorderedCount => Interlocked.Read(ref _outOfOrderReordered);

    public long GetNextOutboundSequence(Guid sourceDeviceId, string streamKey = "default")
    {
        var key = (sourceDeviceId, NormalizeStream(streamKey));
        return _outboundSequences.AddOrUpdate(key, 1, (_, prev) => prev + 1);
    }

    public void ResetStream(Guid sourceDeviceId, string? streamKey = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(streamKey))
            {
                foreach (var k in _inboundStreams.Keys.Where(k => k.SourceId == sourceDeviceId).ToList())
                {
                    _inboundStreams.Remove(k);
                }

                foreach (var k in _outboundSequences.Keys.Where(k => k.SourceId == sourceDeviceId).ToList())
                {
                    _outboundSequences.TryRemove(k, out _);
                }
            }
            else
            {
                var normalized = NormalizeStream(streamKey);
                _inboundStreams.Remove((sourceDeviceId, normalized));
                _outboundSequences.TryRemove((sourceDeviceId, normalized), out _);
            }
        }
    }

    public RealtimeIngestionDecision ProcessIncoming(RealtimeMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var validation = ContractValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            return new RealtimeIngestionDecision(
                RealtimeAckStatus.Rejected,
                Array.Empty<RealtimeMessageEnvelope>(),
                DiagnosticDetail: validation.Errors.FirstOrDefault()?.Message ?? "Envelope inválido.");
        }

        var now = _clock.UtcNow;

        lock (_sync)
        {
            PruneSeenIdsIfNeeded(now);

            if (_seenMessageIds.TryGetValue(envelope.MessageId, out var seenAt) && now - seenAt <= DeduplicationTtl)
            {
                Interlocked.Increment(ref _duplicatesDropped);
                return new RealtimeIngestionDecision(
                    RealtimeAckStatus.DuplicateIgnored,
                    Array.Empty<RealtimeMessageEnvelope>(),
                    DiagnosticDetail: $"Mensagem duplicada ignorada ({envelope.MessageId}).");
            }

            _seenMessageIds[envelope.MessageId] = now;

            // SequenceNumber == 0 indicates non-sequenced/stateless frame: deliver immediately after deduplication.
            if (envelope.SequenceNumber == 0)
            {
                return new RealtimeIngestionDecision(
                    RealtimeAckStatus.Processed,
                    [envelope]);
            }

            var streamKey = (envelope.SourceDeviceId, NormalizeStream(envelope.StreamKey));
            if (!_inboundStreams.TryGetValue(streamKey, out var state))
            {
                state = new StreamState();
                _inboundStreams[streamKey] = state;
            }

            if (envelope.SequenceNumber < state.NextExpectedSequence)
            {
                Interlocked.Increment(ref _duplicatesDropped);
                return new RealtimeIngestionDecision(
                    RealtimeAckStatus.DuplicateIgnored,
                    Array.Empty<RealtimeMessageEnvelope>(),
                    state.NextExpectedSequence,
                    $"Sequência obsoleta ignorada ({envelope.SequenceNumber} < {state.NextExpectedSequence}).");
            }

            if (envelope.SequenceNumber > state.NextExpectedSequence)
            {
                if (state.Buffer.ContainsKey(envelope.SequenceNumber))
                {
                    Interlocked.Increment(ref _duplicatesDropped);
                    return new RealtimeIngestionDecision(
                        RealtimeAckStatus.DuplicateIgnored,
                        Array.Empty<RealtimeMessageEnvelope>(),
                        state.NextExpectedSequence,
                        $"Sequência {envelope.SequenceNumber} já armazenada no buffer.");
                }

                if (state.Buffer.Count >= MaxOutOfOrderBufferSize)
                {
                    return new RealtimeIngestionDecision(
                        RealtimeAckStatus.Rejected,
                        Array.Empty<RealtimeMessageEnvelope>(),
                        state.NextExpectedSequence,
                        "Buffer de reordenação atingiu o limite máximo.");
                }

                state.Buffer[envelope.SequenceNumber] = envelope;
                return new RealtimeIngestionDecision(
                    RealtimeAckStatus.OutOfOrderBuffered,
                    Array.Empty<RealtimeMessageEnvelope>(),
                    state.NextExpectedSequence,
                    $"Mensagem seq={envelope.SequenceNumber} fora de ordem aguardando seq={state.NextExpectedSequence}.");
            }

            // Expected sequence arrived! Deliver this message plus any contiguous buffered messages.
            var ready = new List<RealtimeMessageEnvelope> { envelope };
            state.NextExpectedSequence++;

            while (state.Buffer.TryGetValue(state.NextExpectedSequence, out var nextBuffered))
            {
                state.Buffer.Remove(state.NextExpectedSequence);
                ready.Add(nextBuffered);
                state.NextExpectedSequence++;
                Interlocked.Increment(ref _outOfOrderReordered);
            }

            return new RealtimeIngestionDecision(
                RealtimeAckStatus.Processed,
                ready.AsReadOnly(),
                state.NextExpectedSequence);
        }
    }

    private void PruneSeenIdsIfNeeded(DateTimeOffset now)
    {
        if (_seenMessageIds.Count < MaxTrackedMessageIds)
        {
            return;
        }

        foreach (var entry in _seenMessageIds.Where(kv => now - kv.Value > DeduplicationTtl).ToList())
        {
            _seenMessageIds.Remove(entry.Key);
        }

        if (_seenMessageIds.Count >= MaxTrackedMessageIds)
        {
            foreach (var oldest in _seenMessageIds.OrderBy(kv => kv.Value).Take(MaxTrackedMessageIds / 4).ToList())
            {
                _seenMessageIds.Remove(oldest.Key);
            }
        }
    }

    private static string NormalizeStream(string? streamKey)
        => string.IsNullOrWhiteSpace(streamKey) ? "default" : streamKey.Trim();
}

