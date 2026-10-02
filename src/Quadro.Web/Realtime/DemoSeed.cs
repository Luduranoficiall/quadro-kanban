using Quadro.Core;

namespace Quadro.Web.Realtime;

/// <summary>
/// Quadro de demonstração, criado pelos mesmos comandos que a tela usa: o log de operações já
/// nasce consistente e dá pra reconstruir o quadro só com ele.
/// </summary>
public static class DemoSeed
{
    public const string BoardId = "demo";

    public static async Task EnsureAsync(BoardEngine engine)
    {
        var existing = await engine.GetAsync(BoardId);
        if (existing is { Columns.Count: > 0 }) return;
        await engine.CreateBoardAsync(BoardId, "Lançamento do site novo");

        const string who = "Quadro";
        (string Id, string Title, int? Wip)[] columns = [("todo", "A fazer", null), ("doing", "Fazendo", 3), ("review", "Revisão", 2), ("done", "Feito", null)];
        foreach (var (id, title, wip) in columns)
            await engine.ExecuteAsync(BoardId, new AddColumn(id, title, wip) { Actor = who });

        (string Column, string Title)[] cards =
        [
            ("todo", "Escrever textos da página de serviços"),
            ("todo", "Separar fotos do portfólio"),
            ("todo", "Configurar domínio e e-mail"),
            ("doing", "Montar layout da home no celular"),
            ("doing", "Integrar botão do WhatsApp"),
            ("review", "Formulário de orçamento"),
            ("done", "Definir paleta e tipografia"),
        ];
        var n = 0;
        foreach (var (column, title) in cards)
            await engine.ExecuteAsync(BoardId, new CreateCard($"c{++n}", column, title) { Actor = who });
    }
}
