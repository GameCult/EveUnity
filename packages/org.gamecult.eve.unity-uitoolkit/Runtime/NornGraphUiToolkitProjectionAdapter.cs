using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class NornGraphUiToolkitProjectionAdapter : IEveUiToolkitPluginProjectionAdapter
    {
        public string PluginId => "norn.graph";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "embed.norn"
        };

        public bool CanLower(EveSurfaceComponent component)
        {
            return string.Equals(component.Kind, "embed.norn", StringComparison.Ordinal);
        }

        public VisualElement Lower(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var graph = new VisualElement();
            graph.AddToClassList("eve-plugin-projection");
            graph.AddToClassList("eve-plugin-norn-graph");
            graph.AddToClassList("norn-graph");
            graph.style.flexDirection = FlexDirection.Column;

            var title = component.GetProp("label", component.GetProp("title", "Norn graph"));
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("norn-graph-title");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            graph.Add(titleLabel);

            var mode = component.GetProp("mode", component.GetProp("layoutMode", "embedded"));
            var modeLabel = new Label(mode);
            modeLabel.AddToClassList("norn-graph-mode");
            graph.Add(modeLabel);

            var focusCommand = component.GetProp("command", component.GetProp("focusCommand", "graph.focus"));
            graph.RegisterCallback<ClickEvent>(_ => EmitGraphCommand(document, component, focusCommand, commandSink));

            return graph;
        }

        private static void EmitGraphCommand(
            EveSurfaceDocument document,
            EveSurfaceComponent component,
            string command,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            if (commandSink == null || string.IsNullOrWhiteSpace(command))
                return;

            commandSink(new EveSurfaceCommandRequest(
                document.ProviderId,
                document.Surface.Id,
                ResolveOperation(document, command),
                CultMesh.OperationPayload(component.Props),
                DateTimeOffset.UtcNow,
                "unity-uitoolkit"));
        }

        private static CultMeshOperationInvocationDescriptor ResolveOperation(
            EveSurfaceDocument document,
            string command)
        {
            foreach (var template in document.Commands)
            {
                if (string.Equals(template.Command, command, StringComparison.Ordinal))
                    return CultMesh.OperationInvocation(template.Operation);
            }

            return CultMesh.OperationInvocation(command);
        }
    }
}
