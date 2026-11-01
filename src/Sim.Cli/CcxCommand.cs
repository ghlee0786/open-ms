using System.Globalization;
using System.Text;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

/// <summary>`mns ccx` — 시뮬레이션 물체의 CalculiX 정적·진동·좌굴(PRD Z, FR-FEA-01~05).</summary>
internal static class CcxCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args, Func<string, string, string> opt, Func<string, bool> flag)
    {
        if (args.Length < 2) throw new InvalidDataException("사용법: mns ccx <result.mnsr|scenario.json> --body NAME ...");
        World world; ScenarioDoc doc; string name;
        if (args[1].EndsWith(ResultArchive.Extension, StringComparison.OrdinalIgnoreCase)) { var r = ResultArchive.Load(args[1]); world = r.World; doc = r.Doc; name = r.Doc.Name; }
        else { doc = ScenarioDoc.Load(args[1]); world = doc.Build(); world.Run(); name = doc.Name; }
        var body = world.GetBody(opt("--body", ""));
        var display = StressModel.DisplayMesh(body.Shape ?? throw new InvalidDataException($"{body.Name}: 형상이 없습니다."));
        var analysis = opt("--analysis", "static").ToLowerInvariant() switch
        {
            "static" => CcxAnalysis.Static, "modal" or "frequency" => CcxAnalysis.Modal, "buckle" or "buckling" => CcxAnalysis.Buckle,
            var a => throw new InvalidDataException($"--analysis: static | modal | buckle ('{a}')"),
        };
        var dir = Path.GetFullPath(opt("--out", Path.Combine("results", $"ccx_{name}_{body.Name}_{DateTime.Now:yyyyMMdd_HHmmss}")));
        Directory.CreateDirectory(dir);
        double? size = opt("--size", "") is { Length: > 0 } s ? double.Parse(s, Inv) : null;
        var mesher = opt("--mesher", "auto");
        string? cad = opt("--cad", "") is { Length: > 0 } cp ? cp : null;
        Sim.Core.Numerics.Mat3? cadRot = opt("--cad-frame", "") switch
        {
            "" => null, "body" => Sim.Core.Numerics.Mat3.Identity, "fusion" => Sim.Core.Geometry.Frames.FusionToBody,
            var f => throw new InvalidDataException($"--cad-frame: body | fusion ('{f}')"),
        };
        if (cad is not null && cadRot is null && args[1].EndsWith(ResultArchive.Extension, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  주의: 결과 파일(.mnsr)은 원본 modelFrame·noseAxis 를 잃으므로 --cad-frame 을 주거나 시나리오에서 실행하세요.");
        var fm = CcxBodyFea.BuildModel(body, display, dir, size, mesher, cad, cadRot);
        Console.WriteLine($"{body.Name}: {fm.Mesh.Source} — 절점 {fm.Mesh.UsedNodes().Count(u => u):N0}, 요소 {fm.Mesh.Elements.Count:N0}, 재질 {fm.BaseMaterial.Name}");
        foreach (var n in fm.Notes) Console.WriteLine($"  · {n}");

        FeaLoadInput? lc = null;
        if (analysis != CcxAnalysis.Modal)
        {
            lc = PickLoad(world, body, display, opt);
            Console.WriteLine($"하중 순간: {lc.Name} (연결점 힘 {lc.Points.Count}개 합 {lc.Points.Aggregate(Vec3.Zero, (a, p) => a + p.Force).Norm:F1} N, 최대 면 압력 {(lc.FacePressure.Length > 0 ? lc.FacePressure.Max() / 1e3 : 0):F2} kPa)");
        }
        if (flag("--deck-only"))
        {
            var model = analysis == CcxAnalysis.Modal ? CcxBodyFea.BuildModal(fm) : CcxBodyFea.BuildLoaded(fm, lc!, analysis, flag("--nonlinear")).Model;
            Console.WriteLine($"덱: {CcxDeck.Write(model, dir, "fea")}");
            return 0;
        }
        var inst = CalculixInstall.TryLocate(opt("--install", "") is { Length: > 0 } ins ? ins : null, out var why) ?? throw new InvalidOperationException(why);

        if (flag("--converge"))
        {
            if (fm.Shell) throw new InvalidDataException("얇은 벽(셸)은 표시 메시 1:1 이라 크기 세분 수렴을 하지 않습니다 — 솔리드 물체에 쓰세요.");
            int k = 0;
            var rep = CcxBodyFea.Converge(h => CcxBodyFea.BuildModel(body, display, Path.Combine(dir, $"conv{k}"), h, mesher, cad, cadRot),
                m => CcxBodyFea.Solve(inst, m, CcxAnalysis.Static, lc, Path.Combine(dir, $"conv{k++}"), flag("--nonlinear")), fm.Size, 3, 0.7);
            foreach (var l in rep.Levels) Console.WriteLine($"  h {l.Size * 1000,7:F2} mm  요소 {l.Elements,8:N0}  σvm 최대 {l.MaxVonMises / 1e6,9:F2} MPa @ {l.At}");
            Console.WriteLine(rep.Message);
            File.WriteAllText(Path.Combine(dir, "convergence.json"), rep.ToJson().ToJsonString(J.Pretty));
            return rep.Error is null ? 0 : 1;
        }

        var (run, res) = CcxBodyFea.Solve(inst, fm, analysis, lc, dir, flag("--nonlinear"));
        if (!run.Ok || res is null) { Console.Error.WriteLine($"CalculiX 실패: {run.Error} — 로그 {Path.Combine(dir, "fea.ccx.log")}"); return 1; }
        Console.WriteLine($"완료 {run.Seconds:F1} s — {res.Verdict}");
        if (res.Relief is { } ir)
            Console.WriteLine($"  관성 완화: FE 질량 {ir.Mass:F3} kg, 외력 {ir.ExternalForce.Norm:F1} N → {ir.AccelG:F2} G (기록 {lc!.RecordedAccel.Norm / 9.80665:F2} G), 지지 반력 {res.Reaction?.Norm ?? double.NaN:E2} N");
        if (analysis == CcxAnalysis.Static)
        {
            var approx = StressModel.Build(body, display) is { } sm && lc is not null && lc.Points.All(p => p.Source != "synthetic") && world.Recorder.Frames.Count > 0
                ? sm.Evaluate(sm.InputFrom(world, world.Recorder.Frames[world.Recorder.IndexAt(lc.Time)])).MaxVonMises : double.NaN;
            Console.WriteLine($"  σvm 최대 {res.MaxVonMises / 1e6:F2} MPa @ {res.MaxVonMisesAt} (동체 m), 변위 최대 {res.MaxDisplacement * 1e3:F3} mm, 최소 SF {res.MinSafety:F2}"
                + (double.IsFinite(approx) ? $" · 근사 응력(W절) {approx / 1e6:F2} MPa (근사/FEA {approx / Math.Max(res.MaxVonMises, 1e-30):F2})" : ""));
        }
        var rec = FeaRecord.FromCcx(body.Name, res, lc?.Name ?? "free-free", fm.Notes);
        File.WriteAllText(Path.Combine(dir, "fea_result.json"), rec.ToJson().ToJsonString(J.Pretty), new UTF8Encoding(false));
        using (var w = new StreamWriter(Path.Combine(dir, "faces.csv"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("face,cx_m,cy_m,cz_m,von_mises_Pa,safety,displacement_m");
            for (int i = 0; i < display.TriangleCount; i++)
            {
                var c = (display.Vertices[3 * i] + display.Vertices[3 * i + 1] + display.Vertices[3 * i + 2]) / 3;
                w.WriteLine(string.Format(Inv, "{0},{1:G7},{2:G7},{3:G7},{4:G6},{5:G6},{6:G6}", i, c.X, c.Y, c.Z, res.FaceVonMises[i], res.FaceSafety[i], res.FaceDisplacement[i]));
            }
        }
        if (opt("--archive", "") is { Length: > 0 } arch)
        {
            ResultArchive.Save(arch, doc, world, fea: FeaRecord.Pack([rec]));
            Console.WriteLine($"결과 파일: {Path.GetFullPath(arch)} (앱: 결과 불러오기 → 색상 'FEA von Mises (CalculiX)')");
        }
        Console.WriteLine($"결과: {dir} (fea.inp · fea.frd · fea.dat · fea_result.json · faces.csv)");
        return 0;
    }

    private static Vec3 ParseDir(string s) => s.Trim().ToLowerInvariant() switch
    {
        "x" or "+x" => Vec3.UnitX, "-x" => -Vec3.UnitX, "y" or "+y" => new Vec3(0, 1, 0), "-y" => new Vec3(0, -1, 0),
        "z" or "+z" => Vec3.UnitZ, "-z" => -Vec3.UnitZ,
        var v => v.Split(',') is { Length: 3 } p ? new Vec3(double.Parse(p[0], Inv), double.Parse(p[1], Inv), double.Parse(p[2], Inv)) : throw new InvalidDataException($"방향: ±x|±y|±z|a,b,c ('{s}')"),
    };

    private static FeaLoadInput PickLoad(World world, Sim.Core.Models.Body body, Sim.Core.Geometry.TriMesh display, Func<string, string, string> opt)
    {
        if (opt("--g", "") is { Length: > 0 } g)
        {
            Vec3? at = opt("--at", "") is { Length: > 0 } a ? ParseDir(a) : null;
            return CcxBodyFea.Synthetic(body.OwnMass, display, double.Parse(g, Inv), ParseDir(opt("--dir", "-x")), at, patch: double.Parse(opt("--patch", "0"), Inv));
        }
        if (opt("--loadcases", "") is { Length: > 0 } lcPath)
        {
            var all = CcxBodyFea.FromLoadCasesJson(lcPath, body.Name);
            if (all.Count == 0) throw new InvalidDataException($"{lcPath}: {body.Name} 하중 케이스가 없습니다.");
            for (int i = 0; i < all.Count; i++) Console.WriteLine($"  [{i}] {all[i].Name}");
            return all[int.Parse(opt("--case", "0"), Inv)];
        }
        var model = StressModel.Build(body, display) ?? throw new InvalidDataException($"{body.Name}: 응력 모델을 만들 수 없습니다(형상·질량).");
        var inst = LoadCaseExport.Instants(world, body, 16);
        if (inst.Count == 0) throw new InvalidDataException($"{body.Name}: 자유 비행 중 하중 순간이 없습니다 — --g 로 대표 하중을 주세요.");
        var cands = inst.Select(x => CcxBodyFea.FromWorld(world, model, x.Frame, x.Why)).ToList();
        for (int i = 0; i < cands.Count; i++)
            Console.WriteLine($"  [{i}] t={cands[i].Time,8:F3}s {cands[i].Name,-36} 연결점 {cands[i].Points.Sum(p => p.Force.Norm),8:F1} N · 최대 압력 {cands[i].FacePressure.DefaultIfEmpty(0).Max() / 1e3,7:F2} kPa · 외력 지표 {cands[i].Severity(model.Normal, model.Area),8:F1} N · 기록 {cands[i].RecordedAccel.Norm / 9.80665,6:F2} G");
        if (opt("--time", "") is { Length: > 0 } t)
        {
            int fi = world.Recorder.IndexAt(double.Parse(t, Inv));
            return CcxBodyFea.FromWorld(world, model, fi, $"t={world.Recorder.Frames[fi].T:F3}s");
        }
        if (opt("--case", "") is { Length: > 0 } c) return cands[int.Parse(c, Inv)];
        // 기본: FE 에 걸리는 외력(연결점 + 압력 합력 + 압력 크기)이 가장 큰 순간 — 충돌 충격은 강체 하중 모델에 없어 제외됨
        return cands.MaxBy(x => x.Severity(model.Normal, model.Area))!;
    }

    public static string VerifyReport(List<CcxCheck> checks, CalculixInstall inst, OpenCourantInstall? oc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CalculiX 외부 FEA 검증 (PRD Z · FR-FEA-06)");
        sb.AppendLine();
        sb.AppendLine($"생성: `mns ccx-verify` · {DateTime.Now:yyyy-MM-dd HH:mm} · CalculiX `{inst.Ccx}`{(oc is null ? "" : $" · OpenCourant `{oc.Root}`")}");
        sb.AppendLine($"저장소 테스트 `CalculixTests` 와 적대적 검증 A47·A51 이 같은 사례를 판정한다.");
        sb.AppendLine();
        sb.AppendLine($"**결과: 통과 {checks.Count(c => c.Pass)} / {checks.Count}**");
        sb.AppendLine();
        sb.AppendLine("| ID | 사례 | 양 | CalculiX | 기준 | 비 | 허용 | 판정 | 기준 출처 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var c in checks)
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2} | {3:G5} | {4:G5} | {5:F4} | ±{6:P0} | {7} | {8} |", c.Id, c.Name, c.Quantity, c.Measured, c.Reference, c.Ratio, c.Tolerance, c.Pass ? "✅" : "❌", c.Source));
        sb.AppendLine();
        sb.AppendLine("## 모델");
        sb.AppendLine("- 강재 E 210 GPa, ν 0.3, ρ 7850. 메시는 C# 구조 격자(외부 메셔 없이 재현): 보·기둥 C3D20R 20×2×2, 판 S8R 16×16, 원통 S8R 32×8, 구멍 판 CPS8 1/4 모델 2블록(반경 방향 등비 16).");
        sb.AppendLine("- FEA-7 은 OpenCourant 양해법(HEPH 40×4×4)을 1차 주기의 10배 램프로 정적 한계까지 걸고 유지 구간 한 주기 평균 → CalculiX 정적 결과와 비교.");
        foreach (var c in checks.Where(c => c.Note.Length > 0)) sb.AppendLine($"- {c.Id}: {c.Note}");
        sb.AppendLine();
        sb.AppendLine("## 실측으로 확인한 CalculiX 2.23(Windows) 동작");
        sb.AppendLine("- 알 수 없는 키워드 → 메시지 없이 종료 코드 139 로 죽음 → 덱 작성 전 검사 + 종료 코드·'Job finished'·.frd 확인으로 판정.");
        sb.AppendLine("- 셸(S6/S8R) 결과는 3D 확장 절점(±t/2, 새 번호)에만 있다 → 좌표로 표시 메시에 매핑. 셸 *DLOAD P 양수는 요소 법선(오른손) 방향으로 민다.");
        sb.AppendLine("- 구속 없는 *FREQUENCY 는 실행마다 다른 가짜 저주파 모드를 낸다 → 약한 SPRING1 접지(E·L·1e−10)로 강체 모드 6개를 0.3~0.7 Hz 로 분리.");
        sb.AppendLine("- C3D8I 는 고유진동에서 가짜 음의 고유값, C3D20R 은 한 요소 두께 사슬(복셀 스파이크·날개)에서 모래시계 모드 → 복셀 대체 메시는 완전적분 C3D20.");
        return sb.ToString();
    }
}
