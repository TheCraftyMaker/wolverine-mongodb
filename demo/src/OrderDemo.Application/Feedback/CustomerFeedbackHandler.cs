using OrderDemo.Contracts.Commands.Feedback;
using OrderDemo.Infrastructure.Persistence;
using Wolverine.Persistence;

namespace OrderDemo.Application.Feedback;

/// <summary>
/// Demonstrates the Tier-1 generic entity persistence surface against a <c>{TypeName}Id</c>-keyed
/// entity (no member literally named <c>Id</c>) — the identity shape the 2026-07-07 review
/// findings F6/F7 fixed. Lives alongside <see cref="OrderDemo.Application.Notes.OrderNoteHandler"/>
/// as an independent example; <c>OrderNote</c> itself is untouched.
/// </summary>
public static class CustomerFeedbackHandler
{
    // Insert<CustomerFeedback> — same DetermineInsertFrame path OrderNoteHandler.Handle(AddOrderNoteCommand)
    // uses, but here the frame must resolve CustomerFeedbackId (not Id) to key the write.
    public static Insert<CustomerFeedback> Handle(SubmitCustomerFeedbackCommand cmd)
        => new(new CustomerFeedback
        {
            CustomerFeedbackId = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            Rating = cmd.Rating,
            Comment = cmd.Comment,
            SubmittedAt = DateTimeOffset.UtcNow
        });

    // [Entity("FeedbackId")] loads the CustomerFeedback whose _id == cmd.FeedbackId — resolved via
    // the {TypeName}Id convention. Proves the read side of the identity fix; the command itself is
    // a no-op passthrough so the safety-net test can assert the loaded instance is non-null and
    // matches what Insert wrote.
    public static AcknowledgeCustomerFeedbackCommand.Result Handle(
        AcknowledgeCustomerFeedbackCommand cmd, [Entity("FeedbackId")] CustomerFeedback feedback)
        => new(feedback.CustomerFeedbackId, feedback.Comment);
}
