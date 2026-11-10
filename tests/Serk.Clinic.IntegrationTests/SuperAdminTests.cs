using System.Net;
using System.Text.Json;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>
/// System super administration: who may use it, what it can and cannot reach, and the guardrails around it.
/// Each test provisions its own organisation/users so the shared synthetic demo data is never modified.
/// </summary>
[Collection(ApiCollection.Name)]
public class SuperAdminTests(SerkApiFixture f)
{
    private const string TempPassword = "Temporary-Pass-2026!";
    private const string NewPassword = "My-Own-Strong-Pass-77";

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode == HttpStatusCode.NoContent ? default : await ApiClient.Body(r);
    }

    private static async Task<string> Code(HttpResponseMessage r) => (await ApiClient.Body(r)).GetProperty("code").GetString()!;

    /// <summary>Creates an organisation with one facility; returns (tenantId, facilityId).</summary>
    private static async Task<(Guid Tenant, Guid Facility)> NewOrganisationAsync(ApiClient admin)
    {
        var org = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/organisations",
            new { name = Unique("Synthetic Test Clinic "), facilityCode = "MAIN", facilityName = "Main branch (synthetic)" }), HttpStatusCode.Created);
        return (org.GetProperty("id").GetGuid(), org.GetProperty("facilities")[0].GetProperty("id").GetGuid());
    }

    private static async Task<(Guid Id, string Username)> NewStaffAsync(ApiClient admin)
    {
        var username = Unique("staff.");
        var staff = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/staff",
            new { username, displayName = "Synthetic Staff " + username, temporaryPassword = TempPassword }), HttpStatusCode.Created);
        return (staff.GetProperty("summary").GetProperty("id").GetGuid(), username);
    }

    /// <summary>Signs in a freshly provisioned user and completes the forced password change.</summary>
    private async Task<ApiClient> FirstLoginAsync(string username)
    {
        var c = f.Client();
        var session = await c.LoginAsync(username, TempPassword);
        Assert.True(session.GetProperty("mustChangePassword").GetBoolean());
        await Ok(await c.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TempPassword, newPassword = NewPassword }));
        return c;
    }

    [Fact(DisplayName = "ACCESS-001 only super administrators reach system administration; clinic admins, owners, clinicians and support get 403")]
    public async Task Only_super_admins_reach_platform_api()
    {
        foreach (var (user, facility) in new (string, Guid?)[] { (DemoData.ClinicAdmin, null), (DemoData.Owner, null), (DemoData.Clinician, null), (DemoData.Support, null), (DemoData.ClinicAdminB, null) })
        {
            using var c = await f.LoginAsync(user, facility);
            var r = await c.GetAsync("/api/v1/platform/overview");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("platform_admin_required", await Code(r));
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(HttpMethod.Post, "/api/v1/platform/staff", new { username = "x.y.z", displayName = "x", temporaryPassword = TempPassword })).StatusCode);
        }

        using var anon = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/platform/overview")).StatusCode);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type='authorization.denied' AND outcome='denied:platform.admin'") >= 5);
    }

    [Fact(DisplayName = "ACCESS-002 the super administrator controls access but has no patient or clinical data access")]
    public async Task Super_admin_has_no_phi_access()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var session = await admin.GetJsonAsync("/api/v1/auth/session");
        Assert.True(session.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(0, session.GetProperty("affiliations").GetArrayLength());
        Assert.Equal(0, session.GetProperty("capabilities").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/platform/overview")).StatusCode);

        var patients = await admin.GetAsync("/api/v1/patients?query=Selam");
        Assert.Equal(HttpStatusCode.Forbidden, patients.StatusCode);
        Assert.Equal("facility_not_selected", await Code(patients));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/patients/{DemoData.PatientSelam}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/analytics/overview")).StatusCode);

        // Staff listings and details carry no password hashes or patient data.
        var raw = await (await admin.GetAsync("/api/v1/platform/staff")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("AQAAAA", raw, StringComparison.Ordinal); // ASP.NET Core password hash prefix
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Selam", raw, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ACCESS-003 provision organisation, custom role and person; overrides apply; forced password change gates access")]
    public async Task Provisioning_custom_role_and_overrides()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (tenant, facility) = await NewOrganisationAsync(admin);
        var role = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles", new
        {
            tenantId = tenant,
            name = "Triage nurse",
            description = "Synthetic test role",
            capabilities = new[] { "patient.search", "patient.read.demographics", "clinical.allergy.read" },
        }), HttpStatusCode.Created);
        Assert.Equal("custom.triage-nurse", role.GetProperty("code").GetString());

        var (userId, username) = await NewStaffAsync(admin);
        var preview = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/access-preview", new
        {
            tenantId = tenant,
            userId,
            roles = new[] { "custom.triage-nurse" },
            grantedCapabilities = new[] { "appointment.read" },
            revokedCapabilities = new[] { "patient.search" },
        }));
        var previewCodes = preview.GetProperty("effective").EnumerateArray().Select(c => c.GetProperty("code").GetString()).Order().ToList();
        Assert.Equal(["appointment.read", "clinical.allergy.read", "patient.read.demographics"], previewCodes);

        var assignment = await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments", new
        {
            facilityId = facility,
            roles = new[] { "custom.triage-nurse" },
            grantedCapabilities = new[] { "appointment.read" },
            revokedCapabilities = new[] { "patient.search" },
            reason = "Synthetic onboarding",
        }), HttpStatusCode.Created);
        Assert.Equal(previewCodes, assignment.GetProperty("effectiveCapabilities").EnumerateArray().Select(c => c.GetString()).ToList());

        // First sign-in: only the password change is possible.
        using var user = f.Client();
        var first = await user.LoginAsync(username, TempPassword);
        Assert.True(first.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(0, first.GetProperty("capabilities").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/appointments")).StatusCode);
        Assert.Equal("invalid_current_password", await Code(await user.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = "wrong-password-xx", newPassword = NewPassword })));
        Assert.Equal("weak_password", await Code(await user.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TempPassword, newPassword = "short" })));
        Assert.Equal("weak_password", await Code(await user.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TempPassword, newPassword = username + "-Long-123" })));
        var changed = await Ok(await user.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TempPassword, newPassword = NewPassword }));
        Assert.False(changed.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(previewCodes, changed.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList());

        // Granted permission works; the removed one is refused even though the role includes it.
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/v1/appointments?day={DateTime.UtcNow:yyyy-MM-dd}")).StatusCode);
        Assert.Equal("capability_required", await Code(await user.GetAsync("/api/v1/patients?query=abc")));

        // Every step is in the change log with before/after.
        var changes = (await admin.GetJsonAsync($"/api/v1/platform/changes?targetId={userId}")).EnumerateArray().Select(c => c.GetProperty("action").GetString()).ToList();
        Assert.Contains("staff.create", changes);
        Assert.Contains("assignment.create", changes);
    }

    [Fact(DisplayName = "ACCESS-004 role edits, overrides and deactivation take effect on the next request without re-login")]
    public async Task Changes_take_effect_immediately()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (tenant, facility) = await NewOrganisationAsync(admin);
        var role = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenant, name = "Front desk lite", capabilities = new[] { "appointment.read", "patient.search" } }), HttpStatusCode.Created);
        var (userId, username) = await NewStaffAsync(admin);
        var assignment = await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facility, roles = new[] { "custom.front-desk-lite" } }), HttpStatusCode.Created);
        using var user = await FirstLoginAsync(username);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/patients?query=abc")).StatusCode);

        // Remove patient.search from the role: refused on the very next request.
        await Ok(await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/roles/{role.GetProperty("id").GetGuid()}",
            new { name = "Front desk lite", description = "", capabilities = new[] { "appointment.read" }, isActive = true, version = 1 }));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/patients?query=abc")).StatusCode);

        // Stale role version is rejected (optimistic concurrency).
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/roles/{role.GetProperty("id").GetGuid()}",
            new { name = "Front desk lite", capabilities = new[] { "appointment.read" }, isActive = true, version = 1 })).StatusCode);

        // Deactivate the assignment: the session loses its facility access immediately.
        await Ok(await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/assignments/{assignment.GetProperty("id").GetGuid()}",
            new { facilityId = facility, roles = new[] { "custom.front-desk-lite" }, isActive = false, version = 1, reason = "Left the clinic" }));
        Assert.Equal("facility_not_selected", await Code(await user.GetAsync("/api/v1/appointments")));

        // Deactivate the account: the session is revoked outright.
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/active", new { isActive = false, reason = "Synthetic offboarding" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/session")).StatusCode);
        using var again = f.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await again.LoginRawAsync(username, NewPassword)).StatusCode);
    }

    [Fact(DisplayName = "ACCESS-005 super administrators cannot change their own access, account or password through administration")]
    public async Task No_self_modification()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var self = DemoData.UserId(DemoData.SuperAdmin);
        var r1 = await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{self}/assignments", new { facilityId = DemoData.FacilityABole, roles = new[] { "Clinician" } });
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        Assert.Equal("self_modification", await Code(r1));
        Assert.Equal("self_modification", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{self}/active", new { isActive = false })));
        Assert.Equal("self_modification", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{self}/reset-password", new { temporaryPassword = TempPassword })));
        Assert.Equal("self_modification", await Code(await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/staff/{self}/practitioner",
            new { tenantId = DemoData.TenantA, professionalTitle = "MD", specialty = "General", licenseNumber = "SYN-X", licenseExpiresOn = "2030-01-01" })));
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM identity.affiliations WHERE user_id=@u", ("u", self)));
    }

    [Fact(DisplayName = "ACCESS-006 granting or revoking super administration needs step-up password; revocation is immediate")]
    public async Task Super_admin_grant_and_revoke()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (userId, username) = await NewStaffAsync(admin);
        var denied = await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/grant", new { userId, confirmPassword = "not-my-password", reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("step_up_failed", await Code(denied));
        await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/grant",
            new { userId, confirmPassword = SerkApiFixture.DemoPassword, reason = "Second administrator (synthetic)" }), HttpStatusCode.NoContent);

        using var second = await FirstLoginAsync(username);
        Assert.True((await second.GetJsonAsync("/api/v1/auth/session")).GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/platform/overview")).StatusCode);
        // The new administrator cannot remove itself either.
        Assert.Equal("self_modification", await Code(await second.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/revoke", new { userId, confirmPassword = NewPassword })));

        await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/revoke",
            new { userId, confirmPassword = SerkApiFixture.DemoPassword, reason = "Test complete" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync("/api/v1/platform/overview")).StatusCode);
        var actions = (await admin.GetJsonAsync($"/api/v1/platform/changes?targetId={userId}")).EnumerateArray().Select(c => c.GetProperty("action").GetString()).ToList();
        Assert.Contains("super_admin.grant", actions);
        Assert.Contains("super_admin.revoke", actions);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type='super_admin.step_up_failed'") >= 1);
    }

    [Fact(DisplayName = "ACCESS-007 invalid administration input is rejected: unknown permissions, cross-organisation roles, conflicts, duplicates")]
    public async Task Validation_rules()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (tenantA, facilityA) = await NewOrganisationAsync(admin);
        var (tenantB, _) = await NewOrganisationAsync(admin);
        Assert.Equal("unknown_capability", await Code(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenantA, name = "Bad role", capabilities = new[] { "platform.admin" } })));
        Assert.Equal("capabilities_required", await Code(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenantA, name = "Empty role", capabilities = Array.Empty<string>() })));
        await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenantB, name = "Only in B", capabilities = new[] { "appointment.read" } }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenantB, name = "Only in B", capabilities = new[] { "appointment.read" } })).StatusCode);

        var (userId, username) = await NewStaffAsync(admin);
        Assert.Equal("unknown_role", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facilityA, roles = new[] { "custom.only-in-b" } })));
        Assert.Equal("override_conflict", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facilityA, roles = new[] { "Receptionist" }, grantedCapabilities = new[] { "appointment.read" }, revokedCapabilities = new[] { "appointment.read" } })));
        Assert.Equal("invalid_validity", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facilityA, roles = new[] { "Receptionist" }, validFrom = "2027-01-01T00:00:00Z", validUntil = "2026-01-01T00:00:00Z" })));
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facilityA, roles = new[] { "Receptionist" } }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facilityA, roles = new[] { "Receptionist" } })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/staff",
            new { username, displayName = "Duplicate", temporaryPassword = TempPassword })).StatusCode);
        Assert.Equal("weak_password", await Code(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/staff",
            new { username = Unique("weak."), displayName = "Weak", temporaryPassword = "short" })));
        Assert.Equal("invalid_username", await Code(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/staff",
            new { username = "Bad Name!", displayName = "Bad", temporaryPassword = TempPassword })));
    }

    [Fact(DisplayName = "ACCESS-008 an assignment that has not started yet grants nothing; reset password forces a change and ends sessions")]
    public async Task Validity_window_and_password_reset()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (_, facility) = await NewOrganisationAsync(admin);
        var (userId, username) = await NewStaffAsync(admin);
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facility, roles = new[] { "Receptionist" }, validFrom = DateTimeOffset.UtcNow.AddDays(7) }), HttpStatusCode.Created);
        using var user = await FirstLoginAsync(username);
        var session = await user.GetJsonAsync("/api/v1/auth/session");
        Assert.Equal(0, session.GetProperty("affiliations").GetArrayLength()); // not yet valid
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/appointments")).StatusCode);

        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/reset-password", new { temporaryPassword = TempPassword }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/session")).StatusCode);
        using var again = f.Client();
        Assert.True((await again.LoginAsync(username, TempPassword)).GetProperty("mustChangePassword").GetBoolean());
        var detail = await admin.GetJsonAsync($"/api/v1/platform/staff/{userId}");
        Assert.True(detail.GetProperty("summary").GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact(DisplayName = "ACCESS-009 the administration change log is append-only even for the database owner")]
    public async Task Change_log_is_append_only()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        await NewOrganisationAsync(admin);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM identity.admin_changes") >= 1);
        var update = await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("UPDATE identity.admin_changes SET action='tampered' RETURNING 1"));
        Assert.Contains("serk_immutable", update.MessageText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(() => f.OwnerScalarAsync<int>("DELETE FROM identity.admin_changes RETURNING 1"));
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM identity.admin_changes WHERE action='tampered'"));
    }

    private async Task<(Guid Id, string Username, ApiClient Client)> NewSuperAdminAsync(ApiClient admin)
    {
        var (id, username) = await NewStaffAsync(admin);
        await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/grant",
            new { userId = id, confirmPassword = SerkApiFixture.DemoPassword, reason = "test" }), HttpStatusCode.NoContent);
        return (id, username, await FirstLoginAsync(username));
    }

    [Fact(DisplayName = "ACCESS-010 two super admins removing each other at the same moment never leave the system without one")]
    public async Task Concurrent_cross_revocation_keeps_one_super_admin()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (b, _, bClient) = await NewSuperAdminAsync(admin);
        var (c, _, cClient) = await NewSuperAdminAsync(admin);
        var seed = DemoData.UserId(DemoData.SuperAdmin);
        // Make B and C the only usable super administrators for this test, then restore the seeded one.
        await f.OwnerScalarAsync<int>("UPDATE identity.platform_admins SET is_active=false WHERE user_id=@u RETURNING 1", ("u", seed));
        try
        {
            var results = await Task.WhenAll(
                bClient.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/revoke", new { userId = c, confirmPassword = NewPassword }),
                cClient.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/revoke", new { userId = b, confirmPassword = NewPassword }));
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.UnprocessableEntity || r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.BadRequest);
            Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM identity.platform_admins WHERE is_active AND user_id = ANY(@ids)", ("ids", new[] { b, c })));
        }
        finally
        {
            await f.OwnerScalarAsync<int>("UPDATE identity.platform_admins SET is_active=true WHERE user_id=@u RETURNING 1", ("u", seed));
            await f.OwnerScalarAsync<int>("UPDATE identity.platform_admins SET is_active=false WHERE user_id = ANY(@ids) RETURNING 1", ("ids", new[] { b, c }));
            bClient.Dispose();
            cClient.Dispose();
        }
    }

    [Fact(DisplayName = "ACCESS-011 repeated wrong step-up passwords lock the account and end its sessions")]
    public async Task Step_up_guessing_locks_out()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (b, username, bClient) = await NewSuperAdminAsync(admin);
        using (bClient)
        {
            var (target, _) = await NewStaffAsync(admin);
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal("step_up_failed", await Code(await bClient.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/grant", new { userId = target, confirmPassword = "guess-" + i })));
            }

            await bClient.SendAsync(HttpMethod.Post, "/api/v1/platform/super-admins/grant", new { userId = target, confirmPassword = "guess-final" });
            Assert.Equal(HttpStatusCode.Unauthorized, (await bClient.GetAsync("/api/v1/platform/overview")).StatusCode);
            using var again = f.Client();
            Assert.Equal(HttpStatusCode.BadRequest, (await again.LoginRawAsync(username, NewPassword)).StatusCode); // locked
            Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type='super_admin.step_up_lockout' AND user_id=@u", ("u", b)) >= 1);
            Assert.False(await f.OwnerScalarAsync<bool>("SELECT is_active FROM identity.platform_admins WHERE user_id=@u", ("u", target)));
        }
    }

    [Fact(DisplayName = "ACCESS-012 concurrent edits of one assignment: exactly one wins, the other gets 412; version is required")]
    public async Task Concurrent_assignment_edits()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (_, facility) = await NewOrganisationAsync(admin);
        var (userId, _) = await NewStaffAsync(admin);
        var assignment = await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{userId}/assignments",
            new { facilityId = facility, roles = new[] { "Receptionist" } }), HttpStatusCode.Created);
        var id = assignment.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/assignments/{id}",
            new { facilityId = facility, roles = new[] { "Receptionist" } })).StatusCode); // no version
        using var admin2 = await f.LoginAsync(DemoData.SuperAdmin);
        var results = await Task.WhenAll(
            admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/assignments/{id}", new { facilityId = facility, roles = new[] { "Receptionist" }, revokedCapabilities = new[] { "patient.search" }, version = 1 }),
            admin2.SendAsync(HttpMethod.Put, $"/api/v1/platform/assignments/{id}", new { facilityId = facility, roles = new[] { "Receptionist" }, grantedCapabilities = new[] { "analytics.read" }, version = 1 }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        Assert.Equal(2, await f.OwnerScalarAsync<int>("SELECT version FROM identity.affiliations WHERE id=@i", ("i", id)));
    }

    [Fact(DisplayName = "ACCESS-013 custom roles cannot impersonate built-in role names; deactivating a super admin removes super administration")]
    public async Task Naming_and_deactivation_rules()
    {
        using var admin = await f.LoginAsync(DemoData.SuperAdmin);
        var (tenant, _) = await NewOrganisationAsync(admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenant, name = "receptionist", capabilities = new[] { "clinical.note.sign" } })).StatusCode);
        var role = await Ok(await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/roles",
            new { tenantId = tenant, name = "Desk helper", capabilities = new[] { "appointment.read" } }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/platform/roles/{role.GetProperty("id").GetGuid()}",
            new { name = "Clinician", capabilities = new[] { "appointment.read" }, isActive = true, version = 1 })).StatusCode);

        var (b, _, bClient) = await NewSuperAdminAsync(admin);
        bClient.Dispose();
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{b}/active", new { isActive = false, reason = "test" }), HttpStatusCode.NoContent);
        await Ok(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{b}/active", new { isActive = true, reason = "test" }), HttpStatusCode.NoContent);
        Assert.False(await f.OwnerScalarAsync<bool>("SELECT is_active FROM identity.platform_admins WHERE user_id=@u", ("u", b))); // not silently restored
        Assert.Equal("self_modification", await Code(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/staff/{DemoData.UserId(DemoData.SuperAdmin)}/unlock")));
    }
}
