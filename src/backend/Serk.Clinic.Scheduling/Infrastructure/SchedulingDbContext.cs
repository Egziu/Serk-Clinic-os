using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Scheduling.Domain;

namespace Serk.Clinic.Scheduling.Infrastructure;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("scheduling");
        modelBuilder.Entity<Appointment>(e =>
        {
            e.ToTable("appointments");
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
