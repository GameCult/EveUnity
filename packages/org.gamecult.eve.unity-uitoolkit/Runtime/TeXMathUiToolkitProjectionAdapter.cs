using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class TeXMathUiToolkitProjectionAdapter : IEveUiToolkitPluginProjectionAdapter
    {
        public string PluginId => "tex.math";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "embed.tex",
            "tex.inline",
            "tex.block"
        };

        public bool CanLower(EveSurfaceComponent component)
        {
            return string.Equals(component.Kind, "embed.tex", StringComparison.Ordinal);
        }

        public VisualElement Lower(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var tex = new VisualElement();
            tex.AddToClassList("eve-plugin-projection");
            tex.AddToClassList("eve-plugin-tex-math");
            tex.AddToClassList("tex-math");
            tex.AddToClassList($"tex-display-{SafeClass(component.GetProp("display", "inline"))}");
            tex.style.flexDirection = FlexDirection.Column;

            var label = component.GetProp("label", "TeX");
            if (!string.IsNullOrWhiteSpace(label))
            {
                var labelElement = new Label(label);
                labelElement.AddToClassList("tex-math-label");
                tex.Add(labelElement);
            }

            var source = component.GetProp("source", component.GetProp("sourceUri", ""));
            var sourceElement = new Label(source);
            sourceElement.AddToClassList("tex-math-source-fallback");
            tex.Add(sourceElement);

            return tex;
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
