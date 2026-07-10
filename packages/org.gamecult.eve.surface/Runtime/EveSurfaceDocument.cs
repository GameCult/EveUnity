using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching;
using GameCult.Mesh;
using MessagePack;

#nullable enable

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.surface", "gamecult.eve.surface.v1")]
    [MessagePackObject]
    public sealed class EveSurfaceDocument
    {
        public const string DefaultType = "surface-state";
        public const string SchemaId = "gamecult.eve.surface.v1";

        public EveSurfaceDocument(
            string providerId,
            string providerKind,
            string title,
            long version,
            string updatedAtUtc,
            EveSurfaceTree surface,
            IReadOnlyList<EveCommandTemplate> commands)
            : this(DefaultType, SchemaId, providerId, providerKind, title, version, updatedAtUtc, surface, commands)
        {
        }

        [SerializationConstructor]
        public EveSurfaceDocument(
            string type,
            string schema,
            string providerId,
            string providerKind,
            string title,
            long version,
            string updatedAtUtc,
            EveSurfaceTree surface,
            IReadOnlyList<EveCommandTemplate> commands)
        {
            Type = string.IsNullOrWhiteSpace(type) ? DefaultType : type;
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            ProviderId = providerId ?? "";
            ProviderKind = providerKind ?? "";
            Title = title ?? "";
            Version = version;
            UpdatedAtUtc = updatedAtUtc ?? "";
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
            Commands = commands ?? Array.Empty<EveCommandTemplate>();
        }

        [Key(0)]
        public string Type { get; }

        [Key(1)]
        public string Schema { get; }

        [Key(2)]
        public string ProviderId { get; }

        [Key(3)]
        public string ProviderKind { get; }

        [Key(4)]
        public string Title { get; }

        [Key(5)]
        public long Version { get; }

        [Key(6)]
        public string UpdatedAtUtc { get; }

        [Key(7)]
        public EveSurfaceTree Surface { get; }

        [Key(8)]
        public IReadOnlyList<EveCommandTemplate> Commands { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceTree
    {
        [SerializationConstructor]
        public EveSurfaceTree(string id, EveSurfaceComponent root, IReadOnlyList<EveStyleToken> styles)
        {
            Id = id ?? "";
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Styles = styles ?? Array.Empty<EveStyleToken>();
        }

        [Key(0)]
        public string Id { get; }

        [Key(1)]
        public EveSurfaceComponent Root { get; }

        [Key(2)]
        public IReadOnlyList<EveStyleToken> Styles { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceComponent
    {
        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyList<EveSurfaceComponent> children)
            : this(id, kind, props, children, StateBindingsFromProps(props))
        {
        }

        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyList<EveSurfaceComponent> children,
            IReadOnlyList<CultMeshStateBindingDescriptor> stateBindings)
            : this(id, kind, props, children, stateBindings, Array.Empty<EveEmbeddedDocumentSlot>())
        {
        }

        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyList<EveSurfaceComponent> children,
            IReadOnlyList<CultMeshStateBindingDescriptor> stateBindings,
            IReadOnlyList<EveEmbeddedDocumentSlot> embeddedDocuments,
            IReadOnlyDictionary<string, string>? layout = null,
            IReadOnlyDictionary<string, string>? style = null)
            : this(
                id,
                kind,
                props,
                children,
                (stateBindings ?? Array.Empty<CultMeshStateBindingDescriptor>())
                    .Select(CultMeshStateBindingRecord.FromBinding)
                    .ToArray(),
                embeddedDocuments,
                layout,
                style)
        {
        }

        [SerializationConstructor]
        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyList<EveSurfaceComponent> children,
            CultMeshStateBindingRecord[] stateBindingRecords,
            IReadOnlyList<EveEmbeddedDocumentSlot> embeddedDocuments,
            IReadOnlyDictionary<string, string>? layout = null,
            IReadOnlyDictionary<string, string>? style = null)
        {
            Id = id ?? "";
            Kind = kind ?? "";
            Props = props ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Children = children ?? Array.Empty<EveSurfaceComponent>();
            StateBindingRecords = stateBindingRecords ?? Array.Empty<CultMeshStateBindingRecord>();
            EmbeddedDocuments = embeddedDocuments ?? Array.Empty<EveEmbeddedDocumentSlot>();
            Layout = layout ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Style = style ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        [Key(0)]
        public string Id { get; }

        [Key(1)]
        public string Kind { get; }

        [Key(2)]
        public IReadOnlyDictionary<string, string> Props { get; }

        [Key(3)]
        public IReadOnlyList<EveSurfaceComponent> Children { get; }

        [Key(4)]
        public CultMeshStateBindingRecord[] StateBindingRecords { get; }

        [IgnoreMember]
        public IReadOnlyList<CultMeshStateBindingDescriptor> StateBindings =>
            StateBindingRecords.Select(record => record.ToBinding()).ToArray();

        [Key(5)]
        public IReadOnlyList<EveEmbeddedDocumentSlot> EmbeddedDocuments { get; }

        [Key(6)]
        public IReadOnlyDictionary<string, string> Layout { get; }

        [Key(7)]
        public IReadOnlyDictionary<string, string> Style { get; }

        public string GetProp(string key, string fallback = "")
        {
            return Props.TryGetValue(key, out var value) ? value : fallback;
        }

        private static IReadOnlyList<CultMeshStateBindingDescriptor> StateBindingsFromProps(
            IReadOnlyDictionary<string, string>? props)
        {
            if (props == null || props.Count == 0)
                return Array.Empty<CultMeshStateBindingDescriptor>();

            var bindings = new List<CultMeshStateBindingDescriptor>();
            foreach (var prop in props)
            {
                if (string.IsNullOrWhiteSpace(prop.Value) ||
                    !prop.Key.EndsWith("PointerId", StringComparison.Ordinal))
                {
                    continue;
                }

                var targetProp = prop.Key.Substring(0, prop.Key.Length - "PointerId".Length);
                if (targetProp.Length == 0)
                    targetProp = "value";
                bindings.Add(new CultMeshStateBindingDescriptor(targetProp, prop.Value));
            }

            return bindings;
        }
    }

    [MessagePackObject]
    public sealed class EveEmbeddedDocumentSlot
    {
        public EveEmbeddedDocumentSlot(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            CultMeshRouteHint? routeHint = null)
            : this(slotId, documentId, schemaId, presentationKind, CultMeshRouteRecord.FromRoute(routeHint))
        {
        }

        [SerializationConstructor]
        public EveEmbeddedDocumentSlot(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            CultMeshRouteRecord route)
        {
            SlotId = slotId ?? "";
            DocumentId = documentId ?? "";
            SchemaId = schemaId ?? "";
            PresentationKind = presentationKind ?? "";
            Route = route ?? CultMeshRouteRecord.FromRoute(CultMeshRouteHint.Automatic);
        }

        [Key(0)]
        public string SlotId { get; }

        [Key(1)]
        public string DocumentId { get; }

        [Key(2)]
        public string SchemaId { get; }

        [Key(3)]
        public string PresentationKind { get; }

        [Key(4)]
        public CultMeshRouteRecord Route { get; }

        [IgnoreMember]
        public CultMeshRouteHint RouteHint => Route.ToRoute();
    }

    [MessagePackObject]
    public sealed class EveStyleToken
    {
        [SerializationConstructor]
        public EveStyleToken(string name, string value)
        {
            Name = name ?? "";
            Value = value ?? "";
        }

        [Key(0)]
        public string Name { get; }

        [Key(1)]
        public string Value { get; }
    }

    [MessagePackObject]
    public sealed class EveCommandTemplate
    {
        public EveCommandTemplate(CultMeshOperationBindingDescriptor operation)
            : this(CultMeshOperationBindingRecord.FromBinding(operation))
        {
        }

        [SerializationConstructor]
        public EveCommandTemplate(CultMeshOperationBindingRecord operationRecord)
        {
            OperationRecord = operationRecord ?? throw new ArgumentNullException(nameof(operationRecord));
        }

        [Key(0)]
        public CultMeshOperationBindingRecord OperationRecord { get; }

        [IgnoreMember]
        public CultMeshOperationBindingDescriptor Operation => OperationRecord.ToBinding();

        [IgnoreMember]
        public string Command => Operation.OperationId;

        [IgnoreMember]
        public string Label => Operation.Label;

        [IgnoreMember]
        public string Transport => Operation.RouteHint.Description ?? "";
    }

    [CultDocument("gamecult.eve.command", SchemaId)]
    [MessagePackObject]
    public sealed class EveSurfaceCommandRequest
    {
        public const string SchemaId = "gamecult.eve.command_invocation.v1";

        public EveSurfaceCommandRequest(
            string providerId,
            string surfaceId,
            CultMeshOperationInvocationDescriptor operation,
            CultMeshOperationPayload payload,
            DateTimeOffset issuedAt,
            string clientId,
            string commandBoundary = "",
            string receiptSchema = "")
            : this(
                SchemaId,
                providerId,
                surfaceId,
                CultMeshOperationInvocationRecord.FromInvocation(operation),
                payload?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                issuedAt,
                clientId,
                commandBoundary,
                receiptSchema)
        {
        }

        [SerializationConstructor]
        public EveSurfaceCommandRequest(
            string schema,
            string providerId,
            string surfaceId,
            CultMeshOperationInvocationRecord operation,
            Dictionary<string, string> payload,
            DateTimeOffset issuedAt,
            string clientId,
            string commandBoundary,
            string receiptSchema)
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            ProviderId = providerId;
            SurfaceId = surfaceId;
            OperationRecord = operation ?? throw new ArgumentNullException(nameof(operation));
            PayloadFields = payload == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(payload, StringComparer.Ordinal);
            IssuedAt = issuedAt;
            ClientId = clientId;
            CommandBoundary = commandBoundary ?? "";
            ReceiptSchema = receiptSchema ?? "";
        }

        [Key(0)]
        public string Schema { get; }

        [Key(1)]
        public string ProviderId { get; }

        [Key(2)]
        public string SurfaceId { get; }

        [Key(3)]
        public CultMeshOperationInvocationRecord OperationRecord { get; }

        [IgnoreMember]
        public CultMeshOperationInvocationDescriptor Operation => OperationRecord.ToInvocation();

        [IgnoreMember]
        public string Command => Operation.OperationId;

        [Key(4)]
        public Dictionary<string, string> PayloadFields { get; }

        [IgnoreMember]
        public CultMeshOperationPayload Payload => new CultMeshOperationPayload(PayloadFields);

        [Key(5)]
        public DateTimeOffset IssuedAt { get; }

        [Key(6)]
        public string ClientId { get; }

        [Key(7)]
        public string CommandBoundary { get; }

        [Key(8)]
        public string ReceiptSchema { get; }

        [IgnoreMember]
        public string CommandId => Operation.IdempotencyKey ?? "";
    }
}
