namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Adds a new note to an order. The handler creates the entity and returns
/// Insert&lt;OrderNote&gt; — no pre-existing document required.</summary>
public sealed record AddOrderNoteCommand(Guid OrderId, string Text, string Author);
