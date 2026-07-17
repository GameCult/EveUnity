using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.Surface;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityInputActionBar : MonoBehaviour
    {
        private readonly Dictionary<string, ActionRow> _rows =
            new Dictionary<string, ActionRow>(StringComparer.Ordinal);
        private readonly HashSet<string> _pressedHolds = new HashSet<string>(StringComparer.Ordinal);
        private EveUnityPlayableWorldClientHost? _host;
        private UIDocument? _document;
        private VisualElement? _root;
        private string _structureKey = "";
        private float _nextRefreshAt;

        public VisualElement? Root => _root;
        public IReadOnlyCollection<string> PresentedActionIds => _rows.Keys;

        private void Awake() => EnsureBuilt();

        public void Bind(EveUnityPlayableWorldClientHost host)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            EnsureBuilt();
            RefreshNow();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefreshAt) return;
            _nextRefreshAt = Time.unscaledTime + 0.2f;
            RefreshNow();
        }

        public void RefreshNow()
        {
            EnsureBuilt();
            var capability = _host?.InputCapability;
            var actions = SelectActions(capability).ToArray();
            var structureKey = string.Join("\n", actions.Select(action =>
                $"{action.ActionId}\t{action.Label}\t{action.IconRef}\t{action.InputValue?.Model}"));
            if (!string.Equals(_structureKey, structureKey, StringComparison.Ordinal))
            {
                ReleaseAllHolds();
                Rebuild(actions);
                _structureKey = structureKey;
            }
            foreach (var action in actions)
                if (_rows.TryGetValue(action.ActionId ?? "", out var row))
                    Apply(row, action);
            if (_root != null)
                _root.style.display = actions.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        public static IReadOnlyList<EveInputActionDocument> SelectActions(EveInputCapabilityDocument? capability)
        {
            if (capability == null) return Array.Empty<EveInputActionDocument>();
            var actions = (capability.Actions ?? Array.Empty<EveInputActionDocument>())
                .Where(action => action != null && !string.IsNullOrWhiteSpace(action.ActionId))
                .ToDictionary(action => action.ActionId, StringComparer.Ordinal);
            var selected = new List<EveInputActionDocument>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var profile = (capability.DefaultProfiles ?? Array.Empty<EveInputProfileDocument>())
                .FirstOrDefault(candidate => string.Equals(candidate.DeviceClass, "keyboard-mouse", StringComparison.Ordinal))
                ?? (capability.DefaultProfiles ?? Array.Empty<EveInputProfileDocument>()).FirstOrDefault();
            foreach (var binding in profile?.Bindings ?? Array.Empty<EveInputBindingDocument>())
                if (binding.ActionBar && actions.TryGetValue(binding.ActionId ?? "", out var action) && seen.Add(action.ActionId))
                    selected.Add(action);
            foreach (var action in actions.Values.OrderBy(candidate => candidate.ActionId, StringComparer.Ordinal))
                if (action.ActionBar && seen.Add(action.ActionId))
                    selected.Add(action);
            return selected;
        }

        public void SubmitPerformed(string actionId)
        {
            var host = RequireHost();
            var action = EveUnityAdvertisedInputAction.Resolve(host.InputCapability, actionId);
            if (action.IsViewDirection)
            {
                var view = host.ActiveCameraTransform;
                if (view == null || view.forward.sqrMagnitude <= 0.000001f) return;
                var direction = view.forward.normalized;
                host.SubmitAdvertisedActionViewDirectionIntent(
                    RequireEntityId(host), actionId, direction.x, direction.y, direction.z);
                return;
            }
            host.SubmitAdvertisedActionIntent(RequireEntityId(host), actionId);
        }

        public void SubmitHold(string actionId, bool active)
        {
            var host = RequireHost();
            host.SubmitAdvertisedActionValueIntent(RequireEntityId(host), actionId, active ? 1f : 0f);
        }

        public void SubmitScalar(string actionId, string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed))
                throw new FormatException($"'{value}' is not a finite scalar value.");
            var host = RequireHost();
            host.SubmitAdvertisedActionScalarIntent(RequireEntityId(host), actionId, parsed);
        }

        private void Rebuild(IReadOnlyList<EveInputActionDocument> actions)
        {
            _root!.Clear();
            _rows.Clear();
            _pressedHolds.Clear();
            foreach (var action in actions)
            {
                var row = BuildRow(action);
                _rows[action.ActionId] = row;
                _root.Add(row.Root);
            }
        }

        private void OnDisable() => ReleaseAllHolds();

        private void ReleaseAllHolds()
        {
            foreach (var actionId in _pressedHolds.ToArray())
            {
                try { SubmitHold(actionId, false); }
                catch (InvalidOperationException) { }
            }
            _pressedHolds.Clear();
        }

        private ActionRow BuildRow(EveInputActionDocument action)
        {
            var root = new VisualElement { name = "eve-action-slot-" + SafeName(action.ActionId) };
            root.AddToClassList("eve-input-action-slot");
            root.style.width = 112;
            root.style.minHeight = 72;
            root.style.paddingLeft = root.style.paddingRight = 6;
            root.style.paddingTop = root.style.paddingBottom = 5;
            root.style.marginLeft = root.style.marginRight = 3;
            root.style.backgroundColor = new Color(0.025f, 0.03f, 0.035f, 0.88f);
            root.style.borderLeftWidth = root.style.borderRightWidth = 1;
            root.style.borderTopWidth = root.style.borderBottomWidth = 1;
            root.style.borderLeftColor = root.style.borderRightColor = new Color(0.35f, 0.42f, 0.46f, 0.8f);
            root.style.borderTopColor = root.style.borderBottomColor = new Color(0.35f, 0.42f, 0.46f, 0.8f);

            var icon = new Image { name = "icon", scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("eve-input-action-icon");
            icon.style.height = 28;
            root.Add(icon);

            var label = new Label(action.Label ?? action.ActionId) { name = "label" };
            label.AddToClassList("eve-input-action-label");
            label.style.fontSize = 9;
            label.style.color = Color.white;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            root.Add(label);

            TextField? scalar = null;
            Button? trigger = null;
            if (string.Equals(action.InputValue?.Model, EveUnityAdvertisedInputAction.ScalarValueModel, StringComparison.Ordinal))
            {
                var controls = new VisualElement();
                controls.style.flexDirection = FlexDirection.Row;
                scalar = new TextField { name = "scalar" };
                scalar.style.flexGrow = 1;
                scalar.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                    SubmitScalar(action.ActionId, scalar.value);
                    evt.StopPropagation();
                });
                controls.Add(scalar);
                trigger = new Button(() => SubmitScalar(action.ActionId, scalar.value)) { text = "Set", name = "submit" };
                controls.Add(trigger);
                root.Add(controls);
            }
            else
            {
                trigger = new Button { text = "Use", name = "trigger" };
                if (string.Equals(action.InputValue?.Model, EveUnityAdvertisedInputAction.ButtonHoldValueModel, StringComparison.Ordinal))
                {
                    trigger.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (!_pressedHolds.Add(action.ActionId)) return;
                        trigger.CapturePointer(evt.pointerId);
                        SubmitHold(action.ActionId, true);
                    });
                    trigger.RegisterCallback<PointerUpEvent>(evt =>
                    {
                        ReleaseHold(action.ActionId);
                        if (trigger.HasPointerCapture(evt.pointerId)) trigger.ReleasePointer(evt.pointerId);
                    });
                    trigger.RegisterCallback<PointerCaptureOutEvent>(_ => ReleaseHold(action.ActionId));
                }
                else
                {
                    trigger.clicked += () => SubmitPerformed(action.ActionId);
                }
                root.Add(trigger);
            }
            return new ActionRow(root, icon, label, trigger, scalar);
        }

        private void Apply(ActionRow row, EveInputActionDocument action)
        {
            row.Label.text = action.Label ?? action.ActionId;
            var available = string.Equals(action.Availability, "available", StringComparison.OrdinalIgnoreCase);
            row.Root.SetEnabled(available);
            row.Root.style.opacity = available ? 1f : 0.45f;
            if (row.Scalar != null && action.InputValue?.CurrentValue is double current &&
                row.Scalar.focusController?.focusedElement != row.Scalar)
            {
                row.Scalar.SetValueWithoutNotify(current.ToString("R", CultureInfo.InvariantCulture));
                row.Scalar.tooltip = string.IsNullOrWhiteSpace(action.InputValue.Unit)
                    ? "" : action.InputValue.Unit;
            }
            if (!string.IsNullOrWhiteSpace(action.IconRef) && _host?.NativeAssetProvider != null)
            {
                var texture = _host.NativeAssetProvider.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(action.IconRef, "", "provider-asset-ref"),
                    typeof(Texture2D)) as Texture2D;
                row.Icon.image = texture;
            }
            else row.Icon.image = null;
        }

        private void ReleaseHold(string actionId)
        {
            if (!_pressedHolds.Remove(actionId)) return;
            SubmitHold(actionId, false);
        }

        private EveUnityPlayableWorldClientHost RequireHost() =>
            _host ?? throw new InvalidOperationException("The generic input action bar is not bound to a playable-world host.");

        private static string RequireEntityId(EveUnityPlayableWorldClientHost host) =>
            host.ActiveWorld?.PlayerEntityId ?? throw new InvalidOperationException("The playable world has no active player entity.");

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
            _root = new VisualElement { name = "eve-input-action-bar" };
            _root.AddToClassList("eve-input-action-bar");
            _root.style.position = Position.Absolute;
            _root.style.left = Length.Percent(50);
            _root.style.bottom = 18;
            _root.style.translate = new Translate(Length.Percent(-50), 0);
            _root.style.flexDirection = FlexDirection.Row;
            _root.style.alignItems = Align.FlexEnd;
            AttachToDocument();
        }

        private void AttachToDocument()
        {
            var documentRoot = _document?.rootVisualElement;
            if (documentRoot != null && _root != null && _root.parent == null)
                documentRoot.Add(_root);
        }

        private static string SafeName(string value) =>
            new string((value ?? "").Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

        private sealed class ActionRow
        {
            public ActionRow(VisualElement root, Image icon, Label label, Button? trigger, TextField? scalar)
            {
                Root = root;
                Icon = icon;
                Label = label;
                Trigger = trigger;
                Scalar = scalar;
            }

            public VisualElement Root { get; }
            public Image Icon { get; }
            public Label Label { get; }
            public Button? Trigger { get; }
            public TextField? Scalar { get; }
        }
    }
}
