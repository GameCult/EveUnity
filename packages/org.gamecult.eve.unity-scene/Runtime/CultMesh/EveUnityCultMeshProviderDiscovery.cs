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
            string rendezvousEndpoint,
            string verseId,
            string authorityRuntimeId,
            string providerId,
            string surfaceId,
            string surfaceKind)
        {
            RendezvousEndpoint = rendezvousEndpoint ?? "";
            VerseId = verseId ?? "";
            AuthorityRuntimeId = authorityRuntimeId ?? "";
            ProviderId = providerId ?? "";
            SurfaceId = surfaceId ?? "";
            SurfaceKind = surfaceKind ?? "";
        }

        public string RendezvousEndpoint { get; }
        public string VerseId { get; }
        public string AuthorityRuntimeId { get; }
        public string ProviderId { get; }
        public string SurfaceId { get; }
        public string SurfaceKind { get; }
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

            CultMeshVerseCatalogResponseMessage response;
            try
            {
                response = await CultMesh.CreateVerseDiscoveryClient().FetchAsync(
                    rendezvousEndpoint,
                    new CultMeshVerseCatalogRequestMessage
                    {
                        VerseIds = string.IsNullOrWhiteSpace(verseId) ? null : new[] { verseId },
                        TransportVersion = "cultmesh.v0"
                    });
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"Could not query CultMesh rendezvous endpoint '{rendezvousEndpoint}'.",
                    error);
            }

            var candidates = response.Verses
                .Where(verse => string.IsNullOrWhiteSpace(verseId) ||
                                string.Equals(verse.VerseId, verseId, StringComparison.Ordinal))
                .Where(verse => !string.IsNullOrWhiteSpace(verse.VerseId) &&
                                (verse.DiscoveryEndpoints ?? Array.Empty<string>()).Any(endpoint =>
                                    !string.IsNullOrWhiteSpace(endpoint)))
                .GroupBy(verse => verse.VerseId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("The rendezvous endpoint advertised no compatible Verse endpoints.");

            var failures = new List<string>();
            var observed = new List<string>();
            using var mesh = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = new[] { rendezvousEndpoint }
            });
            foreach (var candidate in candidates)
            {
                foreach (var authorityRuntimeId in (candidate.AuthorityRuntimeIds ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal))
                {
                    try
                    {
                        var target = new CultMeshSessionTarget(candidate.VerseId, authorityRuntimeId);
                        using var advertisementsLease = await mesh
                            .LeaseCollectionAsync<EveProviderAdvertisementDocument>(target, cancellationToken)
                            .ConfigureAwait(false);
                        var advertisements = await advertisementsLease.Handle.LatestAsync().ConfigureAwait(false);
                        observed.AddRange(advertisements.Select(document =>
                            $"{target}: {document.ProviderId}[{string.Join(",", document.Surfaces.Select(surface => $"{surface.SurfaceId}:{surface.SurfaceKind}"))}]"));
                        var advertisement = advertisements
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
                            rendezvousEndpoint,
                            candidate.VerseId,
                            authorityRuntimeId,
                            advertisement.Document.ProviderId,
                            advertisement.Surface.SurfaceId,
                            advertisement.Surface.SurfaceKind);
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{candidate.VerseId}/{authorityRuntimeId}: {error.Message}");
                    }
                }
            }

            var filter = $"provider='{providerId}', surface='{surfaceId}', kind='{surfaceKind}'";
            var detail = failures.Count == 0 ? "" : $" Endpoint failures: {string.Join(" | ", failures)}";
            var observedDetail = observed.Count == 0 ? "" : $" Observed: {string.Join(" | ", observed)}";
            throw new InvalidOperationException($"No advertised Eve surface matched {filter}.{detail}{observedDetail}");
        }

    }
}
