using Quadro.Core;

namespace Quadro.Tests;

public class FractionalIndexTests
{
    private static void AssertValid(string key)
    {
        Assert.NotEmpty(key);
        Assert.NotEqual('0', key[^1]);
        Assert.All(key, c => Assert.Contains(c, FractionalIndex.Digits));
    }

    [Fact]
    public void Primeira_posicao_fica_no_meio_do_alfabeto()
    {
        var key = FractionalIndex.Between(null, null);
        AssertValid(key);
        Assert.Equal("V", key);
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("a", "a1")]
    [InlineData("1", "2")]
    [InlineData("0V", "1")]
    [InlineData("zz", null)]
    [InlineData(null, "01")]
    [InlineData("Az", "B")]
    public void Sempre_cabe_uma_posicao_no_meio(string? a, string? b)
    {
        var mid = FractionalIndex.Between(a, b);
        AssertValid(mid);
        if (a is not null) Assert.True(string.CompareOrdinal(a, mid) < 0, $"{a} < {mid}");
        if (b is not null) Assert.True(string.CompareOrdinal(mid, b) < 0, $"{mid} < {b}");
    }

    [Fact]
    public void Mil_insercoes_em_lugares_aleatorios_mantem_a_ordem()
    {
        var rng = new Random(7);
        var keys = new List<string> { FractionalIndex.Between(null, null) };
        for (var i = 0; i < 1000; i++)
        {
            var at = rng.Next(0, keys.Count + 1);
            var before = at == 0 ? null : keys[at - 1];
            var after = at == keys.Count ? null : keys[at];
            var key = FractionalIndex.Between(before, after);
            AssertValid(key);
            keys.Insert(at, key);
        }
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(keys.Max(k => k.Length) < 12, "posição aleatória não deveria crescer muito");
    }

    [Fact]
    public void Inserir_sempre_no_topo_cresce_devagar()
    {
        string? first = FractionalIndex.Between(null, null);
        for (var i = 0; i < 300; i++)
        {
            var key = FractionalIndex.Between(null, first);
            Assert.True(string.CompareOrdinal(key, first) < 0);
            AssertValid(key);
            first = key;
        }
        Assert.True(first!.Length <= 60);
    }

    [Fact]
    public void Inserir_sempre_no_mesmo_vao_e_o_pior_caso_e_por_isso_existe_o_rebalanceamento()
    {
        string a = "V", b = FractionalIndex.Between("V", null);
        for (var i = 0; i < 200; i++) b = FractionalIndex.Between(a, b);
        Assert.True(b.Length > BoardDecider.RebalanceThreshold);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(61)]
    [InlineData(500)]
    [InlineData(5000)]
    public void Espalhar_gera_posicoes_curtas_unicas_e_em_ordem(int count)
    {
        var keys = FractionalIndex.Spread(count);
        Assert.Equal(count, keys.Count);
        Assert.Equal(count, keys.Distinct().Count());
        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(keys, AssertValid);
        Assert.True(keys.Max(k => k.Length) <= 3);
    }

    [Theory]
    [InlineData("b", "a")]
    [InlineData("a", "a")]
    [InlineData("a0", null)]
    [InlineData("a-", null)]
    public void Recusa_entrada_invalida(string a, string? b) =>
        Assert.Throws<ArgumentException>(() => FractionalIndex.Between(a, b));
}
