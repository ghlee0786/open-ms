using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Reliability;

/// <summary>
/// 공력 모델 교체 비교(PRD FR-REL-01 "시나리오 09 를 L1 대신 CFD DB 로 다시 돌려 결과 변화 보고"):
/// 같은 시나리오·같은 시드를 기준 공력(시나리오 그대로) 과 aero.db(CFD 공력 DB) 로 각각 실행 → 운용 출력(UQ operational 프로필) 비교 +
/// 받음각별 계수 표(L1 형상 근사 vs CFD).
/// </summary>
public static class AeroCompare
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string S3(double x) => Math.Abs(x) < 5e-4 ? "0.000" : x.ToString("+0.000;-0.000", Inv);

    public static string Run(ScenarioDoc doc, string body, IReadOnlyList<string> dbPaths, Action<string>? log, string command)
    {
        var uq = doc.Root["uq"] as JsonObject ?? new JsonObject { ["profile"] = "operational" };
        Dictionary<string, double> RunWith(string? db)
        {
            var d = doc.Clone();
            if (db is not null)
            {
                var b = (d.Root["bodies"] as JsonArray)!.OfType<JsonObject>().First(x => J.Str(x, "name", "") == body);
                var aero = b["aero"] as JsonObject ?? new JsonObject();
                aero["db"] = db;
                b["aero"] = aero;
            }
            var w = d.Build();
            w.Run();
            return UqEngine.Extract("operational", w, uq);
        }
        log?.Invoke("기준(시나리오 공력) 실행");
        var baseRes = RunWith(null);
        var rows = new List<(string Db, Dictionary<string, double> R)>();
        foreach (var p in dbPaths)
        {
            log?.Invoke($"CFD DB 실행: {Path.GetFileName(p)}");
            rows.Add((p, RunWith(p)));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# 시나리오 {doc.Name} — 컨테이너 공력: L1(TR R-300 표) 대 CFD 공력 DB (PRD FR-REL-01)");
        sb.AppendLine();
        sb.AppendLine($"생성: `{command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `{Results.EngineVersion}` · 같은 시드·같은 입력, `{body}.aero` 만 교체(감쇠 미계수는 그대로).");
        sb.AppendLine();
        sb.AppendLine("## 1. 받음각별 계수 (M 0.5, 물체축 — CX 축력, CN 법선력, 기준 S = πd²/4)");
        sb.AppendLine();
        var w0 = doc.Build();
        var aeroL1 = w0.GetBody(body).Aero!;
        var atm = w0.Env.Atmosphere.At(1000);
        double v = 0.5 * atm.SpeedOfSound;
        var dbs = dbPaths.Select(AeroDb.Load).ToList();
        double sref = dbs.Count > 0 ? dbs[0].RefArea : 1;
        sb.AppendLine("| α [deg] | L1 CX | L1 CN | " + string.Join(" | ", dbs.Select(d => $"{d.Name} CX | {d.Name} CN | {d.Name} Cm")) + " |");
        sb.AppendLine("|---:|---:|---:|" + string.Join("", dbs.Select(_ => "---:|---:|---:|")));
        foreach (var a in new[] { 0.0, 15, 30, 45, 60, 75, 90 })
        {
            double r = a * Math.PI / 180;
            var dir = new Vec3(Math.Cos(r), 0, Math.Sin(r));
            double q = 0.5 * atm.Density * v * v;
            var (f, _) = aeroL1.Compute(dir * v, Vec3.Zero, atm.Density, w0.GetBody(body).Cg, 0.5, atm.Viscosity);
            var line = $"| {a.ToString("0", Inv)} | {S3(f.X / (q * sref))} | {S3(f.Z / (q * sref))} |";
            foreach (var d in dbs)
            {
                var c = d.Coefficients(a, 0.5);
                line += $" {S3(c.CX)} | {S3(c.CN)} | {S3(c.Cm)} |";
            }
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("## 2. 운용 출력 변화");
        sb.AppendLine();
        sb.AppendLine("| 출력 | 기준(L1) | " + string.Join(" | ", rows.Select(r => Path.GetFileNameWithoutExtension(r.Db))) + " | 변화 |");
        sb.AppendLine("|---|---:|" + string.Join("", rows.Select(_ => "---:|")) + "---|");
        foreach (var def in UqEngine.OperationalOutputs)
        {
            double b0 = baseRes.GetValueOrDefault(def.Key, double.NaN);
            var vals = rows.Select(r => r.R.GetValueOrDefault(def.Key, double.NaN)).ToList();
            string chg = string.Join(", ", vals.Select(x => double.IsFinite(x) && double.IsFinite(b0)
                ? (Math.Abs(b0) > 1e-9 && !def.Key.StartsWith("impact") ? string.Format(Inv, "{0:+0.0%;-0.0%}", (x - b0) / Math.Abs(b0)) : string.Format(Inv, "{0:+0.0;-0.0}", x - b0))
                : "—"));
            sb.AppendLine(string.Format(Inv, "| {0} [{1}] | {2:G5} | {3} | {4} |", def.Label, def.Unit, b0, string.Join(" | ", vals.Select(x => x.ToString("G5", Inv))), chg));
        }
        sb.AppendLine();
        sb.AppendLine("## 3. 해석");
        sb.AppendLine();
        sb.AppendLine("- **L1 은 피칭 모멘트가 거의 없다**(압력중심 = 측면 투영 도심 ≈ 무게중심). CFD 는 중간 받음각에서 큰 양(+)의 Cm(받음각을 키우는 쪽 — 세장체 Munk 모멘트와 같은 부호)을 준다. " +
                      "두 모델의 텀블링·자세 출력 차이는 대부분 이 모멘트 때문이며, 이것이 텀블링 지표의 **모델 형태 불확도 크기**다(신뢰도 보고서에서 텀블링 출력 = '추정').");
        sb.AppendLine("- 단, CFD Cm 은 격자 수렴하지 않았다(α 45° 굵음→중간→고움 1.57→1.26→0.88, validation/reports/cfd/README.md 2절) — DB(중간 격자) 의 모멘트 크기는 과대일 수 있어 " +
                      "CFD 쪽 텀블링 크기도 상한 쪽 추정으로 읽는다. 모멘트 부호(받음각을 키우는 쪽)는 세 격자 모두 같다.");
        sb.AppendLine("- CFD DB 의 신뢰도는 같은 솔버 설정의 기준 사례(validation/reports/cfd/README.md)로 판단: 2-D 원통 횡류는 정상 RANS 가 −40 % 이상 과소(검증 실패), " +
                      "3-D 컨테이너 α 90° 횡류 CD 는 TR R-300 기반 L1 과 비교해 표 1 의 차이. Cm(α) 를 직접 검증할 공개 자료는 없다 → 풍동 정적·동적 시험이 시험 우선순위 상위.");
        sb.AppendLine("- 기본 시나리오는 실측 표 기반 L1 을 유지하고, CFD 공력 변형 `09c_…_cfd.json` 을 함께 제공한다(앱에서 두 결과 비교 가능).");
        return sb.ToString();
    }
}
