using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnitySceneSurfaceLowerer
    {
        private static readonly SaiVisualNovelUnitySceneProjectionAdapter SaiVisualNovelAdapter = new SaiVisualNovelUnitySceneProjectionAdapter();
        private static readonly NornGraphUnitySceneProjectionAdapter NornGraphAdapter = new NornGraphUnitySceneProjectionAdapter();
        private static readonly TeXMathUnitySceneProjectionAdapter TeXMathAdapter = new TeXMathUnitySceneProjectionAdapter();

        public EveUnitySceneProjection Lower(
            EveSurfaceDocument document,
            EveUnitySceneProviderSurfaceAdvertisement advertisedSurface)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (advertisedSurface == null) throw new ArgumentNullException(nameof(advertisedSurface));
            if (!string.Equals(document.Surface.Id, advertisedSurface.SurfaceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Active surface '{document.Surface.Id}' does not match advertised Unity scene surface '{advertisedSurface.SurfaceId}'.");
            }

            return new EveUnitySceneProjection(
                document.ProviderId,
                document.Surface.Id,
                advertisedSurface.WorldInteraction.ProjectionKind,
                advertisedSurface.WorldInteraction.CommandBoundary,
                advertisedSurface.WorldInteraction.ReceiptSchema,
                advertisedSurface.WorldInteraction.Ownership,
                BuildPlayableWorld(document.Surface.Root),
                BuildSceneGraph(document.Surface.Root));
        }

        public EveSurfaceCommandRequest CreateCommandIntent(
            EveSurfaceDocument document,
            EveUnitySceneProviderSurfaceAdvertisement advertisedSurface,
            string command,
            IReadOnlyDictionary<string, string>? payload = null,
            DateTimeOffset? issuedAt = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (advertisedSurface == null) throw new ArgumentNullException(nameof(advertisedSurface));
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));

            var projection = Lower(document, advertisedSurface);
            return new EveSurfaceCommandRequest(
                projection.ProviderId,
                projection.SurfaceId,
                ResolveOperation(document, command, $"unity-scene-{Guid.NewGuid():N}"),
                CultMesh.OperationPayload(payload ?? new Dictionary<string, string>(StringComparer.Ordinal)),
                issuedAt ?? DateTimeOffset.UtcNow,
                "unity-scene",
                projection.CommandBoundary,
                projection.ReceiptSchema);
        }

        private static CultMeshOperationInvocationDescriptor ResolveOperation(
            EveSurfaceDocument document,
            string command,
            string idempotencyKey)
        {
            foreach (var template in document.Commands)
            {
                if (string.Equals(template.Command, command, StringComparison.Ordinal))
                    return CultMesh.OperationInvocation(template.Operation, idempotencyKey);
            }

            return CultMesh.OperationInvocation(command, idempotencyKey: idempotencyKey);
        }

        private static EveUnitySceneNode BuildSceneGraph(EveSurfaceComponent component)
        {
            var children = new List<EveUnitySceneNode>(component.Children.Count);
            foreach (var child in component.Children)
            {
                children.Add(BuildSceneGraph(child));
            }

            return new EveUnitySceneNode(
                component.Id,
                component.Kind,
                SceneObjectKind(component.Kind),
                component.Props,
                component.Layout,
                component.Style,
                component.StateBindings.Count,
                component.EmbeddedDocuments.Count,
                BuildEmbeddedDocumentSlots(component.EmbeddedDocuments),
                BuildPluginProjection(component),
                children);
        }

        private static EveUnityPlayableWorldProjection? BuildPlayableWorld(EveSurfaceComponent root)
        {
            var worldRoot = FindFirst(root, component => IsWorldScene(component.Kind));
            if (worldRoot == null)
                return null;

            var entities = new List<EveUnityPlayableWorldEntity>();
            foreach (var component in Flatten(worldRoot))
            {
                if (string.Equals(component.Kind, "world.entity3d", StringComparison.Ordinal))
                    entities.Add(BuildPlayableEntity(component));
            }

            return new EveUnityPlayableWorldProjection(
                worldRoot.Id,
                worldRoot.GetProp("statePointerId", worldRoot.GetProp("worldStatePointerId")),
                worldRoot.GetProp("assetManifest", worldRoot.GetProp("assetManifestUri")),
                worldRoot.GetProp("inputProfile"),
                worldRoot.GetProp("cameraRig"),
                worldRoot.GetProp("viewId"),
                worldRoot.GetProp("playerEntityId"),
                worldRoot.GetProp("movementCommand"),
                worldRoot.GetProp("focusCommand"),
                worldRoot.GetProp("targetCommand"),
                worldRoot.GetProp("actionCommand"),
                entities,
                worldRoot.GetProp("entityViewPointerId"),
                worldRoot.GetProp("entityViewSchema"),
                worldRoot.GetProp("zoneRenderPointerId"),
                worldRoot.GetProp("zoneRenderSchema"),
                ParseStringList(worldRoot.GetProp("excludedRenderChannels")),
                worldRoot.GetProp("cameraTargetEntityId"),
                ParseFloat(worldRoot.GetProp("cameraDistance"), 0f),
                ParseFloat(worldRoot.GetProp("cameraVerticalFieldOfViewDegrees"), 0f),
                ParseFloat(worldRoot.GetProp("cameraTargetScreenX"), 0.5f),
                ParseFloat(worldRoot.GetProp("cameraTargetScreenY"), 0.5f),
                ParseFloat(worldRoot.GetProp("cameraPositionDamping"), 0f),
                ParseVector3(worldRoot.GetProp("ambientLightColor")),
                ParseFloat(worldRoot.GetProp("ambientLightIntensity"), 1f),
                ParseFloat(worldRoot.GetProp("cameraNearClipPlane"), 0f),
                ParseFloat(worldRoot.GetProp("cameraFarClipPlane"), 0f),
                worldRoot.GetProp("lookCommand"),
                ParseFloat(worldRoot.GetProp("lookSensitivityRadians"), 0f),
                worldRoot.GetProp("lookModel"),
                worldRoot.GetProp("skyboxAssetRef"),
                worldRoot.GetProp("reflectionAssetRef"),
                ParseFloat(worldRoot.GetProp("reflectionIntensity"), 1f),
                ParseVector3(worldRoot.GetProp("keyLightDirection")),
                ParseVector3(worldRoot.GetProp("keyLightColor")),
                ParseFloat(worldRoot.GetProp("keyLightIntensity"), 0f));
        }

        private static EveUnityPlayableWorldEntity BuildPlayableEntity(EveSurfaceComponent component)
        {
            var position = ParseVector3(component.GetProp("position"));
            return new EveUnityPlayableWorldEntity(
                component.Id,
                component.GetProp("entityId", component.Id),
                component.GetProp("entityKind", component.GetProp("kind")),
                component.GetProp("label", component.GetProp("name")),
                component.GetProp("faction"),
                component.GetProp("assetRef", component.GetProp("meshRef", component.GetProp("prefabRef"))),
                position.x,
                position.y,
                position.z,
                ParseFloat(component.GetProp("rotationY", component.GetProp("yaw")), 0f),
                ParseFloat(component.GetProp("radius"), 0f),
                ParseBoolean(component.GetProp("selectable")),
                ParseBoolean(component.GetProp("controllable")),
                component.GetProp("focusCommand"),
                component.GetProp("moveCommand"),
                component.GetProp("targetCommand"),
                component.GetProp("actionCommand"),
                component.Props);
        }

        private static EveSurfaceComponent? FindFirst(
            EveSurfaceComponent component,
            Predicate<EveSurfaceComponent> predicate)
        {
            if (predicate(component))
                return component;
            foreach (var child in component.Children)
            {
                var found = FindFirst(child, predicate);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static IEnumerable<EveSurfaceComponent> Flatten(EveSurfaceComponent component)
        {
            yield return component;
            foreach (var child in component.Children)
            {
                foreach (var nested in Flatten(child))
                    yield return nested;
            }
        }

        private static (float x, float y, float z) ParseVector3(string value)
        {
            var parts = (value ?? "").Split(',');
            return (
                parts.Length > 0 ? ParseFloat(parts[0], 0f) : 0f,
                parts.Length > 1 ? ParseFloat(parts[1], 0f) : 0f,
                parts.Length > 2 ? ParseFloat(parts[2], 0f) : 0f);
        }

        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static bool ParseBoolean(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> ParseStringList(string value)
        {
            return (value ?? "")
                .Split(',')
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static EveUnityScenePluginProjection? BuildPluginProjection(EveSurfaceComponent component)
        {
            if (SaiVisualNovelAdapter.CanProject(component))
                return SaiVisualNovelAdapter.Project(component);
            if (NornGraphAdapter.CanProject(component))
                return NornGraphAdapter.Project(component);
            if (TeXMathAdapter.CanProject(component))
                return TeXMathAdapter.Project(component);
            return null;
        }

        private static IReadOnlyList<EveUnitySceneEmbeddedDocumentSlot> BuildEmbeddedDocumentSlots(
            IReadOnlyList<EveEmbeddedDocumentSlot> embeddedDocuments)
        {
            var slots = new List<EveUnitySceneEmbeddedDocumentSlot>(embeddedDocuments.Count);
            foreach (var slot in embeddedDocuments)
            {
                slots.Add(new EveUnitySceneEmbeddedDocumentSlot(
                    slot.SlotId,
                    slot.DocumentId,
                    slot.SchemaId,
                    slot.PresentationKind));
            }
            return slots;
        }

        private static string SceneObjectKind(string componentKind)
        {
            if (string.IsNullOrWhiteSpace(componentKind))
                return "empty";
            if (IsWorldScene(componentKind))
                return "playable-world-root";
            if (string.Equals(componentKind, "world.entity3d", StringComparison.Ordinal))
                return "playable-world-entity";
            if (string.Equals(componentKind, "field.vector3d", StringComparison.Ordinal) ||
                string.Equals(componentKind, "field.scalar3d", StringComparison.Ordinal))
                return "world-field-3d";
            if (string.Equals(componentKind, "vn.stage", StringComparison.Ordinal))
                return "sai-vn-scene-stage";
            if (string.Equals(componentKind, "panel.dialogue", StringComparison.Ordinal) ||
                string.Equals(componentKind, "text.dialogue", StringComparison.Ordinal))
                return "sai-vn-scene-dialogue";
            if (string.Equals(componentKind, "rail.actions", StringComparison.Ordinal))
                return "sai-vn-scene-action-rail";
            if (componentKind.StartsWith("control.", StringComparison.Ordinal))
                return "command-control";
            if (string.Equals(componentKind, "embed.norn", StringComparison.Ordinal))
                return "norn-graph-scene-projection";
            if (string.Equals(componentKind, "embed.tex", StringComparison.Ordinal))
                return "tex-math-scene-projection";
            if (componentKind.StartsWith("embed.", StringComparison.Ordinal))
                return "plugin-placeholder";
            if (string.Equals(componentKind, "surface.slot", StringComparison.Ordinal))
                return "embedded-surface-slot";
            if (componentKind.StartsWith("world.", StringComparison.Ordinal) ||
                componentKind.StartsWith("field.", StringComparison.Ordinal))
                return "world-projection-node";
            if (componentKind.StartsWith("text.", StringComparison.Ordinal) || string.Equals(componentKind, "label", StringComparison.Ordinal))
                return "scene-label";
            return "scene-node";
        }

        private static bool IsWorldScene(string componentKind)
        {
            return string.Equals(componentKind, "world.scene3d", StringComparison.Ordinal) ||
                   string.Equals(componentKind, "world.scene2d", StringComparison.Ordinal);
        }
    }

    public sealed class EveUnitySceneProjection
    {
        public EveUnitySceneProjection(
            string providerId,
            string surfaceId,
            string projectionKind,
            string commandBoundary,
            string receiptSchema,
            string ownership,
            EveUnityPlayableWorldProjection? playableWorld,
            EveUnitySceneNode root)
        {
            ProviderId = providerId ?? "";
            SurfaceId = surfaceId ?? "";
            ProjectionKind = projectionKind ?? "";
            CommandBoundary = commandBoundary ?? "";
            ReceiptSchema = receiptSchema ?? "";
            Ownership = ownership ?? "";
            PlayableWorld = playableWorld;
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string ProviderId { get; }

        public string SurfaceId { get; }

        public string ProjectionKind { get; }

        public string CommandBoundary { get; }

        public string ReceiptSchema { get; }

        public string Ownership { get; }

        public EveUnityPlayableWorldProjection? PlayableWorld { get; }

        public EveUnitySceneNode Root { get; }
    }

    public sealed class EveUnityPlayableWorldProjection
    {
        public EveUnityPlayableWorldProjection(
            string worldRootId,
            string statePointerId,
            string assetManifest,
            string inputProfile,
            string cameraRig,
            string viewId,
            string playerEntityId,
            string movementCommand,
            string focusCommand,
            string targetCommand,
            string actionCommand,
            IReadOnlyList<EveUnityPlayableWorldEntity> entities,
            string entityViewPointerId = "",
            string entityViewSchema = "",
            string zoneRenderPointerId = "",
            string zoneRenderSchema = "",
            IReadOnlyList<string>? excludedRenderChannels = null,
            string cameraTargetEntityId = "",
            float cameraDistance = 0f,
            float cameraVerticalFieldOfViewDegrees = 0f,
            float cameraTargetScreenX = 0.5f,
            float cameraTargetScreenY = 0.5f,
            float cameraPositionDamping = 0f,
            (float r, float g, float b) ambientLightColor = default,
            float ambientLightIntensity = 1f,
            float cameraNearClipPlane = 0f,
            float cameraFarClipPlane = 0f,
            string lookCommand = "",
            float lookSensitivityRadians = 0f,
            string lookModel = "",
            string skyboxAssetRef = "",
            string reflectionAssetRef = "",
            float reflectionIntensity = 1f,
            (float x, float y, float z) keyLightDirection = default,
            (float r, float g, float b) keyLightColor = default,
            float keyLightIntensity = 0f)
        {
            WorldRootId = worldRootId ?? "";
            StatePointerId = statePointerId ?? "";
            AssetManifest = assetManifest ?? "";
            InputProfile = inputProfile ?? "";
            CameraRig = cameraRig ?? "";
            ViewId = viewId ?? "";
            PlayerEntityId = playerEntityId ?? "";
            MovementCommand = movementCommand ?? "";
            FocusCommand = focusCommand ?? "";
            TargetCommand = targetCommand ?? "";
            ActionCommand = actionCommand ?? "";
            Entities = entities ?? Array.Empty<EveUnityPlayableWorldEntity>();
            EntityViewPointerId = entityViewPointerId ?? "";
            EntityViewSchema = entityViewSchema ?? "";
            ZoneRenderPointerId = zoneRenderPointerId ?? "";
            ZoneRenderSchema = zoneRenderSchema ?? "";
            ExcludedRenderChannels = excludedRenderChannels ?? Array.Empty<string>();
            CameraTargetEntityId = cameraTargetEntityId ?? "";
            CameraDistance = cameraDistance;
            CameraVerticalFieldOfViewDegrees = cameraVerticalFieldOfViewDegrees;
            CameraTargetScreenX = cameraTargetScreenX;
            CameraTargetScreenY = cameraTargetScreenY;
            CameraPositionDamping = cameraPositionDamping;
            AmbientLightR = ambientLightColor.r;
            AmbientLightG = ambientLightColor.g;
            AmbientLightB = ambientLightColor.b;
            AmbientLightIntensity = ambientLightIntensity;
            CameraNearClipPlane = cameraNearClipPlane;
            CameraFarClipPlane = cameraFarClipPlane;
            LookCommand = lookCommand ?? "";
            LookSensitivityRadians = lookSensitivityRadians;
            LookModel = lookModel ?? "";
            SkyboxAssetRef = skyboxAssetRef ?? "";
            ReflectionAssetRef = reflectionAssetRef ?? "";
            ReflectionIntensity = reflectionIntensity;
            KeyLightDirectionX = keyLightDirection.x;
            KeyLightDirectionY = keyLightDirection.y;
            KeyLightDirectionZ = keyLightDirection.z;
            KeyLightColorR = keyLightColor.r;
            KeyLightColorG = keyLightColor.g;
            KeyLightColorB = keyLightColor.b;
            KeyLightIntensity = keyLightIntensity;
        }

        public string WorldRootId { get; }

        public string StatePointerId { get; }

        public string EntityViewPointerId { get; }

        public string EntityViewSchema { get; }

        public string ZoneRenderPointerId { get; }

        public string ZoneRenderSchema { get; }

        public string AssetManifest { get; }

        public string InputProfile { get; }

        public string CameraRig { get; }

        public string CameraTargetEntityId { get; }

        public float CameraDistance { get; }

        public float CameraVerticalFieldOfViewDegrees { get; }

        public float CameraTargetScreenX { get; }

        public float CameraTargetScreenY { get; }

        public float CameraPositionDamping { get; }

        public float CameraNearClipPlane { get; }

        public float CameraFarClipPlane { get; }

        public float AmbientLightR { get; }

        public float AmbientLightG { get; }

        public float AmbientLightB { get; }

        public float AmbientLightIntensity { get; }

        public string ViewId { get; }

        public string PlayerEntityId { get; }

        public string MovementCommand { get; }

        public string LookCommand { get; }

        public float LookSensitivityRadians { get; }

        public string LookModel { get; }

        public string SkyboxAssetRef { get; }

        public string ReflectionAssetRef { get; }

        public float ReflectionIntensity { get; }

        public float KeyLightDirectionX { get; }

        public float KeyLightDirectionY { get; }

        public float KeyLightDirectionZ { get; }

        public float KeyLightColorR { get; }

        public float KeyLightColorG { get; }

        public float KeyLightColorB { get; }

        public float KeyLightIntensity { get; }

        public string FocusCommand { get; }

        public string TargetCommand { get; }

        public string ActionCommand { get; }

        public IReadOnlyList<string> ExcludedRenderChannels { get; }

        public int EntityCount => Entities.Count;

        public IReadOnlyList<EveUnityPlayableWorldEntity> Entities { get; }
    }

    public sealed class EveUnityPlayableWorldEntity
    {
        public EveUnityPlayableWorldEntity(
            string nodeId,
            string entityId,
            string entityKind,
            string label,
            string faction,
            string assetRef,
            float positionX,
            float positionY,
            float positionZ,
            float rotationY,
            float radius,
            bool selectable,
            bool controllable,
            string focusCommand,
            string moveCommand,
            string targetCommand,
            string actionCommand,
            IReadOnlyDictionary<string, string>? props = null)
        {
            NodeId = nodeId ?? "";
            EntityId = entityId ?? "";
            EntityKind = entityKind ?? "";
            Label = label ?? "";
            Faction = faction ?? "";
            AssetRef = assetRef ?? "";
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            RotationY = rotationY;
            Radius = radius;
            Selectable = selectable;
            Controllable = controllable;
            FocusCommand = focusCommand ?? "";
            MoveCommand = moveCommand ?? "";
            TargetCommand = targetCommand ?? "";
            ActionCommand = actionCommand ?? "";
            Props = props ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public string NodeId { get; }

        public string EntityId { get; }

        public string EntityKind { get; }

        public string Label { get; }

        public string Faction { get; }

        public string AssetRef { get; }

        public float PositionX { get; }

        public float PositionY { get; }

        public float PositionZ { get; }

        public float RotationY { get; }

        public float Radius { get; }

        public bool Selectable { get; }

        public bool Controllable { get; }

        public string FocusCommand { get; }

        public string MoveCommand { get; }

        public string TargetCommand { get; }

        public string ActionCommand { get; }

        public IReadOnlyDictionary<string, string> Props { get; }
    }

    public sealed class EveUnitySceneNode
    {
        public EveUnitySceneNode(
            string id,
            string componentKind,
            string sceneObjectKind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyDictionary<string, string> layout,
            IReadOnlyDictionary<string, string> style,
            int stateBindingCount,
            int embeddedDocumentCount,
            IReadOnlyList<EveUnitySceneEmbeddedDocumentSlot> embeddedDocuments,
            EveUnityScenePluginProjection? pluginProjection,
            IReadOnlyList<EveUnitySceneNode> children)
        {
            Id = id ?? "";
            ComponentKind = componentKind ?? "";
            SceneObjectKind = sceneObjectKind ?? "";
            Props = props ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Layout = layout ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Style = style ?? new Dictionary<string, string>(StringComparer.Ordinal);
            StateBindingCount = stateBindingCount;
            EmbeddedDocumentCount = embeddedDocumentCount;
            EmbeddedDocuments = embeddedDocuments ?? Array.Empty<EveUnitySceneEmbeddedDocumentSlot>();
            PluginProjection = pluginProjection;
            Children = children ?? Array.Empty<EveUnitySceneNode>();
        }

        public string Id { get; }

        public string ComponentKind { get; }

        public string SceneObjectKind { get; }

        public IReadOnlyDictionary<string, string> Props { get; }

        public IReadOnlyDictionary<string, string> Layout { get; }

        public IReadOnlyDictionary<string, string> Style { get; }

        public int StateBindingCount { get; }

        public int EmbeddedDocumentCount { get; }

        public IReadOnlyList<EveUnitySceneEmbeddedDocumentSlot> EmbeddedDocuments { get; }

        public EveUnityScenePluginProjection? PluginProjection { get; }

        public IReadOnlyList<EveUnitySceneNode> Children { get; }
    }

    public sealed class EveUnityScenePluginProjection
    {
        public EveUnityScenePluginProjection(
            string pluginId,
            string projectionKind,
            string abiSchema,
            string commandBoundary,
            IReadOnlyList<string> capabilities,
            string command,
            string documentId,
            string semanticOwner)
        {
            PluginId = pluginId ?? "";
            ProjectionKind = projectionKind ?? "";
            AbiSchema = abiSchema ?? "";
            CommandBoundary = commandBoundary ?? "";
            Capabilities = capabilities ?? Array.Empty<string>();
            Command = command ?? "";
            DocumentId = documentId ?? "";
            SemanticOwner = semanticOwner ?? "";
        }

        public string PluginId { get; }

        public string ProjectionKind { get; }

        public string AbiSchema { get; }

        public string CommandBoundary { get; }

        public IReadOnlyList<string> Capabilities { get; }

        public string Command { get; }

        public string DocumentId { get; }

        public string SemanticOwner { get; }
    }

    public sealed class EveUnitySceneEmbeddedDocumentSlot
    {
        public EveUnitySceneEmbeddedDocumentSlot(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind)
        {
            SlotId = slotId ?? "";
            DocumentId = documentId ?? "";
            SchemaId = schemaId ?? "";
            PresentationKind = presentationKind ?? "";
        }

        public string SlotId { get; }

        public string DocumentId { get; }

        public string SchemaId { get; }

        public string PresentationKind { get; }
    }

    public sealed class EveUnitySceneProviderSurfaceAdvertisement
    {
        public EveUnitySceneProviderSurfaceAdvertisement(
            string surfaceId,
            string surfaceKind,
            EveUnitySceneWorldInteraction worldInteraction)
        {
            SurfaceId = surfaceId ?? "";
            SurfaceKind = surfaceKind ?? "";
            WorldInteraction = worldInteraction ?? throw new ArgumentNullException(nameof(worldInteraction));
        }

        public string SurfaceId { get; }

        public string SurfaceKind { get; }

        public EveUnitySceneWorldInteraction WorldInteraction { get; }
    }

    public sealed class EveUnitySceneWorldInteraction
    {
        public EveUnitySceneWorldInteraction(
            string projectionKind,
            string commandBoundary,
            string receiptSchema,
            string ownership)
        {
            ProjectionKind = projectionKind ?? "";
            CommandBoundary = commandBoundary ?? "";
            ReceiptSchema = receiptSchema ?? "";
            Ownership = ownership ?? "";
        }

        public string ProjectionKind { get; }

        public string CommandBoundary { get; }

        public string ReceiptSchema { get; }

        public string Ownership { get; }
    }
}
