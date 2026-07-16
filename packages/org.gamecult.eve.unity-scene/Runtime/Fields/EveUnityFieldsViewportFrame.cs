using System;
using System.Collections.Generic;
using System.Globalization;
using GameCult.Eve.PluginFields;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Fields
{
    public static class EveUnityFieldsViewportFrame
    {
        public static bool TryResolve(
            EveFieldsSplatsDocument source,
            IReadOnlyDictionary<string, string> props,
            Vector2 cameraPosition,
            int snapTextureWidth,
            int snapTextureHeight,
            out EveFieldsSplatsDocument projected,
            out Vector2 center)
        {
            projected = source;
            center = default;
            if (source == null || source.Viewport == null) return false;

            var width = (float)(source.Viewport.MaxX - source.Viewport.MinX);
            var height = (float)(source.Viewport.MaxY - source.Viewport.MinY);
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
                return false;

            center = new Vector2(
                (float)((source.Viewport.MinX + source.Viewport.MaxX) * 0.5),
                (float)((source.Viewport.MinY + source.Viewport.MaxY) * 0.5));
            if (!string.Equals(Prop(props, "viewportAnchor"), "active-camera.xz", StringComparison.Ordinal))
                return true;

            var span = PositiveInt(props, "span");
            var cellWorldSize = PositiveFloat(props, "cellWorldSize");
            var snapTexels = PositiveInt(props, "viewportSnapTexels");
            if (!TryValidateSpatialLattice(
                    width, height, snapTextureWidth, snapTextureHeight,
                    span, cellWorldSize, snapTexels))
                return false;

            center.x = SnapCameraCoordinate(cameraPosition.x, width, snapTextureWidth, snapTexels);
            center.y = SnapCameraCoordinate(cameraPosition.y, height, snapTextureHeight, snapTexels);
            projected = Project(source, center, width, height);
            return true;
        }

        public static float SnapCameraCoordinate(
            float coordinate,
            float viewportWidth,
            int snapTargetWidth,
            float snapTexels)
        {
            if (!float.IsFinite(coordinate) || !float.IsFinite(viewportWidth) ||
                !float.IsFinite(snapTexels) || viewportWidth <= 0f ||
                snapTargetWidth <= 0 || snapTexels <= 0f)
                throw new ArgumentOutOfRangeException(nameof(viewportWidth));
            var snapLength = viewportWidth * snapTexels / snapTargetWidth;
            return (int)(coordinate / snapLength) * snapLength;
        }

        public static bool TryValidateSpatialLattice(
            float viewportWidth,
            float viewportHeight,
            int gravityTextureWidth,
            int gravityTextureHeight,
            int span,
            float cellWorldSize,
            int gravityTexelsPerCell)
        {
            if (!float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight) ||
                !float.IsFinite(cellWorldSize) || viewportWidth <= 0f || viewportHeight <= 0f ||
                gravityTextureWidth <= 0 || gravityTextureHeight <= 0 || span <= 0 ||
                cellWorldSize <= 0f || gravityTexelsPerCell <= 0)
                return false;
            var expectedExtent = span * cellWorldSize;
            var expectedCellFromX = viewportWidth * gravityTexelsPerCell / gravityTextureWidth;
            var expectedCellFromY = viewportHeight * gravityTexelsPerCell / gravityTextureHeight;
            const float tolerance = 0.0001f;
            return Mathf.Abs(viewportWidth - expectedExtent) <= tolerance &&
                   Mathf.Abs(viewportHeight - expectedExtent) <= tolerance &&
                   Mathf.Abs(expectedCellFromX - cellWorldSize) <= tolerance &&
                   Mathf.Abs(expectedCellFromY - cellWorldSize) <= tolerance;
        }

        private static EveFieldsSplatsDocument Project(
            EveFieldsSplatsDocument source,
            Vector2 center,
            float width,
            float height) =>
            new EveFieldsSplatsDocument
            {
                Schema = source.Schema,
                FrameId = source.FrameId,
                PublishedAtUtc = source.PublishedAtUtc,
                SimulationTimeSeconds = source.SimulationTimeSeconds,
                RunId = source.RunId,
                ZoneIndex = source.ZoneIndex,
                ZoneName = source.ZoneName,
                Viewport = new EveFieldsViewport
                {
                    MinX = center.x - width * 0.5,
                    MinY = center.y - height * 0.5,
                    MaxX = center.x + width * 0.5,
                    MaxY = center.y + height * 0.5
                },
                Layers = source.Layers,
                Splats = source.Splats
            };

        private static string Prop(IReadOnlyDictionary<string, string> props, string key) =>
            props != null && props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static int PositiveInt(IReadOnlyDictionary<string, string> props, string key) =>
            int.TryParse(Prop(props, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 0;

        private static float PositiveFloat(IReadOnlyDictionary<string, string> props, string key) =>
            float.TryParse(Prop(props, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            float.IsFinite(value) && value > 0f
                ? value
                : 0f;
    }
}
