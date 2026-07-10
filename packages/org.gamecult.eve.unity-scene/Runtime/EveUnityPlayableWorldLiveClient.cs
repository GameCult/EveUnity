using System;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldLiveClient : IDisposable
    {
        private readonly EveUnitySceneProviderConnection _connection;
        private readonly EveUnityPlayableWorldPresenter _presenter;
        private readonly IEveUnitySceneCommandReceiptSource? _receiptSource;
        private readonly bool _refreshOnTerminalReceipt;
        private bool _connected;
        private bool _receiptConnected;

        public EveUnityPlayableWorldLiveClient(
            EveUnitySceneProviderConnection connection,
            EveUnityPlayableWorldPresenter presenter,
            IEveUnitySceneCommandReceiptSource? receiptSource = null,
            bool refreshOnTerminalReceipt = true)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _receiptSource = receiptSource;
            _refreshOnTerminalReceipt = refreshOnTerminalReceipt;
        }

        public EveUnitySceneProjection? ActiveProjection => _connection.ActiveProjection;

        public EveUnityPlayableWorldProjection? ActiveWorld => ActiveProjection?.PlayableWorld;

        public EveUnityPlayableWorldPresentation? LastPresentation { get; private set; }

        public EveUnitySceneCommandReceipt? LastReceipt { get; private set; }

        public long ActiveVersion => _connection.ActiveVersion;

        public string SourcePointer => _connection.SourcePointer;

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;

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
        }

        private void OnReceiptAvailable(EveUnitySceneCommandReceipt receipt)
        {
            LastReceipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
            ReceiptAvailable?.Invoke(receipt);

            if (_refreshOnTerminalReceipt && receipt.ShouldRefreshProviderSurface)
                Refresh();
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
            DateTimeOffset? issuedAtUtc = null)
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

        public bool IsProviderOwned => !string.IsNullOrWhiteSpace(OwnerRepo) && !string.Equals(OwnerRepo, "EveUnity", StringComparison.Ordinal);

        public bool ShouldRefreshProviderSurface =>
            string.Equals(State, "accepted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(State, "reconciled", StringComparison.OrdinalIgnoreCase);
    }
}
