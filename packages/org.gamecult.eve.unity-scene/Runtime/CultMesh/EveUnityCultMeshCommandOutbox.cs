using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    /// <summary>
    /// Owns receipt-backed asynchronous command delivery for the CultMesh Unity
    /// transport. A successful transport send only moves a command into
    /// AwaitingCanonicalReceipt. The provider receipt is the sole completion
    /// signal. Retries retain the original idempotency key, and one failed
    /// command cannot monopolize delivery of the rest of the bounded outbox.
    /// </summary>
    internal sealed class EveUnityCultMeshCommandOutbox : IDisposable
    {
        internal enum DeliveryState
        {
            Queued,
            Sending,
            AwaitingCanonicalReceipt
        }

        private readonly object _gate = new object();
        private readonly List<OutboxItem> _items = new List<OutboxItem>();
        private readonly Dictionary<string, OutboxItem> _byCommandId =
            new Dictionary<string, OutboxItem>(StringComparer.Ordinal);
        private readonly Dictionary<string, OutboxItem> _continuous =
            new Dictionary<string, OutboxItem>(StringComparer.Ordinal);
        private readonly Func<EveSurfaceCommandRequest, CancellationToken, Task> _send;
        private readonly Func<EveSurfaceCommandRequest, string?> _continuousKey;
        private readonly Action<string> _discard;
        private readonly int _commandCapacity;
        private readonly int _continuousCapacity;
        private readonly TimeSpan _retryDelay;
        private readonly TimeSpan _maximumRetryDelay;
        private readonly TimeSpan _sendAttemptTimeout;
        private readonly SemaphoreSlim _changed = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Task _worker;
        private bool _disposed;
        private long _sequence;

        public EveUnityCultMeshCommandOutbox(
            Func<EveSurfaceCommandRequest, CancellationToken, Task> send,
            Func<EveSurfaceCommandRequest, string?> continuousKey,
            Action<string>? discard = null,
            int commandCapacity = 256,
            int continuousCapacity = 32,
            TimeSpan? retryDelay = null,
            TimeSpan? maximumRetryDelay = null,
            TimeSpan? sendAttemptTimeout = null)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _continuousKey = continuousKey ?? throw new ArgumentNullException(nameof(continuousKey));
            _discard = discard ?? (_ => { });
            if (commandCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
            if (continuousCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(continuousCapacity));
            _commandCapacity = commandCapacity;
            _continuousCapacity = continuousCapacity;
            _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
            _maximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(2);
            _sendAttemptTimeout = sendAttemptTimeout ?? TimeSpan.FromSeconds(2);
            if (_retryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
            if (_maximumRetryDelay < _retryDelay) throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
            if (_sendAttemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sendAttemptTimeout));
            _worker = Task.Run(DeliverAsync);
        }

        internal int PendingCount
        {
            get { lock (_gate) return _items.Count; }
        }

        internal DeliveryState? StateOf(string commandId)
        {
            lock (_gate)
                return _byCommandId.TryGetValue(commandId, out var item) ? item.State : null;
        }

        public void Enqueue(EveSurfaceCommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.CommandId))
                throw new InvalidOperationException("Eve command invocations require an idempotency key.");

            string? discarded = null;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(EveUnityCultMeshCommandOutbox));
                if (_byCommandId.ContainsKey(request.CommandId))
                    throw new InvalidOperationException($"Eve command '{request.CommandId}' is already pending.");

                var key = _continuousKey(request);
                key = string.IsNullOrWhiteSpace(key) ? null : key;
                if (key == null)
                {
                    if (_items.Count(item => item.ContinuousKey == null) >= _commandCapacity)
                        throw new InvalidOperationException(
                            $"The EveUnity command outbox reached its {_commandCapacity}-command bound.");
                }
                else if (_continuous.TryGetValue(key, out var previous))
                {
                    RemoveLocked(previous);
                    discarded = previous.Request.CommandId;
                }
                else if (_continuous.Count >= _continuousCapacity)
                {
                    throw new InvalidOperationException(
                        $"The EveUnity continuous-control outbox reached its {_continuousCapacity}-binding bound.");
                }

                var item = new OutboxItem(request, key, ++_sequence);
                _items.Add(item);
                _byCommandId.Add(request.CommandId, item);
                if (key != null) _continuous[key] = item;
            }

            if (discarded != null) _discard(discarded);
            _changed.Release();
        }

        /// <summary>Completes a command only when its canonical provider receipt arrives.</summary>
        public bool Acknowledge(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId)) return false;
            lock (_gate)
            {
                if (!_byCommandId.TryGetValue(commandId, out var item)) return false;
                RemoveLocked(item);
            }
            _changed.Release();
            return true;
        }

        public void Dispose()
        {
            OutboxItem[] discarded;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                discarded = _items.ToArray();
                _items.Clear();
                _byCommandId.Clear();
                _continuous.Clear();
            }
            foreach (var item in discarded) _discard(item.Request.CommandId);
            _lifetime.Cancel();
            _changed.Release();
        }

        private async Task DeliverAsync()
        {
            var cancellationToken = _lifetime.Token;
            try
            {
                while (true)
                {
                    var selection = TakeNext();
                    if (selection.Item == null)
                    {
                        await WaitForChangeOrDueAsync(selection.Delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    await DeliverOnceAsync(selection.Item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task DeliverOnceAsync(OutboxItem item, CancellationToken cancellationToken)
        {
            var sent = false;
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attempt.CancelAfter(_sendAttemptTimeout);
                try
                {
                    await _send(item.Request, attempt.Token).ConfigureAwait(false);
                    sent = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The same idempotency key is retried after other due items.
                }
            }

            lock (_gate)
            {
                if (!_byCommandId.TryGetValue(item.Request.CommandId, out var current) ||
                    !ReferenceEquals(item, current))
                    return;
                item.Attempts++;
                item.State = sent ? DeliveryState.AwaitingCanonicalReceipt : DeliveryState.Queued;
                item.NextAttemptAt = DateTimeOffset.UtcNow + RetryDelay(item.Attempts);
                item.Sequence = ++_sequence;
            }
            _changed.Release();
        }

        private Selection TakeNext()
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var item = _items
                    .Where(candidate => candidate.State != DeliveryState.Sending && candidate.NextAttemptAt <= now)
                    .OrderBy(candidate => candidate.Sequence)
                    .FirstOrDefault();
                if (item != null)
                {
                    item.State = DeliveryState.Sending;
                    return new Selection(item, null);
                }
                var next = _items
                    .Where(candidate => candidate.State != DeliveryState.Sending)
                    .Select(candidate => (DateTimeOffset?)candidate.NextAttemptAt)
                    .OrderBy(value => value)
                    .FirstOrDefault();
                return new Selection(null, next.HasValue ? Max(TimeSpan.Zero, next.Value - now) : null);
            }
        }

        private async Task WaitForChangeOrDueAsync(TimeSpan? delay, CancellationToken cancellationToken)
        {
            if (!delay.HasValue)
            {
                await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, delay.Value.TotalMilliseconds));
            await _changed.WaitAsync(milliseconds, cancellationToken).ConfigureAwait(false);
        }

        private TimeSpan RetryDelay(int attempts)
        {
            var multiplier = Math.Pow(2, Math.Min(attempts - 1, 10));
            return TimeSpan.FromMilliseconds(Math.Min(
                _maximumRetryDelay.TotalMilliseconds,
                _retryDelay.TotalMilliseconds * multiplier));
        }

        private void RemoveLocked(OutboxItem item)
        {
            _items.Remove(item);
            _byCommandId.Remove(item.Request.CommandId);
            if (item.ContinuousKey != null &&
                _continuous.TryGetValue(item.ContinuousKey, out var current) &&
                ReferenceEquals(item, current))
                _continuous.Remove(item.ContinuousKey);
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

        private sealed class OutboxItem
        {
            public OutboxItem(EveSurfaceCommandRequest request, string? continuousKey, long sequence)
            {
                Request = request;
                ContinuousKey = continuousKey;
                Sequence = sequence;
                NextAttemptAt = DateTimeOffset.MinValue;
            }

            public EveSurfaceCommandRequest Request { get; }
            public string? ContinuousKey { get; }
            public DeliveryState State { get; set; }
            public DateTimeOffset NextAttemptAt { get; set; }
            public int Attempts { get; set; }
            public long Sequence { get; set; }
        }

        private readonly struct Selection
        {
            public Selection(OutboxItem? item, TimeSpan? delay)
            {
                Item = item;
                Delay = delay;
            }

            public OutboxItem? Item { get; }
            public TimeSpan? Delay { get; }
        }
    }
}
