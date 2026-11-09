using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Patients.Domain;
using Serk.Clinic.Patients.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Patients.Application;

public sealed record PatientSummaryDto(Guid Id, string Mrn, string DisplayName, string Sex, DateOnly BirthDate, bool BirthDateIsEstimated, int AgeYears);

public sealed record AllergyDto(Guid Id, string Substance, string Reaction, string Severity, string Status, DateTimeOffset RecordedAt);

public sealed record PatientDetailDto(
    Guid Id,
    string Mrn,
    string GivenName,
    string FatherName,
    string GrandfatherName,
    string DisplayName,
    string Sex,
    DateOnly BirthDate,
    bool BirthDateIsEstimated,
    int AgeYears,
    string Phone,
    string? NationalId,
    string? AllergyStatus,
    IReadOnlyList<AllergyDto>? Allergies,
    int Version);

public sealed record DuplicateCandidateDto(Guid Id, string Mrn, string DisplayName, DateOnly BirthDate, string Strength, IReadOnlyList<string> Reasons);

public sealed record RegisterPatientRequest(
    string? GivenName,
    string? FatherName,
    string? GrandfatherName,
    Sex? Sex,
    DateOnly? BirthDate,
    bool BirthDateIsEstimated,
    string? Phone,
    string? NationalId,
    string? DuplicateOverrideReason);

/// <summary>Created, or a duplicate review is required before registration can proceed.</summary>
public sealed record RegisterPatientResponse(string Outcome, PatientDetailDto? Patient, IReadOnlyList<DuplicateCandidateDto> Candidates);

public sealed record AddAllergyRequest(string? Substance, string? Reaction, AllergySeverity? Severity);

public sealed record ChangeAllergyStatusRequest(AllergyRecordStatus? Status, string? Reason);

public sealed class PatientService(DbSession session, RequestContext context, AuditTrail audit, IdempotencyStore idempotency, TimeProvider clock) : IPatientDirectory
{
    public const int SearchLimit = 25;

