using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.Surface
{
    public static class EveInventoryInteraction
    {
        public const string GridKind = "inventory.grid";
        public const string ItemKind = "inventory.item";
        public const string DragSessionKind = "inventory.drag_session";

        public static bool TryCreateDropRequest(
            EveSurfaceDocument document,
            EveSurfaceComponent source,
            EveSurfaceComponent target,
            int destinationX,
            int destinationY,
            string clientId,
            out EveSurfaceCommandRequest? request)
        {
            request = null;
            if (document == null || source == null || target == null ||
                !string.Equals(source.Kind, ItemKind, StringComparison.Ordinal) ||
                !string.Equals(target.Kind, GridKind, StringComparison.Ordinal))
            {
                return false;
            }

            var sourceKind = source.GetProp("sourceKind", source.GetProp("source"));
            var command = target.GetProp($"dropCommand.{sourceKind}", target.GetProp("dropCommand"));
            if (string.IsNullOrWhiteSpace(sourceKind) || string.IsNullOrWhiteSpace(command))
                return false;

            var sourceIndex = Int(source.GetProp("sourceIndex"), -1);
            var targetIndex = Int(target.GetProp("targetIndex"), -1);
            var targetKind = target.GetProp("targetKind");
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceKind"] = sourceKind,
                ["originEntityKey"] = source.GetProp("sourceEntityKey", source.GetProp("entityKey")),
                ["originIndex"] = sourceIndex.ToString(CultureInfo.InvariantCulture),
                ["originCargoIndex"] = (string.Equals(sourceKind, "cargo", StringComparison.Ordinal) ? sourceIndex : -1)
                    .ToString(CultureInfo.InvariantCulture),
                ["itemKey"] = source.GetProp("itemKey"),
                ["quantity"] = Math.Max(1, Int(source.GetProp("quantity"), 1)).ToString(CultureInfo.InvariantCulture),
                ["sourceX"] = Int(source.GetProp("x"), int.MinValue).ToString(CultureInfo.InvariantCulture),
                ["sourceY"] = Int(source.GetProp("y"), int.MinValue).ToString(CultureInfo.InvariantCulture),
                ["destinationKind"] = targetKind,
                ["destinationEntityKey"] = target.GetProp("targetEntityKey", target.GetProp("entityKey")),
                ["destinationIndex"] = targetIndex.ToString(CultureInfo.InvariantCulture),
                ["destinationCargoIndex"] = (string.Equals(targetKind, "cargo", StringComparison.Ordinal) ? targetIndex : -1)
                    .ToString(CultureInfo.InvariantCulture),
                ["destinationX"] = destinationX.ToString(CultureInfo.InvariantCulture),
                ["destinationY"] = destinationY.ToString(CultureInfo.InvariantCulture),
                ["hasDestinationPosition"] = "true"
            };
            var template = document.Commands.FirstOrDefault(candidate =>
                string.Equals(candidate.Command, command, StringComparison.Ordinal));
            var operation = template == null
                ? CultMesh.OperationInvocation(command)
                : CultMesh.OperationInvocation(template.Operation);
            request = new EveSurfaceCommandRequest(
                document.ProviderId,
                document.Surface.Id,
                operation,
                CultMesh.OperationPayload(payload),
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(clientId) ? "unity-uitoolkit" : clientId);
            return true;
        }

        private static int Int(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }
}
