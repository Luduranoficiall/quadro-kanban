using Quadro.Core;

namespace Quadro.Tests;

public class BoardDeciderTests
{
    /// <summary>Decide e aplica, como o servidor faz. Falha o teste se não for aplicado.</summary>
    private static BoardState Do(BoardState s, BoardCommand c)
    {
        var r = BoardDecider.Decide(s, c);
        Assert.True(r.Status == CommandStatus.Applied, r.Message);
        return BoardDecider.Apply(s, r.Op! with { Seq = s.Seq + 1 });
    }

    private static BoardState Board()
    {
        var s = BoardState.Empty("b", "Teste");
        s = Do(s, new AddColumn("todo", "A fazer", null) { Actor = "ana" });
        s = Do(s, new AddColumn("doing", "Fazendo", 2) { Actor = "ana" });
        foreach (var id in new[] { "a", "b", "c" })
            s = Do(s, new CreateCard(id, "todo", $"Cartão {id}") { Actor = "ana" });
        return s;
    }

    private static string[] Ids(BoardState s, string column) => s.CardsIn(column).Select(c => c.Id).ToArray();

    [Fact]
    public void Cria_cartoes_no_fim_da_coluna_com_sequencia_sem_buraco()
    {
        var s = Board();
        Assert.Equal(["a", "b", "c"], Ids(s, "todo"));
        Assert.Equal(5, s.Seq);
    }

    [Fact]
    public void Move_entre_dois_cartoes_e_so_o_cartao_movido_muda()
    {
        var s = Board();
        var before = s.Cards.ToDictionary(c => c.Id, c => c.Position);
        s = Do(s, new MoveCard("c", 1, "todo", AfterCardId: "a", BeforeCardId: null) { Actor = "bia" });
        Assert.Equal(["a", "c", "b"], Ids(s, "todo"));
        Assert.Equal(before["a"], s.Card("a")!.Position);
        Assert.Equal(before["b"], s.Card("b")!.Position);
        Assert.Equal(2, s.Card("c")!.Version);
        Assert.Equal("bia", s.Card("c")!.UpdatedBy);
    }

    [Fact]
    public void Versao_velha_vira_conflito_com_o_cartao_atual()
    {
        var s = Board();
        s = Do(s, new EditCard("a", 1, "Novo título", "") { Actor = "bia" });
        var r = BoardDecider.Decide(s, new MoveCard("a", 1, "doing", null, null) { Actor = "caio" });
        Assert.Equal(CommandStatus.Conflict, r.Status);
        Assert.Equal(2, r.Current!.Version);
        Assert.Contains("bia", r.Message);
    }

    [Fact]
    public void Limite_de_WIP_barra_cartao_novo_e_cartao_que_chega()
    {
        var s = Board();
        s = Do(s, new MoveCard("a", 1, "doing", null, null) { Actor = "ana" });
        s = Do(s, new MoveCard("b", 1, "doing", null, null) { Actor = "ana" });

        var move = BoardDecider.Decide(s, new MoveCard("c", 1, "doing", null, null) { Actor = "ana" });
        Assert.Equal(CommandStatus.Rejected, move.Status);
        Assert.Contains("limite", move.Message);
        Assert.Equal(CommandStatus.Rejected, BoardDecider.Decide(s, new CreateCard("d", "doing", "x") { Actor = "ana" }).Status);

        // Reordenar dentro da coluna cheia é permitido: não aumenta o trabalho em andamento.
        Do(s, new MoveCard("b", 2, "doing", null, "a") { Actor = "ana" });
    }

    [Fact]
    public void Vizinho_que_saiu_da_coluna_e_ignorado_em_vez_de_quebrar_a_ordem()
    {
        var s = Board();
        // Bia viu "a" na coluna e mandou "b depois de a". Antes disso, Caio levou "a" pra Fazendo.
        // O vizinho sumiu: o cartão vai pro fim da coluna, em vez de ganhar posição fora de ordem.
        s = Do(s, new MoveCard("a", 1, "doing", null, null) { Actor = "caio" });
        s = Do(s, new MoveCard("b", 1, "todo", AfterCardId: "a", BeforeCardId: null) { Actor = "bia" });
        Assert.Equal(["c", "b"], Ids(s, "todo"));
    }

