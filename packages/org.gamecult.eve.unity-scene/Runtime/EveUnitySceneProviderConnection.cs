using System;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnitySceneProviderSurfaceSource
    {
        string ProviderId { get; }

        string SurfaceId { get; }

        string SourcePointer { get; }

        EveUnitySceneProviderSurfaceSnapshot CurrentSnapshot { get; }

        event Action<EveUnitySceneProviderSurfaceSnapshot> SnapshotAvailable;

        void Connect();

        void Refresh();

        void Disconnect();
    }

    public interface IEveUnitySceneCommandSink
    {
        string SinkKind { get; }

        void Submit(EveSurfaceCommandRequest request);
    }

    public interface IEveUnitySceneProviderSurfaceDocumentSource
    {
        EveUnitySceneProviderSurfaceDocument CurrentDocument { get; }

        event Action<EveUnitySceneProviderSurfaceDocument> DocumentAvailable;
    }

    public interface IEveUnitySceneProviderSurfaceDocumentConnection
    {
        void Connect();

        void Disconnect();
    }

    public sealed class EveUnitySceneProviderSurfaceDocument
    {
        public EveUnitySceneProviderSurfaceDocument(
            EveSurfaceDocument surfaceDocument,
            EveUnitySceneProviderSurfaceAdvertisement advertisedSurface,
            string sourcePointer,
            long version = 0)
        {
            SurfaceDocument = surfaceDocument ?? throw new ArgumentNullException(nameof(surfaceDocument));
            AdvertisedSurface = advertisedSurface ?? throw new ArgumentNullException(nameof(advertisedSurface));
            SourcePointer = sourcePointer ?? "";
            Version = version == 0 ? surfaceDocument.Version : version;
        }

        public EveSurfaceDocument SurfaceDocument { get; }

        public EveUnitySceneProviderSurfaceAdvertisement AdvertisedSurface { get; }

        public string SourcePointer { get; }

        public long Version { get; }

        public EveUnitySceneProviderSurfaceSnapshot ToSnapshot()
        {
            return new EveUnitySceneProviderSurfaceSnapshot(
                SurfaceDocument,
                AdvertisedSurface,
                SourcePointer,
                Version);
        }
    }

    public sealed class EveUnitySceneProviderSurfaceDocumentSource : IEveUnitySceneProviderSurfaceSource, IDisposable
    {
        private readonly IEveUnitySceneProviderSurfaceDocumentSource _documentSource;
        private readonly IEveUnitySceneProviderSurfaceDocumentConnection? _documentConnection;
        private readonly IEveUnityProviderRefreshSource? _refreshSource;
        private bool _connected;

        public EveUnitySceneProviderSurfaceDocumentSource(
            IEveUnitySceneProviderSurfaceDocumentSource documentSource)
        {
            _documentSource = documentSource ?? throw new ArgumentNullException(nameof(documentSource));
            _documentConnection = documentSource as IEveUnitySceneProviderSurfaceDocumentConnection;
            _refreshSource = documentSource as IEveUnityProviderRefreshSource;
            CurrentSnapshot = _documentSource.CurrentDocument.ToSnapshot();
        }

        public string ProviderId => CurrentSnapshot.Document.ProviderId;

        public string SurfaceId => CurrentSnapshot.AdvertisedSurface.SurfaceId;

        public string SourcePointer => CurrentSnapshot.SourcePointer;

        public EveUnitySceneProviderSurfaceSnapshot CurrentSnapshot { get; private set; }

        public event Action<EveUnitySceneProviderSurfaceSnapshot>? SnapshotAvailable;

        public void Connect()
        {
            if (_connected)
                return;

            _documentSource.DocumentAvailable += OnDocumentAvailable;
            _documentConnection?.Connect();
            _connected = true;
        }

        public void Refresh()
        {
            _refreshSource?.Refresh();
        }

        public void Disconnect()
        {
            if (!_connected)
                return;

            _documentConnection?.Disconnect();
            _documentSource.DocumentAvailable -= OnDocumentAvailable;
            _connected = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void OnDocumentAvailable(EveUnitySceneProviderSurfaceDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            CurrentSnapshot = document.ToSnapshot();
            SnapshotAvailable?.Invoke(CurrentSnapshot);
        }
    }

    public sealed class EveUnitySceneProviderConnection : IDisposable
    {
        private readonly IEveUnitySceneProviderSurfaceSource _surfaceSource;
        private readonly IEveUnitySceneCommandSink _commandSink;
        private readonly EveUnitySceneClientSession _session;
        private bool _connected;

        public EveUnitySceneProviderConnection(
            IEveUnitySceneProviderSurfaceSource surfaceSource,
            IEveUnitySceneCommandSink commandSink)
            : this(surfaceSource, commandSink, new EveUnitySceneClientSession())
        {
        }

        public EveUnitySceneProviderConnection(
            IEveUnitySceneProviderSurfaceSource surfaceSource,
            IEveUnitySceneCommandSink commandSink,
            EveUnitySceneClientSession session)
        {
            _surfaceSource = surfaceSource ?? throw new ArgumentNullException(nameof(surfaceSource));
            _commandSink = commandSink ?? throw new ArgumentNullException(nameof(commandSink));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public string ProviderId => _surfaceSource.ProviderId;

        public string SurfaceId => _surfaceSource.SurfaceId;

        public string SourcePointer => _surfaceSource.SourcePointer;

        public EveUnitySceneProjection? ActiveProjection => _session.ActiveProjection;

        public long ActiveVersion => _session.ActiveVersion;

        public event Action<EveUnitySceneProjection>? ProjectionUpdated;

        public EveUnitySceneProjection Connect()
        {
            if (!_connected)
            {
                _surfaceSource.SnapshotAvailable += OnSnapshotAvailable;
                _surfaceSource.Connect();
                _connected = true;
            }

            if (HasAppliedCurrentSnapshot())
                return _session.ActiveProjection!;

            return ApplySnapshot(_surfaceSource.CurrentSnapshot);
        }

        public EveUnitySceneProjection Refresh()
        {
            _surfaceSource.Refresh();
            if (HasAppliedCurrentSnapshot())
                return _session.ActiveProjection!;

            return ApplySnapshot(_surfaceSource.CurrentSnapshot);
        }

        public EveSurfaceCommandRequest SubmitMoveIntent(
            string entityId,
            float targetX,
            float targetY,
            float targetZ,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateMoveIntent(entityId, targetX, targetY, targetZ, issuedAt));
        }

        public EveSurfaceCommandRequest SubmitMoveVectorIntent(
            string entityId,
            float directionX,
            float directionY,
            float scalarValue = 1f,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateMoveVectorIntent(entityId, directionX, directionY, scalarValue, issuedAt));
        }

        public EveSurfaceCommandRequest SubmitLookDirectionIntent(
            string entityId,
            float directionX,
            float directionY,
            float directionZ,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateLookDirectionIntent(entityId, directionX, directionY, directionZ, issuedAt));
        }

        public EveSurfaceCommandRequest SubmitFocusIntent(
            string entityId,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateFocusIntent(entityId, issuedAt));
        }

        public EveSurfaceCommandRequest SubmitTargetIntent(
            string sourceEntityId,
            string targetEntityId,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateTargetIntent(sourceEntityId, targetEntityId, issuedAt));
        }

        public EveSurfaceCommandRequest SubmitActionIntent(
            string entityId,
            string actionId,
            DateTimeOffset? issuedAt = null)
        {
            return Submit(_session.CreateActionIntent(entityId, actionId, issuedAt));
        }

        public void Disconnect()
        {
            if (!_connected)
                return;

            _surfaceSource.SnapshotAvailable -= OnSnapshotAvailable;
            _surfaceSource.Disconnect();
            _connected = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private EveSurfaceCommandRequest Submit(EveSurfaceCommandRequest request)
        {
            _commandSink.Submit(request);
            return request;
        }

        private EveUnitySceneProjection ApplySnapshot(EveUnitySceneProviderSurfaceSnapshot snapshot)
        {
            var projection = _session.ApplySnapshot(snapshot);
            ProjectionUpdated?.Invoke(projection);
            return projection;
        }

        private bool HasAppliedCurrentSnapshot()
        {
            var projection = _session.ActiveProjection;
            if (projection == null)
                return false;

            var snapshot = _surfaceSource.CurrentSnapshot;
            return _session.ActiveVersion == snapshot.Version &&
                string.Equals(_session.ActiveSourcePointer, snapshot.SourcePointer, StringComparison.Ordinal);
        }

        private void OnSnapshotAvailable(EveUnitySceneProviderSurfaceSnapshot snapshot)
        {
            ApplySnapshot(snapshot);
        }
    }
}