    public async Task<Result<IReadOnlyList<PatientSummaryDto>>> SearchAsync(string? query, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var q = query?.Trim() ?? string.Empty;
        if (q.Length < 2 || q.Length > 100)
        {
            return Error.Validation("query_length", "Enter 2 to 100 characters.");
        }

        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var normalized = Patient.NormalizeName(q);
        var phone = Patient.NormalizePhone(q);
        var upper = q.ToUpperInvariant();
        // Explicit tenant predicate in addition to RLS (defence in depth).
        var rows = await db.Patients.AsNoTracking()
            .Where(p => p.TenantId == actor.TenantId)
            .Where(p => p.Mrn == upper || p.NationalId == upper || (phone.Length >= 6 && p.Phone == phone) || p.NormalizedName.StartsWith(normalized) || p.NormalizedName.Contains(" " + normalized))
            .OrderBy(p => p.NormalizedName).ThenBy(p => p.Mrn)
            .Take(SearchLimit)
            .ToListAsync(ct);
        var today = Today();
        await audit.RecordAsync(new AuditEntry("patient.search", "patient_search", actor.UserId, Metadata: new Dictionary<string, string> { ["resultCount"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct);
        await session.CommitAsync(ct);
        return rows.Select(p => Summary(p, today)).ToList();
    }

    public async Task<Result<RegisterPatientResponse>> RegisterAsync(RegisterPatientRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var registration = new PatientRegistration(request.GivenName, request.FatherName, request.GrandfatherName, request.Sex, request.BirthDate, request.BirthDateIsEstimated, request.Phone, request.NationalId);
        var valid = Patient.Validate(registration, Today());
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        if (request.DuplicateOverrideReason is { Length: > 500 })
        {
            return Error.Validation("override_reason_length", "Override reason must be at most 500 characters.");
        }

        var result = await idempotency.ExecuteAsync("patient.register", idempotencyKey, request, async innerCt =>
        {
            var db = await session.ContextAsync<PatientsDbContext>(innerCt);
            var probe = Patient.Register(valid.Value, Guid.Empty, actor.TenantId, actor.FacilityId, "probe", actor.UserId, clock.GetUtcNow());
            var firstTwo = string.Join(' ', probe.NormalizedName.Split(' ').Take(2));
            var candidates = await db.Patients.AsNoTracking()
                .Where(p => p.TenantId == actor.TenantId)
                .Where(p => (probe.NationalId != null && p.NationalId == probe.NationalId)
                            || p.NormalizedName.StartsWith(firstTwo)
                            || (probe.Phone != "" && p.Phone == probe.Phone))
                .Take(200)
                .ToListAsync(innerCt);
            var matches = DuplicateMatcher.Score(probe, candidates);
            if (matches.Any(m => m.Strength == DuplicateStrength.Definite)
                || (matches.Count > 0 && string.IsNullOrWhiteSpace(request.DuplicateOverrideReason)))
            {
                // Candidate identities are disclosed to the registrar: audit that disclosure even though nothing is created.
                await audit.RecordIndependentAsync(
                    new AuditEntry(
                        "patient.duplicate_candidates_disclosed",
                        "patient_registration",
                        Guid.CreateVersion7(),
                        Metadata: new Dictionary<string, string> { ["candidateIds"] = string.Join(",", matches.Select(m => m.Candidate.Id)) }),
                    innerCt);
                return Result<RegisterPatientResponse>.Ok(new RegisterPatientResponse("DuplicateReviewRequired", null, ToCandidates(matches)));
            }

            var mrn = await NextMrnAsync(actor.TenantId, innerCt);
            var patient = Patient.Register(valid.Value, Guid.CreateVersion7(), actor.TenantId, actor.FacilityId, mrn, actor.UserId, clock.GetUtcNow());
            db.Patients.Add(patient);
            await audit.RecordAsync(
                new AuditEntry(
                    "patient.register",
                    "patient",
                    patient.Id,
                    patient.Id,
                    Reason: matches.Count > 0 ? "duplicate_override" : null,
                    Metadata: new Dictionary<string, string> { ["candidateCount"] = matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                innerCt);
            return Result<RegisterPatientResponse>.Ok(new RegisterPatientResponse("Created", Detail(patient, [], includeClinical: false), ToCandidates(matches)));
        }, ct);

        if (result.IsSuccess && result.Value.Outcome == "Created")
        {
            try
            {
                await session.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                return Error.Conflict("duplicate_identifier", "A patient with this national ID was registered at the same time. Search before retrying.");
            }
        }

        return result;
    }

    public async Task<Result<PatientDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.TenantId == actor.TenantId, ct);
        if (patient is null)
        {
            return Error.NotFound();
        }

        var includeClinical = actor.Has(Capabilities.AllergyRead);
        var allergies = includeClinical
            ? await db.Allergies.AsNoTracking().Where(a => a.PatientId == id && a.TenantId == actor.TenantId).OrderByDescending(a => a.RecordedAt).ToListAsync(ct)
            : [];
        await audit.RecordAsync(new AuditEntry("patient.read", "patient", id, id, Metadata: new Dictionary<string, string> { ["clinicalFields"] = includeClinical ? "yes" : "no" }), ct);
        await session.CommitAsync(ct);
        return Detail(patient, allergies, includeClinical);
    }

    public async Task<Result<AllergyDto>> AddAllergyAsync(Guid patientId, AddAllergyRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == patientId && p.TenantId == actor.TenantId, ct);
        if (patient is null)
        {
            return Error.NotFound();
        }

        var allergy = Allergy.Record(patient, request.Substance, request.Reaction, request.Severity, actor.UserId, clock.GetUtcNow());
        if (!allergy.IsSuccess)
        {
            return allergy.Error!;
        }

        db.Allergies.Add(allergy.Value);
        await audit.RecordAsync(new AuditEntry("allergy.record", "allergy", allergy.Value.Id, patientId), ct);
        await session.CommitAsync(ct);
        var a = allergy.Value;
        return new AllergyDto(a.Id, a.Substance, a.Reaction, a.Severity.ToString(), a.Status.ToString(), a.RecordedAt);
    }

    public async Task<Result<AllergyDto>> ChangeAllergyStatusAsync(Guid patientId, Guid allergyId, ChangeAllergyStatusRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == patientId && p.TenantId == actor.TenantId, ct);
        var allergy = await db.Allergies.SingleOrDefaultAsync(a => a.Id == allergyId && a.PatientId == patientId && a.TenantId == actor.TenantId, ct);
        if (patient is null || allergy is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        var changed = allergy.ChangeStatus(request.Status, request.Reason, actor.UserId, now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        var othersActive = await db.Allergies.AnyAsync(a => a.PatientId == patientId && a.Id != allergyId && a.Status == AllergyRecordStatus.Active, ct);
        patient.ApplyAllergyCorrection(othersActive, actor.UserId, now);
        await audit.RecordAsync(new AuditEntry("allergy.status_change", "allergy", allergyId, patientId, Reason: request.Status.ToString(), Metadata: new Dictionary<string, string> { ["newStatus"] = request.Status.ToString()! }), ct);
        await session.CommitAsync(ct);
        return new AllergyDto(allergy.Id, allergy.Substance, allergy.Reaction, allergy.Severity.ToString(), allergy.Status.ToString(), allergy.RecordedAt);
    }

    public async Task<Result<PatientDetailDto>> RecordNoKnownAllergiesAsync(Guid patientId, int? expectedVersion, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (expectedVersion is null)
        {
            return Error.Precondition("if_match_required", "If-Match with the current patient version is required.");
        }

        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == patientId && p.TenantId == actor.TenantId, ct);
        if (patient is null)
        {
            return Error.NotFound();
        }

        var hasActive = await db.Allergies.AnyAsync(a => a.PatientId == patientId && a.Status == AllergyRecordStatus.Active, ct);
        var updated = patient.RecordNoKnownAllergies(expectedVersion.Value, hasActive, actor.UserId, clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        await audit.RecordAsync(new AuditEntry("allergy.no_known_allergies", "patient", patientId, patientId), ct);
        await session.CommitAsync(ct);
        return Detail(patient, [], includeClinical: true);
    }

    // ---- IPatientDirectory (published contract) ----
    public async Task<bool> ExistsAsync(Guid patientId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        return await db.Patients.AnyAsync(p => p.Id == patientId && p.TenantId == actor.TenantId, ct);
    }

    public async Task<PatientBanner?> GetBannerAsync(Guid patientId, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == patientId && x.TenantId == tenant, ct);
        if (p is null)
        {
            return null;
        }

        var allergies = await db.Allergies.AsNoTracking()
            .Where(a => a.PatientId == patientId && a.TenantId == tenant && a.Status == AllergyRecordStatus.Active)
            .OrderBy(a => a.Substance)
            .Select(a => new AllergySummary(a.Substance, a.Reaction, a.Severity.ToString()))
            .ToListAsync(ct);
        return new PatientBanner(p.Id, p.Mrn, p.DisplayName, p.Sex.ToString(), p.BirthDate, p.BirthDateIsEstimated, p.AllergyStatus.ToString(), p.AllergyStatusUpdatedAt, allergies);
    }

    public async Task<IReadOnlyDictionary<Guid, PatientBanner>> GetBannersAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken ct)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Tenant context required.");
        if (patientIds.Count == 0)
        {
            return new Dictionary<Guid, PatientBanner>();
        }

        var ids = patientIds.Distinct().ToList();
        var db = await session.ContextAsync<PatientsDbContext>(ct);
        var patients = await db.Patients.AsNoTracking().Where(p => p.TenantId == tenant && ids.Contains(p.Id)).ToListAsync(ct);
        var allergies = (await db.Allergies.AsNoTracking()
                .Where(a => a.TenantId == tenant && ids.Contains(a.PatientId) && a.Status == AllergyRecordStatus.Active)
                .OrderBy(a => a.Substance)
                .ToListAsync(ct))
            .ToLookup(a => a.PatientId);
        return patients.ToDictionary(
            p => p.Id,
            p => new PatientBanner(p.Id, p.Mrn, p.DisplayName, p.Sex.ToString(), p.BirthDate, p.BirthDateIsEstimated, p.AllergyStatus.ToString(), p.AllergyStatusUpdatedAt,
                allergies[p.Id].Select(a => new AllergySummary(a.Substance, a.Reaction, a.Severity.ToString())).ToList()));
    }

