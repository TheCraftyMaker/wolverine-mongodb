using FluentAssertions;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands.Feedback;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Safety-net integration tests for the demo's non-<c>Id</c> identity-convention showcase:
/// <c>Insert&lt;CustomerFeedback&gt;</c> / <c>[Entity("FeedbackId")]</c> against a
/// <c>{TypeName}Id</c>-keyed entity, proving the 2026-07-07 review findings F6/F7 identity fix
/// end-to-end through the packaged <c>Wolverine.MongoDB</c> nupkg.
///
/// The <c>CustomerFeedback</c> document is read straight from MongoDB (bypassing Wolverine) to
/// independently verify persistence — the collection name is the library's un-prefixed entity
/// convention: <c>type.Name.ToLowerInvariant()</c> = <c>customerfeedback</c>.
/// </summary>
[Collection("orders")]
public class CustomerFeedbackFlowTests(OrdersFixture fixture)
{
    private const string FeedbackCollection = "customerfeedback";

    [Fact]
    public async Task Can_Submit_Customer_Feedback()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new SubmitCustomerFeedbackCommand(orderId, 5, "Fast delivery!"));

        var feedback = await mongo.GetCollection<CustomerFeedback>(FeedbackCollection)
            .Find(Builders<CustomerFeedback>.Filter.Eq(f => f.OrderId, orderId))
            .FirstOrDefaultAsync();

        feedback.Should().NotBeNull("Insert<CustomerFeedback> must persist the feedback via the generated entity frame");
        feedback!.OrderId.Should().Be(orderId);
        feedback.Rating.Should().Be(5);
        feedback.Comment.Should().Be("Fast delivery!");

        // The point of this test: the document's _id IS the CustomerFeedbackId value, in its
        // native Guid BSON type — not a driver-assigned ObjectId (the pre-F6/F7 failure mode).
        var byId = await mongo.GetCollection<CustomerFeedback>(FeedbackCollection)
            .Find(Builders<CustomerFeedback>.Filter.Eq(f => f.CustomerFeedbackId, feedback.CustomerFeedbackId))
            .FirstOrDefaultAsync();
        byId.Should().NotBeNull("the document's _id must be keyed by CustomerFeedbackId under the {TypeName}Id convention");

        var rawDoc = await mongo.GetCollection<MongoDB.Bson.BsonDocument>(FeedbackCollection)
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("OrderId", orderId))
            .FirstOrDefaultAsync();
        rawDoc.Should().NotBeNull();
        rawDoc!["_id"].BsonType.Should().Be(MongoDB.Bson.BsonType.Binary, "_id must be a native BSON Guid, not a server-assigned ObjectId");
        rawDoc["_id"].AsGuid.Should().Be(feedback.CustomerFeedbackId);
    }

    [Fact]
    public async Task Can_Acknowledge_Customer_Feedback_Via_Entity_Load()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new SubmitCustomerFeedbackCommand(orderId, 4, "Good, but late"));

        var created = await mongo.GetCollection<CustomerFeedback>(FeedbackCollection)
            .Find(Builders<CustomerFeedback>.Filter.Eq(f => f.OrderId, orderId))
            .FirstOrDefaultAsync();
        created.Should().NotBeNull();

        var result = await host.MessageBus().InvokeAsync<AcknowledgeCustomerFeedbackCommand.Result>(
            new AcknowledgeCustomerFeedbackCommand(created!.CustomerFeedbackId));

        result.Should().NotBeNull("[Entity(\"FeedbackId\")] must resolve the same {TypeName}Id-convention identity Insert wrote");
        result.FeedbackId.Should().Be(created.CustomerFeedbackId);
        result.Comment.Should().Be("Good, but late");
    }

    [Fact]
    public async Task Acknowledge_Missing_Feedback_Is_Skipped()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);

        var missingId = Guid.NewGuid();

        // Required=true (the [Entity] default): a missing entity short-circuits the handler
        // end-to-end rather than throwing — matching the library's entity_atomicity coverage.
        var act = async () => await host.MessageBus().InvokeAsync<AcknowledgeCustomerFeedbackCommand.Result?>(
            new AcknowledgeCustomerFeedbackCommand(missingId));

        await act.Should().NotThrowAsync();
    }
}
