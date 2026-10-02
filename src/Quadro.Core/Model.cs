using System.Text.Json.Serialization;

namespace Quadro.Core;

public sealed record Column(string Id, string Title, string Position, int? WipLimit);

public sealed record Card(string Id, string ColumnId, string Title, string Description, string Position, int Version, string UpdatedBy);

/// <summary>
/// Foto do quadro num instante. Imutável: aplicar uma operação devolve outra foto. O mesmo
/// código roda no servidor (fonte da verdade) e na tela (que aplica o que chega pelo SignalR),
/// então os dois nunca discordam sobre o que uma operação faz.
/// </summary>
public sealed record BoardState(string BoardId, string Name, long Seq, IReadOnlyList<Column> Columns, IReadOnlyList<Card> Cards)
{
    [JsonIgnore]
    public IEnumerable<Column> OrderedColumns => Columns.OrderBy(c => c.Position, StringComparer.Ordinal);

    public IReadOnlyList<Card> CardsIn(string columnId) =>
        Cards.Where(c => c.ColumnId == columnId).OrderBy(c => c.Position, StringComparer.Ordinal).ToList();

    public Card? Card(string id) => Cards.FirstOrDefault(c => c.Id == id);
    public Column? Column(string id) => Columns.FirstOrDefault(c => c.Id == id);

    public static BoardState Empty(string boardId, string name) => new(boardId, name, 0, [], []);
}
