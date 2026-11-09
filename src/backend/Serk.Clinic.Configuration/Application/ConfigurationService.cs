using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Configuration.Contracts;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Configuration.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Storage;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Application;

public sealed record AssetDto(Guid Id, string Kind, string ContentType, string Sha256, int Width, int Height, int SizeBytes, DateTimeOffset UploadedAt);

public sealed record FacilitySettingsDto(
    Guid FacilityId,
    string AccentColor,
    string DefaultClinicalPaper,
    bool AutoPrepareDocuments,
    AssetDto? Logo,
    AssetDto? Stamp,
    int Version);

public sealed record UpdateSettingsRequest(string? AccentColor, PaperSize? DefaultClinicalPaper, bool? AutoPrepareDocuments);

public sealed record PrinterProfileDto(Guid Id, string Name, string Kind, string Paper, string Purpose, bool IsDefault, double WidthMm, double HeightMm, int Version);

public sealed record PrinterProfileRequest(string? Name, PaperSize? Paper, PrintPurpose? Purpose, bool IsDefault);

public sealed record AssetContent(byte[] Bytes, string ContentType, string Sha256);

public sealed class ConfigurationService(DbSession session, RequestContext context, AuditTrail audit, IObjectStore objects, TimeProvider clock) : IBrandingReader
{
    // ---------- settings ----------
    public async Task<Result<FacilitySettingsDto>> GetSettingsAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.FacilityId == actor.FacilityId && s.TenantId == actor.TenantId, ct)
            ?? new FacilitySettingsRow { FacilityId = actor.FacilityId, TenantId = actor.TenantId, Version = 0 };
        var assets = await ActiveFacilityAssetsAsync(db, actor.FacilityId, ct);
        return ToDto(row, assets);
    }

    public async Task<Result<FacilitySettingsDto>> UpdateSettingsAsync(UpdateSettingsRequest request, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.FacilityId == actor.FacilityId && s.TenantId == actor.TenantId, ct);
        var currentVersion = row?.Version ?? 0;
        if (ifMatch is null || ifMatch != currentVersion)
        {
            return Error.Precondition("settings_changed", "Settings were changed by someone else (or If-Match missing). Reload and retry.");
        }

        if (row is null)
        {
            row = new FacilitySettingsRow { FacilityId = actor.FacilityId, TenantId = actor.TenantId, Version = 0 };
            db.Settings.Add(row);
        }

        if (request.AccentColor is not null)
        {
            var color = AccentColor.Validate(request.AccentColor);
            if (!color.IsSuccess)
            {
                return color.Error!;
            }

            row.AccentColor = color.Value;
        }

        if (request.DefaultClinicalPaper is { } paper)
        {
            if (!PaperRules.AllowedFor(PrintPurpose.ClinicalDocuments, paper))
            {
                return Error.Validation("thermal_not_allowed", "Clinical documents cannot default to thermal paper (not archival).");
            }

            row.DefaultClinicalPaper = paper.ToString();
        }

        if (request.AutoPrepareDocuments is { } auto)
        {
            row.AutoPrepareDocuments = auto;
        }

        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = actor.UserId;
        row.Version++;
        await audit.RecordAsync(new AuditEntry("settings.update", "facility_settings", actor.FacilityId, Metadata: new Dictionary<string, string>
        {
            ["accentColor"] = row.AccentColor,
            ["defaultClinicalPaper"] = row.DefaultClinicalPaper,
            ["autoPrepareDocuments"] = row.AutoPrepareDocuments.ToString(),
        }), ct);
        var dto = ToDto(row, await ActiveFacilityAssetsAsync(db, actor.FacilityId, ct));
        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Error.Precondition("settings_changed", "Settings were changed concurrently. Reload and retry.");
        }

        return dto;
    }

    // ---------- branding assets ----------
    public async Task<Result<AssetDto>> UploadAsync(BrandingAssetKind kind, byte[] bytes, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (kind == BrandingAssetKind.Signature && actor.PractitionerId is null)
        {
            return Error.Forbidden("not_practitioner", "Only registered practitioners can upload a signature.");
        }

        var inspected = ImageInspector.Inspect(bytes);
        if (!inspected.IsSuccess)
        {
            await audit.RecordSecurityEventAsync("branding.upload_rejected", inspected.Error!.Code, "branding_asset", null, ct);
            return inspected.Error!;
        }

        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var now = clock.GetUtcNow();
        var existing = kind == BrandingAssetKind.Signature
            ? await db.Assets.Where(a => a.TenantId == actor.TenantId && a.Kind == kind && a.PractitionerId == actor.PractitionerId && a.Status == "Active").ToListAsync(ct)
            : await db.Assets.Where(a => a.TenantId == actor.TenantId && a.FacilityId == actor.FacilityId && a.Kind == kind && a.Status == "Active").ToListAsync(ct);
        foreach (var old in existing)
        {
            old.Status = "Retired";
            old.RetiredAt = now;
        }

        await db.SaveChangesAsync(ct); // retire first: partial unique index allows one active asset

        var info = inspected.Value;
        var id = Guid.CreateVersion7();
        var ext = info.ContentType == "image/png" ? "png" : "jpg";
        var key = $"branding/{actor.TenantId:N}/{id:N}.{ext}";
        var row = new BrandingAssetRow
        {
            Id = id,
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            Kind = kind,
            PractitionerId = kind == BrandingAssetKind.Signature ? actor.PractitionerId : null,
            ContentType = info.ContentType,
            Sha256 = CanonicalJson.Sha256Hex(bytes),
            SizeBytes = bytes.Length,
            Width = info.Width,
            Height = info.Height,
            ObjectKey = key,
            Status = "Active",
            UploadedBy = actor.UserId,
            UploadedAt = now,
        };
        db.Assets.Add(row);
        await objects.PutAsync(key, bytes, ct);
        await audit.RecordAsync(new AuditEntry("branding.upload", "branding_asset", id, Metadata: new Dictionary<string, string> { ["kind"] = kind.ToString(), ["sha256"] = row.Sha256 }), ct);
        await session.CommitAsync(ct);
        return ToAssetDto(row);
    }

    public async Task<Result<AssetDto?>> GetMySignatureAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (actor.PractitionerId is null)
        {
            return Result<AssetDto?>.Ok(null);
        }

        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Kind == BrandingAssetKind.Signature && a.PractitionerId == actor.PractitionerId && a.Status == "Active", ct);
        return Result<AssetDto?>.Ok(row is null ? null : ToAssetDto(row));
    }

    /// <summary>
    /// Logo/stamp images are visible to any member of the facility. Signature images (forgery risk) only to their owner
    /// and to branding managers. Responses are private, no-store, nosniff.
    /// </summary>
    public async Task<Result<AssetContent>> GetContentAsync(Guid assetId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assetId && a.TenantId == actor.TenantId && a.FacilityId == actor.FacilityId, ct);
        if (row is null || (row.Kind == BrandingAssetKind.Signature && row.PractitionerId != actor.PractitionerId && !actor.Has(Capabilities.BrandingManage)))
        {
            return Error.NotFound();
        }

        var bytes = await objects.GetVerifiedAsync(row.ObjectKey, row.Sha256, ct);
        return new AssetContent(bytes, row.ContentType, row.Sha256);
    }

    // ---------- printers ----------
    public async Task<Result<IReadOnlyList<PrinterProfileDto>>> ListPrintersAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var rows = await db.Printers.AsNoTracking()
            .Where(p => p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId && p.IsActive)
            .OrderBy(p => p.Purpose).ThenByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .ToListAsync(ct);
        return rows.Select(ToPrinterDto).ToList();
    }

    public async Task<Result<PrinterProfileDto>> CreatePrinterAsync(PrinterProfileRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var v = new ValidationCollector()
            .Required(request.Name, "name").MaxLength(request.Name?.Trim(), 80, "name")
            .Require(request.Paper is not null, "paper", "Required.")
            .Require(request.Purpose is not null, "purpose", "Required.");
        if (v.HasErrors)
        {
            return v.ToError();
        }

        if (!PaperRules.AllowedFor(request.Purpose!.Value, request.Paper!.Value))
        {
            return Error.Validation("thermal_not_allowed", "Thermal printers cannot be used for clinical documents (not archival). Use them for appointment slips.",
                new Dictionary<string, string[]> { ["paper"] = ["Thermal paper is only for appointment slips."] });
        }

        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        if (request.IsDefault)
        {
            var defaults = await db.Printers.Where(p => p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId && p.Purpose == request.Purpose && p.IsDefault && p.IsActive).ToListAsync(ct);
            foreach (var d in defaults)
            {
                d.IsDefault = false;
                d.Version++;
            }

            await db.SaveChangesAsync(ct);
        }

        var row = new PrinterProfileRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            Name = request.Name!.Trim(),
            Kind = PaperRules.KindOf(request.Paper.Value),
            Paper = request.Paper.Value,
            Purpose = request.Purpose.Value,
            IsDefault = request.IsDefault,
            CreatedBy = actor.UserId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Printers.Add(row);
        await audit.RecordAsync(new AuditEntry("printer.create", "printer_profile", row.Id, Metadata: new Dictionary<string, string> { ["paper"] = row.Paper.ToString(), ["purpose"] = row.Purpose.ToString() }), ct);
        await session.CommitAsync(ct);
        return ToPrinterDto(row);
    }

    public async Task<Result<bool>> DeactivatePrinterAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Printers.SingleOrDefaultAsync(p => p.Id == id && p.TenantId == actor.TenantId && p.FacilityId == actor.FacilityId && p.IsActive, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        row.IsActive = false;
        row.IsDefault = false;
        row.Version++;
        await audit.RecordAsync(new AuditEntry("printer.deactivate", "printer_profile", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    // ---------- IBrandingReader (published contract) ----------
    public async Task<FacilityBranding> GetFacilityBrandingAsync(Guid facilityId, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.FacilityId == facilityId && s.TenantId == tenant, ct);
        var assets = await ActiveFacilityAssetsAsync(db, facilityId, ct);
        AssetRef? Ref(BrandingAssetKind k) => assets.TryGetValue(k, out var a) ? new AssetRef(a.Id, a.Sha256, a.ContentType) : null;
        return new FacilityBranding(row?.AccentColor ?? AccentColor.Default, Ref(BrandingAssetKind.Logo), Ref(BrandingAssetKind.Stamp), row?.DefaultClinicalPaper ?? "A4", row?.AutoPrepareDocuments ?? true);
    }

    public async Task<AssetRef?> GetActiveSignatureAsync(Guid practitionerId, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var a = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Kind == BrandingAssetKind.Signature && x.PractitionerId == practitionerId && x.Status == "Active", ct);
        return a is null ? null : new AssetRef(a.Id, a.Sha256, a.ContentType);
    }

    public async Task<byte[]> ReadAssetAsync(AssetRef asset, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == asset.Id && a.TenantId == tenant, ct)
            ?? throw new InvalidDataException("Branding asset not found.");
        if (!string.Equals(row.Sha256, asset.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Branding asset hash mismatch.");
        }

        return await objects.GetVerifiedAsync(row.ObjectKey, row.Sha256, ct);
    }

    private static async Task<Dictionary<BrandingAssetKind, BrandingAssetRow>> ActiveFacilityAssetsAsync(ConfigurationDbContext db, Guid facilityId, CancellationToken ct) =>
        (await db.Assets.AsNoTracking().Where(a => a.FacilityId == facilityId && a.Status == "Active" && a.Kind != BrandingAssetKind.Signature).ToListAsync(ct))
        .ToDictionary(a => a.Kind);

    private static FacilitySettingsDto ToDto(FacilitySettingsRow row, Dictionary<BrandingAssetKind, BrandingAssetRow> assets) =>
        new(row.FacilityId, row.AccentColor, row.DefaultClinicalPaper, row.AutoPrepareDocuments,
            assets.TryGetValue(BrandingAssetKind.Logo, out var l) ? ToAssetDto(l) : null,
            assets.TryGetValue(BrandingAssetKind.Stamp, out var s) ? ToAssetDto(s) : null,
            row.Version);

    private static AssetDto ToAssetDto(BrandingAssetRow a) => new(a.Id, a.Kind.ToString(), a.ContentType, a.Sha256, a.Width, a.Height, a.SizeBytes, a.UploadedAt);

    private static PrinterProfileDto ToPrinterDto(PrinterProfileRow p)
    {
        var (w, h) = PaperRules.Dimensions(p.Paper);
        return new PrinterProfileDto(p.Id, p.Name, p.Kind.ToString(), p.Paper.ToString(), p.Purpose.ToString(), p.IsDefault, w, h, p.Version);
    }
}
