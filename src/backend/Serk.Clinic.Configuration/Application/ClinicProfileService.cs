using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Configuration.Contracts;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Configuration.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Application;

public sealed record ClinicProfileDto(
    Guid FacilityId,
    string DisplayName,
    string LegalName,
    string Motto,
    string AddressLine1,
    string AddressLine2,
    string City,
    string Region,
    string Country,
    string PostalCode,
    string Phone,
    string AltPhone,
    string Email,
    string Website,
    string TaxId,
    string RegistrationNo,
    string LicenceNo,
    string OpeningHours,
    string ReceiptFooter,
    string DocumentFooter,
    string DefaultLanguage,
    string CalendarDisplay,
    string Currency,
    int Completeness,
    DateTimeOffset? UpdatedAt,
    int Version);

/// <summary>Full replacement of the profile (every field is sent; null means empty).</summary>
public sealed record UpdateClinicProfileRequest(
    string? DisplayName,
    string? LegalName,
    string? Motto,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? Country,
    string? PostalCode,
    string? Phone,
    string? AltPhone,
    string? Email,
    string? Website,
    string? TaxId,
    string? RegistrationNo,
    string? LicenceNo,
    string? OpeningHours,
    string? ReceiptFooter,
    string? DocumentFooter,
    string? DefaultLanguage,
    string? CalendarDisplay,
    string? Currency);

