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
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
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
    public string[] GrantedCapabilities { get; set; } = [];
    public string[] RevokedCapabilities { get; set; } = [];
    public int Version { get; set; } = 1;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class PlatformAdminRow
{
    public Guid UserId { get; set; }
    public bool IsActive { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RoleDefinitionRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Capabilities { get; set; } = [];
    public bool IsActive { get; set; }
    public int Version { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdminChangeRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public Guid? TenantId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public string CorrelationId { get; set; } = "";
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
    public DbSet<PlatformAdminRow> PlatformAdmins => Set<PlatformAdminRow>();
    public DbSet<RoleDefinitionRow> RoleDefinitions => Set<RoleDefinitionRow>();
    public DbSet<AdminChangeRow> AdminChanges => Set<AdminChangeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identity");
        modelBuilder.Entity<TenantRow>().ToTable("tenants");
        modelBuilder.Entity<FacilityRow>().ToTable("facilities");
        modelBuilder.Entity<StaffUserRow>().ToTable("staff_users");
        modelBuilder.Entity<PractitionerRow>().ToTable("practitioners");
        modelBuilder.Entity<AffiliationRow>().ToTable("affiliations");
        modelBuilder.Entity<SessionRow>().ToTable("sessions");
        modelBuilder.Entity<PlatformAdminRow>().ToTable("platform_admins").HasKey(p => p.UserId);
        modelBuilder.Entity<RoleDefinitionRow>().ToTable("role_definitions");
        var change = modelBuilder.Entity<AdminChangeRow>().ToTable("admin_changes");
        change.Property(c => c.Before).HasColumnType("jsonb");
        change.Property(c => c.After).HasColumnType("jsonb");
        // Declared relationships (mirroring the SQL foreign keys) let EF order inserts in one SaveChanges, e.g. a new organisation,
        // its first facility and the change-log row that references it, all in a single transaction.
        modelBuilder.Entity<FacilityRow>().HasOne<TenantRow>().WithMany().HasForeignKey(f => f.TenantId);
        modelBuilder.Entity<PractitionerRow>().HasOne<StaffUserRow>().WithMany().HasForeignKey(p => p.UserId);
        modelBuilder.Entity<PractitionerRow>().HasOne<TenantRow>().WithMany().HasForeignKey(p => p.TenantId);
        modelBuilder.Entity<AffiliationRow>().HasOne<StaffUserRow>().WithMany().HasForeignKey(a => a.UserId);
        modelBuilder.Entity<AffiliationRow>().HasOne<TenantRow>().WithMany().HasForeignKey(a => a.TenantId);
        modelBuilder.Entity<AffiliationRow>().HasOne<FacilityRow>().WithMany().HasForeignKey(a => a.FacilityId);
        modelBuilder.Entity<PlatformAdminRow>().HasOne<StaffUserRow>().WithMany().HasForeignKey(p => p.UserId);
        modelBuilder.Entity<RoleDefinitionRow>().HasOne<TenantRow>().WithMany().HasForeignKey(r => r.TenantId);
        modelBuilder.Entity<AdminChangeRow>().HasOne<TenantRow>().WithMany().HasForeignKey(c => c.TenantId);
        modelBuilder.Entity<AdminChangeRow>().HasOne<StaffUserRow>().WithMany().HasForeignKey(c => c.ActorUserId);
        modelBuilder.ApplySnakeCaseColumns();
    }
}
