namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Deletes a note. Wolverine loads the entity via [Entity("NoteId")] and the
/// handler returns Delete&lt;OrderNote&gt;.</summary>
public sealed record DeleteOrderNoteCommand(string NoteId);
