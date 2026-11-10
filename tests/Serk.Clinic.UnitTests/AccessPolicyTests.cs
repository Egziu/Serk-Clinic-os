using System.Reflection;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.UnitTests;

public class AccessPolicyTests
{
    private static readonly Dictionary<string, IReadOnlyCollection<string>> NoCustom = new(StringComparer.Ordinal);

    [Fact]
    public void Every_capability_constant_is_described_in_the_catalog_and_vice_versa()
    {
        var constants = typeof(Capabilities).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order().ToList();
        Assert.Equal(constants, CapabilityCatalog.All.Select(c => c.Code).Order().ToList());
        Assert.All(CapabilityCatalog.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void Built_in_roles_keep_the_safety_baseline()
    {
        // Management roles never carry clinical or patient-identifying capabilities.
        foreach (var code in new[] { RoleCatalog.ClinicAdmin, RoleCatalog.Owner })
        {
            var role = RoleCatalog.BuiltIns.Single(r => r.Code == code);
            Assert.DoesNotContain(role.Capabilities, c => CapabilityCatalog.Find(c)!.Sensitivity == CapabilitySensitivity.Clinical);
            Assert.DoesNotContain(Capabilities.PatientReadDemographics, role.Capabilities);
        }

        Assert.DoesNotContain(Capabilities.AllergyRead, RoleCatalog.BuiltIns.Single(r => r.Code == RoleCatalog.Receptionist).Capabilities);
        Assert.Equal(7, RoleCatalog.BuiltIns.Count);
    }

    [Fact]
    public void Effective_is_roles_plus_granted_minus_revoked()
    {
        var custom = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["custom.triage"] = [Capabilities.AllergyRead, Capabilities.PatientSearch] };
        var effective = AccessPolicy.Effective(
            [RoleCatalog.Receptionist, "custom.triage"], custom, [Capabilities.AnalyticsRead], [Capabilities.PatientSearch, Capabilities.AppointmentBook]);
        Assert.Contains(Capabilities.AllergyRead, effective);
        Assert.Contains(Capabilities.AnalyticsRead, effective);
        Assert.Contains(Capabilities.AppointmentCheckIn, effective);
        Assert.DoesNotContain(Capabilities.PatientSearch, effective);   // revoked wins over two roles granting it
        Assert.DoesNotContain(Capabilities.AppointmentBook, effective);
    }

    [Fact]
    public void Unknown_roles_and_uncatalogued_capabilities_grant_nothing()
    {
        var custom = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { ["custom.x"] = ["platform.admin", Capabilities.AppointmentRead] };
        Assert.Empty(AccessPolicy.Effective(["Superuser", "custom.missing"], NoCustom, ["platform.admin"], []));
        Assert.Equal([Capabilities.AppointmentRead], AccessPolicy.Effective(["custom.x"], custom, [], []).ToList());
    }

    [Theory]
    [InlineData("Senior nurse", "custom.senior-nurse")]
    [InlineData("  Lab / Imaging desk  ", "custom.lab-imaging-desk")]
    [InlineData("Ünïcode ✓ role", "custom.n-code-role")]
    public void Custom_role_codes_are_stable_slugs(string name, string code) => Assert.Equal(code, AccessPolicy.CodeFromName(name));

    [Theory]
    [InlineData("short", false)]
    [InlineData("aaaaaaaaaaaaaaaa", false)]          // too repetitive
    [InlineData("staff.one-Password-1", false)]       // contains username
    [InlineData("Correct-Horse-Battery-9", true)]
    public void Password_policy(string password, bool ok) => Assert.Equal(ok, AccessPolicy.ValidateNewPassword(password, "staff.one").IsSuccess);

    [Theory]
    [InlineData("reception.a", true)]
    [InlineData("Bad Name", false)]
    [InlineData("ab", false)]
    [InlineData("x.y_z-1", true)]
    public void Username_rules(string username, bool ok) => Assert.Equal(ok, AccessPolicy.ValidateUsername(username).IsSuccess);

    [Fact]
    public void Capability_validation_rejects_unknown_and_normalises()
    {
        Assert.Equal("unknown_capability", AccessPolicy.ValidateCapabilities(["patient.search", "platform.admin"], "c", false).Error!.Code);
        Assert.Equal("capabilities_required", AccessPolicy.ValidateCapabilities([], "c", false).Error!.Code);
        Assert.Equal(["appointment.read", "patient.search"], AccessPolicy.ValidateCapabilities(["patient.search", "appointment.read", "patient.search"], "c", false).Value);
    }
}
