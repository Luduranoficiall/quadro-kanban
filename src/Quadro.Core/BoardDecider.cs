namespace Quadro.Core;

/// <summary>
/// Núcleo funcional do quadro: <see cref="Decide"/> olha o estado e o comando e diz o que vai
/// acontecer (sem gravar nada); <see cref="Apply"/> aplica uma operação já decidida. Nenhum dos
/// dois toca em banco, rede ou relógio, então o comportamento inteiro é testável com dados.
/// </summary>
public static class BoardDecider
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 4000;

    /// <summary>Posição maior que isso indica muita inserção no mesmo ponto: hora de rebalancear.</summary>
    public const int RebalanceThreshold = 24;

    public static CommandResult Decide(BoardState state, BoardCommand command) => command switch
    {
        AddColumn c => DecideAddColumn(state, c),
        CreateCard c => DecideCreate(state, c),
        MoveCard c => DecideMove(state, c),
        EditCard c => DecideEdit(state, c),
        DeleteCard c => DecideDelete(state, c),
        _ => throw new NotSupportedException(command.GetType().Name),
    };

    private static CommandResult DecideAddColumn(BoardState state, AddColumn c)
    {
        var title = c.Title.Trim();
        if (title.Length == 0) return CommandResult.Rejected("A coluna precisa de um nome.");
        if (state.Column(c.ColumnId) is not null) return CommandResult.Rejected("Essa coluna já existe.");
        if (c.WipLimit is < 1) return CommandResult.Rejected("O limite de cartões precisa ser pelo menos 1.");
        var last = state.OrderedColumns.LastOrDefault()?.Position;
        return CommandResult.Applied(new ColumnAdded(c.ColumnId, title, FractionalIndex.Between(last, null), c.WipLimit) { Actor = c.Actor });
    }

    private static CommandResult DecideCreate(BoardState state, CreateCard c)
    {
        var title = c.Title.Trim();
        if (title.Length == 0) return CommandResult.Rejected("O cartão precisa de um título.");
        if (title.Length > MaxTitle) return CommandResult.Rejected($"Título com mais de {MaxTitle} caracteres.");
        var column = state.Column(c.ColumnId);
        if (column is null) return CommandResult.NotFound("Essa coluna não existe mais.");
        if (state.Card(c.CardId) is not null) return CommandResult.Rejected("Já existe um cartão com esse id.");
        if (WipFull(state, column, movingCardId: null)) return WipMessage(column);

        var last = state.CardsIn(column.Id).LastOrDefault()?.Position;
        return CommandResult.Applied(new CardCreated(c.CardId, column.Id, title, FractionalIndex.Between(last, null)) { Actor = c.Actor });
    }

    private static CommandResult DecideMove(BoardState state, MoveCard c)
    {
        var card = state.Card(c.CardId);
        if (card is null) return CommandResult.NotFound("Esse cartão foi apagado por outra pessoa.");
        if (card.Version != c.ExpectedVersion) return CommandResult.Conflict(card);
        var column = state.Column(c.ToColumnId);
        if (column is null) return CommandResult.NotFound("Essa coluna não existe mais.");
        if (column.Id != card.ColumnId && WipFull(state, column, card.Id)) return WipMessage(column);

        // Vizinhos como estão AGORA, sem o próprio cartão. Se um vizinho pedido saiu dessa coluna
        // (outra pessoa moveu), ele é ignorado em vez de gerar uma posição fora de ordem.
        var others = state.CardsIn(column.Id).Where(x => x.Id != card.Id).ToList();
        var after = others.FirstOrDefault(x => x.Id == c.AfterCardId);
        var before = others.FirstOrDefault(x => x.Id == c.BeforeCardId);

        string? lower, upper;
        if (after is not null)
        {
            lower = after.Position;
            upper = others.SkipWhile(x => x.Id != after.Id).Skip(1).FirstOrDefault()?.Position;
        }
        else if (before is not null)
        {
            upper = before.Position;
            lower = others.TakeWhile(x => x.Id != before.Id).LastOrDefault()?.Position;
        }
        else if (c.AfterCardId is null && c.BeforeCardId is not null && others.Count > 0)
        {
            lower = null; upper = others[0].Position; // pediu "antes de X" e X sumiu: vai pro topo
        }
        else
        {
            lower = others.LastOrDefault()?.Position; upper = null; // fim da coluna
        }

        var position = FractionalIndex.Between(lower, upper);
        if (position == card.Position && column.Id == card.ColumnId)
            return CommandResult.Rejected("O cartão já está nesse lugar.");
        return CommandResult.Applied(new CardMoved(card.Id, column.Id, position, card.Version + 1) { Actor = c.Actor });
    }

    private static CommandResult DecideEdit(BoardState state, EditCard c)
    {
        var card = state.Card(c.CardId);
        if (card is null) return CommandResult.NotFound("Esse cartão foi apagado por outra pessoa.");
        if (card.Version != c.ExpectedVersion) return CommandResult.Conflict(card);
        var title = c.Title.Trim();
        if (title.Length == 0) return CommandResult.Rejected("O cartão precisa de um título.");
        if (title.Length > MaxTitle) return CommandResult.Rejected($"Título com mais de {MaxTitle} caracteres.");
        if (c.Description.Length > MaxDescription) return CommandResult.Rejected($"Descrição com mais de {MaxDescription} caracteres.");
        return CommandResult.Applied(new CardEdited(card.Id, title, c.Description.Trim(), card.Version + 1) { Actor = c.Actor });
    }

    private static CommandResult DecideDelete(BoardState state, DeleteCard c)
    {
        var card = state.Card(c.CardId);
        if (card is null) return CommandResult.NotFound("Esse cartão já foi apagado.");
        if (card.Version != c.ExpectedVersion) return CommandResult.Conflict(card);
        return CommandResult.Applied(new CardDeleted(card.Id) { Actor = c.Actor });
    }

    private static bool WipFull(BoardState state, Column column, string? movingCardId) =>
        column.WipLimit is { } limit && state.Cards.Count(x => x.ColumnId == column.Id && x.Id != movingCardId) >= limit;

    private static CommandResult WipMessage(Column column) =>
        CommandResult.Rejected($"\"{column.Title}\" já tem {column.WipLimit} cartões, o limite da coluna. Termine um antes de puxar outro.");

    /// <summary>Aplica uma operação. Operação com Seq que já foi aplicada é ignorada (chegou duplicada).</summary>
    public static BoardState Apply(BoardState state, BoardOp op)
    {
        if (op.Seq != 0 && op.Seq <= state.Seq) return state;
        var seq = op.Seq == 0 ? state.Seq : op.Seq;

        return op switch
        {
            ColumnAdded o => state with { Seq = seq, Columns = [.. state.Columns, new Column(o.ColumnId, o.Title, o.Position, o.WipLimit)] },
            CardCreated o => state with { Seq = seq, Cards = [.. state.Cards, new Card(o.CardId, o.ColumnId, o.Title, "", o.Position, 1, o.Actor)] },
            CardMoved o => state with { Seq = seq, Cards = Replace(state, o.CardId, c => c with { ColumnId = o.ToColumnId, Position = o.Position, Version = o.NewVersion, UpdatedBy = o.Actor }) },
            CardEdited o => state with { Seq = seq, Cards = Replace(state, o.CardId, c => c with { Title = o.Title, Description = o.Description, Version = o.NewVersion, UpdatedBy = o.Actor }) },
            CardDeleted o => state with { Seq = seq, Cards = state.Cards.Where(c => c.Id != o.CardId).ToList() },
            ColumnRebalanced o => state with
            {
                Seq = seq,
                Cards = state.Cards.Select(c => o.Positions.FirstOrDefault(p => p.CardId == c.Id) is { } p ? c with { Position = p.Position } : c).ToList(),
            },
            _ => throw new NotSupportedException(op.GetType().Name),
        };
    }

    private static IReadOnlyList<Card> Replace(BoardState state, string id, Func<Card, Card> change) =>
        state.Cards.Select(c => c.Id == id ? change(c) : c).ToList();

    public static ColumnRebalanced Rebalance(BoardState state, string columnId, string actor)
    {
        var cards = state.CardsIn(columnId);
        var keys = FractionalIndex.Spread(cards.Count);
        return new ColumnRebalanced(columnId, cards.Select((c, i) => new CardPosition(c.Id, keys[i])).ToList()) { Actor = actor };
    }

    /// <summary>Colunas cujas posições ficaram compridas demais (muitas inserções no mesmo ponto).</summary>
    public static IEnumerable<string> ColumnsNeedingRebalance(BoardState state) =>
        state.Columns.Where(col => state.Cards.Any(c => c.ColumnId == col.Id && c.Position.Length > RebalanceThreshold)).Select(c => c.Id);
}
