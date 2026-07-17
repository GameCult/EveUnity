using System;
using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.input_capability", SchemaId)]
    [MessagePackObject]
    public sealed class EveInputCapabilityDocument
    {
        public const string SchemaId = "gamecult.eve.input_capability.v1";
        [Key(0)] public string Schema { get; set; } = SchemaId;
        [Key(1)] public string ProviderId { get; set; } = "";
        [Key(2)] public string CapabilityId { get; set; } = "";
        [Key(3)] public long Version { get; set; }
        [Key(4)] public EveInputActionDocument[] Actions { get; set; } = Array.Empty<EveInputActionDocument>();
        [Key(5)] public EveInputProfileDocument[] DefaultProfiles { get; set; } = Array.Empty<EveInputProfileDocument>();
    }

    [MessagePackObject]
    public sealed class EveInputActionDocument
    {
        [Key(0)] public string ActionId { get; set; } = "";
        [Key(1)] public string Label { get; set; } = "";
        [Key(2)] public string Operation { get; set; } = "";
        [Key(3)] public string Context { get; set; } = "pilot";
        [Key(4)] public string Category { get; set; } = "ship";
        [Key(5)] public string Availability { get; set; } = "available";
        [Key(6)] public string SourceRef { get; set; } = "";
        [Key(7)] public Dictionary<string, string> Payload { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        [Key(8)] public EveInputValueDocument? InputValue { get; set; }
    }

    [MessagePackObject]
    public sealed class EveInputValueDocument
    {
        [Key(0)] public string Model { get; set; } = "";
        [Key(1)] public string PayloadKey { get; set; } = "";
        [Key(2)] public string[] PayloadKeys { get; set; } = Array.Empty<string>();
    }

    [MessagePackObject]
    public sealed class EveInputProfileDocument
    {
        [Key(0)] public string ProfileId { get; set; } = "";
        [Key(1)] public string DeviceClass { get; set; } = "";
        [Key(2)] public EveInputBindingDocument[] Bindings { get; set; } = Array.Empty<EveInputBindingDocument>();
    }

    [MessagePackObject]
    public sealed class EveInputBindingDocument
    {
        [Key(0)] public string BindingId { get; set; } = "";
        [Key(1)] public string ActionId { get; set; } = "";
        [Key(2)] public EveInputGestureDocument Gesture { get; set; } = new EveInputGestureDocument();
        [Key(3)] public bool ActionBar { get; set; }
    }

    [MessagePackObject]
    public sealed class EveInputGestureDocument
    {
        [Key(0)] public string Kind { get; set; } = "direct";
        [Key(1)] public string[] Controls { get; set; } = Array.Empty<string>();
        [Key(2)] public int MaxStepIntervalMs { get; set; } = 650;
        [Key(3)] public string CompletionControl { get; set; } = "";
    }
}
