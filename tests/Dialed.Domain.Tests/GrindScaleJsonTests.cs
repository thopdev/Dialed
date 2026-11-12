using System.Text.Json;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class GrindScaleJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_grinder_with_a_scale_survives_a_trip_through_JSON_like_sync_does()
    {
        var grinder = new EquipmentItem { Id = "g1", Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40) };

        var back = JsonSerializer.Deserialize<EquipmentItem>(JsonSerializer.Serialize(grinder, Web), Web)!;

        back.GrindScale.ShouldBe(grinder.GrindScale);
        back.GrindScale!.Kind.ShouldBe(GrindSettingKind.Clicks);
        back.GrindScale.Max.ShouldBe(40m);
    }

    [Fact]
    public void A_numbered_dial_with_a_step_keeps_its_step()
    {
        var scale = GrindScale.Number(0, 11, 0.5m);

        JsonSerializer.Deserialize<GrindScale>(JsonSerializer.Serialize(scale, Web), Web).ShouldBe(scale);
    }

    [Fact]
    public void A_scale_that_does_not_make_sense_is_refused_when_read()
    {
        Should.Throw<Exception>(() => JsonSerializer.Deserialize<GrindScale>("{\"kind\":1,\"min\":10,\"max\":5,\"step\":1}", Web));
    }
}