    [Fact]
    public void Mover_pro_mesmo_lugar_nao_gera_operacao()
    {
        var s = Board();
        var r = BoardDecider.Decide(s, new MoveCard("c", 1, "todo", "b", null) { Actor = "ana" });
        Assert.Equal(CommandStatus.Rejected, r.Status);
    }

    [Fact]
    public void Editar_cartao_apagado_e_nao_encontrado()
    {
        var s = Do(Board(), new DeleteCard("b", 1) { Actor = "ana" });
        Assert.Equal(CommandStatus.NotFound, BoardDecider.Decide(s, new EditCard("b", 1, "x", "") { Actor = "bia" }).Status);
        Assert.Equal(["a", "c"], Ids(s, "todo"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Titulo_vazio_e_recusado(string title) =>
        Assert.Equal(CommandStatus.Rejected, BoardDecider.Decide(Board(), new CreateCard("x", "todo", title) { Actor = "ana" }).Status);

    [Fact]
    public void Operacao_repetida_nao_e_aplicada_duas_vezes()
    {
        var s = Board();
        var r = BoardDecider.Decide(s, new CreateCard("z", "todo", "Z") { Actor = "ana" });
        var op = r.Op! with { Seq = s.Seq + 1 };
        var once = BoardDecider.Apply(s, op);
        var twice = BoardDecider.Apply(once, op);
        Assert.Same(once, twice);
    }

    [Fact]
    public void Rebalancear_encurta_posicoes_sem_mudar_ordem_nem_versao()
    {
        var s = Board();
        // Sempre "logo depois de a": o pior caso pra posição.
        for (var i = 0; i < 200; i++)
            s = Do(s, new CreateCard($"n{i}", "todo", "x") { Actor = "ana" }) is var t
                ? Do(t, new MoveCard($"n{i}", 1, "todo", "a", null) { Actor = "ana" })
                : s;
        Assert.Contains("todo", BoardDecider.ColumnsNeedingRebalance(s));

        var order = Ids(s, "todo");
        var versions = s.Cards.ToDictionary(c => c.Id, c => c.Version);
        s = BoardDecider.Apply(s, BoardDecider.Rebalance(s, "todo", "Quadro") with { Seq = s.Seq + 1 });

        Assert.Equal(order, Ids(s, "todo"));
        Assert.All(s.Cards, c => Assert.Equal(versions[c.Id], c.Version));
        Assert.Empty(BoardDecider.ColumnsNeedingRebalance(s));
    }
}

public class CardMergeTests
{
    private static readonly Card Original = new("c", "todo", "Título", "", "V", 1, "ana");

    [Fact]
    public void Campo_que_eu_nao_mexi_vem_da_versao_nova()
    {
        var theirs = Original with { Description = "Usar o número da empresa", Version = 2, UpdatedBy = "bia" };
        var r = CardMerge.Merge(Original, new CardDraft("Título (Ana)", ""), theirs);
        Assert.True(r.Clean);
        Assert.Equal("Título (Ana)", r.Draft.Title);
        Assert.Equal("Usar o número da empresa", r.Draft.Description); // salvar de novo não apaga o da Bia
    }

    [Fact]
    public void Os_dois_mexeram_no_mesmo_campo_fica_o_meu_e_o_campo_e_apontado()
    {
        var theirs = Original with { Title = "Título (Bia)", Version = 2 };
        var r = CardMerge.Merge(Original, new CardDraft("Título (Ana)", ""), theirs);
        Assert.Equal(["título"], r.BothChanged);
        Assert.Equal("Título (Ana)", r.Draft.Title);
    }

    [Fact]
    public void Mesma_mudanca_dos_dois_lados_nao_e_conflito()
    {
        var theirs = Original with { Title = "Igual", Version = 2 };
        Assert.True(CardMerge.Merge(Original, new CardDraft("Igual", ""), theirs).Clean);
    }
}
