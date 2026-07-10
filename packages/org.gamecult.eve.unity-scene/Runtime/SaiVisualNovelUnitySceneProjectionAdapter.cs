using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class SaiVisualNovelUnitySceneProjectionAdapter
    {
        public string PluginId => "sai.vn";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "vn.stage",
            "story.choose",
            "story.continue",
            "story.jump"
        };

        public bool CanProject(EveSurfaceComponent component)
        {
            if (component == null)
                return false;

            switch (component.Kind)
            {
                case "vn.stage":
                case "panel.dialogue":
                case "text.dialogue":
                case "rail.actions":
                    return true;
                case "control.button":
                    return StoryCommand(component).StartsWith("story.", StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        public EveUnityScenePluginProjection Project(EveSurfaceComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (!CanProject(component))
            {
                throw new InvalidOperationException(
                    $"Sai scene projection adapter cannot project component kind '{component.Kind}'.");
            }

            return new EveUnityScenePluginProjection(
                PluginId,
                ProjectionKind(component),
                "gamecult.eve.plugin_abi.v1",
                "sidecar-advertised-plugin-abi",
                Capabilities,
                StoryCommand(component),
                component.GetProp("storyId", component.GetProp("currentPath", "")),
                "Sai");
        }

        private static string ProjectionKind(EveSurfaceComponent component)
        {
            switch (component.Kind)
            {
                case "vn.stage":
                    return "sai-vn-scene-stage-shell";
                case "panel.dialogue":
                case "text.dialogue":
                    return "sai-vn-scene-dialogue-shell";
                case "rail.actions":
                    return "sai-vn-scene-action-rail-shell";
                case "control.button":
                    return "sai-vn-scene-story-command-shell";
                default:
                    return "sai-vn-scene-projection-shell";
            }
        }

        private static string StoryCommand(EveSurfaceComponent component)
        {
            return component.GetProp(
                "command",
                component.GetProp(
                    "action.command",
                    component.GetProp("action", component.GetProp("type", ""))));
        }
    }
}
