using System;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    internal static class EveUnityUiDocumentRuntime
    {
        private const string ThemeResource = "EveUnity/UnityDefaultRuntimeTheme";

        public static UIDocument Ensure(GameObject host)
        {
            var document = host.GetComponent<UIDocument>();
            if (document != null) return document;

            var theme = Resources.Load<ThemeStyleSheet>(ThemeResource);
            if (theme == null)
                throw new InvalidOperationException($"EveUnity runtime theme resource '{ThemeResource}' is missing.");

            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = "Eve playable-world HUD panel";
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.match = 0.5f;
            panel.themeStyleSheet = theme;

            document = host.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.sortingOrder = 100;
            return document;
        }
    }
}
