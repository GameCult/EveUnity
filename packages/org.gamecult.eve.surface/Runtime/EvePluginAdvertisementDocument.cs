using System;
using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

#nullable enable

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.plugin_advertisement", SchemaId)]
    [MessagePackObject]
    public sealed class EvePluginAdvertisementDocument
    {
        public const string SchemaId = "gamecult.eve.plugin_advertisement.v1";

        public EvePluginAdvertisementDocument(
            string pluginId,
            string ownerService,
            string version,
            string manifestAddress,
            EvePluginRuntimeAdvertisement runtime,
            IReadOnlyList<string> schemas,
            IReadOnlyList<string> componentKinds,
            IReadOnlyList<string> commands,
            IReadOnlyList<string> fixtures)
            : this(SchemaId, pluginId, ownerService, version, manifestAddress, runtime, schemas, componentKinds, commands, fixtures)
        {
        }

        [SerializationConstructor]
        public EvePluginAdvertisementDocument(
            string schema,
            string pluginId,
            string ownerService,
            string version,
            string manifestAddress,
            EvePluginRuntimeAdvertisement runtime,
            IReadOnlyList<string> schemas,
            IReadOnlyList<string> componentKinds,
            IReadOnlyList<string> commands,
            IReadOnlyList<string> fixtures)
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            PluginId = pluginId ?? "";
            OwnerService = ownerService ?? "";
            Version = version ?? "";
            ManifestAddress = manifestAddress ?? "";
            Runtime = runtime ?? new EvePluginRuntimeAdvertisement("", "", Array.Empty<string>(), Array.Empty<string>(), null);
            Schemas = schemas ?? Array.Empty<string>();
            ComponentKinds = componentKinds ?? Array.Empty<string>();
            Commands = commands ?? Array.Empty<string>();
            Fixtures = fixtures ?? Array.Empty<string>();
        }

        [Key(0)] public string Schema { get; }
        [Key(1), CultName] public string PluginId { get; }
        [Key(2)] public string OwnerService { get; }
        [Key(3)] public string Version { get; }
        [Key(4)] public string ManifestAddress { get; }
        [Key(5)] public EvePluginRuntimeAdvertisement Runtime { get; }
        [Key(6)] public IReadOnlyList<string> Schemas { get; }
        [Key(7)] public IReadOnlyList<string> ComponentKinds { get; }
        [Key(8)] public IReadOnlyList<string> Commands { get; }
        [Key(9)] public IReadOnlyList<string> Fixtures { get; }
    }

    [MessagePackObject]
    public sealed class EvePluginRuntimeAdvertisement
    {
        [SerializationConstructor]
        public EvePluginRuntimeAdvertisement(
            string invocationModel,
            string contract,
            IReadOnlyList<string> transports,
            IReadOnlyList<string> authority,
            EvePluginSidecarAdvertisement? sidecar)
        {
            InvocationModel = invocationModel ?? "";
            Contract = contract ?? "";
            Transports = transports ?? Array.Empty<string>();
            Authority = authority ?? Array.Empty<string>();
            Sidecar = sidecar;
        }

        [Key(0)] public string InvocationModel { get; }
        [Key(1)] public string Contract { get; }
        [Key(2)] public IReadOnlyList<string> Transports { get; }
        [Key(3)] public IReadOnlyList<string> Authority { get; }
        [Key(4)] public EvePluginSidecarAdvertisement? Sidecar { get; }
    }

    [MessagePackObject]
    public sealed class EvePluginSidecarAdvertisement
    {
        [SerializationConstructor]
        public EvePluginSidecarAdvertisement(
            string processKind,
            string protocol,
            string requestSchema,
            string responseSchema,
            IReadOnlyList<string> operations,
            string commandEnvelope,
            string receiptSchema,
            string stateAuthority)
        {
            ProcessKind = processKind ?? "";
            Protocol = protocol ?? "";
            RequestSchema = requestSchema ?? "";
            ResponseSchema = responseSchema ?? "";
            Operations = operations ?? Array.Empty<string>();
            CommandEnvelope = commandEnvelope ?? "";
            ReceiptSchema = receiptSchema ?? "";
            StateAuthority = stateAuthority ?? "";
        }

        [Key(0)] public string ProcessKind { get; }
        [Key(1)] public string Protocol { get; }
        [Key(2)] public string RequestSchema { get; }
        [Key(3)] public string ResponseSchema { get; }
        [Key(4)] public IReadOnlyList<string> Operations { get; }
        [Key(5)] public string CommandEnvelope { get; }
        [Key(6)] public string ReceiptSchema { get; }
        [Key(7)] public string StateAuthority { get; }
    }
}
