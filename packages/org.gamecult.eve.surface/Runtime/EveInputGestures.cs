using System;
using System.Collections.Generic;
using System.Linq;

namespace GameCult.Eve.Surface
{
    public sealed class EveInputGesture
    {
        public string Kind { get; set; } = "direct";
        public IReadOnlyList<string> Controls { get; set; } = Array.Empty<string>();
        public int MaxStepIntervalMs { get; set; } = 650;
        public string CompletionControl { get; set; } = "";
    }

    public sealed class EveInputBinding
    {
        public string BindingId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public EveInputGesture Gesture { get; set; } = new EveInputGesture();
        public bool ActionBar { get; set; }
    }

    public sealed class EveInputGestureResolver
    {
        private readonly IReadOnlyList<EveInputBinding> _bindings;
        private readonly HashSet<string> _pressed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, SequenceState> _sequences = new Dictionary<string, SequenceState>(StringComparer.Ordinal);

        public EveInputGestureResolver(IEnumerable<EveInputBinding> bindings)
        {
            _bindings = (bindings ?? Array.Empty<EveInputBinding>()).Where(binding => binding != null).ToArray();
        }

        public IReadOnlyList<string> Press(string control, long timestampMs)
        {
            _pressed.Add(control ?? "");
            var actions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in _bindings)
            {
                var gesture = binding.Gesture ?? new EveInputGesture();
                if (gesture.Kind == "direct" && gesture.Controls.FirstOrDefault() == control)
                    actions.Add(binding.ActionId);
                else if (gesture.Kind == "chord" && gesture.Controls.Count > 0 && gesture.Controls.All(_pressed.Contains))
                    actions.Add(binding.ActionId);
                else if (gesture.Kind == "sequence" && Advance(binding, control ?? "", timestampMs))
                    actions.Add(binding.ActionId);
            }
            return actions.ToArray();
        }

        public void Release(string control) => _pressed.Remove(control ?? "");

        public void Reset()
        {
            _pressed.Clear();
            _sequences.Clear();
        }

        private bool Advance(EveInputBinding binding, string control, long timestampMs)
        {
            var controls = binding.Gesture.Controls;
            if (controls.Count == 0) return false;
            _sequences.TryGetValue(binding.BindingId, out var previous);
            var index = previous.Index > 0 && timestampMs - previous.TimestampMs <= Math.Max(50, binding.Gesture.MaxStepIntervalMs) ? previous.Index : 0;
            var next = controls[index] == control ? index + 1 : controls[0] == control ? 1 : 0;
            if (next == controls.Count)
            {
                _sequences.Remove(binding.BindingId);
                return string.IsNullOrWhiteSpace(binding.Gesture.CompletionControl);
            }
            _sequences[binding.BindingId] = new SequenceState(next, timestampMs);
            return false;
        }

        private readonly struct SequenceState
        {
            public SequenceState(int index, long timestampMs) { Index = index; TimestampMs = timestampMs; }
            public int Index { get; }
            public long TimestampMs { get; }
        }
    }
}
