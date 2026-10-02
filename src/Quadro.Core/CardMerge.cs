namespace Quadro.Core;

public sealed record CardDraft(string Title, string Description);

public sealed record MergeResult(CardDraft Draft, IReadOnlyList<string> BothChanged)
{
    public bool Clean => BothChanged.Count == 0;
}

/// <summary>
/// Junta o rascunho de quem estava editando com a versão que outra pessoa salvou no meio
/// (merge de três vias, campo a campo). Campo que você não mexeu vem da versão nova; campo que
/// só você mexeu fica com o seu. Se os dois mexeram no mesmo campo, fica o seu na tela e o campo
/// é apontado, pra pessoa decidir antes de salvar. Sem isso, salvar de novo depois do aviso de
/// conflito apagaria em silêncio o que a outra pessoa escreveu.
/// </summary>
public static class CardMerge
{
    public static MergeResult Merge(Card original, CardDraft mine, Card theirs)
    {
        var both = new List<string>();
        string Pick(string field, string baseValue, string mineValue, string theirValue)
        {
            var iChanged = mineValue != baseValue;
            var theyChanged = theirValue != baseValue;
            if (!iChanged) return theirValue;
            if (theyChanged && theirValue != mineValue) both.Add(field);
            return mineValue;
        }

        var title = Pick("título", original.Title, mine.Title.Trim(), theirs.Title);
        var description = Pick("descrição", original.Description, mine.Description.Trim(), theirs.Description);
        return new MergeResult(new CardDraft(title, description), both);
    }
}
