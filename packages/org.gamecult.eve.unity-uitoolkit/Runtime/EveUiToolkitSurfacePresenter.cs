using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameCult.Eve.Surface;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class EveUiToolkitSurfacePresenter
    {
        private readonly EveUiToolkitSurfaceLowerer _lowerer;
        private string _structure = "";

        public EveUiToolkitSurfacePresenter(EveUiToolkitSurfaceOptions? options = null)
        {
            _lowerer = new EveUiToolkitSurfaceLowerer(options);
        }

        public VisualElement? Root { get; private set; }

        public bool Present(
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var structure = StructureOf(document);
            if (Root == null || !string.Equals(_structure, structure, StringComparison.Ordinal))
            {
                Root = _lowerer.Lower(document, commandSink);
                _structure = structure;
                return true;
            }

            var elements = Root.Query<VisualElement>()
                .ToList()
                .Where(element => element.userData is EveSurfaceComponent)
                .ToDictionary(element => ((EveSurfaceComponent)element.userData).Id, StringComparer.Ordinal);
            if (Root.userData is EveSurfaceComponent rootComponent)
                elements[rootComponent.Id] = Root;
            ApplyDynamic(document.Surface.Root, elements);
            return false;
        }

        private static void ApplyDynamic(
            EveSurfaceComponent component,
            IReadOnlyDictionary<string, VisualElement> elements)
        {
            if (!elements.TryGetValue(component.Id, out var element))
                return;
            element.userData = component;
            switch (NormalizeKind(component.Kind))
            {
                case "pane":
                case "modal":
                case "card":
                    var title = element.Q<Label>("title");
                    if (title != null) title.text = component.GetProp("title");
                    break;
                case "metric":
                    var label = element.Q<Label>("label");
                    var value = element.Q<Label>("value");
                    if (label != null) label.text = component.GetProp("label");
                    if (value != null) value.text = component.GetProp("value");
                    break;
                case "progress":
                    if (element is ProgressBar progress)
                    {
                        progress.title = component.GetProp("label");
                        progress.value = ParseRatio(component.GetProp("ratio", component.GetProp("value")));
                    }
                    break;
                case "text":
                    if (element is Label text)
                        text.text = component.GetProp("text", component.GetProp("value", component.GetProp("title")));
                    break;
            }

            if (EveUiToolkitSurfaceLowerer.IsHidden(component) ||
                EveUiToolkitSurfaceLowerer.IsExternalProjectionRoot(component.Kind))
                return;
            foreach (var child in component.Children)
                ApplyDynamic(child, elements);
        }

        private static string StructureOf(EveSurfaceDocument document)
        {
            var value = new StringBuilder();
            value.Append(document.ProviderId).Append('\n')
                .Append(document.Surface.Id).Append('\n');
            AppendStructure(value, document.Surface.Root);
            foreach (var command in document.Commands)
                value.Append("command\t").Append(command.Command).Append('\t').Append(command.Operation).Append('\n');
            return value.ToString();
        }

        private static void AppendStructure(StringBuilder value, EveSurfaceComponent component)
        {
            var kind = NormalizeKind(component.Kind);
            value.Append(component.Id).Append('\t').Append(kind).Append('\t');
            foreach (var entry in component.Layout.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                value.Append("l:").Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            foreach (var entry in component.Style.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                value.Append("s:").Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            foreach (var entry in component.Props.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (IsDynamicProp(kind, entry.Key)) continue;
                value.Append("p:").Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
            value.Append('\n');

            if (EveUiToolkitSurfaceLowerer.IsHidden(component) ||
                EveUiToolkitSurfaceLowerer.IsExternalProjectionRoot(component.Kind))
                return;
            foreach (var child in component.Children)
                AppendStructure(value, child);
        }

        private static bool IsDynamicProp(string kind, string key)
        {
            if (kind == "metric") return key == "label" || key == "value";
            if (kind == "progress") return key == "label" || key == "ratio" || key == "value";
            if (kind == "text") return key == "text" || key == "value" || key == "title";
            if (kind == "pane" || kind == "modal" || kind == "card")
                return key == "title";
            if (string.IsNullOrWhiteSpace(kind) || kind.StartsWith("world.", StringComparison.Ordinal) ||
                kind.StartsWith("entity.", StringComparison.Ordinal))
                return true;
            return false;
        }

        private static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return "";
            if (kind == "panel.dialogue") return "panel";
            if (kind.StartsWith("text.", StringComparison.Ordinal)) return "text";
            if (kind.StartsWith("control.button.", StringComparison.Ordinal)) return "control.button";
            if (kind.StartsWith("control.text.", StringComparison.Ordinal)) return "control.text";
            return kind;
        }

        private static float ParseRatio(string value) =>
            float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ratio)
                ? UnityEngine.Mathf.Clamp01(ratio)
                : 0f;
    }
}