/// <summary>Owner/administrator setup of the clinic's identity: name, motto, address, contacts, registration numbers.</summary>
public sealed class ClinicProfileService(DbSession session, RequestContext context, AuditTrail audit, TimeProvider clock)
{
    public async Task<Result<ClinicProfileDto>> GetAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.FacilityId == actor.FacilityId && p.TenantId == actor.TenantId, ct);
        return ToDto(row ?? new FacilityProfileRow { FacilityId = actor.FacilityId, TenantId = actor.TenantId, Version = 0 }, row is null);
    }

    public async Task<Result<ClinicProfileDto>> UpdateAsync(UpdateClinicProfileRequest r, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var fields = Fields(r);
        var language = ClinicProfileRules.Clean(r.DefaultLanguage) is { Length: > 0 } l ? l : "en";
        var calendar = ClinicProfileRules.Clean(r.CalendarDisplay) is { Length: > 0 } c ? c : "Both";
        var currency = ClinicProfileRules.Clean(r.Currency).ToUpperInvariant() is { Length: > 0 } cur ? cur : "ETB";
        var valid = ClinicProfileRules.Validate(fields, language, calendar, currency);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        var db = await session.ContextAsync<ConfigurationDbContext>(ct);
        var row = await db.Profiles.SingleOrDefaultAsync(p => p.FacilityId == actor.FacilityId && p.TenantId == actor.TenantId, ct);
        if (ifMatch is null || ifMatch != (row?.Version ?? 0))
        {
            return Error.Precondition("profile_changed", "The clinic profile was changed by someone else (or If-Match missing). Reload and retry.");
        }

        var before = row is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Fields(row);
        if (row is null)
        {
            row = new FacilityProfileRow { FacilityId = actor.FacilityId, TenantId = actor.TenantId, Version = 0 };
            db.Profiles.Add(row);
        }

        Apply(row, fields);
        row.DefaultLanguage = language;
        row.CalendarDisplay = calendar;
        row.Currency = currency;
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = actor.UserId;
        row.Version++;
        var changed = fields.Where(f => before.GetValueOrDefault(f.Key, "") != f.Value).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
        await audit.RecordAsync(new AuditEntry("clinic_profile.update", "facility_profile", actor.FacilityId, Metadata: new Dictionary<string, string>
        {
            ["changedFields"] = string.Join(',', changed),
            ["version"] = row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }), ct);
        var dto = ToDto(row, false);
        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Error.Precondition("profile_changed", "The clinic profile was changed concurrently. Reload and retry.");
        }

        return dto;
    }

    internal static async Task<ClinicLetterhead> LetterheadAsync(ConfigurationDbContext db, Guid tenantId, Guid facilityId, CancellationToken ct)
    {
        var p = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x => x.FacilityId == facilityId && x.TenantId == tenantId, ct) ?? new FacilityProfileRow();
        return new ClinicLetterhead(p.DisplayName, p.Motto, p.AddressLine1, p.AddressLine2, p.City, p.Region, p.Country, p.Phone, p.Email, p.Website,
            p.TaxId, p.LicenceNo, p.DocumentFooter, p.ReceiptFooter, p.Currency);
    }

    private static Dictionary<string, string> Fields(UpdateClinicProfileRequest r) => new(StringComparer.Ordinal)
    {
        ["displayName"] = ClinicProfileRules.Clean(r.DisplayName),
        ["legalName"] = ClinicProfileRules.Clean(r.LegalName),
        ["motto"] = ClinicProfileRules.Clean(r.Motto),
        ["addressLine1"] = ClinicProfileRules.Clean(r.AddressLine1),
        ["addressLine2"] = ClinicProfileRules.Clean(r.AddressLine2),
        ["city"] = ClinicProfileRules.Clean(r.City),
        ["region"] = ClinicProfileRules.Clean(r.Region),
        ["country"] = ClinicProfileRules.Clean(r.Country),
        ["postalCode"] = ClinicProfileRules.Clean(r.PostalCode),
        ["phone"] = ClinicProfileRules.Clean(r.Phone),
        ["altPhone"] = ClinicProfileRules.Clean(r.AltPhone),
        ["email"] = ClinicProfileRules.Clean(r.Email),
        ["website"] = ClinicProfileRules.Clean(r.Website),
        ["taxId"] = ClinicProfileRules.Clean(r.TaxId),
        ["registrationNo"] = ClinicProfileRules.Clean(r.RegistrationNo),
        ["licenceNo"] = ClinicProfileRules.Clean(r.LicenceNo),
        ["openingHours"] = ClinicProfileRules.Clean(r.OpeningHours),
        ["receiptFooter"] = ClinicProfileRules.Clean(r.ReceiptFooter),
        ["documentFooter"] = ClinicProfileRules.Clean(r.DocumentFooter),
    };

    private static Dictionary<string, string> Fields(FacilityProfileRow p) => new(StringComparer.Ordinal)
    {
        ["displayName"] = p.DisplayName,
        ["legalName"] = p.LegalName,
        ["motto"] = p.Motto,
        ["addressLine1"] = p.AddressLine1,
        ["addressLine2"] = p.AddressLine2,
        ["city"] = p.City,
        ["region"] = p.Region,
        ["country"] = p.Country,
        ["postalCode"] = p.PostalCode,
        ["phone"] = p.Phone,
        ["altPhone"] = p.AltPhone,
        ["email"] = p.Email,
        ["website"] = p.Website,
        ["taxId"] = p.TaxId,
        ["registrationNo"] = p.RegistrationNo,
        ["licenceNo"] = p.LicenceNo,
        ["openingHours"] = p.OpeningHours,
        ["receiptFooter"] = p.ReceiptFooter,
        ["documentFooter"] = p.DocumentFooter,
    };

    private static void Apply(FacilityProfileRow p, Dictionary<string, string> f)
    {
        p.DisplayName = f["displayName"];
        p.LegalName = f["legalName"];
        p.Motto = f["motto"];
        p.AddressLine1 = f["addressLine1"];
        p.AddressLine2 = f["addressLine2"];
        p.City = f["city"];
        p.Region = f["region"];
        p.Country = f["country"].Length > 0 ? f["country"] : "Ethiopia";
        p.PostalCode = f["postalCode"];
        p.Phone = f["phone"];
        p.AltPhone = f["altPhone"];
        p.Email = f["email"];
        p.Website = f["website"];
        p.TaxId = f["taxId"];
        p.RegistrationNo = f["registrationNo"];
        p.LicenceNo = f["licenceNo"];
        p.OpeningHours = f["openingHours"];
        p.ReceiptFooter = f["receiptFooter"];
        p.DocumentFooter = f["documentFooter"];
    }

    private static ClinicProfileDto ToDto(FacilityProfileRow p, bool isNew) => new(
        p.FacilityId, p.DisplayName, p.LegalName, p.Motto, p.AddressLine1, p.AddressLine2, p.City, p.Region, p.Country, p.PostalCode,
        p.Phone, p.AltPhone, p.Email, p.Website, p.TaxId, p.RegistrationNo, p.LicenceNo, p.OpeningHours, p.ReceiptFooter, p.DocumentFooter,
        p.DefaultLanguage, p.CalendarDisplay, p.Currency, ClinicProfileRules.Completeness(Fields(p)), isNew ? null : p.UpdatedAt, p.Version);
}
