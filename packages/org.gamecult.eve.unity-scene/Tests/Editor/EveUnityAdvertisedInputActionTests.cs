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

        [Test]
        public void UnavailableActionCanOnlyBeResolvedForReleaseCleanup()
        {
            var capability = new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.scoop",
                        Operation = "provider.commands.SetPower",
                        Availability = "unavailable",
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ButtonHoldValueModel,
                            PayloadKey = "active"
                        }
                    }
                }
            };

            Assert.Throws<InvalidOperationException>(() =>
                EveUnityAdvertisedInputAction.Resolve(capability, "pilot.scoop"));
            Assert.DoesNotThrow(() =>
                EveUnityAdvertisedInputAction.Resolve(capability, "pilot.scoop", requireAvailable: false)
                    .BuildPayload("entity.4", 0));
        }

        [Test]
        public void ViewDirectionUsesAdvertisedKeysAndNormalizesWithoutMutatingCapability()
        {
            var sourcePayload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "reticle"
            };
            var action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.target-reticle",
                        Operation = "provider.commands.TargetReticle",
                        Availability = "available",
                        Payload = sourcePayload,
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ViewDirectionValueModel,
                            PayloadKeys = new[] { "rayX", "rayY", "rayZ" }
                        }
                    }
                }
            }, "pilot.target-reticle");

            var payload = action.BuildViewDirectionPayload("entity.4", 3f, 4f, 0f);

            Assert.That(action.IsViewDirection, Is.True);
            Assert.That(payload["entityId"], Is.EqualTo("entity.4"));
            Assert.That(payload["actionId"], Is.EqualTo("pilot.target-reticle"));
            Assert.That(payload["mode"], Is.EqualTo("reticle"));
            Assert.That(float.Parse(payload["rayX"], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(0.6f).Within(0.000001f));
            Assert.That(float.Parse(payload["rayY"], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(0.8f).Within(0.000001f));
            Assert.That(float.Parse(payload["rayZ"], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(0f).Within(0.000001f));
            Assert.That(sourcePayload.ContainsKey("rayX"), Is.False);
            Assert.Throws<InvalidOperationException>(() => action.BuildPayload("entity.4", 1f));
        }

        [Test]
        public void ViewDirectionFailsClosedForMalformedKeysAndZeroVector()
        {
            var action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.target-reticle",
                        Operation = "provider.commands.TargetReticle",
                        Availability = "available",
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ViewDirectionValueModel,
                            PayloadKeys = new[] { "direction", "direction", "direction" }
                        }
                    }
                }
            }, "pilot.target-reticle");

            Assert.Throws<InvalidOperationException>(() => action.BuildViewDirectionPayload("entity.4", 0f, 0f, 1f));
            action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "pilot.target-reticle",
                        Operation = "provider.commands.TargetReticle",
                        Availability = "available",
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ViewDirectionValueModel,
                            PayloadKeys = new[] { "x", "y", "z" }
                        }
                    }
                }
            }, "pilot.target-reticle");
            Assert.Throws<ArgumentOutOfRangeException>(() => action.BuildViewDirectionPayload("entity.4", 0f, 0f, 0f));
        }

        [Test]
        public void ScalarUsesAdvertisedStateAndRangeWithoutMutatingCapability()
        {
            var sourcePayload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scalarValue"] = "300",
                ["equipmentIndex"] = "2"
            };
            var action = EveUnityAdvertisedInputAction.Resolve(new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "equipment.2.temperature",
                        Operation = "provider.commands.SetTemperature",
                        Availability = "available",
                        Payload = sourcePayload,
                        InputValue = new EveInputValueDocument
                        {
                            Model = EveUnityAdvertisedInputAction.ScalarValueModel,
                            PayloadKey = "scalarValue",
                            CurrentValue = 300,
                            MinimumValue = 100,
                            MaximumValue = 600,
                            StepValue = 5,
                            Unit = "kelvin"
                        }
                    }
                }
            }, "equipment.2.temperature");

            var payload = action.BuildScalarPayload("entity.4", 425.5);

            Assert.That(action.IsScalar, Is.True);
            Assert.That(action.CurrentValue, Is.EqualTo(300));
            Assert.That(action.Unit, Is.EqualTo("kelvin"));
            Assert.That(payload["scalarValue"], Is.EqualTo("425.5"));
            Assert.That(payload["equipmentIndex"], Is.EqualTo("2"));
            Assert.That(sourcePayload["scalarValue"], Is.EqualTo("300"));
            Assert.Throws<ArgumentOutOfRangeException>(() => action.BuildScalarPayload("entity.4", 99));
            Assert.Throws<ArgumentOutOfRangeException>(() => action.BuildScalarPayload("entity.4", 601));
            Assert.Throws<ArgumentOutOfRangeException>(() => action.BuildScalarPayload("entity.4", double.NaN));
        }
    }
}
