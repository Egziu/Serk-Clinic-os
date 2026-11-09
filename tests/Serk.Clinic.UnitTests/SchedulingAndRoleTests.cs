using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Scheduling.Domain;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.UnitTests;

public class SchedulingAndRoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4)]
    [InlineData(241)]
    public void Booking_rejects_out_of_range_duration(int minutes) =>
        Assert.False(Appointment.Book(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(1), minutes, null, Guid.NewGuid(), Now).IsSuccess);

    [Fact]
    public void Booking_rejects_past_start_and_truncates_seconds()
    {
        Assert.False(Appointment.Book(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-2), 15, null, Guid.NewGuid(), Now).IsSuccess);
        var a = Appointment.Book(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(1).AddMilliseconds(1234), 15, " x ", Guid.NewGuid(), Now).Value;
        Assert.Equal(0, a.StartsAt.Millisecond);
        Assert.Equal(TimeSpan.FromMinutes(15), a.EndsAt - a.StartsAt);
        Assert.Equal("x", a.Reason);
    }

    [Fact]
    public void Check_in_is_one_way_and_version_checked()
    {
        var a = Appointment.Book(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(1), 15, null, Guid.NewGuid(), Now).Value;
        Assert.Equal("version_mismatch", a.CheckIn(5, Guid.NewGuid(), Now).Error!.Code);
        Assert.True(a.CheckIn(1, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal("invalid_transition", a.CheckIn(null, Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("invalid_transition", a.Cancel(null).Error!.Code);
    }

    [Fact]
    public void Clinic_admin_has_no_clinical_capabilities()
    {
        var caps = RoleCatalog.CapabilitiesFor([RoleCatalog.ClinicAdmin]);
        Assert.DoesNotContain(Capabilities.EncounterRead, caps);
        Assert.DoesNotContain(Capabilities.AllergyRead, caps);
        Assert.DoesNotContain(Capabilities.DocumentRead, caps);
    }

    [Fact]
    public void Reception_cannot_read_clinical_data_or_sign()
    {
        var caps = RoleCatalog.CapabilitiesFor([RoleCatalog.Receptionist]);
        Assert.DoesNotContain(Capabilities.EncounterRead, caps);
        Assert.DoesNotContain(Capabilities.AllergyRead, caps);
        Assert.DoesNotContain(Capabilities.NoteSign, caps);
    }

    [Fact]
    public void Unknown_roles_grant_nothing()
    {
        Assert.Empty(RoleCatalog.CapabilitiesFor(["SuperUser", "SerkSupport", "admin"]));
    }
}
