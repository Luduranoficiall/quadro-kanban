using Microsoft.AspNetCore.SignalR;
using Quadro.Core;

namespace Quadro.Web.Realtime;

public sealed record JoinResult(BoardState? Snapshot, IReadOnlyList<BoardOp> Ops, IReadOnlyList<string> Online);

/// <summary>
/// Porta de entrada SignalR pro quadro, pra qualquer cliente: app, script, outra integração. A
/// tela em Blazor Server usa o mesmo <see cref="BoardEngine"/> direto, então as duas formas de
/// mexer no quadro veem as mesmas operações, na mesma ordem.
///
/// Quem fez a ação é sempre o nome registrado nesta conexão, nunca o que o cliente manda no
/// comando: ninguém consegue mover cartão "como se fosse" outra pessoa.
/// </summary>
public sealed class BoardHub(BoardEngine engine, PresenceTracker presence) : Hub
{
    public const string Path = "/hubs/board";
    public const string OpMethod = "Op";
    public const string PresenceMethod = "Presence";

    public static string Group(string boardId) => $"board:{boardId}";

    /// <summary>
    /// Entra no quadro. Com <paramref name="lastSeq"/> &gt; 0 (reconexão), devolve só as operações
    /// perdidas; sem isso, ou se perdeu muita coisa, devolve a foto completa.
    /// </summary>
    public async Task<JoinResult> Join(string boardId, string name, long lastSeq)
    {
        var state = await engine.GetAsync(boardId) ?? throw new HubException("Esse quadro não existe.");
        var clean = string.IsNullOrWhiteSpace(name) ? "Anônimo" : name.Trim()[..Math.Min(name.Trim().Length, 40)];
        Context.Items["board"] = boardId;
        Context.Items["name"] = clean;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(boardId));
        var online = presence.Join(boardId, Context.ConnectionId, clean);
        await Clients.Group(Group(boardId)).SendAsync(PresenceMethod, online);

        if (lastSeq > 0 && lastSeq <= state.Seq)
        {
            var missed = await engine.OpsSinceAsync(boardId, lastSeq);
            if (missed is not null) return new JoinResult(null, missed, online);
        }
        return new JoinResult(state, [], online);
    }

    public Task<CommandResult> CreateCard(CreateCard command) => Run(command);
    public Task<CommandResult> MoveCard(MoveCard command) => Run(command);
    public Task<CommandResult> EditCard(EditCard command) => Run(command);
    public Task<CommandResult> DeleteCard(DeleteCard command) => Run(command);

    private Task<CommandResult> Run(BoardCommand command)
    {
        if (Context.Items["board"] is not string boardId || Context.Items["name"] is not string name)
            throw new HubException("Chame Join antes de mexer no quadro.");
        return engine.ExecuteAsync(boardId, command with { Actor = name });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["board"] is string boardId)
            await Clients.Group(Group(boardId)).SendAsync(PresenceMethod, presence.Leave(boardId, Context.ConnectionId));
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Repassa cada operação gravada pro grupo do quadro no SignalR.</summary>
public sealed class HubBroadcaster(IHubContext<BoardHub> hub)
{
    public Task SendAsync(OpEnvelope e) => hub.Clients.Group(BoardHub.Group(e.BoardId)).SendAsync(BoardHub.OpMethod, e);
}
