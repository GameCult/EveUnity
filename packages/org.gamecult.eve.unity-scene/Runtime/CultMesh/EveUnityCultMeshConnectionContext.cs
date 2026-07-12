using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshConnectionContext : IDisposable
    {
        private readonly CultMeshDiscoveryService _discovery;

        internal EveUnityCultMeshConnectionContext(
            CultMeshEndpointId endpointId,
            CultMeshDiscoveryService discovery,
            CultMeshSessionManager sessions)
        {
            EndpointId = endpointId ?? throw new ArgumentNullException(nameof(endpointId));
            _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public CultMeshEndpointId EndpointId { get; }
        public CultMeshSessionManager Sessions { get; }

        public Task<CultMeshSession> OpenDocumentsAsync(CancellationToken cancellationToken = default) =>
            Sessions.ConnectAsync(EndpointId, CultMeshProtocols.Documents, cancellationToken);

        public void Dispose()
        {
            Sessions.Dispose();
            _discovery.Dispose();
        }

        internal static EveUnityCultMeshConnectionContext Create(
            string endpointId,
            IEnumerable<CultMeshVerseDescriptor> routes)
        {
            var identity = CultMeshEndpointId.Parse(endpointId);
            var discovery = new CultMeshDiscoveryService(new[] { new FixedRouteSource(routes) });
            return new EveUnityCultMeshConnectionContext(
                identity,
                discovery,
                new CultMeshSessionManager(discovery, new ICultMeshTransportConnector[]
                {
                    new CultMeshSchemaTransportConnector()
                }));
        }

        private sealed class FixedRouteSource : ICultMeshLookupSource
        {
            private readonly CultMeshVerseDescriptor[] _routes;

            public FixedRouteSource(IEnumerable<CultMeshVerseDescriptor> routes)
            {
                _routes = routes?.ToArray() ?? throw new ArgumentNullException(nameof(routes));
            }

            public string SourceId => "eve-unity-rendezvous";

            public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
                CultMeshDiscoveryQuery query,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                IReadOnlyList<CultMeshDiscoveryObservation> observations = _routes
                    .Where(route => string.Equals(route.VerseId, query.EndpointId, StringComparison.Ordinal))
                    .Select(route => new CultMeshDiscoveryObservation(
                        route,
                        SourceId,
                        now,
                        now.AddMinutes(5),
                        CultMeshDiscoveryTrust.Unsigned,
                        "CultMesh rendezvous catalog"))
                    .ToArray();
                return Task.FromResult(observations);
            }
        }
    }
}
