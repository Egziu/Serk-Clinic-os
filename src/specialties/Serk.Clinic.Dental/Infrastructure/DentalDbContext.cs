using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Dental.Domain;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Dental.Infrastructure;

public sealed class DentalDbContext(DbContextOptions<DentalDbContext> options) : DbContext(options)
{
    public DbSet<DentalFinding> Findings => Set<DentalFinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dental");
        modelBuilder.Entity<DentalFinding>(e =>
        {
            e.ToTable("findings");
            e.Property(x => x.Condition).HasConversion<string>();
            e.Property(x => x.ToothCode).HasColumnType("smallint");
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
