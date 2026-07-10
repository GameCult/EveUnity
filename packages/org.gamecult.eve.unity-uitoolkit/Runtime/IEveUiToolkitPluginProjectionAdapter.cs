using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public interface IEveUiToolkitPluginProjectionAdapter
    {
        string PluginId { get; }

        IReadOnlyList<string> Capabilities { get; }

        bool CanLower(EveSurfaceComponent component);

        VisualElement Lower(
            EveSurfaceComponent component,
            EveSurfaceDocument document,
            Action<EveSurfaceCommandRequest>? commandSink);
    }
}
