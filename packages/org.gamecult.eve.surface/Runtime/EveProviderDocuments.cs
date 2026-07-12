using System;
using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

#nullable enable

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.provider_advertisement", SchemaId)]
    [MessagePackObject]
    public sealed class EveProviderAdvertisementDocument
    {
        public const string SchemaId = "gamecult.eve.provider_advertisement.v1";

        public EveProviderAdvertisementDocument(
            string providerId,
            string serviceId,
            string verseId,
            string title,
            string kind,
            string cultMeshAddress,
            string updatedAtUtc,
            EveProviderFreshness freshness,
            IReadOnlyList<string> schemas,
            IReadOnlyList<EveProviderWitness> witnesses,
            IReadOnlyList<EveAdvertisedSurface> surfaces,
            IReadOnlyList<EveAdvertisedCommand> commands)
            : this(SchemaId, providerId, serviceId, verseId, title, kind, cultMeshAddress, updatedAtUtc, freshness, schemas, witnesses, surfaces, commands)
        {
        }

        [SerializationConstructor]
        public EveProviderAdvertisementDocument(
            string schema,
            string providerId,
            string serviceId,
            string verseId,
            string title,
            string kind,
            string cultMeshAddress,
            string updatedAtUtc,
            EveProviderFreshness freshness,
            IReadOnlyList<string> schemas,
            IReadOnlyList<EveProviderWitness> witnesses,
            IReadOnlyList<EveAdvertisedSurface> surfaces,
            IReadOnlyList<EveAdvertisedCommand> commands)
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            ProviderId = providerId ?? "";
            ServiceId = serviceId ?? "";
            VerseId = verseId ?? "";
            Title = title ?? "";
            Kind = kind ?? "";
            CultMeshAddress = cultMeshAddress ?? "";
            UpdatedAtUtc = updatedAtUtc ?? "";
            Freshness = freshness ?? new EveProviderFreshness("unknown", "", 0);
            Schemas = schemas ?? Array.Empty<string>();
            Witnesses = witnesses ?? Array.Empty<EveProviderWitness>();
            Surfaces = surfaces ?? Array.Empty<EveAdvertisedSurface>();
            Commands = commands ?? Array.Empty<EveAdvertisedCommand>();
        }

        [Key(0)] public string Schema { get; }
        [Key(1)] public string ProviderId { get; }
        [Key(2)] public string ServiceId { get; }
        [Key(3)] public string VerseId { get; }
        [Key(4)] public string Title { get; }
        [Key(5)] public string Kind { get; }
        [Key(6)] public string CultMeshAddress { get; }
        [Key(7)] public string UpdatedAtUtc { get; }
        [Key(8)] public EveProviderFreshness Freshness { get; }
        [Key(9)] public IReadOnlyList<string> Schemas { get; }
        [Key(10)] public IReadOnlyList<EveProviderWitness> Witnesses { get; }
        [Key(11)] public IReadOnlyList<EveAdvertisedSurface> Surfaces { get; }
        [Key(12)] public IReadOnlyList<EveAdvertisedCommand> Commands { get; }
    }

    [MessagePackObject]
    public sealed class EveProviderFreshness
    {
        [SerializationConstructor]
        public EveProviderFreshness(string state, string lastSeenAtUtc, long maxAgeMs)
        {
            State = state ?? "";
            LastSeenAtUtc = lastSeenAtUtc ?? "";
            MaxAgeMs = maxAgeMs;
        }

        [Key(0)] public string State { get; }
        [Key(1)] public string LastSeenAtUtc { get; }
        [Key(2)] public long MaxAgeMs { get; }
    }

    [MessagePackObject]
    public sealed class EveProviderWitness
    {
        [SerializationConstructor]
        public EveProviderWitness(string kind, string reference, string summary)
        {
            Kind = kind ?? "";
            Reference = reference ?? "";
            Summary = summary ?? "";
        }

        [Key(0)] public string Kind { get; }
        [Key(1)] public string Reference { get; }
        [Key(2)] public string Summary { get; }
    }

    [MessagePackObject]
    public sealed class EveAdvertisedSurface
    {
        [SerializationConstructor]
        public EveAdvertisedSurface(
            string surfaceId,
            string schema,
            string recordRef,
            string transport,
            string status,
            string surfaceKind,
            EveWorldInteractionAdvertisement? worldInteraction = null,
            IReadOnlyList<EvePluginRequirement>? requiresPlugins = null)
        {
            SurfaceId = surfaceId ?? "";
            Schema = schema ?? "";
            RecordRef = recordRef ?? "";
            Transport = transport ?? "";
            Status = status ?? "";
            SurfaceKind = surfaceKind ?? "";
            WorldInteraction = worldInteraction;
            RequiresPlugins = requiresPlugins ?? Array.Empty<EvePluginRequirement>();
        }

        [Key(0)] public string SurfaceId { get; }
        [Key(1)] public string Schema { get; }
        [Key(2)] public string RecordRef { get; }
        [Key(3)] public string Transport { get; }
        [Key(4)] public string Status { get; }
        [Key(5)] public string SurfaceKind { get; }
        [Key(6)] public EveWorldInteractionAdvertisement? WorldInteraction { get; }
        [Key(7)] public IReadOnlyList<EvePluginRequirement> RequiresPlugins { get; }
    }

    [MessagePackObject]
    public sealed class EvePluginRequirement
    {
        [SerializationConstructor]
        public EvePluginRequirement(
            string pluginId,
            string versionRange,
            string availability,
            IReadOnlyList<string> requiredCapabilities,
            IReadOnlyList<string> optionalCapabilities)
        {
            PluginId = pluginId ?? "";
            VersionRange = versionRange ?? "";
            Availability = string.IsNullOrWhiteSpace(availability) ? "required" : availability;
            RequiredCapabilities = requiredCapabilities ?? Array.Empty<string>();
            OptionalCapabilities = optionalCapabilities ?? Array.Empty<string>();
        }

        [Key(0)] public string PluginId { get; }
        [Key(1)] public string VersionRange { get; }
        [Key(2)] public string Availability { get; }
        [Key(3)] public IReadOnlyList<string> RequiredCapabilities { get; }
        [Key(4)] public IReadOnlyList<string> OptionalCapabilities { get; }
    }

    [MessagePackObject]
    public sealed class EveWorldInteractionAdvertisement
    {
        [SerializationConstructor]
        public EveWorldInteractionAdvertisement(
            string projectionKind,
            IReadOnlyList<string> stateSchemas,
            string commandBoundary,
            string commandRecordRef,
            string receiptSchema,
            string receiptRecordRef,
            string assetManifestRecordRef,
            IReadOnlyList<string> loweringTargets,
            string ownership)
        {
            ProjectionKind = projectionKind ?? "";
            StateSchemas = stateSchemas ?? Array.Empty<string>();
            CommandBoundary = commandBoundary ?? "";
            CommandRecordRef = commandRecordRef ?? "";
            ReceiptSchema = receiptSchema ?? "";
            ReceiptRecordRef = receiptRecordRef ?? "";
            AssetManifestRecordRef = assetManifestRecordRef ?? "";
            LoweringTargets = loweringTargets ?? Array.Empty<string>();
            Ownership = ownership ?? "";
        }

        [Key(0)] public string ProjectionKind { get; }
        [Key(1)] public IReadOnlyList<string> StateSchemas { get; }
        [Key(2)] public string CommandBoundary { get; }
        [Key(3)] public string CommandRecordRef { get; }
        [Key(4)] public string ReceiptSchema { get; }
        [Key(5)] public string ReceiptRecordRef { get; }
        [Key(6)] public string AssetManifestRecordRef { get; }
        [Key(7)] public IReadOnlyList<string> LoweringTargets { get; }
        [Key(8)] public string Ownership { get; }
    }

    [MessagePackObject]
    public sealed class EveAdvertisedCommand
    {
        [SerializationConstructor]
        public EveAdvertisedCommand(string command, string surfaceId, string transport, string summary)
        {
            Command = command ?? "";
            SurfaceId = surfaceId ?? "";
            Transport = transport ?? "";
            Summary = summary ?? "";
        }

        [Key(0)] public string Command { get; }
        [Key(1)] public string SurfaceId { get; }
        [Key(2)] public string Transport { get; }
        [Key(3)] public string Summary { get; }
    }

    [CultDocument("gamecult.eve.command_receipt", SchemaId)]
    [MessagePackObject]
    public sealed class EveCommandReceiptDocument
    {
        public const string SchemaId = "gamecult.eve.command_receipt.v1";

        public EveCommandReceiptDocument(
            string receiptId,
            string commandId,
            string command,
            string state,
            string ownerRepo,
            string authority,
            string providerId,
            string surfaceId,
            string message,
            string issuedAtUtc,
            long sourceVersion)
            : this(SchemaId, receiptId, commandId, command, state, ownerRepo, authority, providerId, surfaceId, message, issuedAtUtc, sourceVersion)
        {
        }

        [SerializationConstructor]
        public EveCommandReceiptDocument(
            string schema,
            string receiptId,
            string commandId,
            string command,
            string state,
            string ownerRepo,
            string authority,
            string providerId,
            string surfaceId,
            string message,
            string issuedAtUtc,
            long sourceVersion)
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            ReceiptId = receiptId ?? "";
            CommandId = commandId ?? "";
            Command = command ?? "";
            State = state ?? "";
            OwnerRepo = ownerRepo ?? "";
            Authority = authority ?? "";
            ProviderId = providerId ?? "";
            SurfaceId = surfaceId ?? "";
            Message = message ?? "";
            IssuedAtUtc = issuedAtUtc ?? "";
            SourceVersion = sourceVersion;
        }

        [Key(0)] public string Schema { get; }
        [Key(1)] public string ReceiptId { get; }
        [Key(2)] public string CommandId { get; }
        [Key(3)] public string Command { get; }
        [Key(4)] public string State { get; }
        [Key(5)] public string OwnerRepo { get; }
        [Key(6)] public string Authority { get; }
        [Key(7)] public string ProviderId { get; }
        [Key(8)] public string SurfaceId { get; }
        [Key(9)] public string Message { get; }
        [Key(10)] public string IssuedAtUtc { get; }
        [Key(11)] public long SourceVersion { get; }
    }
}
