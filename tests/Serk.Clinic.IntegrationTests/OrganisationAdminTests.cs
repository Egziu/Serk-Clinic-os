using System.Net;
using System.Text.Json;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Owners add staff and customise roles for their own organisation only.</summary>
[Collection(ApiCollection.Name)]
public class OrganisationAdminTests(SerkApiFixture f)
{
    private const string TempPassword = "Temporary-Pass-2026!";
    private const string Base = "/api/v1/organisation/admin";

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode == HttpStatusCode.NoContent ? default : await ApiClient.Body(r);
    }

    [Fact(DisplayName = "ORGADMIN-001 the owner adds a dentist with a temporary password; the new person must change it before anything else")]
    public async Task Owner_adds_staff()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var username = Unique("dentist.");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, $"{Base}/staff",
            new { username, displayName = "Dr. Synthetic", temporaryPassword = TempPassword, facilityId = DemoData.FacilityABole, roles = Array.Empty<string>() })).StatusCode);
        var created = await Ok(await owner.SendAsync(HttpMethod.Post, $"{Base}/staff",
            new { username, displayName = "Dr. Synthetic Dentist", temporaryPassword = TempPassword, facilityId = DemoData.FacilityABole, roles = new[] { "Dentist" } }), HttpStatusCode.Created);
        var id = created.GetProperty("summary").GetProperty("id").GetGuid();
        Assert.Equal("Dentist", created.GetProperty("assignments")[0].GetProperty("roles")[0].GetString());
        Assert.True(created.GetProperty("summary").GetProperty("mustChangePassword").GetBoolean());

        // Licence profile so the dentist can sign and chart.
        await Ok(await owner.SendAsync(HttpMethod.Put, $"{Base}/staff/{id}/practitioner",
            new { tenantId = DemoData.TenantA, professionalTitle = "Dentist", specialty = "General dentistry", licenseNumber = "SYN-DEN-77", licenseExpiresOn = "2028-12-31" }));

        using var newcomer = f.Client();
        var session = await newcomer.LoginAsync(username, TempPassword);
        Assert.True(session.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await newcomer.GetAsync("/api/v1/patients?q=Selam")).StatusCode);

        var log = await Ok(await owner.GetAsync($"{Base}/changes?targetId={id}"));
        Assert.Contains(log.Items(), c => c.GetProperty("action").GetString() == "assignment.create" && c.GetProperty("actor").GetString()!.Contains("Owner", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DemoData.TenantA, await f.OwnerScalarAsync<Guid>("SELECT tenant_id FROM identity.admin_changes WHERE target_id = @t AND action = 'staff.create'", ("t", id)));
    }

    [Fact(DisplayName = "ORGADMIN-002 custom roles are created in the owner's own organisation and can be assigned; preview shows warnings")]
    public async Task Owner_customises_roles()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var name = Unique("Dental assistant ");
        // Even if a different organisation is named, the role lands in the owner's own.
        var role = await Ok(await owner.SendAsync(HttpMethod.Post, $"{Base}/roles",
            new { tenantId = DemoData.TenantB, name, description = "Chairside assistant", capabilities = new[] { "patient.search", "patient.read.demographics", "appointment.read", "dental.chart.read" } }), HttpStatusCode.Created);
        Assert.Equal(DemoData.TenantA, role.GetProperty("tenantId").GetGuid());
        var roles = await Ok(await owner.GetAsync($"{Base}/organisations/{DemoData.TenantA}/roles"));
        Assert.Contains(roles.Items(), r => r.GetProperty("name").GetString() == name);

        var person = await Ok(await owner.SendAsync(HttpMethod.Post, $"{Base}/staff",
            new { username = Unique("assistant."), displayName = "Synthetic Assistant", temporaryPassword = TempPassword, facilityId = DemoData.FacilityABole, roles = new[] { role.GetProperty("code").GetString() } }), HttpStatusCode.Created);
        var assignment = person.GetProperty("assignments")[0];
        Assert.Contains(assignment.GetProperty("effectiveCapabilities").Items(), c => c.GetString() == "dental.chart.read");

        var preview = await Ok(await owner.SendAsync(HttpMethod.Post, $"{Base}/access-preview",
            new { tenantId = DemoData.TenantA, roles = new[] { "Dentist", "Owner" } }));
        Assert.Contains(preview.GetProperty("warnings").Items(), w => w.GetString()!.Contains("separation of duties", StringComparison.Ordinal));

        // Extra permission on one assignment, then removal.
        var updated = await Ok(await owner.SendAsync(HttpMethod.Put, $"{Base}/assignments/{assignment.GetProperty("id").GetGuid()}",
            new { facilityId = DemoData.FacilityABole, roles = new[] { role.GetProperty("code").GetString() }, grantedCapabilities = new[] { "care.desk" }, revokedCapabilities = new[] { "dental.chart.read" }, version = assignment.GetProperty("version").GetInt32(), reason = "Helps with follow-up calls" }));
        var effective = updated.GetProperty("effectiveCapabilities").Items().Select(c => c.GetString()).ToList();
        Assert.Contains("care.desk", effective);
        Assert.DoesNotContain("dental.chart.read", effective);
    }

    [Fact(DisplayName = "ORGADMIN-003 limits: never one's own access, never another organisation, shared accounts or super admins; other roles refused")]
    public async Task Organisation_admin_limits()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var self = DemoData.UserId(DemoData.Owner);
        var selfAssignment = (await Ok(await owner.GetAsync($"{Base}/staff/{self}"))).GetProperty("assignments")[0];
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(HttpMethod.Put, $"{Base}/assignments/{selfAssignment.GetProperty("id").GetGuid()}",
            new { facilityId = DemoData.FacilityABole, roles = new[] { "Owner", "Dentist" }, version = selfAssignment.GetProperty("version").GetInt32() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(HttpMethod.Post, $"{Base}/staff/{self}/reset-password", new { temporaryPassword = TempPassword })).StatusCode);

        // Other organisation's people and facilities are invisible.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{Base}/staff/{DemoData.UserId(DemoData.ReceptionB)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Post, $"{Base}/staff/{DemoData.UserId(DemoData.ReceptionB)}/active", new { isActive = false })).StatusCode);
        var list = await (await owner.GetAsync($"{Base}/staff")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("reception.b", list, StringComparison.Ordinal);
        var mine = await Ok(await owner.SendAsync(HttpMethod.Post, $"{Base}/staff",
            new { username = Unique("cashier."), displayName = "Synthetic Cashier", temporaryPassword = TempPassword, facilityId = DemoData.FacilityABole, roles = new[] { "Cashier" } }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Post, $"{Base}/staff/{mine.GetProperty("summary").GetProperty("id").GetGuid()}/assignments",
            new { facilityId = DemoData.FacilityBHawassa, roles = new[] { "Cashier" } })).StatusCode);

        // A person who also works for another organisation is changed only by the system administrator.
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var shared = mine.GetProperty("summary").GetProperty("id").GetGuid();
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{shared}/assignments", new { facilityId = DemoData.FacilityBHawassa, roles = new[] { "Cashier" } }), HttpStatusCode.Created);
        var refused = await owner.SendAsync(HttpMethod.Post, $"{Base}/staff/{shared}/reset-password", new { temporaryPassword = TempPassword });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("shared_account", (await ApiClient.Body(refused)).GetProperty("code").GetString());
        var detail = await Ok(await owner.GetAsync($"{Base}/staff/{shared}"));
        Assert.All(detail.GetProperty("assignments").Items(), a => Assert.Equal(DemoData.TenantA, a.GetProperty("tenantId").GetGuid())); // other organisation hidden

        foreach (var user in new[] { DemoData.Reception, DemoData.Clinician, DemoData.FinanceSupervisor })
        {
            using var c = await f.LoginAsync(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{Base}/staff")).StatusCode);
        }
    }
}
