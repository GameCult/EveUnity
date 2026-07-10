using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class SaiVisualNovelUiToolkitProjectionAdapter : IEveUiToolkitPluginProjectionAdapter
    {
        public string PluginId => "sai.vn";

        public IReadOnlyList<string> Capabilities { get; } = new[]
        {
            "vn.stage",
            "story.choose",
            "story.continue",
            "story.jump"
        };

        public bool CanLower(EveSurfaceComponent component)
        {
            switch (component.Kind)
            {
                case "vn.stage":
                case "panel.dialogue":
                case "text.dialogue":
                case "rail.actions":
                    return true;
                case "control.button":
                    return IsStoryCommand(component);
                default:
                    return false;
            }
        }

        public VisualElement Lower(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            switch (component.Kind)
            {
                case "vn.stage":
                    return Stage(component);
                case "panel.dialogue":
                    return DialoguePanel(component);
                case "text.dialogue":
                    return DialogueText(component);
                case "rail.actions":
                    return ActionRail();
                case "control.button":
                    return StoryButton(component, document, commandSink);
                default:
                    return new VisualElement();
            }
        }

        private static VisualElement Stage(EveSurfaceComponent component)
        {
            var stage = new VisualElement();
            stage.AddToClassList("eve-plugin-projection");
            stage.AddToClassList("eve-plugin-sai-vn");
            stage.AddToClassList("sai-vn-stage");
            stage.style.flexGrow = 1;
            stage.style.flexDirection = FlexDirection.Column;

            var storyId = component.GetProp("storyId");
            var currentPath = component.GetProp("currentPath");
            if (!string.IsNullOrWhiteSpace(storyId) || !string.IsNullOrWhiteSpace(currentPath))
            {
                var state = new Label(string.IsNullOrWhiteSpace(currentPath)
                    ? storyId
                    : $"{storyId}:{currentPath}");
                state.AddToClassList("sai-vn-state");
                state.style.unityFontStyleAndWeight = FontStyle.Bold;
                stage.Add(state);
            }

            return stage;
        }

        private static VisualElement DialoguePanel(EveSurfaceComponent component)
        {
            var panel = new VisualElement();
            panel.AddToClassList("eve-plugin-projection");
            panel.AddToClassList("eve-plugin-sai-vn");
            panel.AddToClassList("sai-vn-dialogue");
            panel.style.flexDirection = FlexDirection.Column;

            var speaker = component.GetProp("speaker");
            if (!string.IsNullOrWhiteSpace(speaker))
            {
                var speakerLabel = new Label(speaker);
                speakerLabel.AddToClassList("sai-vn-speaker");
                speakerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                panel.Add(speakerLabel);
            }

            var scene = component.GetProp("scene");
            if (!string.IsNullOrWhiteSpace(scene))
            {
                var sceneLabel = new Label(scene);
                sceneLabel.AddToClassList("sai-vn-scene");
                panel.Add(sceneLabel);
            }

            return panel;
        }

        private static VisualElement DialogueText(EveSurfaceComponent component)
        {
            var text = new Label(component.GetProp("text"));
            text.AddToClassList("eve-plugin-projection");
            text.AddToClassList("eve-plugin-sai-vn");
            text.AddToClassList("sai-vn-dialogue-text");
            return text;
        }

        private static VisualElement ActionRail()
        {
            var rail = new VisualElement();
            rail.AddToClassList("eve-plugin-projection");
            rail.AddToClassList("eve-plugin-sai-vn");
            rail.AddToClassList("sai-vn-actions");
            rail.style.flexDirection = FlexDirection.Row;
            rail.style.flexWrap = Wrap.Wrap;
            return rail;
        }

        private static Button StoryButton(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink)
        {
            var label = component.GetProp("label", component.GetProp("title", "Continue"));
            var command = StoryCommand(component);
            var button = new Button(() => EmitStoryCommand(document, component, command, commandSink)) { text = label };
            button.AddToClassList("eve-plugin-projection");
            button.AddToClassList("eve-plugin-sai-vn");
            button.AddToClassList("sai-vn-story-command");
            button.AddToClassList($"sai-vn-command-{SafeClass(command)}");
            return button;
        }

        private static bool IsStoryCommand(EveSurfaceComponent component)
        {
            return StoryCommand(component).StartsWith("story.", StringComparison.Ordinal);
        }

        private static string StoryCommand(EveSurfaceComponent component)
        {
            return component.GetProp("command", component.GetProp("action", component.GetProp("type")));
        }

        private static void EmitStoryCommand(
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
