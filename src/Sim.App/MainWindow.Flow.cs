using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Structures;

namespace Sim.App;

/// <summary>CFD 공기흐름 색 표시(시나리오 aero.flowField, Sim.Core <see cref="SurfaceFlowField"/>): 현재 자세의 표면 Cp 를 면마다.</summary>
public partial class MainWindow
{
    /// <summary>물체 i 의 면별 Cp(현재 대기상대 자세 α_t·공력 롤, 마하가 가장 가까운 조건 묶음). 유동장이 없거나 정지면 null.</summary>
    private double[]? CfdFaceValues(int i, StressModel model, BodySample s)
    {
        var ff = _world?.Bodies[i].Aero?.FlowField;
        if (ff is null) return null;
        var air = s.Q.InverseRotate(s.AirVelocity);
        if (air.Norm < 1e-6) return null;
        double mach = s.AirVelocity.Norm / 340.3;          // 묶음 선택용 근사 음속(시각화 전용)
        return ff.Sample(model.Centroid, air, "cp", ff.GroupNearest(mach));
    }

    private string CfdNote()
    {
        var ff = _world?.Bodies.Select(b => b.Aero?.FlowField).FirstOrDefault(f => f is not null);
        return ff is null ? "aero.flowField 가 지정된 물체 없음" : $"{ff.Name} · 정상 RANS 시각화(공력 계산 아님)";
    }
}
