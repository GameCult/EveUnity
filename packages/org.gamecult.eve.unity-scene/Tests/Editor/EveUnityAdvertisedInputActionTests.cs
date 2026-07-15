using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using NUnit.Framework;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityAdvertisedInputActionTests
    {
        [Test]
        public void HeldValueOverridesAdvertisedPayloadWithoutMutatingCapability()
        {
            var sourcePayload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scalarValue"] = "0"
            };
            var action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.scoop",
                        Operation = "aetheria.daemon.commands.SetTractorPower",
                        Availability = "available",
                        Payload = sourcePayload,
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ButtonHoldValueModel,
                            PayloadKey = "scalarValue"
                        }
                    }
                }
            }, "pilot.scoop");

            var pressed = action.BuildPayload("entity.4", 1f);
            var released = action.BuildPayload("entity.4", 0f);

            Assert.That(action.IsButtonHold, Is.True);
            Assert.That(pressed["scalarValue"], Is.EqualTo("1"));
            Assert.That(released["scalarValue"], Is.EqualTo("0"));
            Assert.That(pressed["entityId"], Is.EqualTo("entity.4"));
            Assert.That(pressed["actionId"], Is.EqualTo("pilot.scoop"));
            Assert.That(sourcePayload["scalarValue"], Is.EqualTo("0"));
        }

        [Test]
        public void ValueFailsClosedWhenActionDoesNotAdvertiseValueContract()
        {
            var action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.dock",
                        Operation = "aetheria.daemon.commands.DockNearest",
                        Availability = "available"
                    }
                }
            }, "pilot.dock");

            Assert.Throws<InvalidOperationException>(() => action.BuildPayload("entity.4", 1f));
        }
    }
}
