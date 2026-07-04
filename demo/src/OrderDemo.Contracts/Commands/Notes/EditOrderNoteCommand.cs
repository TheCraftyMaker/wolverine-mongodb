namespace OrderDemo.Contracts.Commands.Notes;

/// <summary>Edits an existing note. Wolverine loads the entity via [Entity("NoteId")]
/// before invoking the handler. Returns Update&lt;OrderNote&gt; to persist changes.</summary>
public sealed record EditOrderNoteCommand(string NoteId, string NewText);
