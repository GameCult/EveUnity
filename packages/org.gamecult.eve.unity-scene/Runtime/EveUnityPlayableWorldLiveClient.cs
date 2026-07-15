using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldLiveClient : IDisposable
    {
        private readonly EveUnitySceneProviderConnection _connection;
        private readonly EveUnityPlayableWorldPresenter _presenter;
        private readonly IEveUnitySceneCommandReceiptSource? _receiptSource;
        private readonly EveUnityFeedbackPresenter _feedback = new EveUnityFeedbackPresenter();
        private readonly EveUnityShotReceiptPresenter _shots = new EveUnityShotReceiptPresenter();
        private readonly List<EveUnitySceneCommandReceipt> _pendingReceipts =
            new List<EveUnitySceneCommandReceipt>();
        private bool _connected;
        private bool _receiptConnected;

        public EveUnityPlayableWorldLiveClient(
            EveUnitySceneProviderConnection connection,
            EveUnityPlayableWorldPresenter presenter,
            IEveUnitySceneCommandReceiptSource? receiptSource = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _receiptSource = receiptSource;
        }

        public EveUnitySceneProjection? ActiveProjection => _connection.ActiveProjection;

        public EveUnityPlayableWorldProjection? ActiveWorld => ActiveProjection?.PlayableWorld;

        public EveUnityPlayableWorldPresentation? LastPresentation { get; private set; }

        public EveUnitySceneCommandReceipt? LastReceipt { get; private set; }

        public long ActiveVersion => _connection.ActiveVersion;

        public string SourcePointer => _connection.SourcePointer;

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;
        public event Action<EveUnityFeedbackEvent>? FeedbackAvailable
        {
            add { _feedback.FeedbackAvailable += value; }
            remove { _feedback.FeedbackAvailable -= value; }
        }

        public event Action<EveUnityShotReceipt>? ShotAvailable
        {
            add { _shots.ShotAvailable += value; }
            remove { _shots.ShotAvailable -= value; }
        }

        public EveUnityPlayableWorldPresentation Connect()
        {
            EnsureSubscribed();
            EnsureReceiptSubscribed();
            _connection.Connect();
            return RequirePresentation();
        }

        public EveUnityPlayableWorldPresentation Refresh()
        {
            EnsureSubscribed();
            _connection.Refresh();
            return RequirePresentation();
        }

        public EveSurfaceCommandRequest SubmitMoveIntent(
            string entityId,
            float targetX,
            float targetY,
            float targetZ,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitMoveIntent(entityId, targetX, targetY, targetZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitMoveVectorIntent(
            string entityId,
            float directionX,
            float directionY,
            float scalarValue = 1f,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitMoveVectorIntent(entityId, directionX, directionY, scalarValue, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitLookDirectionIntent(
            string entityId,
            float directionX,
            float directionY,
            float directionZ,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitLookDirectionIntent(entityId, directionX, directionY, directionZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitFocusIntent(
            string entityId,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitFocusIntent(entityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitTargetIntent(
            string sourceEntityId,
            string targetEntityId,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitTargetIntent(sourceEntityId, targetEntityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitActionIntent(
            string entityId,
            string actionId,
            DateTimeOffset? issuedAt = null)
        {
            return _connection.SubmitActionIntent(entityId, actionId, issuedAt);
        }

        public void Disconnect()
        {
            if (_connected)
            {
                _connection.ProjectionUpdated -= OnProjectionUpdated;
                _connected = false;
            }

            if (_receiptConnected && _receiptSource != null)
            {
                _receiptSource.ReceiptAvailable -= OnReceiptAvailable;
                _receiptConnected = false;
            }

            _connection.Disconnect();
            _feedback.Reset();
            _shots.Reset();
            _pendingReceipts.Clear();
        }

        public void Dispose()
        {
            Disconnect();
            _connection.Dispose();
        }

        private void EnsureSubscribed()
        {
            if (_connected)
                return;

            _connection.ProjectionUpdated += OnProjectionUpdated;
            _connected = true;
        }

        private void EnsureReceiptSubscribed()
        {
            if (_receiptSource == null || _receiptConnected)
                return;

            _receiptSource.ReceiptAvailable += OnReceiptAvailable;
            _receiptConnected = true;
        }

        private void OnProjectionUpdated(EveUnitySceneProjection projection)
        {
            LastPresentation = _presenter.Apply(projection);
            _feedback.Apply(projection);
            _shots.Apply(projection);
            PublishReceiptsWhoseStateIsVisible();
        }

        private void OnReceiptAvailable(EveUnitySceneCommandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));

            if (receipt.SourceVersion > ActiveVersion)
            {
                _pendingReceipts.Add(receipt);
                return;
            }

            PublishReceipt(receipt);
        }

        private void PublishReceiptsWhoseStateIsVisible()
        {
            for (var index = _pendingReceipts.Count - 1; index >= 0; index--)
            {
                var receipt = _pendingReceipts[index];
                if (receipt.SourceVersion > ActiveVersion)
                    continue;

                _pendingReceipts.RemoveAt(index);
                PublishReceipt(receipt);
            }
        }

        private void PublishReceipt(EveUnitySceneCommandReceipt receipt)
        {
            LastReceipt = receipt;
            ReceiptAvailable?.Invoke(receipt);
        }

        private EveUnityPlayableWorldPresentation RequirePresentation()
        {
            if (LastPresentation == null)
                throw new InvalidOperationException("The active Unity scene surface has not produced a playable world presentation.");
            return LastPresentation;
        }
    }

    public interface IEveUnitySceneCommandReceiptSource
    {
        event Action<EveUnitySceneCommandReceipt> ReceiptAvailable;
    }

    public sealed class EveUnitySceneCommandReceipt
    {
        public EveUnitySceneCommandReceipt(
            string receiptId,
            string command,
            string commandId,
            string state,
            string ownerRepo,
            string authority,
            string schema = "gamecult.eve.command_receipt.v1",
            string providerId = "",
            string surfaceId = "",
            string message = "",
            DateTimeOffset? issuedAtUtc = null,
            long sourceVersion = 0)
        {
            ReceiptId = receiptId ?? "";
            Command = command ?? "";
            CommandId = commandId ?? "";
            State = state ?? "";
            OwnerRepo = ownerRepo ?? "";
            Authority = authority ?? "";
            Schema = string.IsNullOrWhiteSpace(schema) ? "gamecult.eve.command_receipt.v1" : schema;
            ProviderId = providerId ?? "";
            SurfaceId = surfaceId ?? "";
            Message = message ?? "";
            IssuedAtUtc = issuedAtUtc;
            SourceVersion = sourceVersion;
        }

        public string Schema { get; }

        public string ReceiptId { get; }

        public string Command { get; }

        public string CommandId { get; }

        public string State { get; }

        public string OwnerRepo { get; }

        public string Authority { get; }

        public string ProviderId { get; }

        public string SurfaceId { get; }

        public string Message { get; }

        public DateTimeOffset? IssuedAtUtc { get; }

        public long SourceVersion { get; }

        public bool IsProviderOwned => !string.IsNullOrWhiteSpace(OwnerRepo) && !string.Equals(OwnerRepo, "EveUnity", StringComparison.Ordinal);

        public bool ShouldRefreshProviderSurface =>
            string.Equals(State, "accepted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(State, "reconciled", StringComparison.OrdinalIgnoreCase);
    }
}
