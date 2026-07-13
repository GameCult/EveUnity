using System;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshProviderSelection
    {
        internal EveUnityCultMeshProviderSelection(
            string endpointId,
            CultMeshClient mesh,
            EveSurfaceSelection surface)
        {
            EndpointId = endpointId;
            Mesh = mesh;
            Surface = surface;
        }

        public string EndpointId { get; }
        public string VerseId => Surface.Provider.VerseId;
        public string ProviderId => Surface.Provider.ProviderId;
        public string SurfaceId => Surface.Advertisement.SurfaceId;
        public string SurfaceKind => Surface.Advertisement.SurfaceKind;
        public CultMeshClient Mesh { get; }
        public EveSurfaceSelection Surface { get; }
    }

    public sealed class EveUnityCultMeshProviderDiscovery
    {
        public async Task<EveUnityCultMeshProviderSelection> DiscoverAsync(
            string rendezvousEndpoint,
            string providerId = "",
            string surfaceId = "",
            string surfaceKind = "interactive-world",
            string verseId = "",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rendezvousEndpoint))
                throw new ArgumentException("Rendezvous endpoint must be non-empty.", nameof(rendezvousEndpoint));
            var endpointId = !string.IsNullOrWhiteSpace(verseId)
                ? verseId
                : !string.IsNullOrWhiteSpace(providerId) ? providerId : "eve.providers";

            var mesh = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = new[] { rendezvousEndpoint }
            });
            try
            {
                var surface = await mesh.SurfaceAsync(
                    endpointId,
                    new EveSurfaceRequest
                    {
                        ProviderId = providerId,
                        SurfaceId = surfaceId,
                        SurfaceKind = surfaceKind,
                        LoweringTarget = "unity-scene"
                    },
                    cancellationToken,
                    stage => Debug.Log($"EveUnity CultMesh discovery: {stage}")).ConfigureAwait(false);
                return new EveUnityCultMeshProviderSelection(endpointId, mesh, surface);
            }
            catch
            {
                mesh.Dispose();
                throw;
            }
        }
    }
}
