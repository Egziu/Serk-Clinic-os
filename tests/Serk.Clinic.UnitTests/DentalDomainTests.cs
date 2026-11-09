using Serk.Clinic.Dental.Domain;

namespace Serk.Clinic.UnitTests;

/// <summary>DENT-001/002/003 domain rules. Clinical vocabulary is provisional (PENDING CLINICIAN REVIEW).</summary>
public class DentalDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "DENT-001 exactly 32 permanent and 20 primary FDI codes are valid")]
    public void Fdi_code_sets()
    {
        Assert.Equal(32, ToothCode.All(Dentition.Permanent).Count());
        Assert.Equal(20, ToothCode.All(Dentition.Primary).Count());
        var valid = Enumerable.Range(0, 100).Where(c => ToothCode.Parse(c).IsSuccess).ToHashSet();
        Assert.Equal(52, valid.Count);
        Assert.True(ToothCode.All(Dentition.Permanent).Concat(ToothCode.All(Dentition.Primary)).All(t => valid.Contains(t.Code)));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(10)]
    [InlineData(9)]
    [InlineData(49)]
    [InlineData(56)]
    [InlineData(86)]
    [InlineData(91)]
    [InlineData(0)]
    public void Invalid_fdi_codes_rejected(int code) => Assert.Equal("invalid_fdi_code", ToothCode.Parse(code).Error!.Code);

    [Theory]
    [InlineData(11, "11 upper right central incisor", true, Dentition.Permanent)]
    [InlineData(23, "23 upper left canine", true, Dentition.Permanent)]
    [InlineData(36, "36 lower left first molar", false, Dentition.Permanent)]
    [InlineData(48, "48 lower right third molar", false, Dentition.Permanent)]
    [InlineData(55, "55 upper right primary second molar", false, Dentition.Primary)]
    [InlineData(71, "71 lower left primary central incisor", true, Dentition.Primary)]
    public void Fdi_names_and_types(int code, string name, bool anterior, Dentition dentition)
    {
        var t = ToothCode.Parse(code).Value;
        Assert.Equal(name, t.DisplayName);
        Assert.Equal(anterior, t.IsAnterior);
        Assert.Equal(dentition, t.Dentition);
    }

    private static RecordFindingInput Input(int tooth, int? confirm = null, ToothCondition cond = ToothCondition.Caries, params ToothSurface[] surfaces) =>
        new(tooth, confirm ?? tooth, surfaces, cond, "synthetic", null, null);

    [Fact(DisplayName = "DENT-003 mismatched tooth confirmation is rejected")]
    public void Wrong_tooth_confirmation()
    {
        var r = DentalFinding.Record(Input(36, confirm: 46, surfaces: ToothSurface.Occlusal), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Equal("tooth_confirmation_mismatch", r.Error!.Code);
    }

    [Fact(DisplayName = "DENT-002 surfaces must fit the tooth type and condition")]
    public void Surface_rules()
    {
        Record(Input(11, surfaces: ToothSurface.Occlusal), ok: false);   // incisor has no occlusal surface
        Record(Input(36, surfaces: ToothSurface.Incisal), ok: false);    // molar has no incisal edge
        Record(Input(36, cond: ToothCondition.Missing, surfaces: ToothSurface.Mesial), ok: false);
        Record(Input(36, cond: ToothCondition.Caries), ok: false);       // caries needs a surface
        Record(Input(11, surfaces: [ToothSurface.Incisal, ToothSurface.Mesial]), ok: true);
        Record(Input(36, surfaces: [ToothSurface.Occlusal, ToothSurface.Distal]), ok: true);
        Record(Input(85, cond: ToothCondition.Missing), ok: true);
    }

    [Fact(DisplayName = "DENT-002 current state uses latest per tooth and honours corrections; history is preserved")]
    public void Current_state_and_corrections()
    {
        var p = Guid.NewGuid();
        var first = Record(Input(36, surfaces: ToothSurface.Occlusal), ok: true, patient: p, at: Now);
        var other = Record(Input(11, cond: ToothCondition.Sound), ok: true, patient: p, at: Now.AddMinutes(1));
        var correction = DentalFinding.Record(new RecordFindingInput(36, 36, [ToothSurface.Distal], ToothCondition.Caries, "", first!.Id, "Surface misrecorded"), Guid.NewGuid(), p, Guid.NewGuid(), first, Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(2)).Value;
        var current = DentalFinding.CurrentState([first, other!, correction]);
        Assert.Equal([11, 36], current.Select(f => f.ToothCode));
        Assert.Equal(["Distal"], current.Single(f => f.ToothCode == 36).Surfaces);
        Assert.False(DentalFinding.Record(new RecordFindingInput(46, 46, [ToothSurface.Distal], ToothCondition.Caries, "", first.Id, "Wrong tooth corrected"), Guid.NewGuid(), p, Guid.NewGuid(), first, Guid.NewGuid(), Guid.NewGuid(), Now).IsSuccess);
        Assert.False(DentalFinding.Record(new RecordFindingInput(36, 36, [ToothSurface.Distal], ToothCondition.Caries, "", first.Id, "no"), Guid.NewGuid(), p, Guid.NewGuid(), first, Guid.NewGuid(), Guid.NewGuid(), Now).IsSuccess);
    }

    private static DentalFinding? Record(RecordFindingInput input, bool ok, Guid? patient = null, DateTimeOffset? at = null)
    {
        var r = DentalFinding.Record(input, Guid.NewGuid(), patient ?? Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), at ?? Now);
        Assert.True(r.IsSuccess == ok, r.Error?.Message);
        return r.IsSuccess ? r.Value : null;
    }
}
