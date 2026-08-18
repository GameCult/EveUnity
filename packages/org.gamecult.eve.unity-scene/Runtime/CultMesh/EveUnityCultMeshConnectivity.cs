using System;
using GameCult.Mesh;
using GameCult.Networking;
using GameCult.Networking.WebSockets;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    /// <summary>
    /// Owns the transport set used by EveUnity for both rendezvous discovery and
    /// the selected provider. Trust remains caller-owned; this class only makes
    /// every supported secure plane reachable through the same configuration.
    /// </summary>
    internal static class EveUnityCultMeshConnectivity
    {
        public static CultMeshVerseDiscoveryClientOptions Discovery() => new()
        {
            CreateClientForEndpoint = CreateSchemaClient
        };

        public static ICultMeshTransportConnector[] SchemaConnectors() => new ICultMeshTransportConnector[]
        {
            new CultMeshTcpSchemaTransportConnector(),
            new CultMeshUriSchemaTransportConnector(
                "cultnet-websocket",
                new[] { "ws", "wss" },
                _ => new CultNetWebSocketSchemaClient())
        };

        public static ICultMeshContentTransportConnector[] ContentConnectors() =>
            new ICultMeshContentTransportConnector[]
            {
                new CultMeshHttpsContentTransportConnector(),
                new CultMeshTcpContentTransportConnector()
            };

        private static ICultNetSchemaClient CreateSchemaClient(string endpoint)
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
            {
                return new CultNetWebSocketSchemaClient();
            }
            return CultNetSchemaClients.CreateForEndpoint(endpoint);
        }
    }
}
