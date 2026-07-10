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

            foreach (var child in component.Children)
                element.Add(LowerComponent(child, document, commandSink));

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
                        card.Add(TitleLabel(title));
                    return card;
                }
                case "metric":
                {
                    var metric = new VisualElement();
                    metric.AddToClassList("eve-metric");
                    metric.style.flexDirection = FlexDirection.Column;
                    metric.Add(MutedLabel(component.GetProp("label")));
                    metric.Add(ValueLabel(component.GetProp("value")));
                    return metric;
                }
                case "progress":
                {
                    var progress = new VisualElement();
                    progress.AddToClassList("eve-progress");
                    progress.style.flexDirection = FlexDirection.Column;
                    progress.Add(MutedLabel(component.GetProp("label")));
                    progress.Add(ValueLabel(component.GetProp("value")));
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
