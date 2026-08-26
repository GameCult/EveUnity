using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityAdvertisedInputAction
    {
        public const string ButtonHoldValueModel = "button-hold.v1";
        public const string AxisValueModel = "axis.v1";
        public const string ScalarValueModel = "scalar.v1";
        public const string ViewDirectionValueModel = "view-direction.v1";

        private readonly EveInputActionDocument _action;

        private EveUnityAdvertisedInputAction(EveInputActionDocument action)
        {
            _action = action;
        }

        public string ActionId => _action.ActionId ?? "";
        public string Operation => _action.Operation ?? "";
        public string Availability => _action.Availability ?? "";
        public string InputValueModel => _action.InputValue?.Model ?? "";
        public string InputValuePayloadKey => _action.InputValue?.PayloadKey ?? "";
        public IReadOnlyList<string> InputValuePayloadKeys => _action.InputValue?.PayloadKeys ?? Array.Empty<string>();
        public double? CurrentValue => _action.InputValue?.CurrentValue;
        public double? MinimumValue => _action.InputValue?.MinimumValue;
        public double? MaximumValue => _action.InputValue?.MaximumValue;
        public double? StepValue => _action.InputValue?.StepValue;
        public string Unit => _action.InputValue?.Unit ?? "";
        public bool IsButtonHold => string.Equals(InputValueModel, ButtonHoldValueModel, StringComparison.Ordinal);
        public bool IsScalar => string.Equals(InputValueModel, ScalarValueModel, StringComparison.Ordinal);
        public bool IsViewDirection => string.Equals(InputValueModel, ViewDirectionValueModel, StringComparison.Ordinal);

        public static EveUnityAdvertisedInputAction Resolve(
            EveInputCapabilityDocument? capability,
            string actionId,
            bool requireAvailable = true)
        {
            if (capability == null)
                throw new InvalidOperationException("The provider did not advertise an input capability.");
            var action = (capability.Actions ?? Array.Empty<EveInputActionDocument>()).FirstOrDefault(candidate =>
                string.Equals(candidate?.ActionId, actionId, StringComparison.Ordinal));
            if (action == null || string.IsNullOrWhiteSpace(action.Operation))
                throw new InvalidOperationException($"Input action '{actionId}' is not advertised with an operation.");
            if (requireAvailable && !string.Equals(action.Availability, "available", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Input action '{actionId}' is not available.");
            return new EveUnityAdvertisedInputAction(action);
        }

        public IReadOnlyDictionary<string, string> BuildPayload(string entityId, float? inputValue = null)
        {
            var payload = _action.Payload == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(_action.Payload, StringComparer.Ordinal);
            payload["entityId"] = entityId ?? "";
            payload["actionId"] = ActionId;

            if (inputValue.HasValue)
            {
                if (float.IsNaN(inputValue.Value) || float.IsInfinity(inputValue.Value))
                    throw new ArgumentOutOfRangeException(nameof(inputValue), "Input action value must be finite.");
                if (string.IsNullOrWhiteSpace(InputValueModel) || string.IsNullOrWhiteSpace(InputValuePayloadKey))
                    throw new InvalidOperationException($"Input action '{ActionId}' does not advertise a value payload.");
                payload[InputValuePayloadKey] = inputValue.Value.ToString("R", CultureInfo.InvariantCulture);
            }

            return payload;
        }

        public IReadOnlyDictionary<string, string> BuildViewDirectionPayload(
            string entityId,
            float directionX,
            float directionY,
            float directionZ)
        {
            if (!IsViewDirection)
                throw new InvalidOperationException($"Input action '{ActionId}' does not advertise a view-direction value.");
            if (InputValuePayloadKeys.Count != 3 || InputValuePayloadKeys.Any(string.IsNullOrWhiteSpace) ||
                InputValuePayloadKeys.Distinct(StringComparer.Ordinal).Count() != 3)
                throw new InvalidOperationException($"Input action '{ActionId}' does not advertise three distinct view-direction payload keys.");
            if (!IsFinite(directionX) || !IsFinite(directionY) || !IsFinite(directionZ))
                throw new ArgumentOutOfRangeException(nameof(directionX), "View direction must be finite.");
            var length = Math.Sqrt(
                (directionX * directionX) +
                (directionY * directionY) +
                (directionZ * directionZ));
            if (length <= 0.000001)
                throw new ArgumentOutOfRangeException(nameof(directionX), "View direction must be non-zero.");

            var payload = BuildPayload(entityId).ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
            payload[InputValuePayloadKeys[0]] = ((float)(directionX / length)).ToString("R", CultureInfo.InvariantCulture);
            payload[InputValuePayloadKeys[1]] = ((float)(directionY / length)).ToString("R", CultureInfo.InvariantCulture);
            payload[InputValuePayloadKeys[2]] = ((float)(directionZ / length)).ToString("R", CultureInfo.InvariantCulture);
            return payload;
        }

        public IReadOnlyDictionary<string, string> BuildScalarPayload(string entityId, double value)
        {
            if (!IsScalar)
                throw new InvalidOperationException($"Input action '{ActionId}' does not advertise a scalar value.");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Scalar input value must be finite.");
            if (!CurrentValue.HasValue || double.IsNaN(CurrentValue.Value) || double.IsInfinity(CurrentValue.Value))
                throw new InvalidOperationException($"Input action '{ActionId}' does not advertise a finite current scalar value.");
            if (MinimumValue.HasValue && value < MinimumValue.Value)
                throw new ArgumentOutOfRangeException(nameof(value), $"Scalar input value is below the advertised minimum {MinimumValue.Value}.");
            if (MaximumValue.HasValue && value > MaximumValue.Value)
                throw new ArgumentOutOfRangeException(nameof(value), $"Scalar input value is above the advertised maximum {MaximumValue.Value}.");
            if (MinimumValue.HasValue && MaximumValue.HasValue && MinimumValue.Value > MaximumValue.Value)
                throw new InvalidOperationException($"Input action '{ActionId}' advertises an inverted scalar range.");
            if (StepValue.HasValue && (!IsFinite(StepValue.Value) || StepValue.Value <= 0))
                throw new InvalidOperationException($"Input action '{ActionId}' advertises an invalid scalar step.");
            if (string.IsNullOrWhiteSpace(InputValuePayloadKey))
                throw new InvalidOperationException($"Input action '{ActionId}' does not advertise a scalar payload key.");

            var payload = BuildPayload(entityId).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            payload[InputValuePayloadKey] = value.ToString("R", CultureInfo.InvariantCulture);
            return payload;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
