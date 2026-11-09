using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Identity.Infrastructure;

public sealed class TenantRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FacilityRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AddressLine { get; set; } = "";
    public string Phone { get; set; } = "";
    public string LicenseNumber { get; set; } = "";
    public string TimeZone { get; set; } = "";
}

public sealed class StaffUserRow
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PractitionerRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string ProfessionalTitle { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string LicenseNumber { get; set; } = "";
    public DateOnly LicenseExpiresOn { get; set; }
}

public sealed class AffiliationRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public string[] Roles { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
}

public sealed class SessionRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public string CsrfToken { get; set; } = "";
    public Guid? ActiveAffiliationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>Identity persistence. Uses its own pooled connection: authentication happens before tenant context exists.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<TenantRow> Tenants => Set<TenantRow>();
    public DbSet<FacilityRow> Facilities => Set<FacilityRow>();
    public DbSet<StaffUserRow> StaffUsers => Set<StaffUserRow>();
    public DbSet<PractitionerRow> Practitioners => Set<PractitionerRow>();
    public DbSet<AffiliationRow> Affiliations => Set<AffiliationRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identity");
        modelBuilder.Entity<TenantRow>().ToTable("tenants");
        modelBuilder.Entity<FacilityRow>().ToTable("facilities");
        modelBuilder.Entity<StaffUserRow>().ToTable("staff_users");
        modelBuilder.Entity<PractitionerRow>().ToTable("practitioners");
        modelBuilder.Entity<AffiliationRow>().ToTable("affiliations");
        modelBuilder.Entity<SessionRow>().ToTable("sessions");
        modelBuilder.ApplySnakeCaseColumns();
    }
}
