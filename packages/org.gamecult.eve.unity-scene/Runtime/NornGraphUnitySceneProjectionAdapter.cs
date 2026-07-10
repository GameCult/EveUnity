using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class NornGraphUnitySceneProjectionAdapter
    {
        public string PluginId => "norn.graph";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "embed.norn"
        };

        public bool CanProject(EveSurfaceComponent component)
        {
            return component != null && string.Equals(component.Kind, "embed.norn", StringComparison.Ordinal);
        }

        public EveUnityScenePluginProjection Project(EveSurfaceComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (!CanProject(component))
            {
                throw new InvalidOperationException(
                    $"Norn scene projection adapter cannot project component kind '{component.Kind}'.");
            }

            return new EveUnityScenePluginProjection(
                PluginId,
                "norn-scene-embedded-graph-shell",
                "gamecult.eve.plugin_abi.v1",
                "sidecar-advertised-plugin-abi",
                Capabilities,
                component.GetProp(
                    "interaction.nodeAction",
                    component.GetProp("focusCommand", component.GetProp("command", "graph.focus"))),
                component.GetProp("graph.document", component.GetProp("documentId", "")),
                "Norn");
        }
    }
}
