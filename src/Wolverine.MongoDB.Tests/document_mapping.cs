using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

public class document_mapping
{
    [Fact]
    public void incoming_round_trips_core_fields()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 2;

        var doc = new IncomingMessage(envelope, envelope.Id.ToString());
        var read = doc.Read();

        read.Id.ShouldBe(envelope.Id);
        read.MessageType.ShouldBe(envelope.MessageType);
        read.Attempts.ShouldBe(2);
    }

    [Fact]
    public void outgoing_round_trips_core_fields()
    {
        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 5;

        var doc = new OutgoingMessage(envelope);
        var read = doc.Read();

        read.Id.ShouldBe(envelope.Id);
        read.OwnerId.ShouldBe(5);
    }
}
