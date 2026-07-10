using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class TeXMathUnitySceneProjectionAdapter
    {
        public string PluginId => "tex.math";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "embed.tex",
            "tex.inline",
            "tex.block",
            "tex.scene-placement"
        };

        public bool CanProject(EveSurfaceComponent component)
        {
            return component != null && string.Equals(component.Kind, "embed.tex", StringComparison.Ordinal);
        }

        public EveUnityScenePluginProjection Project(EveSurfaceComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (!CanProject(component))
            {
                throw new InvalidOperationException(
                    $"TeX scene projection adapter cannot project component kind '{component.Kind}'.");
            }

            return new EveUnityScenePluginProjection(
                PluginId,
                ProjectionKind(component),
                "gamecult.eve.plugin_abi.v1",
                "sidecar-advertised-plugin-abi",
                Capabilities,
                "",
                component.GetProp("sourceUri", component.GetProp("source", "")),
                "EvePlugins");
        }

        private static string ProjectionKind(EveSurfaceComponent component)
        {
            var display = component.GetProp("display", "inline");
            if (string.Equals(display, "block", StringComparison.Ordinal))
                return "tex-math-scene-block-fallback-shell";
            if (string.Equals(display, "page", StringComparison.Ordinal))
                return "tex-math-scene-page-fallback-shell";
            return "tex-math-scene-inline-fallback-shell";
        }
    }
}
