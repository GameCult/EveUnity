using System.Buffers;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Eve.Surface.Tests;

[TestFixture]
public sealed class EveSurfaceSerializationCompatibilityTests
{
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
}
