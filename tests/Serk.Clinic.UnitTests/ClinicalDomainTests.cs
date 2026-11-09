using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Clinical.Domain;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.UnitTests;

/// <summary>EMR-002/003/004/005 domain rules. Clinical rules are provisional pending clinician review.</summary>
public class ClinicalDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Attending = Guid.NewGuid();
    private static readonly SignerCredentials Signer = new(Attending, "Dr. Test", "MD", "LIC-1", new DateOnly(2030, 1, 1));

    private static (Encounter E, NoteDraft D) Fresh()
    {
        var e = Encounter.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Attending, Now);
        var d = NoteDraft.CreateEmpty(e, Guid.NewGuid(), Now);
        Assert.True(d.Update(e, new NoteContentV1("Cough", "3 days", "Clear chest", "Viral URTI", "Fluids"), 1, Attending, Guid.NewGuid(), Now).IsSuccess);
        return (e, d);
    }

    [Fact]
    public void Draft_update_requires_matching_version()
    {
        var (e, d) = Fresh();
        var r = d.Update(e, NoteContentV1.Empty, 1, Attending, Guid.NewGuid(), Now);
        Assert.Equal("draft_conflict", r.Error!.Code);
        Assert.Equal("if_match_required", d.Update(e, NoteContentV1.Empty, null, Attending, Guid.NewGuid(), Now).Error!.Code);
    }

    [Fact]
    public void Only_attending_may_edit_draft()
    {
        var (e, d) = Fresh();
        Assert.Equal(ErrorKind.Forbidden, d.Update(e, NoteContentV1.Empty, 2, Guid.NewGuid(), Guid.NewGuid(), Now).Error!.Kind);
    }

    [Fact]
    public void Sign_produces_hashed_immutable_version_and_locks_draft()
    {
        var (e, d) = Fresh();
        var v = e.Sign(d, 2, true, Signer, "NoKnownAllergies", [], Now);
        Assert.True(v.IsSuccess);
        Assert.Equal(EncounterStatus.Signed, e.Status);
        Assert.Equal(1, v.Value.VersionNo);
        Assert.Equal(64, v.Value.ContentSha256.Length);
        Assert.True(v.Value.VerifyIntegrity());
        Assert.Equal(ErrorKind.Conflict, d.Update(e, NoteContentV1.Empty, 2, Attending, Guid.NewGuid(), Now).Error!.Kind);
        Assert.Equal("already_signed", e.Sign(d, 2, true, Signer, "NoKnownAllergies", [], Now).Error!.Code);
    }

    [Theory]
    [InlineData(false, "NoKnownAllergies", "allergies_not_reviewed")]
    [InlineData(true, "Unknown", "allergy_status_unknown")]
    public void Sign_requires_allergy_review_and_known_status(bool reviewed, string status, string code)
    {
        var (e, d) = Fresh();
        Assert.Equal(code, e.Sign(d, 2, reviewed, Signer, status, [], Now).Error!.Code);
        Assert.Equal(EncounterStatus.InProgress, e.Status);
    }

    [Fact]
    public void Sign_rejects_non_attending_expired_license_and_stale_draft()
    {
        var (e, d) = Fresh();
        Assert.Equal("not_attending", e.Sign(d, 2, true, Signer with { PractitionerId = Guid.NewGuid() }, "NoKnownAllergies", [], Now).Error!.Code);
        Assert.Equal("license_expired", e.Sign(d, 2, true, Signer with { LicenseExpiresOn = new DateOnly(2026, 11, 8) }, "NoKnownAllergies", [], Now).Error!.Code);
        Assert.Equal("draft_changed", e.Sign(d, 1, true, Signer, "NoKnownAllergies", [], Now).Error!.Code);
    }

    [Fact]
    public void Sign_requires_chief_complaint_and_assessment()
    {
        var e = Encounter.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Attending, Now);
        var d = NoteDraft.CreateEmpty(e, Guid.NewGuid(), Now);
        Assert.Equal("note_incomplete", e.Sign(d, 1, true, Signer, "NoKnownAllergies", [], Now).Error!.Code);
    }

    [Fact]
    public void Addendum_references_prior_version_and_requires_reason()
    {
        var (e, d) = Fresh();
        var original = e.Sign(d, 2, true, Signer, "HasAllergies", [new AllergySnapshot("Penicillin", "Rash", "Moderate")], Now).Value;
        Assert.False(e.AddAddendum(original, "Late result reviewed", "no", Signer, "HasAllergies", [], Now).IsSuccess);
        var add = e.AddAddendum(original, "Late result reviewed", "Lab result arrived after signing", Signer, "HasAllergies", [], Now.AddHours(1));
        Assert.True(add.IsSuccess);
        Assert.Equal(2, add.Value.VersionNo);
        Assert.Equal(original.Id, add.Value.AmendsVersionId);
        Assert.Equal(NoteVersionKind.Addendum, add.Value.Kind);
        Assert.True(original.VerifyIntegrity());
        Assert.Equal("Penicillin", original.ReadSnapshot().ActiveAllergiesAtSigning[0].Substance);
    }

    [Fact]
    public void Addendum_rejected_before_signing()
    {
        var (e, d) = Fresh();
        var fake = Encounter.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Attending, Now);
        var fd = NoteDraft.CreateEmpty(fake, Guid.NewGuid(), Now);
        fd.Update(fake, new NoteContentV1("a", "", "", "b", ""), 1, Attending, Guid.NewGuid(), Now);
        var v = fake.Sign(fd, 2, true, Signer, "NoKnownAllergies", [], Now).Value;
        Assert.Equal("not_signed", e.AddAddendum(v, "x", "reason here", Signer, "NoKnownAllergies", [], Now).Error!.Code);
    }

    [Fact]
    public void Tampered_snapshot_fails_integrity_check()
    {
        var (e, d) = Fresh();
        var v = e.Sign(d, 2, true, Signer, "NoKnownAllergies", [], Now).Value;
        var tampered = v.Content.Replace("Viral URTI", "Bacterial pneumonia", StringComparison.Ordinal);
        typeof(NoteVersion).GetProperty(nameof(NoteVersion.Content))!.SetValue(v, tampered);
        Assert.False(v.VerifyIntegrity());
    }

    [Fact]
    public void Canonical_hash_survives_jsonb_style_reordering()
    {
        var (e, d) = Fresh();
        var v = e.Sign(d, 2, true, Signer, "NoKnownAllergies", [], Now).Value;
        // PostgreSQL jsonb reorders keys and strips whitespace; verification re-serializes canonically from the typed record.
        var doc = System.Text.Json.JsonDocument.Parse(v.Content);
        var reordered = "{" + string.Join(",", doc.RootElement.EnumerateObject().Reverse().Select(p => $"\"{p.Name}\": {p.Value.GetRawText()}")) + "}";
        typeof(NoteVersion).GetProperty(nameof(NoteVersion.Content))!.SetValue(v, reordered);
        Assert.True(v.VerifyIntegrity());
    }
}
