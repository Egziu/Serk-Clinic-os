using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Documents.Domain;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Documents.Infrastructure;

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : DbContext(options)
{
    public DbSet<DocumentVersion> Documents => Set<DocumentVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("documents");
        modelBuilder.Entity<DocumentVersion>(e =>
        {
            e.ToTable("document_versions");
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.PaperSize).HasConversion<string>();
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
