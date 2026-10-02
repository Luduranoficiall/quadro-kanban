using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quadro.Core;
using Quadro.Web.Data;

namespace Quadro.Web.Realtime;

public sealed record OpEnvelope(string BoardId, BoardOp Op);

/// <summary>
/// Único lugar que muda um quadro. Cada quadro tem uma fila de um por vez (semáforo): duas
/// pessoas soltando cartão no mesmo instante viram duas operações em sequência, com número de
/// sequência sem buraco, gravadas no banco antes de qualquer tela saber delas. Depois de
/// gravada, a operação sai pra todo mundo na mesma ordem: telas Blazor (evento) e clientes do
/// hub SignalR.
/// </summary>
public sealed class BoardEngine(IServiceScopeFactory scopes, ILogger<BoardEngine> log)
{
    public const int MaxCatchUpOps = 500;
    public const string SystemActor = "Quadro";

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public BoardState? State;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();

    /// <summary>Disparado dentro da fila do quadro, então chega na ordem do número de sequência.</summary>
    public event Func<OpEnvelope, Task>? OpApplied;

    public async Task<BoardState?> GetAsync(string boardId)
    {
        var slot = _slots.GetOrAdd(boardId, _ => new Slot());
        await slot.Gate.WaitAsync();
        try { return slot.State ??= await LoadAsync(boardId); }
        finally { slot.Gate.Release(); }
    }

    public async Task<CommandResult> ExecuteAsync(string boardId, BoardCommand command)
    {
        var slot = _slots.GetOrAdd(boardId, _ => new Slot());
        await slot.Gate.WaitAsync();
        try
        {
            var state = slot.State ??= await LoadAsync(boardId);
            if (state is null) return CommandResult.NotFound("Esse quadro não existe.");

            var result = BoardDecider.Decide(state, command);
            if (result.Status != CommandStatus.Applied) return result;

            var op = result.Op! with { Seq = state.Seq + 1 };
            state = await CommitAsync(state, op);

            // Muita inserção no mesmo ponto deixa a posição comprida; arruma na hora, ainda na fila.
            foreach (var columnId in BoardDecider.ColumnsNeedingRebalance(state).ToList())
                state = await CommitAsync(state, BoardDecider.Rebalance(state, columnId, SystemActor) with { Seq = state.Seq + 1 });

            slot.State = state;
            return result with { Op = op };
        }
        finally { slot.Gate.Release(); }
    }

    /// <summary>
    /// Operações depois de <paramref name="afterSeq"/>, pra quem reconectou alcançar o resto sem
    /// baixar o quadro inteiro. Null quando o buraco é grande demais: aí vale mais a foto completa.
    /// </summary>
    public async Task<IReadOnlyList<BoardOp>?> OpsSinceAsync(string boardId, long afterSeq)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
        var rows = await db.Ops.Where(o => o.BoardId == boardId && o.Seq > afterSeq)
            .OrderBy(o => o.Seq).Take(MaxCatchUpOps + 1).ToListAsync();
        if (rows.Count > MaxCatchUpOps) return null;
        return rows.Select(r => JsonSerializer.Deserialize<BoardOp>(r.Json, Json.Options)!).ToList();
    }

    public async Task CreateBoardAsync(string boardId, string name)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
        if (await db.Boards.AnyAsync(b => b.Id == boardId)) return;
        db.Boards.Add(new BoardRow { Id = boardId, Name = name });
        await db.SaveChangesAsync();
    }

    private async Task<BoardState> CommitAsync(BoardState state, BoardOp op)
    {
        var next = BoardDecider.Apply(state, op);
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Ops.Add(new OpRow { BoardId = state.BoardId, Seq = op.Seq, Json = JsonSerializer.Serialize(op, Json.Options), At = DateTimeOffset.UtcNow });
            await ApplyRowsAsync(db, state, next, op);
            var board = await db.Boards.SingleAsync(b => b.Id == state.BoardId);
            board.Seq = op.Seq;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        if (OpApplied is { } handlers)
        {
            var envelope = new OpEnvelope(state.BoardId, op);
            foreach (var handler in handlers.GetInvocationList().Cast<Func<OpEnvelope, Task>>())
            {
                try { await handler(envelope); }
                catch (Exception ex) { log.LogWarning(ex, "Falha ao avisar um ouvinte da operação {Seq}", op.Seq); }
            }
        }
        return next;
    }

    private static async Task ApplyRowsAsync(QuadroDb db, BoardState before, BoardState after, BoardOp op)
    {
        switch (op)
        {
            case ColumnAdded o:
                db.Columns.Add(new ColumnRow { Id = o.ColumnId, BoardId = before.BoardId, Title = o.Title, Position = o.Position, WipLimit = o.WipLimit });
                break;
            case CardCreated o:
                db.Cards.Add(new CardRow { Id = o.CardId, BoardId = before.BoardId, ColumnId = o.ColumnId, Title = o.Title, Position = o.Position, Version = 1, UpdatedBy = o.Actor });
                break;
            case CardMoved or CardEdited:
            {
                var id = op is CardMoved m ? m.CardId : ((CardEdited)op).CardId;
                var row = await db.Cards.SingleAsync(c => c.Id == id);
                var card = after.Card(id)!;
                // Versão que o banco tem que ter agora; se outra instância mudou, SaveChanges falha.
                db.Entry(row).Property(r => r.Version).OriginalValue = before.Card(id)!.Version;
                row.ColumnId = card.ColumnId; row.Position = card.Position; row.Title = card.Title;
                row.Description = card.Description; row.Version = card.Version; row.UpdatedBy = card.UpdatedBy;
                break;
            }
            case CardDeleted o:
                db.Cards.Remove(await db.Cards.SingleAsync(c => c.Id == o.CardId));
                break;
            case ColumnRebalanced o:
                var ids = o.Positions.Select(p => p.CardId).ToList();
                foreach (var row in await db.Cards.Where(c => ids.Contains(c.Id)).ToListAsync())
                    row.Position = o.Positions.First(p => p.CardId == row.Id).Position;
                break;
        }
    }

    private async Task<BoardState?> LoadAsync(string boardId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
        var board = await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == boardId);
        if (board is null) return null;
        var columns = await db.Columns.AsNoTracking().Where(c => c.BoardId == boardId).ToListAsync();
        var cards = await db.Cards.AsNoTracking().Where(c => c.BoardId == boardId).ToListAsync();
        return new BoardState(board.Id, board.Name, board.Seq,
            columns.Select(c => new Column(c.Id, c.Title, c.Position, c.WipLimit)).ToList(),
            cards.Select(c => new Card(c.Id, c.ColumnId, c.Title, c.Description, c.Position, c.Version, c.UpdatedBy)).ToList());
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
