namespace Quadro.Core;

/// <summary>
/// Posição de cartão como texto que se ordena sozinho. Entre "a" e "b" sempre existe "aV", entre
/// "a" e "aV" existe "aG", e assim por diante. Mover um cartão grava uma posição nova só pra
/// ele, sem renumerar a coluna inteira; com várias pessoas mexendo ao mesmo tempo, isso evita
/// que um movimento sobrescreva a numeração que o outro acabou de gravar.
///
/// O alfabeto está em ordem ASCII, então a ordenação é a ordinal comum de string, a mesma que
/// o banco faz num ORDER BY. Posição nunca termina em '0' (o menor dígito): sem isso, existiria
/// posição sem espaço nenhum antes dela.
/// </summary>
public static class FractionalIndex
{
    public const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Uma posição estritamente entre <paramref name="before"/> e <paramref name="after"/> (null = ponta).</summary>
    public static string Between(string? before, string? after)
    {
        before ??= "";
        Validate(before, nameof(before));
        if (after is not null)
        {
            Validate(after, nameof(after));
            if (string.CompareOrdinal(before, after) >= 0)
                throw new ArgumentException($"'{before}' precisa vir antes de '{after}'.");
        }
        return Midpoint(before, after);
    }

    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            // Prefixo comum fica igual; a disputa é no primeiro dígito diferente.
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : Digits[0]) == b[n]) n++;
            if (n > 0) return b[..n] + Midpoint(n < a.Length ? a[n..] : "", b[n..]);
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Digits.Length;

        if (digitB - digitA > 1)
            return Digits[(digitA + digitB + 1) / 2].ToString();

        // Dígitos vizinhos: não cabe nada no meio nesse nível, desce um nível.
        if (b is not null && b.Length > 1) return b[..1];
        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : "", null);
    }

    /// <summary>
    /// <paramref name="count"/> posições igualmente espaçadas e curtas. Usado pra "rebalancear"
    /// uma coluna quando muita inserção no mesmo ponto deixou as posições compridas.
    /// </summary>
    public static IReadOnlyList<string> Spread(int count)
    {
        if (count <= 0) return [];
        var width = 1;
        // Folga de pelo menos 2 entre posições vizinhas, pra arredondamento nunca gerar duas iguais.
        while (Math.Pow(Digits.Length, width) < 2.0 * (count + 1)) width++;
        var space = Math.Pow(Digits.Length, width);
        var step = space / (count + 1);
        var keys = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            var value = (long)Math.Round(step * i);
            var chars = new char[width];
            for (var p = width - 1; p >= 0; p--)
            {
                chars[p] = Digits[(int)(value % Digits.Length)];
                value /= Digits.Length;
            }
            keys.Add(new string(chars).TrimEnd(Digits[0]));
        }
        return keys;
    }

    private static void Validate(string key, string name)
    {
        if (key.Length > 0 && key[^1] == Digits[0])
            throw new ArgumentException($"Posição '{key}' termina em '{Digits[0]}'.", name);
        foreach (var c in key)
            if (Digits.IndexOf(c) < 0) throw new ArgumentException($"Caractere inválido '{c}' em '{key}'.", name);
    }
}
