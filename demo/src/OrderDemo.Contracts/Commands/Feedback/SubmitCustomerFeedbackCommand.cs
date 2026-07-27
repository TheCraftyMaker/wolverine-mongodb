namespace OrderDemo.Contracts.Commands.Feedback;

/// <summary>Submits feedback for an order. The handler creates the entity and returns
/// Insert&lt;CustomerFeedback&gt; — no pre-existing document required.</summary>
public sealed record SubmitCustomerFeedbackCommand(Guid OrderId, int Rating, string Comment);
