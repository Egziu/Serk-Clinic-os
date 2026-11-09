using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Clinical.Domain;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Clinical.Infrastructure;

public sealed class ClinicalDbContext(DbContextOptions<ClinicalDbContext> options) : DbContext(options)
{
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<NoteDraft> NoteDrafts => Set<NoteDraft>();
    public DbSet<NoteVersion> NoteVersions => Set<NoteVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("clinical");
        modelBuilder.Entity<Encounter>(e =>
        {
            e.ToTable("encounters");
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<NoteDraft>(e =>
        {
            e.ToTable("note_drafts");
            e.HasKey(x => x.EncounterId);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<NoteVersion>(e =>
        {
            e.ToTable("note_versions");
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Content).HasColumnType("jsonb");
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
