using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Patients.Domain;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Patients.Infrastructure;

public sealed class PatientsDbContext(DbContextOptions<PatientsDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Allergy> Allergies => Set<Allergy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("patients");
        modelBuilder.Entity<Patient>(e =>
        {
            e.ToTable("patients");
            e.Ignore(p => p.DisplayName);
            e.Property(p => p.Sex).HasConversion<string>();
            e.Property(p => p.AllergyStatus).HasConversion<string>();
            e.Property(p => p.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<Allergy>(e =>
        {
            e.ToTable("allergies");
            e.Property(a => a.Severity).HasConversion<string>();
            e.Property(a => a.Status).HasConversion<string>();
            e.Property(a => a.Version).IsConcurrencyToken();
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
