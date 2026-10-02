using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quadro.Core;
using Quadro.Web.Data;
using Quadro.Web.Realtime;

namespace Quadro.Tests;

/// <summary>
/// Sobe o app de verdade (servidor em memória, SQLite em arquivo temporário) e conecta clientes
/// SignalR reais. É aqui que se prova o "tempo real com várias pessoas", não no teste de unidade.
/// </summary>
public sealed class HubIntegrationTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quadro-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _app = null!;
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync()
    {
        _app = NewApp();
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> NewApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Quadro", $"Data Source={_dbPath}"));

    private async Task<(HubConnection Conn, JoinResult Join, ConcurrentQueue<BoardOp> Received)> Connect(string name, long lastSeq = 0, WebApplicationFactory<Program>? app = null)
    {
        app ??= _app;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, BoardHub.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        var received = new ConcurrentQueue<BoardOp>();
        conn.On<OpEnvelope>(BoardHub.OpMethod, e => received.Enqueue(e.Op));
        await conn.StartAsync();
        _connections.Add(conn);
        var join = await conn.InvokeAsync<JoinResult>("Join", DemoSeed.BoardId, name, lastSeq);
        return (conn, join, received);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task O_que_um_faz_chega_no_outro_com_o_mesmo_numero_de_sequencia()
    {
        var ana = await Connect("Ana");
        var bia = await Connect("Bia");
        Assert.Contains("Ana", bia.Join.Online);

        var result = await ana.Conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard("novo", "todo", "Revisar contrato") { Actor = "" });

        Assert.Equal(CommandStatus.Applied, result.Status);
        await Eventually(() => bia.Received.Any(o => o.Seq == result.Op!.Seq), "Bia recebeu a operação");
        var op = Assert.IsType<CardCreated>(bia.Received.Single(o => o.Seq == result.Op!.Seq));
        Assert.Equal("Revisar contrato", op.Title);
        Assert.Equal("Ana", op.Actor);
    }

    [Fact]
    public async Task Duas_pessoas_movendo_o_mesmo_cartao_ao_mesmo_tempo_uma_ganha_e_a_outra_recebe_conflito()
    {
        var ana = await Connect("Ana");
        var bia = await Connect("Bia");
        var card = ana.Join.Snapshot!.Card("c1")!;

        var results = await Task.WhenAll(
            ana.Conn.InvokeAsync<CommandResult>("MoveCard", new MoveCard(card.Id, card.Version, "doing", null, null) { Actor = "" }),
            bia.Conn.InvokeAsync<CommandResult>("MoveCard", new MoveCard(card.Id, card.Version, "review", null, null) { Actor = "" }));

        Assert.Single(results, r => r.Status == CommandStatus.Applied);
        var conflict = Assert.Single(results, r => r.Status == CommandStatus.Conflict);
        Assert.Equal(card.Version + 1, conflict.Current!.Version);
    }

    [Fact]
    public async Task Quem_reconecta_recebe_so_o_que_perdeu_e_na_ordem()
    {
        var bia = await Connect("Bia");
        var lastSeq = bia.Join.Snapshot!.Seq;
        await bia.Conn.StopAsync();

        var ana = await Connect("Ana");
        for (var i = 0; i < 3; i++)
            await ana.Conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard($"r{i}", "todo", $"Item {i}") { Actor = "" });

        var back = await Connect("Bia", lastSeq);
        Assert.Null(back.Join.Snapshot);
        Assert.Equal([lastSeq + 1, lastSeq + 2, lastSeq + 3], back.Join.Ops.Select(o => o.Seq));
    }

    [Fact]
    public async Task Nao_da_pra_agir_em_nome_de_outra_pessoa()
    {
        var ana = await Connect("Ana");
        var r = await ana.Conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard("x1", "todo", "Teste") { Actor = "Chefe" });
        Assert.Equal("Ana", r.Op!.Actor);
    }

    [Fact]
    public async Task Comando_antes_de_entrar_no_quadro_e_recusado()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_app.Server.BaseAddress, BoardHub.Path), o => { o.HttpMessageHandlerFactory = _ => _app.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; })
            .Build();
        await conn.StartAsync();
        _connections.Add(conn);
        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard("x", "todo", "x") { Actor = "" }));
        Assert.Contains("Join", ex.Message);
    }

    [Fact]
    public async Task Muitos_cartoes_criados_em_paralelo_saem_com_sequencia_continua_e_posicoes_unicas()
    {
        var people = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Connect($"P{i}")));
        var start = people[0].Join.Snapshot!.Seq;

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            people[i % 4].Conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard($"p{i}", "todo", $"Paralelo {i}") { Actor = "" })));

        var seqs = results.Select(r => r.Op!.Seq).OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, 40).Select(i => start + i), seqs);

        var state = await _app.Services.GetRequiredService<BoardEngine>().GetAsync(DemoSeed.BoardId);
        var positions = state!.CardsIn("todo").Select(c => c.Position).ToList();
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }

    [Fact]
    public async Task Log_de_operacoes_reconstroi_o_quadro_e_o_quadro_sobrevive_ao_reinicio()
    {
        var ana = await Connect("Ana");
        await ana.Conn.InvokeAsync<CommandResult>("CreateCard", new CreateCard("persist", "todo", "Sobrevive") { Actor = "" });
        var card = (await _app.Services.GetRequiredService<BoardEngine>().GetAsync(DemoSeed.BoardId))!.Card("c2")!;
        await ana.Conn.InvokeAsync<CommandResult>("MoveCard", new MoveCard(card.Id, card.Version, "done", null, null) { Actor = "" });
        await ana.Conn.InvokeAsync<CommandResult>("EditCard", new EditCard("persist", 1, "Sobrevive ao reinício", "com descrição") { Actor = "" });

        var live = (await _app.Services.GetRequiredService<BoardEngine>().GetAsync(DemoSeed.BoardId))!;

        // 1) Reconstruir só com o log dá o mesmo quadro que as tabelas.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
            var ops = await db.Ops.Where(o => o.BoardId == DemoSeed.BoardId).OrderBy(o => o.Seq).ToListAsync();
            var replayed = ops.Select(o => JsonSerializer.Deserialize<BoardOp>(o.Json, Json.Options)!)
                .Aggregate(BoardState.Empty(DemoSeed.BoardId, live.Name), BoardDecider.Apply);
            Assert.Equal(live.Seq, replayed.Seq);
            Assert.Equal(Normalize(live), Normalize(replayed));
        }

        // 2) Derruba o app e sobe outro no mesmo arquivo.
        foreach (var c in _connections) await c.DisposeAsync();
        _connections.Clear();
        await _app.DisposeAsync();
        _app = NewApp();
        var again = await Connect("Bia");
        Assert.Equal(Normalize(live), Normalize(again.Join.Snapshot!));
        Assert.Equal("com descrição", again.Join.Snapshot!.Card("persist")!.Description);
    }

    private static string Normalize(BoardState s) => JsonSerializer.Serialize(new
    {
        Columns = s.Columns.OrderBy(c => c.Id),
        Cards = s.Cards.OrderBy(c => c.Id),
    });

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
