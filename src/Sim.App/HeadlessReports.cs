using System.Text;
using Sim.Core.Analysis;
using Sim.Core.Scenario;

namespace Sim.App;

/// <summary>
/// 창 없이 보고서만 만드는 일괄 모드(CLI `mns opening-shock` 과 같은 생성기):
///   MnS.Simulator --opening-shock-report &lt;scenario.json&gt; [--variant &lt;scenario.json&gt;] [--runs N] [--ccx] [--no-sweep]
///                 [--out validation/reports/opening_shock] [--raw DIR] [--real-models DIR]
/// 진행 로그는 &lt;out&gt;/generation.log (GUI 실행 파일이라 콘솔이 없을 수 있음).
/// </summary>
internal static class HeadlessReports
{
    public static int OpeningShock(string[] args, int at)
    {
        string? Opt(string n) => Array.IndexOf(args, n) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
        var outDir = Opt("--out") ?? Path.Combine("validation", "reports", "opening_shock");
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        void Log(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss} {s}"); try { Console.WriteLine(s); } catch { } }
        int code = 0;
        try
        {
            if (at + 1 >= args.Length) throw new InvalidDataException("--opening-shock-report <scenario.json> 이 필요합니다.");
            var doc = ScenarioDoc.Load(args[at + 1]);
            var variant = Opt("--variant") is { } vp ? ScenarioDoc.Load(vp) : null;
            int runs = Opt("--runs") is { } r ? int.Parse(r) : (int)J.Num(doc.Root["monteCarlo"] as System.Text.Json.Nodes.JsonObject ?? new(), "runs", 200);
            var real = Opt("--real-models");
            var cmd = "mns opening-shock " + string.Join(" ", args.Where((a, i) => i > at && !(real is not null && a == real)).Select(a => a == "--real-models" ? "--real-models <실모델 폴더>" : a));
            var rep = OpeningShockReport.Generate(doc, variant, new OpeningShockReportOptions
            {
                OutDir = outDir, RawDir = Opt("--raw") ?? Path.Combine("results", $"{doc.Name}_report"), McRuns = runs, Ccx = args.Contains("--ccx"),
                CcxInstall = Opt("--install"), SweepThickness = args.Contains("--no-sweep") ? [] : new OpeningShockReportOptions { OutDir = "" }.SweepThickness,
                Log = Log, Command = cmd,
            });
            Log($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))} (단계 {rep.Nominal.Stages.Count}, MC {rep.Mc?.Runs ?? 0}, CalculiX {rep.Ccx.Count})");
        }
        catch (Exception ex) { Log("오류: " + ex); code = 1; }
        File.WriteAllText(Path.Combine(outDir, "generation.log"), log.ToString(), new UTF8Encoding(false));
        return code;
    }
}
