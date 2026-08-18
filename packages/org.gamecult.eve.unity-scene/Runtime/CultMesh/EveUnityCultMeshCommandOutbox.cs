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
    /// Owns asynchronous command delivery for the CultMesh Unity transport.
    /// The Unity input path only enqueues. One-shot commands retain their
    /// idempotency key; continuous controls retain only the newest unsent value.
    /// </summary>
    internal sealed class EveUnityCultMeshCommandOutbox : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Queue<OutboxItem> _commands = new Queue<OutboxItem>();
        private readonly Dictionary<string, OutboxItem> _continuous =
            new Dictionary<string, OutboxItem>(StringComparer.Ordinal);
        private readonly Func<EveSurfaceCommandRequest, CancellationToken, Task> _send;
        private readonly Func<EveSurfaceCommandRequest, string?> _continuousKey;
        private readonly Action<string> _discard;
        private readonly int _commandCapacity;
        private readonly int _continuousCapacity;
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Task _worker;
        private bool _disposed;
        private int _inFlight;

        public EveUnityCultMeshCommandOutbox(
            Func<EveSurfaceCommandRequest, CancellationToken, Task> send,
            Func<EveSurfaceCommandRequest, string?> continuousKey,
            Action<string>? discard = null,
            int commandCapacity = 256,
            int continuousCapacity = 32)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _continuousKey = continuousKey ?? throw new ArgumentNullException(nameof(continuousKey));
            _discard = discard ?? (_ => { });
            if (commandCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
            if (continuousCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(continuousCapacity));
            _commandCapacity = commandCapacity;
            _continuousCapacity = continuousCapacity;
            _worker = Task.Run(DeliverAsync);
        }

        internal int PendingCount
        {
            get
            {
                lock (_gate) return _commands.Count + _continuous.Count + _inFlight;
            }
        }

        public void Enqueue(EveSurfaceCommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.CommandId))
                throw new InvalidOperationException("Eve command invocations require an idempotency key.");

            string? discarded = null;
            var signal = false;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(EveUnityCultMeshCommandOutbox));
                var key = _continuousKey(request);
                var item = new OutboxItem(request, string.IsNullOrWhiteSpace(key) ? null : key);
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (_commands.Count >= _commandCapacity)
                        throw new InvalidOperationException(
                            $"The EveUnity command outbox reached its {_commandCapacity}-command bound.");
                    _commands.Enqueue(item);
                    signal = true;
                }
                else if (_continuous.TryGetValue(key, out var previous))
                {
                    _continuous[key] = item;
                    discarded = previous.Request.CommandId;
                }
                else
                {
                    if (_continuous.Count >= _continuousCapacity)
                        throw new InvalidOperationException(
                            $"The EveUnity continuous-control outbox reached its {_continuousCapacity}-binding bound.");
                    _continuous.Add(key, item);
                    signal = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(discarded)) _discard(discarded);
            if (signal) _available.Release();
        }

        public void Dispose()
        {
            OutboxItem[] discarded;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                discarded = _commands.Concat(_continuous.Values).ToArray();
                _commands.Clear();
                _continuous.Clear();
            }
            foreach (var item in discarded) _discard(item.Request.CommandId);
            _lifetime.Cancel();
            _available.Release();
        }

        private async Task DeliverAsync()
        {
            var cancellationToken = _lifetime.Token;
            try
            {
                while (true)
                {
                    await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var item = TakeNext();
                    if (item == null) continue;
                    try
                    {
                        var retryDelay = 50;
                        while (true)
                        {
                            try
                            {
                                await _send(item.Request, cancellationToken).ConfigureAwait(false);
                                break;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch
                            {
                                if (WasSuperseded(item)) break;
                                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                                retryDelay = Math.Min(retryDelay * 2, 2000);
                            }
                        }
                    }
                    finally
                    {
                        lock (_gate) _inFlight--;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private OutboxItem? TakeNext()
        {
            lock (_gate)
            {
                if (_commands.Count > 0)
                {
                    _inFlight++;
                    return _commands.Dequeue();
                }
                if (_continuous.Count == 0) return null;
                var pair = _continuous.First();
                _continuous.Remove(pair.Key);
                _inFlight++;
                return pair.Value;
            }
        }

        private bool WasSuperseded(OutboxItem item)
        {
            if (item.ContinuousKey == null) return false;
            lock (_gate) return _continuous.ContainsKey(item.ContinuousKey);
        }

        private sealed class OutboxItem
        {
            public OutboxItem(EveSurfaceCommandRequest request, string? continuousKey)
            {
                Request = request;
                ContinuousKey = continuousKey;
            }

            public EveSurfaceCommandRequest Request { get; }
            public string? ContinuousKey { get; }
        }
    }
}
