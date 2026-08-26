using System;
using GameCult.Eve.PluginFields;

namespace GameCult.Eve.UnityScene.Fields
{
    public interface IEveUnityFieldsSplatsDocumentSource
    {
        event Action<EveFieldsSplatsDocument> FieldsSplatsAvailable;
    }
}
