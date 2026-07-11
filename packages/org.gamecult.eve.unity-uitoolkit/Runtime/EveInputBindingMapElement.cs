using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameCult.Eve.UnityUIToolkit
{
    public sealed class EveInputBindingMapElement : VisualElement
    {
        private readonly VisualElement _device = new VisualElement();
        private readonly Label _sequence = new Label();
        private readonly List<string> _pendingSequence = new List<string>();
        private EveInputDeviceLayout _layout = new EveInputDeviceLayout();
        private IReadOnlyList<EveInputBinding> _bindings = Array.Empty<EveInputBinding>();
        private string _draggedActionId = "";

        public EveInputBindingMapElement()
        {
            style.position = Position.Relative;
            style.flexGrow = 1;
            _device.style.position = Position.Relative;
            _device.style.minHeight = 360;
            _device.style.flexGrow = 1;
            _sequence.style.unityTextAlign = TextAnchor.MiddleCenter;
            _sequence.style.minHeight = 32;
            Add(_device);
            Add(_sequence);
        }

        public event Action<string, EveInputGesture>? BindingRequested;

        public void SetState(EveInputDeviceLayout layout, IReadOnlyList<EveInputBinding> bindings)
        {
            _layout = layout ?? new EveInputDeviceLayout();
            _bindings = bindings ?? Array.Empty<EveInputBinding>();
            Render();
        }

        public void BeginActionDrag(string actionId)
        {
            _draggedActionId = actionId ?? "";
        }

        public void CancelActionDrag()
        {
            _draggedActionId = "";
            _pendingSequence.Clear();
            RenderSequence();
        }

        public void RecordDirectionalStep(string control)
        {
            if (!_layout.SupportsDirectionalStrings || string.IsNullOrWhiteSpace(_draggedActionId)) return;
            if (!control.StartsWith("gamepad.dpad.", StringComparison.Ordinal)) return;
            _pendingSequence.Add(control);
            RenderSequence();
        }

        public void CommitDirectionalString()
        {
            if (string.IsNullOrWhiteSpace(_draggedActionId) || _pendingSequence.Count == 0) return;
            BindingRequested?.Invoke(_draggedActionId, new EveInputGesture { Kind = "sequence", Controls = _pendingSequence.ToArray(), MaxStepIntervalMs = 650 });
            CancelActionDrag();
        }

        private void Render()
        {
            _device.Clear();
            foreach (var control in _layout.Controls)
            {
                var button = new Button(() => BindDirect(control.Control));
                button.text = BindingLabel(control.Control, control.Label);
                button.style.position = Position.Absolute;
                button.style.left = control.X * 54;
                button.style.top = control.Y * 54;
                button.style.width = control.Width * 50;
                button.style.height = control.Height * 46;
                button.tooltip = control.Control;
                _device.Add(button);
            }
            RenderSequence();
        }

        private void BindDirect(string control)
        {
            if (string.IsNullOrWhiteSpace(_draggedActionId)) return;
            if (_layout.SupportsDirectionalStrings && control.StartsWith("gamepad.dpad.", StringComparison.Ordinal))
            {
                RecordDirectionalStep(control);
                return;
            }
            BindingRequested?.Invoke(_draggedActionId, new EveInputGesture { Kind = "direct", Controls = new[] { control } });
            CancelActionDrag();
        }

        private string BindingLabel(string control, string fallback)
        {
            var actions = _bindings.Where(binding => binding.Gesture.Controls.Contains(control)).Select(binding => binding.ActionId).ToArray();
            return actions.Length == 0 ? fallback : fallback + "\n" + string.Join(" + ", actions);
        }

        private void RenderSequence()
        {
            _sequence.text = _pendingSequence.Count == 0 ? "" : string.Join("  ", _pendingSequence.Select(DirectionGlyph));
        }

        private static string DirectionGlyph(string control) => control.EndsWith(".up") ? "UP" : control.EndsWith(".down") ? "DOWN" : control.EndsWith(".left") ? "LEFT" : "RIGHT";
    }
}