    private async Task<string> NextMrnAsync(Guid tenantId, CancellationToken ct)
    {
        await using var cmd = await session.CommandAsync(
            """
            INSERT INTO patients.mrn_counters (tenant_id, next_value) VALUES (@t, 2)
            ON CONFLICT (tenant_id) DO UPDATE SET next_value = patients.mrn_counters.next_value + 1
            RETURNING next_value - 1
            """,
            ct);
        cmd.Parameters.AddWithValue("t", tenantId);
        var value = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return "MRN-" + value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Facility-local civil date (v1: default facility zone Africa/Addis_Ababa), not the UTC date.</summary>
    private DateOnly Today() => BusinessTime.LocalDate(clock.GetUtcNow(), BusinessTime.Resolve(null));

    private static PatientSummaryDto Summary(Patient p, DateOnly today) =>
        new(p.Id, p.Mrn, p.DisplayName, p.Sex.ToString(), p.BirthDate, p.BirthDateIsEstimated, Patient.AgeInYears(p.BirthDate, today));

    private PatientDetailDto Detail(Patient p, IReadOnlyList<Allergy> allergies, bool includeClinical) =>
        new(
            p.Id,
            p.Mrn,
            p.GivenName,
            p.FatherName,
            p.GrandfatherName,
            p.DisplayName,
            p.Sex.ToString(),
            p.BirthDate,
            p.BirthDateIsEstimated,
            Patient.AgeInYears(p.BirthDate, Today()),
            p.Phone,
            p.NationalId,
            includeClinical ? p.AllergyStatus.ToString() : null,
            includeClinical ? allergies.Select(a => new AllergyDto(a.Id, a.Substance, a.Reaction, a.Severity.ToString(), a.Status.ToString(), a.RecordedAt)).ToList() : null,
            p.Version);

    private static List<DuplicateCandidateDto> ToCandidates(IReadOnlyList<DuplicateMatch> matches) =>
        matches.Select(m => new DuplicateCandidateDto(m.Candidate.Id, m.Candidate.Mrn, m.Candidate.DisplayName, m.Candidate.BirthDate, m.Strength.ToString(), m.Reasons)).ToList();
}
