using System.Buffers;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Eve.Surface.Tests;

[TestFixture]
public sealed class EveSurfaceSerializationCompatibilityTests
{
    [Test]
    public void EntitySoaV2SerializesLogicalLayoutWithoutTransportFields()
    {
        var document = new EveEntitySoaViewDocument
        {
            ProviderId = "provider",
            ViewId = "entities",
            BodySchemaId = "entity.slab.v1",
            LayoutVersion = 2,
            ProducerEpoch = 3,
            Sequence = 4,
            Capacity = 8,
            Buffers = new[] { new EveEntitySoaBuffer { BufferId = "entity-hot", ByteLength = 64 } }
        };

        var bytes = MessagePackSerializer.Serialize(document, Options);
        var reader = new MessagePackReader(bytes);
        var restored = MessagePackSerializer.Deserialize<EveEntitySoaViewDocument>(bytes, Options);

        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(14));
        Assert.That(restored.Schema, Is.EqualTo(EveEntitySoaViewDocument.SchemaId));
        Assert.That(restored.Buffers[0].BufferId, Is.EqualTo("entity-hot"));
    }

    [Test]
    public void EntitySoaContractsExposeNoTransportAuthorityFields()
    {
        var forbidden = new[] { "Backend", "Location", "CapabilityToken", "Synchronization", "SynchronizationMode", "Descriptor" };
        var exposed = new[] { typeof(EveEntitySoaViewDocument), typeof(EveEntitySoaBuffer), typeof(EveEntitySoaColumn) }
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name);

        Assert.That(exposed, Has.None.Matches<string>(name => forbidden.Contains(name, StringComparer.Ordinal)));
    }

    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    [Test]
    public void LegacySevenFieldSurfaceDocumentDeserializes()
    {
        var bytes = WriteLegacySurfaceDocument();

        var document = MessagePackSerializer.Deserialize<EveSurfaceDocument>(bytes, Options);

        Assert.Multiple(() =>
        {
            Assert.That(document.Type, Is.EqualTo(EveSurfaceDocument.DefaultType));
            Assert.That(document.Schema, Is.EqualTo(EveSurfaceDocument.SchemaId));
            Assert.That(document.ProviderId, Is.EqualTo("legacy-provider"));
            Assert.That(document.ProviderKind, Is.EqualTo("legacy-kind"));
            Assert.That(document.Title, Is.EqualTo("Legacy surface"));
            Assert.That(document.Version, Is.EqualTo(17));
            Assert.That(document.UpdatedAtUtc, Is.EqualTo("2026-07-13T12:00:00Z"));
            Assert.That(document.Surface.Id, Is.EqualTo("legacy-surface"));
            Assert.That(document.Commands, Has.Count.EqualTo(1));
            Assert.That(document.Commands[0].Command, Is.EqualTo("legacy.execute"));
        });
    }

    [Test]
    public void LegacyDirectFiveStringCommandTemplateDeserializes()
    {
        var bytes = WriteLegacyCommandTemplate();

        var command = MessagePackSerializer.Deserialize<EveCommandTemplate>(bytes, Options);

        Assert.Multiple(() =>
        {
            Assert.That(command.Command, Is.EqualTo("legacy.execute"));
            Assert.That(command.Label, Is.EqualTo("Execute legacy command"));
            Assert.That(command.Operation.SchemaId, Is.EqualTo("gamecult.legacy.command.v1"));
            Assert.That(command.OperationRecord.RouteKind, Is.EqualTo("cultmesh"));
            Assert.That(command.OperationRecord.RouteDescription, Is.EqualTo("legacy.commands"));
            Assert.That(command.Transport, Is.EqualTo("legacy.commands"));
        });
    }

    [Test]
    public void CurrentSerializationWritesNineFieldSurfaceAndWrappedStructuredCommandBinding()
    {
        var document = CreateCurrentDocument();

        var bytes = MessagePackSerializer.Serialize(document, Options);
        var reader = new MessagePackReader(bytes);

        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(9));
        for (var field = 0; field < 8; field++)
            reader.Skip();
        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(1), "commands collection");
        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(1), "command template wrapper");
        Assert.That(reader.NextMessagePackType, Is.EqualTo(MessagePackType.Array), "structured operation binding");

        var roundTrip = MessagePackSerializer.Deserialize<EveSurfaceDocument>(bytes, Options);
        Assert.That(roundTrip.Commands[0].Command, Is.EqualTo("current.execute"));
    }

    [Test]
    public void LegacyExpandedProviderAdvertisementDeserializesAsCanonicalContract()
    {
        var document = MessagePackSerializer.Deserialize<EveProviderAdvertisementDocument>(
            WriteLegacyProviderAdvertisement(),
            Options);

        Assert.Multiple(() =>
        {
            Assert.That(document.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(document.ServiceId, Is.EqualTo("aetheria-daemon"));
            Assert.That(document.VerseId, Is.EqualTo("aetheria.local"));
            Assert.That(document.Title, Is.EqualTo("Aetheria"));
            Assert.That(document.CultMeshAddress, Is.EqualTo("cultmesh://aetheria.local/aetheria"));
            Assert.That(document.Surfaces[0].SurfaceId, Is.EqualTo("aetheria.pilot"));
            Assert.That(document.Surfaces[0].Schema, Is.EqualTo(EveSurfaceDocument.SchemaId));
            Assert.That(document.Commands[0].Command, Is.EqualTo("aetheria.pilot.move"));
            Assert.That(document.Commands[0].SurfaceId, Is.Empty);
        });
    }

    [Test]
    public void CurrentProviderAdvertisementSerializationWritesCanonicalThirteenFields()
    {
        var document = new EveProviderAdvertisementDocument(
            "provider", "service", "verse", "Title", "game.runtime", "cultmesh://verse/provider",
            "2026-07-13T14:00:00Z", new EveProviderFreshness("fresh", "2026-07-13T14:00:00Z", 15000),
            new[] { EveSurfaceDocument.SchemaId }, Array.Empty<EveProviderWitness>(),
            Array.Empty<EveAdvertisedSurface>(), Array.Empty<EveAdvertisedCommand>());

        var bytes = MessagePackSerializer.Serialize(document, Options);
        var reader = new MessagePackReader(bytes);

        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(13));
        Assert.That(MessagePackSerializer.Deserialize<EveProviderAdvertisementDocument>(bytes, Options).Title,
            Is.EqualTo("Title"));
    }

    private static EveSurfaceDocument CreateCurrentDocument() => new(
        "current-provider",
        "current-kind",
        "Current surface",
        23,
        "2026-07-13T13:00:00Z",
        CreateSurfaceTree("current-surface"),
        new[]
        {
            new EveCommandTemplate(new CultMeshOperationBindingRecord(
                "current.execute",
                "Execute current command",
                "gamecult.current.command.v1",
                "cultmesh",
                "current.commands"))
        });

    private static EveSurfaceTree CreateSurfaceTree(string id) => new(
        id,
        new EveSurfaceComponent(
            "root",
            "column",
            new Dictionary<string, string>(),
            Array.Empty<EveSurfaceComponent>()),
        Array.Empty<EveStyleToken>());

    private static byte[] WriteLegacySurfaceDocument()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(7);
        writer.Write("legacy-provider");
        writer.Write("legacy-kind");
        writer.Write("Legacy surface");
        writer.Write(17L);
        writer.Write("2026-07-13T12:00:00Z");
        writer.WriteRaw(MessagePackSerializer.Serialize(CreateSurfaceTree("legacy-surface"), Options));
        writer.WriteArrayHeader(1);
        writer.WriteRaw(WriteLegacyCommandTemplate());
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteLegacyCommandTemplate()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write("legacy.execute");
        writer.Write("Execute legacy command");
        writer.Write("gamecult.legacy.command.v1");
        writer.Write("cultmesh");
        writer.Write("legacy.commands");
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteLegacyProviderAdvertisement()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(16);
        writer.Write(EveProviderAdvertisementDocument.SchemaId);
        writer.Write("aetheria");
        writer.Write("aetheria-daemon");
        writer.Write("aetheria.local");
        writer.Write("asgard");
        writer.Write("aetheria");
        writer.Write("aetheria.local/aetheria");
        writer.Write("cultmesh://aetheria.local/aetheria");
        writer.Write("Aetheria");
        writer.Write("game.runtime");
        writer.Write("2026-07-13T14:00:00Z");
        writer.WriteArrayHeader(3);
        writer.Write("fresh");
        writer.Write("2026-07-13T14:00:00Z");
        writer.Write(15000);
        writer.WriteArrayHeader(1);
        writer.Write(EveSurfaceDocument.SchemaId);
        writer.WriteArrayHeader(0);
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(5);
        writer.Write(EveSurfaceDocument.SchemaId);
        writer.Write("aetheria.pilot");
        writer.Write("eve:surface:aetheria.pilot");
        writer.Write("cultmesh");
        writer.Write("available");
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(3);
        writer.Write("aetheria.pilot.move");
        writer.Write("cultmesh");
        writer.Write("Move the pilot ship");
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
