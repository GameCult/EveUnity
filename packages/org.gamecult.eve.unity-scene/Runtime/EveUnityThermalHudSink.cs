using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityThermalHudSink : MonoBehaviour, IEveUnityThermalPresentationSink
    {
        private UIDocument? _document;
        private VisualElement? _root;
        private Label? _temperature;
        private VisualElement? _temperatureMarker;
        private VisualElement? _heatstrokeFill;
        private VisualElement? _hypothermiaFill;
        private Label? _heatstrokeValue;
        private Label? _hypothermiaValue;

        public EveUnityThermalPresentationFrame LastFrame { get; private set; }
        public VisualElement? Root => _root;

        private void Awake() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_root != null)
            {
                AttachToDocument();
                return;
            }
            _document = GetComponent<UIDocument>();
            if (_document == null)
            {
                var panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.name = "Eve playable-world HUD panel";
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1920, 1080);
                panel.match = 0.5f;
                _document = gameObject.AddComponent<UIDocument>();
                _document.panelSettings = panel;
                _document.sortingOrder = 100;
            }
            _root = new VisualElement();
            Build(_root);
            AttachToDocument();
        }

        private void AttachToDocument()
        {
            var documentRoot = _document?.rootVisualElement;
            if (documentRoot != null && _root != null && _root.parent == null)
                documentRoot.Add(_root);
        }

        public void ApplyThermalPresentation(EveUnityThermalPresentationFrame frame)
        {
            EnsureBuilt();
            LastFrame = frame;
            if (_temperature == null) return;
            _temperature.text = FormatTemperature(frame.CockpitTemperature);
            _heatstrokeValue!.text = FormatPercent(frame.Heatstroke);
            _hypothermiaValue!.text = FormatPercent(frame.Hypothermia);
            _heatstrokeFill!.style.width = Length.Percent(Mathf.Clamp01(frame.Heatstroke) * 100);
            _hypothermiaFill!.style.width = Length.Percent(Mathf.Clamp01(frame.Hypothermia) * 100);
            _heatstrokeFill.style.backgroundColor = frame.HeatstrokeRisk
                ? new Color(1f, 0.2f, 0.1f, 0.95f) : new Color(0.86f, 0.38f, 0.16f, 0.9f);
            _hypothermiaFill.style.backgroundColor = frame.HypothermiaRisk
                ? new Color(0.12f, 0.55f, 1f, 0.95f) : new Color(0.2f, 0.65f, 0.78f, 0.9f);
            var normalizedTemperature = Mathf.InverseLerp(273f, 330f, frame.CockpitTemperature);
            _temperatureMarker!.style.left = Length.Percent(normalizedTemperature * 100);
            Root!.style.display = string.IsNullOrWhiteSpace(frame.EntityId) && frame.DeathWeight <= 0
                ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void Build(VisualElement root)
        {
            root.name = "eve-thermal-hud-root";
            root.style.position = Position.Absolute;
            root.style.left = 18;
            root.style.bottom = 18;
            root.style.width = 320;
            root.style.height = 142;
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;
            root.style.backgroundColor = new Color(0.025f, 0.03f, 0.035f, 0.88f);
            root.style.borderLeftWidth = root.style.borderRightWidth = 1;
            root.style.borderTopWidth = root.style.borderBottomWidth = 1;
            root.style.borderLeftColor = root.style.borderRightColor = new Color(0.35f, 0.42f, 0.46f, 0.8f);
            root.style.borderTopColor = root.style.borderBottomColor = new Color(0.35f, 0.42f, 0.46f, 0.8f);

            var header = new VisualElement { name = "cockpit-temperature" };
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.Add(Label("COCKPIT", 11, new Color(0.72f, 0.78f, 0.8f)));
            _temperature = Label("-- K", 15, Color.white);
            header.Add(_temperature);
            root.Add(header);

            var temperatureTrack = Track("temperature-track", 6);
            temperatureTrack.style.marginTop = 7;
            temperatureTrack.style.backgroundColor = new Color(0.12f, 0.48f, 0.7f, 0.8f);
            _temperatureMarker = new VisualElement { name = "temperature-marker" };
            _temperatureMarker.style.position = Position.Absolute;
            _temperatureMarker.style.top = -2;
            _temperatureMarker.style.width = 2;
            _temperatureMarker.style.height = 10;
            _temperatureMarker.style.backgroundColor = Color.white;
            temperatureTrack.Add(_temperatureMarker);
            root.Add(temperatureTrack);

            root.Add(Meter("HEATSTROKE", "heatstroke", out _heatstrokeFill, out _heatstrokeValue));
            root.Add(Meter("HYPOTHERMIA", "hypothermia", out _hypothermiaFill, out _hypothermiaValue));
        }

        private static VisualElement Meter(string title, string name, out VisualElement fill, out Label value)
        {
            var row = new VisualElement { name = name + "-meter" };
            row.style.marginTop = 9;
            var labels = new VisualElement();
            labels.style.flexDirection = FlexDirection.Row;
            labels.style.justifyContent = Justify.SpaceBetween;
            labels.Add(Label(title, 10, new Color(0.72f, 0.78f, 0.8f)));
            value = Label("0%", 10, Color.white);
            labels.Add(value);
            row.Add(labels);
            var track = Track(name + "-track", 8);
            fill = new VisualElement { name = name + "-fill" };
            fill.style.height = Length.Percent(100);
            fill.style.width = Length.Percent(0);
            track.Add(fill);
            row.Add(track);
            return row;
        }

        private static VisualElement Track(string name, float height)
        {
            var track = new VisualElement { name = name };
            track.style.position = Position.Relative;
            track.style.height = height;
            track.style.backgroundColor = new Color(0.12f, 0.14f, 0.15f, 1f);
            return track;
        }

        private static Label Label(string text, float size, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 0;
            return label;
        }

        private static string FormatTemperature(float value) =>
            value.ToString("0.#", CultureInfo.InvariantCulture) + " K";
        private static string FormatPercent(float value) =>
            (Mathf.Clamp01(value) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
