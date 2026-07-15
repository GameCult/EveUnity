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
        public bool IsButtonHold => string.Equals(InputValueModel, ButtonHoldValueModel, StringComparison.Ordinal);

        public static EveUnityAdvertisedInputAction Resolve(EveInputCapabilityDocument? capability, string actionId)
        {
            if (capability == null)
                throw new InvalidOperationException("The provider did not advertise an input capability.");
            var action = (capability.Actions ?? Array.Empty<EveInputActionDocument>()).FirstOrDefault(candidate =>
                string.Equals(candidate?.ActionId, actionId, StringComparison.Ordinal));
            if (action == null || string.IsNullOrWhiteSpace(action.Operation))
                throw new InvalidOperationException($"Input action '{actionId}' is not advertised with an operation.");
            if (!string.Equals(action.Availability, "available", StringComparison.OrdinalIgnoreCase))
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
    }
}
