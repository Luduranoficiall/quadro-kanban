using Microsoft.EntityFrameworkCore;

namespace Quadro.Web.Data;

public sealed class BoardRow
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public long Seq { get; set; }
}

public sealed class ColumnRow
{
    public required string Id { get; set; }
    public required string BoardId { get; set; }
    public required string Title { get; set; }
    public required string Position { get; set; }
    public int? WipLimit { get; set; }
}

public sealed class CardRow
{
    public required string Id { get; set; }
    public required string BoardId { get; set; }
    public required string ColumnId { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string Position { get; set; }
    /// <summary>Token de concorrência: segunda trava, caso um dia rodem duas instâncias do app.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public int Version { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>Log de operações: tudo que aconteceu no quadro, em ordem. Dá pra reconstruir o quadro só com ele.</summary>
public sealed class OpRow
{
    public required string BoardId { get; set; }
    public long Seq { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class QuadroDb(DbContextOptions<QuadroDb> options) : DbContext(options)
{
    public DbSet<BoardRow> Boards => Set<BoardRow>();
    public DbSet<ColumnRow> Columns => Set<ColumnRow>();
    public DbSet<CardRow> Cards => Set<CardRow>();
    public DbSet<OpRow> Ops => Set<OpRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BoardRow>().HasKey(x => x.Id);
        b.Entity<ColumnRow>().HasKey(x => x.Id);
        b.Entity<ColumnRow>().HasIndex(x => x.BoardId);
        b.Entity<CardRow>().HasKey(x => x.Id);
        // Posição é comparada byte a byte (ordinal), igual ao FractionalIndex.
        b.Entity<CardRow>().Property(x => x.Position).UseCollation("BINARY");
        b.Entity<CardRow>().HasIndex(x => new { x.ColumnId, x.Position });
        b.Entity<OpRow>().HasKey(x => new { x.BoardId, x.Seq });
    }
}
