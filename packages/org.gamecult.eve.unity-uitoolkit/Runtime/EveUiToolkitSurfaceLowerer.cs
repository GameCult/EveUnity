using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.Surface;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class EveUiToolkitSurfaceLowerer
    {
        private readonly EveUiToolkitSurfaceOptions _options;
        private EveSurfaceComponent? _inventoryDragSource;
        private Vector2 _inventoryDragStart;
        private bool _inventoryPointerMoved;
        private bool _suppressInventoryClick;

        public EveUiToolkitSurfaceLowerer(EveUiToolkitSurfaceOptions? options = null)
        {
            _options = options ?? EveUiToolkitSurfaceOptions.Default;
        }

        public VisualElement Lower(EveSurfaceDocument document, Action<EveSurfaceCommandRequest>? commandSink = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var root = LowerComponent(document.Surface.Root, document, commandSink);
            root.name = string.IsNullOrWhiteSpace(root.name) ? document.Surface.Id : root.name;
            root.AddToClassList("eve-surface-root");
            ApplyStyleTokens(root, document.Surface.Styles);
            return root;
        }

        private VisualElement LowerComponent(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var element = CreateElement(component, document, commandSink);
            element.name = SafeName(component.Id);
            element.AddToClassList("eve-component");
            element.AddToClassList($"eve-kind-{SafeClass(component.Kind)}");
            element.userData = component;
            ApplyGeneratedLayout(element, component);

            if (IsHidden(component) || IsExternalProjectionRoot(component.Kind))
                return element;

            foreach (var child in component.Children)
            {
                var loweredChild = LowerComponent(child, document, commandSink);
                PositionInventoryChild(component, child, loweredChild);
                element.Add(loweredChild);
            }

            foreach (var slot in component.EmbeddedDocuments)
            {
                var nested = LowerEmbeddedDocument(slot, commandSink);
                if (nested != null)
                    element.Add(nested);
            }

            return element;
        }

        private VisualElement? LowerEmbeddedDocument(
            EveEmbeddedDocumentSlot slot,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var document = _options.EmbeddedDocumentResolver?.Invoke(slot);
            if (document == null)
                return null;

            var element = LowerComponent(document.Surface.Root, document, commandSink);
            element.name = SafeName(string.IsNullOrWhiteSpace(slot.SlotId) ? document.Surface.Id : slot.SlotId);
            element.AddToClassList("eve-embedded-document");
            element.AddToClassList($"eve-embedded-kind-{SafeClass(slot.PresentationKind)}");
            ApplyStyleTokens(element, document.Surface.Styles);
            return element;
        }

        private VisualElement CreateElement(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var projectionAdapter = _options.FindPluginProjectionAdapter(component);
            if (projectionAdapter != null)
                return projectionAdapter.Lower(component, document, commandSink);

            switch (NormalizeKind(component.Kind))
            {
                case "surface":
                {
                    var element = new VisualElement();
                    element.style.flexGrow = 1;
                    element.style.flexDirection = FlexDirection.Column;
                    return element;
                }
                case "grid":
                {
                    var element = new VisualElement();
                    element.style.flexDirection = FlexDirection.Row;
                    element.style.flexWrap = Wrap.Wrap;
                    element.style.alignItems = Align.Stretch;
                    return element;
                }
                case EveInventoryInteraction.GridKind:
                    return InventoryGrid(component, document, commandSink);
                case EveInventoryInteraction.ItemKind:
                    return InventoryItem(component, document, commandSink);
                case EveInventoryInteraction.DragSessionKind:
                    return InventoryDragSession(component);
                case "partition":
                {
                    var element = new VisualElement();
                    element.style.flexDirection = component.GetProp("split") == "x"
                        ? FlexDirection.Row
                        : FlexDirection.Column;
                    element.style.flexWrap = Wrap.Wrap;
                    return element;
                }
                case "pane":
                case "modal":
                case "card":
                {
                    var card = new VisualElement();
                    card.AddToClassList("eve-card");
                    card.style.flexDirection = FlexDirection.Column;
                    var title = component.GetProp("title");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var titleLabel = TitleLabel(title);
                        titleLabel.name = "title";
                        card.Add(titleLabel);
                    }
                    return card;
                }
                case "metric":
                {
                    var metric = new VisualElement();
                    metric.AddToClassList("eve-metric");
                    metric.style.flexDirection = FlexDirection.Column;
                    var label = MutedLabel(component.GetProp("label"));
                    label.name = "label";
                    metric.Add(label);
                    var value = ValueLabel(component.GetProp("value"));
                    value.name = "value";
                    metric.Add(value);
                    return metric;
                }
                case "progress":
                {
                    var progress = new ProgressBar
                    {
                        title = component.GetProp("label"),
                        lowValue = 0f,
                        highValue = 1f,
                        value = ParseRatio(component.GetProp("ratio", component.GetProp("value")))
                    };
                    progress.AddToClassList("eve-progress");
                    return progress;
                }
                case "options":
                {
                    var options = new VisualElement();
                    options.AddToClassList("eve-options");
                    options.style.flexDirection = FlexDirection.Column;
                    var label = component.GetProp("label");
                    if (!string.IsNullOrWhiteSpace(label))
                        options.Add(TitleLabel(label));
                    return options;
                }
                case "inspector.kv":
                {
                    var inspector = new VisualElement();
                    inspector.AddToClassList("eve-inspector");
                    inspector.style.flexDirection = FlexDirection.Column;
                    return inspector;
                }
                case "row":
                {
                    var row = new VisualElement();
                    row.AddToClassList("eve-row");
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.flexWrap = Wrap.Wrap;
                    foreach (var prop in component.Props)
                        row.Add(FieldLabel(prop.Key, prop.Value));
                    return row;
                }
                case "text":
                {
                    return BodyLabel(component.GetProp("text", component.GetProp("value", component.GetProp("title"))));
                }
                case "control.button":
                case "control.popup":
                {
                    var label = component.GetProp("label", component.GetProp("title", "Invoke"));
                    var command = component.GetProp("command", component.GetProp("action", "invoke"));
                    return new Button(() => EmitCommand(document, component, command, commandSink)) { text = label };
                }
                case "control.text":
                case "control.input.text":
                {
                    var label = component.GetProp("label", component.GetProp("title", "Value"));
                    var command = component.GetProp("command", component.GetProp("action", "set"));
                    var value = component.GetProp("value");
                    var field = new TextField(label);
                    field.SetValueWithoutNotify(value);
                    field.RegisterValueChangedCallback(evt =>
                    {
                        if (string.Equals(evt.newValue, value, StringComparison.Ordinal))
                            return;

                        var payload = new Dictionary<string, string>(component.Props, StringComparer.Ordinal)
                        {
                            ["value"] = evt.newValue ?? ""
                        };
                        EmitCommand(document, component, command, GameCult.Mesh.CultMesh.OperationPayload(payload), commandSink);
                    });
                    return field;
                }
                default:
                {
                    var element = new VisualElement();
                    element.AddToClassList("eve-unknown");
                    var title = component.GetProp("title", component.GetProp("label"));
                    if (!string.IsNullOrWhiteSpace(title))
                        element.Add(BodyLabel(title));
                    return element;
                }
            }
        }

        private VisualElement InventoryGrid(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var grid = new VisualElement();
            grid.AddToClassList("eve-inventory-grid");
            grid.style.position = Position.Relative;
            var columns = Math.Max(1, ParseInt(component.GetProp("columns"), 6));
            var rows = Math.Max(1, ParseInt(component.GetProp("rows"), 3));
            var cellSize = Math.Max(1f, ParseNumber(component.GetProp("cellSize"), 72f));
            var gap = Math.Max(0f, ParseNumber(component.GetProp("cellGap"), 4f));
            grid.style.width = columns * cellSize + Math.Max(0, columns - 1) * gap;
            grid.style.height = rows * cellSize + Math.Max(0, rows - 1) * gap;
            for (var index = 0; index < Math.Min(columns * rows, 256); index++)
            {
                var cell = new VisualElement();
                cell.AddToClassList("eve-inventory-cell");
                cell.style.position = Position.Absolute;
                cell.style.left = (index % columns) * (cellSize + gap);
                cell.style.top = (index / columns) * (cellSize + gap);
                cell.style.width = cellSize;
                cell.style.height = cellSize;
                grid.Add(cell);
            }
            grid.RegisterCallback<ClickEvent>(evt =>
            {
                if (_inventoryDragSource == null || _suppressInventoryClick)
                    return;
                if (TryEmitInventoryDrop(document, _inventoryDragSource, component, grid, evt.position, commandSink))
                    _inventoryDragSource = null;
            });
            return grid;
        }

        private VisualElement InventoryItem(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var item = new Button { text = component.GetProp("label", component.GetProp("itemKey")) };
            item.AddToClassList("eve-inventory-item");
            item.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (!ParseBool(component.GetProp("draggable", "true")))
                    return;
                _inventoryDragSource = component;
                _inventoryDragStart = evt.position;
                _inventoryPointerMoved = false;
                item.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            item.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_inventoryDragSource != component)
                    return;
                var pointerPosition = new Vector2(evt.position.x, evt.position.y);
                if ((pointerPosition - _inventoryDragStart).sqrMagnitude > 16f)
                    _inventoryPointerMoved = true;
            });
            item.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_inventoryDragSource != component)
                    return;
                item.ReleasePointer(evt.pointerId);
                if (_inventoryPointerMoved)
                {
                    var picked = item.panel?.Pick(evt.position);
                    var targetElement = InventoryGridAncestor(picked);
                    if (targetElement?.userData is EveSurfaceComponent target)
                        TryEmitInventoryDrop(document, component, target, targetElement, evt.position, commandSink);
                    _inventoryDragSource = null;
                    _suppressInventoryClick = true;
                }
                evt.StopPropagation();
            });
            item.RegisterCallback<ClickEvent>(evt =>
            {
                if (_suppressInventoryClick)
                {
                    _suppressInventoryClick = false;
                    evt.StopPropagation();
                    return;
                }
                _inventoryDragSource = component;
                evt.StopPropagation();
            });
            return item;
        }

        private static VisualElement InventoryDragSession(EveSurfaceComponent component)
        {
            var panel = new VisualElement();
            panel.AddToClassList("eve-inventory-drag-session");
            var active = ParseBool(component.GetProp("active"));
            panel.Add(BodyLabel(active
                ? component.GetProp("itemKey", "Dragging")
                : "No active drag"));
            return panel;
        }

        private static void PositionInventoryChild(
            EveSurfaceComponent parent,
            EveSurfaceComponent child,
            VisualElement element)
        {
            if (!string.Equals(parent.Kind, EveInventoryInteraction.GridKind, StringComparison.Ordinal) ||
                !string.Equals(child.Kind, EveInventoryInteraction.ItemKind, StringComparison.Ordinal))
                return;
            var cellSize = Math.Max(1f, ParseNumber(parent.GetProp("cellSize"), 72f));
            var gap = Math.Max(0f, ParseNumber(parent.GetProp("cellGap"), 4f));
            var widthCells = Math.Max(1, ParseInt(child.GetProp("shapeWidth"), 1));
            var heightCells = Math.Max(1, ParseInt(child.GetProp("shapeHeight"), 1));
            var rotation = child.GetProp("rotation");
            if (string.Equals(rotation, "Clockwise", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rotation, "CounterClockwise", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rotation, "Right", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rotation, "Left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rotation, "Rotate90", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rotation, "Rotate270", StringComparison.OrdinalIgnoreCase))
            {
                (widthCells, heightCells) = (heightCells, widthCells);
            }
            element.style.position = Position.Absolute;
            element.style.left = Math.Max(0, ParseInt(child.GetProp("x"), 0)) * (cellSize + gap);
            element.style.top = Math.Max(0, ParseInt(child.GetProp("y"), 0)) * (cellSize + gap);
            element.style.width = widthCells * cellSize + Math.Max(0, widthCells - 1) * gap;
            element.style.height = heightCells * cellSize + Math.Max(0, heightCells - 1) * gap;
        }

        private static VisualElement? InventoryGridAncestor(VisualElement? element)
        {
            while (element != null)
            {
                if (element.userData is EveSurfaceComponent component &&
                    string.Equals(component.Kind, EveInventoryInteraction.GridKind, StringComparison.Ordinal))
                    return element;
                element = element.parent;
            }
            return null;
        }

        private static bool TryEmitInventoryDrop(
            EveSurfaceDocument document,
            EveSurfaceComponent source,
            EveSurfaceComponent target,
            VisualElement targetElement,
            Vector2 panelPosition,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            if (commandSink == null)
                return false;
            var local = targetElement.WorldToLocal(panelPosition);
            var pitch = Math.Max(1f, ParseNumber(target.GetProp("cellSize"), 72f) +
                Math.Max(0f, ParseNumber(target.GetProp("cellGap"), 4f)));
            var x = Math.Max(0, (int)Math.Floor(local.x / pitch));
            var y = Math.Max(0, (int)Math.Floor(local.y / pitch));
            if (!EveInventoryInteraction.TryCreateDropRequest(
                    document, source, target, x, y, "unity-uitoolkit", out var request) || request == null)
                return false;
            commandSink(request);
            return true;
        }

        private static int ParseInt(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static bool ParseBool(string value) =>
            bool.TryParse(value, out var parsed) && parsed;

        private static float ParseNumber(string value, float fallback) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static void EmitCommand(
            EveSurfaceDocument document,
            EveSurfaceComponent component,
            string command,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            EmitCommand(
                document,
                component,
                command,
                GameCult.Mesh.CultMesh.OperationPayload(component.Props),
                commandSink);
        }

        private static void EmitCommand(
            EveSurfaceDocument document,
            EveSurfaceComponent component,
            string command,
            GameCult.Mesh.CultMeshOperationPayload payload,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            if (commandSink == null || string.IsNullOrWhiteSpace(command))
                return;

            commandSink(new EveSurfaceCommandRequest(
                document.ProviderId,
                document.Surface.Id,
                ResolveOperation(document, command),
                payload,
                DateTimeOffset.UtcNow,
                "unity-uitoolkit"));
        }

        private static GameCult.Mesh.CultMeshOperationInvocationDescriptor ResolveOperation(
            EveSurfaceDocument document,
            string command)
        {
            foreach (var template in document.Commands)
            {
                if (string.Equals(template.Command, command, StringComparison.Ordinal))
                    return GameCult.Mesh.CultMesh.OperationInvocation(template.Operation);
            }

            return GameCult.Mesh.CultMesh.OperationInvocation(command);
        }

        private static Label TitleLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("eve-title");
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        private static Label BodyLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("eve-text");
            return label;
        }

        private static Label MutedLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("eve-muted");
            return label;
        }

        private static Label ValueLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("eve-value");
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        private static VisualElement FieldLabel(string key, string value)
        {
            var field = new VisualElement();
            field.AddToClassList("eve-field");
            field.style.flexDirection = FlexDirection.Row;
            field.Add(MutedLabel(key));
            field.Add(BodyLabel(value));
            return field;
        }

        private static void ApplyStyleTokens(VisualElement root, IReadOnlyList<EveStyleToken> tokens)
        {
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token.Name) || string.IsNullOrWhiteSpace(token.Value))
                    continue;

                root.AddToClassList($"eve-style-token-{SafeClass(token.Name)}");
            }
        }

        private static void ApplyGeneratedLayout(VisualElement element, EveSurfaceComponent component)
        {
            var layout = component.Layout ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var style = component.Style ?? new Dictionary<string, string>(StringComparer.Ordinal);

            if (TryGet(layout, "display", out var display))
                element.style.display = string.Equals(display, "none", StringComparison.OrdinalIgnoreCase)
                    ? DisplayStyle.None
                    : DisplayStyle.Flex;
            if (TryGet(layout, "position", out var position))
                element.style.position = string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase)
                    ? Position.Absolute
                    : Position.Relative;
            if (TryGet(layout, "inset", out var inset))
            {
                var value = ParseLength(inset);
                element.style.top = value;
                element.style.right = value;
                element.style.bottom = value;
                element.style.left = value;
            }
            if (TryGet(layout, "top", out var top))
                element.style.top = ParseLength(top);
            if (TryGet(layout, "right", out var right))
                element.style.right = ParseLength(right);
            if (TryGet(layout, "bottom", out var bottom))
                element.style.bottom = ParseLength(bottom);
            if (TryGet(layout, "left", out var left))
                element.style.left = ParseLength(left);
            if (TryGet(layout, "direction", out var direction))
            {
                if (string.Equals(direction, "horizontal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(direction, "row", StringComparison.OrdinalIgnoreCase))
                    element.style.flexDirection = FlexDirection.Row;
                else if (string.Equals(direction, "vertical", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(direction, "column", StringComparison.OrdinalIgnoreCase))
                    element.style.flexDirection = FlexDirection.Column;
            }
            if (TryGet(layout, "flexWrap", out var flexWrap))
                element.style.flexWrap = string.Equals(flexWrap, "wrap", StringComparison.OrdinalIgnoreCase) ? Wrap.Wrap : Wrap.NoWrap;
            if (TryGet(layout, "alignItems", out var alignItems) && TryParseAlign(alignItems, out var align))
                element.style.alignItems = align;
            if (TryGet(layout, "justifyContent", out var justifyContent) && TryParseJustify(justifyContent, out var justify))
                element.style.justifyContent = justify;
            if (TryGet(layout, "width", out var width))
                element.style.width = ParseLength(width);
            if (TryGet(layout, "minWidth", out var minWidth))
                element.style.minWidth = ParseLength(minWidth);
            if (TryGet(layout, "maxWidth", out var maxWidth))
                element.style.maxWidth = ParseLength(maxWidth);
            if (TryGet(layout, "height", out var height))
                element.style.height = ParseLength(height);
            if (TryGet(layout, "minHeight", out var minHeight))
                element.style.minHeight = ParseLength(minHeight);
            if (TryGet(layout, "maxHeight", out var maxHeight))
                element.style.maxHeight = ParseLength(maxHeight);
            if (TryGet(layout, "padding", out var padding))
                ApplyBoxLength(padding, value =>
                {
                    element.style.paddingTop = value;
                    element.style.paddingRight = value;
                    element.style.paddingBottom = value;
                    element.style.paddingLeft = value;
                });
            if (TryGet(layout, "margin", out var margin))
                ApplyBoxLength(margin, value =>
                {
                    element.style.marginTop = value;
                    element.style.marginRight = value;
                    element.style.marginBottom = value;
                    element.style.marginLeft = value;
                });
            if (TryGet(layout, "overflow", out var overflow))
                element.style.overflow = string.Equals(overflow, "hidden", StringComparison.OrdinalIgnoreCase)
                    ? Overflow.Hidden
                    : Overflow.Visible;

            if (TryGet(style, "background", out var background) && TryParseColor(background, out var backgroundColor))
                element.style.backgroundColor = backgroundColor;
            if (TryGet(style, "color", out var color) && TryParseColor(color, out var textColor))
                element.style.color = textColor;
            if (TryGet(style, "borderWidth", out var borderWidth))
                ApplyBoxFloat(borderWidth, value =>
                {
                    element.style.borderTopWidth = value;
                    element.style.borderRightWidth = value;
                    element.style.borderBottomWidth = value;
                    element.style.borderLeftWidth = value;
                });
            if (TryGet(style, "borderColor", out var borderColor) && TryParseColor(borderColor, out var border))
            {
                element.style.borderTopColor = border;
                element.style.borderRightColor = border;
                element.style.borderBottomColor = border;
                element.style.borderLeftColor = border;
            }
            if (TryGet(style, "borderRadius", out var borderRadius))
                ApplyBoxLength(borderRadius, value =>
                {
                    element.style.borderTopLeftRadius = value;
                    element.style.borderTopRightRadius = value;
                    element.style.borderBottomRightRadius = value;
                    element.style.borderBottomLeftRadius = value;
                });
            if (TryGet(style, "fontSize", out var fontSize))
                element.style.fontSize = ParseLength(fontSize);
            if (TryGet(style, "fontWeight", out var fontWeight) &&
                (string.Equals(fontWeight, "bold", StringComparison.OrdinalIgnoreCase) || fontWeight == "700"))
                element.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value)
        {
            if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                return true;

            value = "";
            return false;
        }

        private static StyleLength ParseLength(string value)
        {
            value = FirstToken(value);
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                return StyleKeyword.Auto;
            if (value.EndsWith("%", StringComparison.Ordinal) &&
                float.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return new Length(percent, LengthUnit.Percent);
            if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 2);
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels))
                return new Length(pixels, LengthUnit.Pixel);

            return StyleKeyword.Null;
        }

        private static StyleFloat ParseFloat(string value)
        {
            value = FirstToken(value);
            if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 2);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels)
                ? pixels
                : StyleKeyword.Null;
        }

        private static float ParseRatio(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
                ? Mathf.Clamp01(ratio)
                : 0f;
        }

        private static void ApplyBoxLength(string value, Action<StyleLength> apply)
        {
            apply(ParseLength(value));
        }

        private static void ApplyBoxFloat(string value, Action<StyleFloat> apply)
        {
            apply(ParseFloat(value));
        }

        private static bool TryParseColor(string value, out Color color)
        {
            value = value.Trim();
            if (value.StartsWith("var(", StringComparison.Ordinal))
            {
                color = default;
                return false;
            }
            return ColorUtility.TryParseHtmlString(value, out color);
        }

        private static bool TryParseAlign(string value, out Align align)
        {
            switch (value)
            {
                case "center":
                    align = Align.Center;
                    return true;
                case "end":
                case "flex-end":
                    align = Align.FlexEnd;
                    return true;
                case "stretch":
                    align = Align.Stretch;
                    return true;
                case "start":
                case "flex-start":
                    align = Align.FlexStart;
                    return true;
                default:
                    align = Align.Auto;
                    return false;
            }
        }

        private static bool TryParseJustify(string value, out Justify justify)
        {
            switch (value)
            {
                case "center":
                    justify = Justify.Center;
                    return true;
                case "end":
                case "flex-end":
                    justify = Justify.FlexEnd;
                    return true;
                case "space-between":
                    justify = Justify.SpaceBetween;
                    return true;
                case "space-around":
                    justify = Justify.SpaceAround;
                    return true;
                case "start":
                case "flex-start":
                    justify = Justify.FlexStart;
                    return true;
                default:
                    justify = Justify.FlexStart;
                    return false;
            }
        }

        private static string FirstToken(string value)
        {
            return (value ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }

        private static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                return "";

            if (kind == "panel.dialogue")
                return "panel";
            if (kind.StartsWith("text.", StringComparison.Ordinal))
                return "text";
            if (kind.StartsWith("control.button.", StringComparison.Ordinal))
                return "control.button";
            if (kind.StartsWith("control.text.", StringComparison.Ordinal))
                return "control.text";

            return kind;
        }

        internal static bool IsHidden(EveSurfaceComponent component) =>
            component.Layout != null &&
            component.Layout.TryGetValue("display", out var display) &&
            string.Equals(display, "none", StringComparison.OrdinalIgnoreCase);

        internal static bool IsExternalProjectionRoot(string kind)
        {
            kind = NormalizeKind(kind);
            return string.Equals(kind, "world.scene3d", StringComparison.Ordinal) ||
                   string.Equals(kind, "world.scene2d", StringComparison.Ordinal) ||
                   string.Equals(kind, "field.volume3d", StringComparison.Ordinal) ||
                   string.Equals(kind, "field.particles3d", StringComparison.Ordinal) ||
                   string.Equals(kind, "layer.reactive", StringComparison.Ordinal);
        }

        private static string SafeName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "eve.component" : value;
        }

        private static string SafeClass(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            var characters = value.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
            {
                if (!char.IsLetterOrDigit(characters[index]))
                    characters[index] = '-';
            }

            return new string(characters).Trim('-').ToLowerInvariant();
        }
    }
}
