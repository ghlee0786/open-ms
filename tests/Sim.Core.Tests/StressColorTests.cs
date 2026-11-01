using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>얇은 벽 국부 응력(FX-09) 해석해와 색 척도·레전드(FR-STR-03) 단위 검증.</summary>
public class StressColorTests
{
    private static StressModel Thin(Shape s, double t, double pinKPa, string edge = "simple")
    {
        var b = new Body { Name = "x", OwnMass = 2, Shape = s, OwnInertia = s.UniformInertia(2) };
        b.Structure = StructureDef.Parse(new JsonObject { ["material"] = "al6061-t6", ["thickness"] = t, ["internalPressure_kPa"] = pinKPa, ["edge"] = edge },
            null, s, new List<string>(), "x");
        return StressModel.Build(b)!;
    }

    [Theory]
    [InlineData(1.0, 0.2874, 0.3078)]
    [InlineData(2.0, 0.6102, 0.4974)]
    [InlineData(1.5, 0.4851, 0.4518)]
    public void Roark_plate_coefficients_interpolate_table(double ratio, double simple, double clamped)
    {
        Assert.Equal(simple, StressModel.PlateCoefficients(ratio, false).Beta, 4);
        Assert.Equal(clamped, StressModel.PlateCoefficients(ratio, true).Beta, 4);
        Assert.Equal(StressModel.PlateCoefficients(1, false).Beta, StressModel.PlateCoefficients(0.5, false).Beta); // a/b < 1 → 1
    }

    [Fact]
    public void Square_box_under_internal_pressure_matches_roark_center_stress()
    {
        double p = 20e3, t = 0.0015, b = 0.25;
        var m = Thin(new Shape { Kind = ShapeKind.Box, Size = new Vec3(b, b, b) }, t, p / 1e3);
        var r = m.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero, []));
        double hand = 0.2874 * p * b * b / (t * t);
        Assert.InRange(r.MaxVonMises / hand, 0.98, 1.0 + 1e-9);
        Assert.Equal(StressComponent.Panel, r.DominantAtMax);
        Assert.True(r.BendingMoment.Max() < 1e-6);
        // 선형 판 처짐 w/t = α·p·b⁴/(E·t⁴) (α 0.0444, ν 0.33 보정)
        double e = 68.9e9; // 라이브러리 Al 6061-T6 근처
        Assert.InRange(r.MaxPanelDeflectionRatio, 0.0444 * p * Math.Pow(b, 4) / (e * Math.Pow(t, 4)) * 0.9, 0.0444 * p * Math.Pow(b, 4) / (e * Math.Pow(t, 4)) * 1.1);
    }

    [Fact]
    public void Solid_section_has_no_local_panel_term()
    {
        var s = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.3, 0.2, 0.2) };
        var b = new Body { Name = "s", OwnMass = 2, Shape = s, OwnInertia = s.UniformInertia(2) };
        b.Structure = StructureDef.Parse(new JsonObject { ["material"] = "al6061-t6", ["section"] = "solid" }, null, s, new List<string>(), "s");
        var r = StressModel.Build(b)!.Evaluate(new StressInput(new Vec3(40, 5, 0), 1.2, 0.1, Vec3.Zero, []));
        Assert.All(r.Local, x => Assert.Equal(0, x));
        Assert.DoesNotContain(StressComponent.Panel, r.Component);
    }

    [Fact]
    public void Closed_cylinder_internal_pressure_gives_hoop_and_axial_membrane()
    {
        double p = 50e3, t = 0.002, rad = 0.15;
        var m = Thin(new Shape { Kind = ShapeKind.Cylinder, Radius = rad, Length = 0.6, Axis = 0 }, t, p / 1e3);
        var r = m.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero, []));
        var side = Enumerable.Range(0, m.FaceCount).Where(i => Math.Abs(m.Normal[i].X) < 0.7).ToList();
        Assert.All(side, i => Assert.Equal(p * rad / t, r.Local[i], 6));
        Assert.All(side, i => Assert.Equal(p * rad / (2 * t), r.Axial[i], 6));
        Assert.All(side, i => Assert.Equal(StressComponent.Membrane, r.Component[i]));
    }

    [Fact]
    public void Color_scale_resolves_degenerate_and_reversed_ranges()
    {
        var z = ColorScale.Resolve([0, 0], zeroBased: true);
        Assert.Equal((0.0, 1.0), (z.Min, z.Max));
        var sf = ColorScale.Resolve([double.PositiveInfinity, 2, double.NaN], zeroBased: true, cap: 5, atLeastMax: 1.5);
        Assert.Equal((0.0, 5.0), (sf.Min, sf.Max));
        var rev = ColorScale.Resolve([1, 2], fixedMin: 9, fixedMax: 3);
        Assert.Equal((3.0, 9.0), (rev.Min, rev.Max));
        Assert.NotNull(rev.Warning);
        var ticks = ColorScale.Ticks(new ScaleRange(10, 10.001, false));
        Assert.True(ticks.Length >= 5);
        Assert.Equal(ticks.Length, ticks.Select(t => t.Label).Distinct().Count());
    }

    [Fact]
    public void Legend_gradient_color_equals_face_color_and_nan_is_gray()
    {
        var r = new ScaleRange(0, 200, false);
        foreach (var rev in new[] { false, true })
            foreach (var v in new[] { 0.0, 37.5, 100, 199.9, 200 })
                Assert.Equal(ColorScale.ValueColor("Viridis", v, r, rev), ColorScale.LegendColor("Viridis", r, (200 - v) / 200, rev));
        Assert.Equal(ColorScale.NoData, ColorScale.ValueColor("Turbo", double.NaN, r));
        // Turbo 공식 표 끝색(짙은 파랑 → 짙은 빨강)
        var t0 = ColorScale.Turbo(0); var t1 = ColorScale.Turbo(1);
        Assert.Equal(0.18995, t0.R, 1e-5); Assert.Equal(0.23217, t0.B, 1e-5);
        Assert.Equal(0.47960, t1.R, 1e-5); Assert.Equal(0.01055, t1.B, 1e-5);
    }
}
