namespace OrderDemo.Contracts.Commands.Feedback;

/// <summary>Acknowledges feedback. Wolverine loads the entity via [Entity("FeedbackId")]
/// before invoking the handler — proving the read side resolves the same
/// <c>{TypeName}Id</c>-convention identity that Insert wrote.</summary>
public sealed record AcknowledgeCustomerFeedbackCommand(Guid FeedbackId)
{
    public sealed record Result(Guid FeedbackId, string Comment);
}
