using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class EveUiToolkitSurfaceOptions
    {
        public static EveUiToolkitSurfaceOptions Default { get; } = new EveUiToolkitSurfaceOptions();

        public EveUiToolkitSurfaceOptions(
            Func<EveEmbeddedDocumentSlot, EveSurfaceDocument?>? embeddedDocumentResolver = null,
            IReadOnlyList<IEveUiToolkitPluginProjectionAdapter>? pluginProjectionAdapters = null)
        {
            EmbeddedDocumentResolver = embeddedDocumentResolver;
            PluginProjectionAdapters = pluginProjectionAdapters ?? new IEveUiToolkitPluginProjectionAdapter[]
            {
                new SaiVisualNovelUiToolkitProjectionAdapter(),
                new NornGraphUiToolkitProjectionAdapter(),
                new TeXMathUiToolkitProjectionAdapter()
            };
        }

        public Func<EveEmbeddedDocumentSlot, EveSurfaceDocument?>? EmbeddedDocumentResolver { get; }

        public IReadOnlyList<IEveUiToolkitPluginProjectionAdapter> PluginProjectionAdapters { get; }

        public IEveUiToolkitPluginProjectionAdapter? FindPluginProjectionAdapter(EveSurfaceComponent component)
        {
            return PluginProjectionAdapters.FirstOrDefault(adapter => adapter.CanLower(component));
        }
    }
}
