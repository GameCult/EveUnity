using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshProviderSelection
    {
        public EveUnityCultMeshProviderSelection(
            string endpoint,
            string verseId,
            string providerId,
            string surfaceId,
            string surfaceKind,
            EveUnityCultMeshConnectionContext connection)
        {
            Endpoint = endpoint ?? "";
            VerseId = verseId ?? "";
            ProviderId = providerId ?? "";
            SurfaceId = surfaceId ?? "";
            SurfaceKind = surfaceKind ?? "";
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public string Endpoint { get; }
        public string VerseId { get; }
        public string ProviderId { get; }
        public string SurfaceId { get; }
        public string SurfaceKind { get; }
        public EveUnityCultMeshConnectionContext Connection { get; }
    }

    public sealed class EveUnityCultMeshProviderDiscovery
    {
        private static readonly Type[] AdvertisementDocumentTypes =
        {
            typeof(EveProviderAdvertisementDocument)
        };

        public EveUnityCultMeshProviderSelection Discover(
            string rendezvousEndpoint,
            string providerId = "",
            string surfaceId = "",
            string surfaceKind = "interactive-world",
            string verseId = "")
            => DiscoverAsync(rendezvousEndpoint, providerId, surfaceId, surfaceKind, verseId)
                .GetAwaiter().GetResult();

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

            var response = await CultMesh.CreateVerseDiscoveryClient().FetchAsync(
                    rendezvousEndpoint,
                    new CultMeshVerseCatalogRequestMessage
                    {
                        VerseIds = string.IsNullOrWhiteSpace(verseId) ? null : new[] { verseId },
                        TransportVersion = "cultmesh.v0"
                    }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = response.Verses
                .Where(verse => string.IsNullOrWhiteSpace(verseId) ||
                                string.Equals(verse.VerseId, verseId, StringComparison.Ordinal))
                .SelectMany(verse => (verse.DiscoveryEndpoints ?? Array.Empty<string>())
                    .Select(endpoint => new { Verse = verse, Endpoint = endpoint }))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Endpoint))
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("The rendezvous endpoint advertised no compatible Verse endpoints.");

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                EveUnityCultMeshConnectionContext? connection = null;
                try
                {
                    connection = EveUnityCultMeshConnectionContext.Create(
                        candidate.Verse.VerseId,
                        new[] { candidate.Verse.ToVerseDescriptor() });
                    var advertisement = (await FetchAdvertisementsAsync(connection, cancellationToken).ConfigureAwait(false))
                        .Where(document => string.IsNullOrWhiteSpace(providerId) ||
                                           string.Equals(document.ProviderId, providerId, StringComparison.Ordinal))
                        .Select(document => new
                        {
                            Document = document,
                            Surface = document.Surfaces.FirstOrDefault(surface =>
                                (string.IsNullOrWhiteSpace(surfaceId) ||
                                 string.Equals(surface.SurfaceId, surfaceId, StringComparison.Ordinal)) &&
                                (string.IsNullOrWhiteSpace(surfaceKind) ||
                                 string.Equals(surface.SurfaceKind, surfaceKind, StringComparison.Ordinal)))
                        })
                        .FirstOrDefault(match => match.Surface != null);
                    if (advertisement?.Surface == null)
                        continue;

                    return new EveUnityCultMeshProviderSelection(
                        candidate.Endpoint,
                        candidate.Verse.VerseId,
                        advertisement.Document.ProviderId,
                        advertisement.Surface.SurfaceId,
                        advertisement.Surface.SurfaceKind,
                        connection);
                }
                catch (Exception error)
                {
                    connection?.Dispose();
                    failures.Add($"{candidate.Endpoint}: {error.Message}");
                }
            }

            var filter = $"provider='{providerId}', surface='{surfaceId}', kind='{surfaceKind}'";
            var detail = failures.Count == 0 ? "" : $" Endpoint failures: {string.Join(" | ", failures)}";
            throw new InvalidOperationException($"No advertised Eve surface matched {filter}.{detail}");
        }

        private static async Task<EveProviderAdvertisementDocument[]> FetchAdvertisementsAsync(
            EveUnityCultMeshConnectionContext connection,
            CancellationToken cancellationToken)
        {
            var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(AdvertisementDocumentTypes);
            var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(AdvertisementDocumentTypes, cacheRegistry);
            using var snapshot = await CultMeshSnapshotSession.ConnectAsync(
                connection.Sessions,
                connection.EndpointId,
                new CultMeshSnapshotEndpointOptions
                {
                    Context = CultMesh.Verse("eve.discovery", "eve-unity").Context,
                    DocumentRegistry = networkRegistry,
                    Request = new CultMeshSnapshotRequestOptions
                    {
                        ShardId = "provider",
                        ShardEpoch = 1,
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                        ResponseTimeout = TimeSpan.FromSeconds(10),
                        MessageIdPrefix = "eve-unity-discovery",
                        RudpRuntimeId = "eve-unity.discovery",
                        RudpMaxFragmentBytes = 1024
                    }
                }.Request,
                networkRegistry,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (await snapshot.FetchDocumentsAsync<EveProviderAdvertisementDocument>()
                .ConfigureAwait(false)).ToArray();
        }
    }
}
