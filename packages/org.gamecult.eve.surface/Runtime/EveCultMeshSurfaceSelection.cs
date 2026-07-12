using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.Surface
{
    public sealed class EveSurfaceRequest
    {
        public string ProviderId { get; set; } = "";
        public string SurfaceId { get; set; } = "";
        public string SurfaceKind { get; set; } = "";
        public string LoweringTarget { get; set; } = "";
        public bool RequireActive { get; set; } = true;
    }

    public sealed class EveSurfaceSelection
    {
        internal EveSurfaceSelection(
            EveProviderAdvertisementDocument provider,
            EveAdvertisedSurface advertisement,
            CultMeshDocumentHandle<EveSurfaceDocument> surface,
            IReadOnlyList<EvePluginAdvertisementDocument> plugins)
        {
            Provider = provider;
            Advertisement = advertisement;
            Surface = surface;
            Plugins = plugins;
        }

        public EveProviderAdvertisementDocument Provider { get; }
        public EveAdvertisedSurface Advertisement { get; }
        public CultMeshDocumentHandle<EveSurfaceDocument> Surface { get; }
        public IReadOnlyList<EvePluginAdvertisementDocument> Plugins { get; }
    }

    public static class EveCultMeshSurfaceSelection
    {
        public static async Task<EveSurfaceSelection> SurfaceAsync(
            this CultMeshClient mesh,
            string endpointId,
            EveSurfaceRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var resolved = request ?? new EveSurfaceRequest();
            var providers = await mesh.CollectionAsync<EveProviderAdvertisementDocument>(endpointId, cancellationToken)
                .ConfigureAwait(false);
            var match = Select(await providers.LatestAsync().ConfigureAwait(false), resolved);
            IReadOnlyList<EvePluginAdvertisementDocument> plugins = Array.Empty<EvePluginAdvertisementDocument>();
            if (match.Surface.RequiresPlugins.Count > 0)
            {
                var advertisements = await mesh.CollectionAsync<EvePluginAdvertisementDocument>(endpointId, cancellationToken)
                    .ConfigureAwait(false);
                plugins = ResolvePlugins(match.Surface, await advertisements.LatestAsync().ConfigureAwait(false));
            }
            var surface = await mesh.DocumentAsync<EveSurfaceDocument>(
                    endpointId,
                    match.Surface.RecordRef,
                    cancellationToken)
                .ConfigureAwait(false);
            return new EveSurfaceSelection(match.Provider, match.Surface, surface, plugins);
        }

        public static IReadOnlyList<EvePluginAdvertisementDocument> ResolvePlugins(
            EveAdvertisedSurface surface,
            IEnumerable<EvePluginAdvertisementDocument> advertisements)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (advertisements == null) throw new ArgumentNullException(nameof(advertisements));
            var available = advertisements
                .GroupBy(plugin => plugin.PluginId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(plugin => plugin.Version, StringComparer.Ordinal).First(), StringComparer.Ordinal);
            var selected = new List<EvePluginAdvertisementDocument>();
            foreach (var requirement in surface.RequiresPlugins)
            {
                if (!available.TryGetValue(requirement.PluginId, out var plugin))
                {
                    if (string.Equals(requirement.Availability, "required", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Eve surface '{surface.SurfaceId}' requires unavailable plugin '{requirement.PluginId}'.");
                    continue;
                }

                var claims = plugin.Schemas.Concat(plugin.ComponentKinds).Concat(plugin.Commands)
                    .ToHashSet(StringComparer.Ordinal);
                var missing = requirement.RequiredCapabilities.Where(capability => !claims.Contains(capability)).ToArray();
                if (missing.Length > 0)
                {
                    if (string.Equals(requirement.Availability, "required", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Eve plugin '{plugin.PluginId}' does not advertise required capabilities: {string.Join(", ", missing)}.");
                    continue;
                }
                selected.Add(plugin);
            }
            return selected;
        }

        public static (EveProviderAdvertisementDocument Provider, EveAdvertisedSurface Surface) Select(
            IEnumerable<EveProviderAdvertisementDocument> providers,
            EveSurfaceRequest request)
        {
            if (providers == null) throw new ArgumentNullException(nameof(providers));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var match = providers
                .Where(provider => Matches(request.ProviderId, provider.ProviderId))
                .SelectMany(provider => provider.Surfaces.Select(surface => new { Provider = provider, Surface = surface }))
                .Where(candidate => Matches(request.SurfaceId, candidate.Surface.SurfaceId))
                .Where(candidate => Matches(request.SurfaceKind, candidate.Surface.SurfaceKind))
                .Where(candidate => !request.RequireActive || string.Equals(candidate.Surface.Status, "active", StringComparison.OrdinalIgnoreCase))
                .Where(candidate => string.IsNullOrWhiteSpace(request.LoweringTarget) ||
                                    candidate.Surface.WorldInteraction?.LoweringTargets.Any(target =>
                                        string.Equals(target, request.LoweringTarget, StringComparison.Ordinal)) == true)
                .OrderBy(candidate => candidate.Provider.ProviderId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Surface.SurfaceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match == null)
                throw new InvalidOperationException(
                    $"No advertised Eve surface matched provider='{request.ProviderId}', surface='{request.SurfaceId}', " +
                    $"kind='{request.SurfaceKind}', loweringTarget='{request.LoweringTarget}'.");
            if (!string.Equals(match.Surface.Transport, "cultmesh-record", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Eve surface '{match.Surface.SurfaceId}' uses unsupported transport '{match.Surface.Transport}'.");
            if (string.IsNullOrWhiteSpace(match.Surface.RecordRef))
                throw new InvalidOperationException($"Eve surface '{match.Surface.SurfaceId}' does not advertise a record reference.");
            return (match.Provider, match.Surface);
        }

        private static bool Matches(string expected, string actual) =>
            string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.Ordinal);
    }
}
