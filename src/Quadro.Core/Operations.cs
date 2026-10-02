using System.Text.Json.Serialization;

namespace Quadro.Core;

// ---------- O que a pessoa pede (comando) ----------

public abstract record BoardCommand
{
    public required string Actor { get; init; }
}

public sealed record AddColumn(string ColumnId, string Title, int? WipLimit) : BoardCommand;

public sealed record CreateCard(string CardId, string ColumnId, string Title) : BoardCommand;

/// <summary>
/// Mover diz entre quais cartões o cartão deve cair, não a posição calculada na tela. Assim o
/// servidor calcula a posição com o quadro atualizado, mesmo que alguém tenha mexido nos
/// vizinhos um instante antes.
/// </summary>
public sealed record MoveCard(string CardId, int ExpectedVersion, string ToColumnId, string? AfterCardId, string? BeforeCardId) : BoardCommand;

public sealed record EditCard(string CardId, int ExpectedVersion, string Title, string Description) : BoardCommand;

public sealed record DeleteCard(string CardId, int ExpectedVersion) : BoardCommand;

// ---------- O que aconteceu (operação aplicada, com número de sequência) ----------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColumnAdded), "column-added")]
[JsonDerivedType(typeof(CardCreated), "card-created")]
[JsonDerivedType(typeof(CardMoved), "card-moved")]
[JsonDerivedType(typeof(CardEdited), "card-edited")]
[JsonDerivedType(typeof(CardDeleted), "card-deleted")]
[JsonDerivedType(typeof(ColumnRebalanced), "column-rebalanced")]
public abstract record BoardOp
{
    public long Seq { get; init; }
    public required string Actor { get; init; }
}

public sealed record ColumnAdded(string ColumnId, string Title, string Position, int? WipLimit) : BoardOp;
public sealed record CardCreated(string CardId, string ColumnId, string Title, string Position) : BoardOp;
public sealed record CardMoved(string CardId, string ToColumnId, string Position, int NewVersion) : BoardOp;
public sealed record CardEdited(string CardId, string Title, string Description, int NewVersion) : BoardOp;
public sealed record CardDeleted(string CardId) : BoardOp;

/// <summary>Posições novas e curtas pra todos os cartões da coluna, mesma ordem. Não muda versão: ninguém perde edição por causa disso.</summary>
public sealed record ColumnRebalanced(string ColumnId, IReadOnlyList<CardPosition> Positions) : BoardOp;

public sealed record CardPosition(string CardId, string Position);

// ---------- Resultado ----------

public enum CommandStatus { Applied, Conflict, Rejected, NotFound }

/// <summary>
/// Conflict: alguém mudou o cartão depois que você o viu (a versão não bate); vem junto o
/// cartão como está agora. Rejected: regra do quadro (limite de WIP, título vazio).
/// </summary>
public sealed record CommandResult(CommandStatus Status, BoardOp? Op, string? Message, Card? Current = null)
{
    public static CommandResult Applied(BoardOp op) => new(CommandStatus.Applied, op, null);
    public static CommandResult Conflict(Card current) =>
        new(CommandStatus.Conflict, null, $"{current.UpdatedBy} mudou esse cartão antes de você. O quadro já foi atualizado.", current);
    public static CommandResult Rejected(string message) => new(CommandStatus.Rejected, null, message);
    public static CommandResult NotFound(string message) => new(CommandStatus.NotFound, null, message);
}
