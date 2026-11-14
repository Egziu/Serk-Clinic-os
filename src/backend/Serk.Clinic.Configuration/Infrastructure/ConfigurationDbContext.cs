using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Configuration.Infrastructure;

public sealed class FacilitySettingsRow
{
    public Guid FacilityId { get; set; }
    public Guid TenantId { get; set; }
    public string AccentColor { get; set; } = Domain.AccentColor.Default;
    public string DefaultClinicalPaper { get; set; } = "A4";
    public bool AutoPrepareDocuments { get; set; } = true;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class BrandingAssetRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public BrandingAssetKind Kind { get; set; }
    public Guid? PractitionerId { get; set; }
    public string ContentType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ObjectKey { get; set; } = "";
    public string Status { get; set; } = "Active";
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class PrinterProfileRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; }
    public string Name { get; set; } = "";
    public PrinterKind Kind { get; set; }
    public PaperSize Paper { get; set; }
    public PrintPurpose Purpose { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class FacilityProfileRow
{
    public Guid FacilityId { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string Motto { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Country { get; set; } = "Ethiopia";
    public string PostalCode { get; set; } = "";
    public string Phone { get; set; } = "";
    public string AltPhone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Website { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string RegistrationNo { get; set; } = "";
    public string LicenceNo { get; set; } = "";
    public string OpeningHours { get; set; } = "";
    public string ReceiptFooter { get; set; } = "";
    public string DocumentFooter { get; set; } = "";
    public string DefaultLanguage { get; set; } = "en";
    public string CalendarDisplay { get; set; } = "Both";
    public string Currency { get; set; } = "ETB";
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options) : DbContext(options)
{
    public DbSet<FacilitySettingsRow> Settings => Set<FacilitySettingsRow>();
    public DbSet<BrandingAssetRow> Assets => Set<BrandingAssetRow>();
    public DbSet<PrinterProfileRow> Printers => Set<PrinterProfileRow>();
    public DbSet<FacilityProfileRow> Profiles => Set<FacilityProfileRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("configuration");
        modelBuilder.Entity<FacilitySettingsRow>(e =>
        {
            e.ToTable("facility_settings");
            e.HasKey(x => x.FacilityId);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<FacilityProfileRow>(e =>
        {
            e.ToTable("facility_profiles");
            e.HasKey(x => x.FacilityId);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<BrandingAssetRow>(e =>
        {
            e.ToTable("branding_assets");
            e.Property(x => x.Kind).HasConversion<string>();
        });
        modelBuilder.Entity<PrinterProfileRow>(e =>
        {
            e.ToTable("printer_profiles");
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Paper).HasConversion<string>();
            e.Property(x => x.Purpose).HasConversion<string>();
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.ApplySnakeCaseColumns();
    }
}
