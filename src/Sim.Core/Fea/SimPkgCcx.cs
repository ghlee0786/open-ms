using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Fea;

/// <summary>
/// Fusion 해석 패키지(LinearStress) → CalculiX 재현.
/// 형상: Fusion 내보내기 STEP(fusion_live_*_groups)은 솔리드 이름이 모두 "본체N" 이라, 같은 폴더 *.massprops.json 의 parts[i].body
/// (가져온 순서 = 솔리드 번호, PartFea 와 같은 규칙)로 패키지 바디 이름(bodyName)과 맞추고 gmsh 부피로 순서를 확인한다.
/// 폴더를 여럿 주면 앞 폴더가 우선(새 설계 폴더에 없는 드론은 이전 설계 폴더에서).
/// 메시: 바디별 gmsh 2차 사면체(C3D10, Parabolic 이 아니면 C3D4). 크기 h = MeshSizeFraction × 매칭 바디 전체 경계 상자 대각선,
/// 바디마다 그 바디 최소 치수(두께)로 낮추되 MinSizeFraction × h 아래로는 내리지 않는다(Fusion 의 최소 요소 크기).
/// 결합 접촉 → *TIE(ADJUST=NO: 비정합 메시에서 슬레이브 이동이 음의 야코비안을 만든다 — 실측), 허용 오차 = SurfaceContactSettings.Tolerance.
/// 고정 구속 → 지정 면의 바디 전체 절점 고정(면 번호는 SMT 내부 번호라 STEP 면과 대응 불가). 고정 바디에 걸린 체적력은 ccx 반력 합(RF)에
/// 나타나지 않으므로 고정 바디에는 GRAV 를 걸지 않고, 평형 확인은 RF ≈ −(자유 바디 FE 질량 × 가속도).
/// 가속도·중력 → 자유 바디마다 *DLOAD GRAV(크기 |a|, 방향 = 패키지 벡터, Fusion 모델 좌표 그대로). 선형이라 부호를 뒤집어도
/// von Mises·변위 크기·안전율은 같다(방향 규약은 결과 크기에 영향 없음).
/// 고정 바디와 TIE 로 이어지지 않은 바디 무리는 강체 운동(특이 행렬)이 되므로 빼고 목록으로 남긴다.
/// </summary>
public static class SimPkgCcx
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("G12", Inv);

    /// <summary>STEP 솔리드 하나: 파일, 번호(1부터), 바디 이름(massprops 또는 바디별 STEP 목록), gmsh 부피 [m³]·경계 상자 [m]·겉넓이 [m²](없으면 NaN).</summary>
    public sealed record StepPart(string Step, int Index, string Body, double Volume, Vec3 Min, Vec3 Max, double Area = double.NaN);

    public sealed record BodyMatch(PkgBody Body, StepPart? Part, string Note);

    /// <summary>
    /// 메시 크기 규칙: 바디마다 h = Scale × clamp(t / ThroughThickness, MinSize, MaxSize), t = 두께 추정 2V/A(겉넓이 없으면 경계 상자 최소 치수).
    /// ThroughThickness = 두께 방향 최소 요소 수(기본 2), MaxSize = 전역 상한, MinSize = 비용 하한(이 아래로는 내리지 않아 t &lt; 2·MinSize 바디는
    /// 두께 2요소를 못 채운다 — 바디 표에 두께/h 로 남긴다). NaN = 모델 대각선 기준 기본값(상한 2 %, 하한 0.4 %). Scale = 수렴 확인용 배율(절반 = 0.5),
    /// ScaleOnly = 배율을 이 바디 이름에만(국부 수렴 — 응력 상위 바디만 세분; null = 전부).
    /// </summary>
    public sealed record MeshRule(double MaxSize = double.NaN, double MinSize = double.NaN, double Scale = 1, int ThroughThickness = 2, IReadOnlySet<string>? ScaleOnly = null);

    /// <summary>두께 추정 [m]: 2V/A(얇은 판·관이면 벽 두께, 꽉 찬 덩어리면 작게 나와 보수적), 겉넓이가 없으면 경계 상자 최소 치수.</summary>
    public static double Thickness(StepPart p)
    {
        if (double.IsFinite(p.Area) && p.Area > 0 && p.Volume > 0) return 2 * p.Volume / p.Area;
        var d = p.Max - p.Min;
        return Math.Min(d.X, Math.Min(d.Y, d.Z));
    }

    /// <summary>
    /// 폴더의 바디별 STEP 목록(설계 FEA 내보내기): station.json = [{body, step, vol_mm3, …}], bodies.json = {cases:{사례:[{body, step, vol_mm3}]}}.
    /// 하위 폴더 station/station.json 도 본다(설계 FEA 폴더를 그대로 줄 때). step 경로가 없으면 같은 폴더의 같은 파일 이름(다른 PC 에서 내보낸 절대 경로).
    /// 이름 매칭 규칙 = 항목의 body 와 패키지 bodyName 이 정확히 같을 때(대소문자 구분). 같은 이름이 여럿이면 목록 순서상 첫 번째.
    /// </summary>
    public static List<(string Body, string Step, double VolumeM3, string Source)> Manifest(string dir, List<string> notes)
    {
        var r = new List<(string, string, double, string)>();
        foreach (var rel in new[] { Path.Combine("station", "station.json"), "station.json", "bodies.json" })
        {
            var path = Path.Combine(dir, rel);
            if (!File.Exists(path)) continue;
            var baseDir = Path.GetDirectoryName(path)!;
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(path)); }
            catch (System.Text.Json.JsonException ex) { notes.Add($"{rel}: JSON 오류 — {ex.Message}"); continue; }
            IEnumerable<JsonObject> entries = root switch
            {
                JsonArray a => a.OfType<JsonObject>(),
                JsonObject o when o["cases"] is JsonObject cs => cs.SelectMany(kv => kv.Value is JsonArray ca ? ca.OfType<JsonObject>() : []),
                JsonObject o when o["bodies"] is JsonArray ba => ba.OfType<JsonObject>(),
                _ => [],
            };
            int n = 0, miss = 0;
            foreach (var e in entries)
            {
                string body = J.Str(e, "body", ""), step = J.Str(e, "step", "");
                if (body == "" || step == "") continue;
                var local = File.Exists(step) ? step : Path.Combine(baseDir, Path.GetFileName(step.Replace('\\', '/')));
                if (!File.Exists(local)) { miss++; continue; }
                r.Add((body, Path.GetFullPath(local), J.Num(e, "vol_mm3", double.NaN) * 1e-9, rel));
                n++;
            }
            notes.Add($"{Path.GetFileName(dir)}/{rel}: 바디별 STEP {n}개" + (miss > 0 ? $" (파일 없음 {miss}개)" : ""));
        }
        return r;
    }

    /// <summary>
    /// 폴더들의 바디별 STEP 목록(<see cref="Manifest"/>, 먼저) + massprops 조립 STEP → 솔리드 목록(gmsh --list-bodies, 부피 2 % 로 확인 —
    /// 어긋난 솔리드는 빼고 notes 에). 폴더 순서 = 우선순위.
    /// </summary>
    public static List<StepPart> Catalog(IEnumerable<string> dirs, string workDir, List<string> notes)
    {
        var list = new List<StepPart>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) { notes.Add($"STEP 폴더가 없습니다: {dir}"); continue; }
            var man = Manifest(dir, notes);
            var solids = new List<(double Vol, Vec3 Min, Vec3 Max, double Area)>?[man.Count];
            var errs = new string?[man.Count];
            Parallel.For(0, man.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8) }, i =>
            {
                try { solids[i] = ListBodies(man[i].Step, workDir); }
                catch (Exception ex) { errs[i] = ex.Message; }
            });
            for (int i = 0; i < man.Count; i++)
            {
                var (body, step, mv, _) = man[i];
                if (solids[i] is not { } ss) { notes.Add($"{Path.GetFileName(step)}('{body}'): 솔리드 목록 실패 — {errs[i]}"); continue; }
                if (ss.Count != 1) { notes.Add($"{Path.GetFileName(step)}('{body}'): 솔리드 {ss.Count}개 — 바디별 STEP 은 1개여야 해서 제외"); continue; }
                var (vol, min, max, area) = ss[0];
                if (double.IsFinite(mv) && !(Math.Abs(vol / mv - 1) <= 0.02)) { notes.Add($"{Path.GetFileName(step)}('{body}'): 부피 {vol:G4} ≠ 목록 {mv:G4} m³ — 제외"); continue; }
                list.Add(new StepPart(step, 1, body, vol, min, max, area));
            }
            foreach (var mp in Directory.GetFiles(dir, "*.massprops.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var step = mp[..^".massprops.json".Length] + ".step";
                if (!File.Exists(step)) { notes.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(mp)}: 같은 이름의 STEP 이 없어 건너뜀"); continue; }
                var parts = (J.ParseObject(File.ReadAllText(mp))["parts"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
                var sol = ListBodies(step, workDir);
                if (sol.Count != parts.Count) notes.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(step)}: STEP 솔리드 {sol.Count}개 ≠ massprops parts {parts.Count}개 — 앞에서부터 부피로 확인");
                for (int i = 0; i < Math.Min(sol.Count, parts.Count); i++)
                {
                    var (vol, min, max, area) = sol[i];
                    double mv = J.Num(parts[i], "volume_m3", double.NaN);
                    string name = J.Str(parts[i], "body", "");
                    if (!(Math.Abs(vol / mv - 1) <= 0.02)) { notes.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(step)} 솔리드 {i + 1}('{name}'): 부피 {vol:G4} ≠ massprops {mv:G4} m³ — 제외"); continue; }
                    list.Add(new StepPart(step, i + 1, name, vol, min, max, area));
                }
            }
        }
        return list;
    }

    /// <summary>바디 이름 매칭(앞 폴더·앞 파일 우선). 못 맞춘 바디는 Part = null.</summary>
    public static List<BodyMatch> Match(FusionStudy s, IReadOnlyList<StepPart> catalog)
    {
        var first = new Dictionary<string, StepPart>(StringComparer.Ordinal);
        var dup = new HashSet<string>();
        foreach (var p in catalog)
            if (!first.TryAdd(p.Body, p) && Path.GetDirectoryName(first[p.Body].Step) == Path.GetDirectoryName(p.Step)) dup.Add(p.Body);
        return s.Bodies.Select(b => first.TryGetValue(b.BodyName, out var p)
            ? new BodyMatch(b, p, $"{Path.GetFileName(Path.GetDirectoryName(p.Step))}/{Path.GetFileName(p.Step)} #{p.Index}" + (dup.Contains(b.BodyName) ? " (같은 이름 여럿 — 첫 번째)" : ""))
            : new BodyMatch(b, null, "STEP 에 같은 이름의 바디 없음")).ToList();
    }

    private static List<(double Vol, Vec3 Min, Vec3 Max, double Area)> ListBodies(string step, string workDir)
    {
        var script = GmshMesher.FindScript() ?? throw new InvalidOperationException("Gmsh 도구(tools/fea/mesh_geometry.py)를 찾지 못했습니다(MNS_GMSH_TOOL 지정).");
        Directory.CreateDirectory(workDir);
        var outJson = Path.Combine(workDir, $"bodies_{(uint)Path.GetFullPath(step).GetHashCode():x8}.json");
        // 같은 STEP 을 다시 볼 때(수렴 확인 · 고유진동 재실행)는 STEP 보다 새 목록을 그대로 쓴다 — 겉넓이가 없는 옛 목록은 다시 만든다
        bool fresh = File.Exists(outJson) && File.GetLastWriteTimeUtc(outJson) > File.GetLastWriteTimeUtc(step) && File.ReadAllText(outJson).Contains("area_m2");
        if (!fresh)
        {
            var py = System.Environment.GetEnvironmentVariable("MNS_PYTHON") is { Length: > 0 } p ? p : "python";
            var psi = new ProcessStartInfo(py) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { script, step, "--list-bodies", outJson }) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{py} 를 시작할 수 없습니다.");
            var err = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || !File.Exists(outJson)) throw new InvalidOperationException($"STEP 솔리드 목록 실패({Path.GetFileName(step)}): {err.Result.Trim()}");
        }
        return JsonNode.Parse(File.ReadAllText(outJson))!.AsArray().OfType<JsonObject>().Select(o =>
        {
            var b = J.Arr(o["bbox_m"]!);
            return (J.Num(o, "volume_m3", 0), new Vec3(b[0], b[1], b[2]), new Vec3(b[3], b[4], b[5]), J.Num(o, "area_m2", double.NaN));
        }).ToList();
    }

    private sealed class Fe
    {
        public required BodyMatch Match { get; init; }
        public required PkgMaterial Material { get; init; }
        public double Size { get; init; }
        public double Thick { get; init; }
        public bool Fixed { get; init; }
        public FeMesh? Mesh { get; set; }
        public List<FeFace> Ext { get; set; } = new();
        public double Mass { get; set; }
        /// <summary>이 바디가 넘겨받은 질량(결합 안 된 바디 · 메시 실패 바디) [kg] — 밀도를 (Mass + Extra)/Mass 배로 올려 반영.</summary>
        public double Extra { get; set; }
        public string MovedTo { get; set; } = "";
        public double MovedMass { get; set; }
        public string Status { get; set; } = "";
        public int Straightened { get; set; }
        /// <summary>
        /// 고정 바디에서 풀어 두는 절점: 결합된 슬레이브 절점에서 (허용 오차 + 요소 2개) 안. 고정 절점에 붙은 TIE(MPC) 힘은 ccx 반력 합에
        /// 잡히지 않고(실측: RF ≈ 0), ccx 는 허용 오차 밖 면에도 투영하므로 결합 면 둘레를 넉넉히 풀어 힘이 요소를 거쳐 고정 절점으로 가게 한다.
        /// </summary>
        public HashSet<int> Interface { get; } = new();
        public int Offset { get; set; } = -1;
        public int ElemOffset { get; set; }
        public bool Kept => Offset >= 0;
        public double Density => Mass > 0 ? Material.Density * (Mass + Extra) / Mass : Material.Density;
    }

    private static Vec3 Center(StepPart p) => (p.Min + p.Max) * 0.5;

    /// <summary>
    /// 결합 안 된 바디의 질량 이관(설계 _fea_station.py 와 같은 방식): 받는 바디 = 해석에 남은 고정 아닌 바디 중 경계 상자 중심이 가장 가까운 것.
    /// (주는 바디 중심, 질량) 목록 → 받는 바디 번호(receivers 안 위치) 목록.
    /// </summary>
    public static int[] NearestReceivers(IReadOnlyList<Vec3> givers, IReadOnlyList<Vec3> receivers) =>
        givers.Select(g => receivers.Count == 0 ? -1 : Enumerable.Range(0, receivers.Count).MinBy(k => (receivers[k] - g).Norm)).ToArray();

    /// <summary>
    /// 선형 정적 재현 → summary(JsonObject). outDir 에 mesh/, ccx/(덱·결과), result.vtu(ParaView) 를 쓴다.
    /// modes &gt; 0 이면 같은 모델(같은 고정·결합, 질량 이관 포함)로 *FREQUENCY 고유진동 modes 개도 푼다(summary.modal).
    /// </summary>
    public static JsonObject RunLinearStress(FusionStudy s, IReadOnlyList<BodyMatch> matches, CalculixInstall inst, string outDir, Action<string> log, int threads = 0,
        MeshRule? rule = null, int modes = 0)
    {
        rule ??= new MeshRule();
        var warn = new List<string>();
        // ---- 하중(활성 하중 조건): 단위 질량당 체적력 ----
        string lc = s.ActiveLoadCase is { Length: > 0 } a0 ? a0 : s.LoadCases.Select(l => l.Id).FirstOrDefault() ?? "";
        bool InCase(string id) => id == "" || id == lc;
        var acc = Vec3.Zero;
        foreach (var l in s.Loads.Where(l => InCase(l.LoadCaseId)))
        {
            if (l.Type == "LinearAcceleration" || (l.Type == "Gravity" && l.On))
            {
                if (l.Bodies.Length > 0) warn.Add($"하중 '{l.Name}' 은 바디 지정 — 전역으로 적용");
                acc += l.Vector.Normalized() * l.Magnitude;
            }
            else if (l.Type != "Gravity") warn.Add($"하중 '{l.Name}'({l.Type}) 은 선형 정적 재현에서 지원하지 않음 — 무시");
        }
        if (!(acc.Norm > 0)) throw new InvalidDataException("활성 하중 조건에 가속도·중력 하중이 없습니다.");
        var fixedIds = new HashSet<string>();
        foreach (var c in s.Constraints.Where(c => InCase(c.LoadCaseId)))
        {
            if (c.Type == "Fixed" && c.Dofs.Values.Take(3).All(v => v == "Fixed")) fixedIds.UnionWith(c.Bodies);
            else warn.Add($"구속 '{c.Name}'({c.Type}) — 고정(전 자유도)만 지원, 무시");
        }
        var nonBonded = s.Contacts.Count(c => c.Type != "Bonded");
        if (nonBonded > 0) warn.Add($"결합(Bonded) 아닌 접촉 {nonBonded}개 — 선형 재현에서 무시(분리 가능 접촉은 비선형)");

        // ---- 메시 크기: 바디마다 두께 방향 ThroughThickness 요소 이상(상한 · 하한) ----
        var cand = matches.Where(m => m.Part is not null).ToList();
        if (cand.Count == 0) throw new InvalidDataException("STEP 과 맞춘 바디가 없습니다(--step-dir 확인).");
        var bmin = cand.Select(m => m.Part!.Min).Aggregate(Vec3.Min);
        var bmax = cand.Select(m => m.Part!.Max).Aggregate(Vec3.Max);
        double diag = (bmax - bmin).Norm;
        double frac = s.MeshNum("MeshSizeFraction", 0), abs = s.MeshNum("AbsoluteSize", 0), minFrac = s.MeshNum("MinSizeFraction", 0.2);
        double hFusion = frac > 0 ? frac * diag : abs > 0 ? abs : 0.1 * diag;
        double hMax = double.IsFinite(rule.MaxSize) && rule.MaxSize > 0 ? rule.MaxSize : 0.02 * diag;
        double hMin = Math.Min(double.IsFinite(rule.MinSize) && rule.MinSize > 0 ? rule.MinSize : 0.004 * diag, hMax);
        int nThick = Math.Max(1, rule.ThroughThickness);
        int order = s.Mesh["MeshElementOrderType"]?.GetValue<string>() is "Linear" ? 1 : 2;
        log($"메시: 바디 h = {rule.Scale:G3}{(rule.ScaleOnly is { } so ? $"(바디 {so.Count}개만)" : "")} × clamp(두께/{nThick}, {hMin * 1000:G3}, {hMax * 1000:G3} mm) (모델 대각선 {diag * 1000:F0} mm; Fusion 평균 크기 {hFusion * 1000:G4} mm), {(order == 2 ? "C3D10" : "C3D4")}");

        var fes = new List<Fe>();
        foreach (var m in cand)
        {
            var mat = s.MaterialOf(m.Body);
            if (mat is null || !(mat.E > 0) || !(mat.Density > 0)) { warn.Add($"'{m.Body.BodyName}': 재료 탄성·밀도 없음 — 제외"); continue; }
            double t = Thickness(m.Part!);
            fes.Add(new Fe { Match = m, Material = mat, Fixed = fixedIds.Contains(m.Body.Id), Thick = t, Size = (rule.ScaleOnly is null || rule.ScaleOnly.Contains(m.Body.BodyName) ? rule.Scale : 1) * Math.Clamp(t / nThick, hMin, hMax) });
        }
        if (!fes.Any(f => f.Fixed)) throw new InvalidDataException("고정 구속 바디가 STEP 과 맞춰지지 않았습니다 — 강체 운동으로 풀 수 없습니다.");

        var meshDir = Path.Combine(outDir, "mesh");
        var sw = Stopwatch.StartNew();
        Parallel.For(0, fes.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8) }, i =>
        {
            var f = fes[i];
            var (mesh, code, msg, _) = GmshMesher.Mesh(f.Match.Part!.Step, f.Size, Path.Combine(meshDir, $"b{i:D3}"), order, TimeSpan.FromMinutes(10), f.Match.Part.Index);
            // 세밀한 형상(실측: 실모델 센서 몸체·날개 — 9 mm 입력의 ÷4 = 2.25 mm 에서야 성공)은 굵은 크기에서 표면 메시가 서로 닫혀 실패 —
            // 크기 ÷2.5, ÷10 으로 다시(도구가 그때마다 ÷2·÷4 까지 시도)
            foreach (var div in new[] { 2.5, 10 })
                if (mesh is null) (mesh, code, msg, _) = GmshMesher.Mesh(f.Match.Part.Step, f.Size / div, Path.Combine(meshDir, $"b{i:D3}"), order, TimeSpan.FromMinutes(30), f.Match.Part.Index);
            if (mesh is null) { f.Status = $"메시 실패(코드 {code}): {msg}"; return; }
            f.Straightened = StraightenInvalid(mesh);
            f.Mesh = mesh;
            f.Ext = mesh.ExteriorFaces();
            f.Mass = CcxMass(mesh, f.Material.Density);
        });
        log($"gmsh {fes.Count(f => f.Mesh is not null)}/{fes.Count} 바디, 요소 {fes.Sum(f => f.Mesh?.Elements.Count ?? 0):N0}개, {sw.Elapsed.TotalSeconds:F0} s");

        // ---- 결합 접촉 → TIE 후보(슬레이브 외곽 절점이 마스터 외곽 면에서 허용 오차 안) ----
        double tol = s.Parameters["SurfaceContactSettings"]?["Tolerance"] is JsonValue tv && tv.TryGetValue(out double t0) && t0 > 0 ? t0 : 1e-3;
        var idx = fes.Select((f, i) => (f, i)).Where(x => x.f.Mesh is not null).ToDictionary(x => x.f.Match.Body.Id, x => x.i);
        var pairs = s.Contacts.Where(c => c.Type == "Bonded").Select(c => string.CompareOrdinal(c.Primary, c.Secondary) < 0 ? (c.Primary, c.Secondary) : (c.Secondary, c.Primary))
            .Where(p => p.Item1 != p.Item2).Distinct().ToList();
        var ties = new List<(int Slave, int Master, int Nodes, double Tol)>();
        var weak = new List<string>();
        int missing = 0, bothFixed = 0;
        foreach (var (p, q) in pairs)
        {
            if (!idx.TryGetValue(p, out var ip) || !idx.TryGetValue(q, out var iq)) { missing++; continue; }
            var (A, B) = (fes[ip], fes[iq]);
            if (A.Fixed && B.Fixed) { bothFixed++; continue; }
            // 마스터 = 고정 바디(고정 절점은 종속이 될 수 없다), 아니면 큰 바디
            bool aMaster = A.Fixed || (!B.Fixed && A.Mass / A.Material.Density >= B.Mass / B.Material.Density);
            var (sl, ma) = aMaster ? (iq, ip) : (ip, iq);
            // 허용 오차: Fusion 값은 실제 곡면 기준 — 굵은 요소의 평면 면은 곡면에서 현 오차만큼 떨어지므로 작은 쪽 요소 크기의 10 %까지 넓힌다
            double tp = Math.Max(tol, 0.1 * Math.Min(fes[sl].Size, fes[ma].Size));
            var near = new List<Vec3>();
            int n = NodesNear(fes[sl], fes[ma], tp, near);
            if (n >= 3)
            {
                ties.Add((sl, ma, n, tp));
                if (fes[ma].Fixed)
                {
                    var mnodes = fes[ma].Mesh!.Nodes;
                    var hash = new SpatialHash(mnodes, fes[ma].Size);
                    foreach (var p0 in near) fes[ma].Interface.UnionWith(hash.Within(p0, tp + 2 * fes[ma].Size));
                }
            }
            else weak.Add($"{fes[sl].Match.Body.BodyName} ↔ {fes[ma].Match.Body.BodyName} ({tp * 1000:G3} mm 안 {n}점)");
        }
        // 고정 바디와 이어진 무리만 남긴다
        var parent = Enumerable.Range(0, fes.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (sl, ma, _, _) in ties) parent[Find(sl)] = Find(ma);
        var grounded = fes.Select((f, i) => (f, i)).Where(x => x.f.Fixed && x.f.Mesh is not null).Select(x => Find(x.i)).ToHashSet();
        int nodeCount = 0, elemCount = 0;
        for (int i = 0; i < fes.Count; i++)
        {
            var f = fes[i];
            if (f.Mesh is null) continue;
            if (!grounded.Contains(Find(i))) { f.Status = "고정 바디와 결합(TIE)으로 이어지지 않음 — 강체 운동이라 메시 제외"; continue; }
            f.Offset = nodeCount; f.ElemOffset = elemCount;
            nodeCount += f.Mesh.Nodes.Count; elemCount += f.Mesh.Elements.Count;
            f.Status = f.Fixed ? "고정(결합 면 절점 외 전 절점)" : "해석";
        }
        ties = ties.Where(t => fes[t.Slave].Kept && fes[t.Master].Kept).ToList();
        log($"결합 접촉 {pairs.Count}쌍 → TIE {ties.Count} (양쪽 고정 {bothFixed}, 형상 없음 {missing}, 허용 오차 안 절점 3개 미만 {weak.Count}, 허용 오차 {tol * 1000:G3} mm ~ {(ties.Count > 0 ? ties.Max(t => t.Tol) * 1000 : 0):G3} mm); 해석 바디 {fes.Count(f => f.Kept)}/{fes.Count}");

        // ---- 결합 안 된 · 메시 실패 바디 질량 → 가장 가까운 해석 바디(전체 질량 보존) ----
        var receivers = fes.Where(f => f.Kept && !f.Fixed).ToList();
        var givers = fes.Where(f => !f.Kept && !f.Fixed).ToList();
        var to = NearestReceivers(givers.Select(g => Center(g.Match.Part!)).ToList(), receivers.Select(r => Center(r.Match.Part!)).ToList());
        for (int k = 0; k < givers.Count; k++)
        {
            var g = givers[k];
            double m = g.Mesh is not null ? g.Mass : g.Match.Part!.Volume * g.Material.Density;
            if (to[k] < 0 || !(m > 0)) continue;
            var rcv = receivers[to[k]];
            rcv.Extra += m; g.MovedTo = rcv.Match.Body.BodyName; g.MovedMass = m;
            g.Status += $" — 질량 {m * 1000:G4} g → {rcv.Match.Body.BodyName}";
        }
        if (givers.Count > 0) log($"질량 이관: {givers.Count}개 바디 {givers.Sum(g => g.MovedMass):G4} kg → 가까운 해석 바디");

        // ---- 덱 · 풀이 ----
        var kept = fes.Where(f => f.Kept).ToList();
        var mats = kept.Select(f => f.Material).DistinctBy(m => m.Id).ToList();
        var ccxDir = Path.Combine(outDir, "ccx");
        Directory.CreateDirectory(ccxDir);
        string Write(string? solver, string job, bool modal)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"** generated by MnS Sim.Core SimPkgCcx — Fusion simpkg LinearStress 재현{(modal ? " (고유진동)" : "")}, units m N Pa kg");
            sb.AppendLine("*HEADING");
            sb.AppendLine($"{s.ProjectName} / {s.StudyName}".Replace('\n', ' '));
            sb.AppendLine("*NODE, NSET=NALL");
            foreach (var f in kept)
                for (int n = 0; n < f.Mesh!.Nodes.Count; n++)
                {
                    var p = f.Mesh.Nodes[n];
                    sb.Append(f.Offset + n + 1).Append(", ").Append(F(p.X)).Append(", ").Append(F(p.Y)).Append(", ").AppendLine(F(p.Z));
                }
            for (int b = 0; b < fes.Count; b++)
            {
                var f = fes[b];
                if (!f.Kept) continue;
                foreach (var g in Enumerable.Range(0, f.Mesh!.Elements.Count).GroupBy(e => f.Mesh.Elements[e].Type))
                {
                    sb.AppendLine($"*ELEMENT, TYPE={g.Key}, ELSET=B{b}");
                    foreach (var e in g) sb.Append(f.ElemOffset + e + 1).Append(", ").AppendLine(string.Join(", ", f.Mesh.Elements[e].N.Select(n => f.Offset + n + 1)));
                }
            }
            void NSet(string name, IEnumerable<int> nodes)
            {
                sb.AppendLine($"*NSET, NSET={name}");
                foreach (var c in nodes.Chunk(12)) sb.AppendLine(string.Join(", ", c));
            }
            NSet("NFIX", kept.Where(f => f.Fixed).SelectMany(f => Enumerable.Range(0, f.Mesh!.Nodes.Count).Where(n => !f.Interface.Contains(n)).Select(n => f.Offset + n + 1)));
            foreach (var b in ties.Select(t => t.Slave).Distinct())
            {
                var f = fes[b];
                NSet($"NS{b}", f.Ext.SelectMany(x => FaceNodes(f.Mesh!, x)).Distinct().Order().Select(n => f.Offset + n + 1));
                sb.AppendLine($"*SURFACE, NAME=SN{b}, TYPE=NODE");
                sb.AppendLine($"NS{b}");
            }
            foreach (var b in ties.Select(t => t.Master).Distinct())
            {
                sb.AppendLine($"*SURFACE, NAME=SM{b}, TYPE=ELEMENT");
                foreach (var x in fes[b].Ext) sb.AppendLine($"{fes[b].ElemOffset + x.Element + 1}, S{x.Face}");
            }
            for (int k = 0; k < mats.Count; k++)
            {
                sb.AppendLine($"** M{k + 1} = {mats[k].Name}");
                sb.AppendLine($"*MATERIAL, NAME=M{k + 1}");
                sb.AppendLine("*ELASTIC");
                sb.AppendLine($"{F(mats[k].E)}, {F(mats[k].Nu)}");
                sb.AppendLine("*DENSITY");
                sb.AppendLine(F(mats[k].Density));
            }
            // 질량을 넘겨받은 바디는 자기 재료(밀도만 올림)
            for (int b = 0; b < fes.Count; b++)
                if (fes[b].Kept && fes[b].Extra > 0)
                {
                    var m = fes[b].Material;
                    sb.AppendLine($"** MB{b} = {m.Name} + 이관 질량 {fes[b].Extra:G6} kg");
                    sb.AppendLine($"*MATERIAL, NAME=MB{b}");
                    sb.AppendLine("*ELASTIC");
                    sb.AppendLine($"{F(m.E)}, {F(m.Nu)}");
                    sb.AppendLine("*DENSITY");
                    sb.AppendLine(F(fes[b].Density));
                }
            for (int b = 0; b < fes.Count; b++)
                if (fes[b].Kept)
                    sb.AppendLine($"*SOLID SECTION, ELSET=B{b}, MATERIAL={(fes[b].Extra > 0 ? $"MB{b}" : $"M{mats.FindIndex(m => m.Id == fes[b].Material.Id) + 1}")}");
            sb.AppendLine("*BOUNDARY");
            sb.AppendLine("NFIX, 1, 3");
            for (int t = 0; t < ties.Count; t++)
            {
                sb.AppendLine($"*TIE, NAME=T{t + 1}, POSITION TOLERANCE={F(ties[t].Tol)}, ADJUST=NO");
                sb.AppendLine($"SN{ties[t].Slave}, SM{ties[t].Master}");
            }
            sb.AppendLine("*STEP");
            if (modal)
            {
                sb.AppendLine("*FREQUENCY" + (solver is { Length: > 0 } ? $", SOLVER={solver}" : ""));
                sb.AppendLine(modes.ToString(Inv));
                sb.AppendLine("*NODE FILE");
                sb.AppendLine("U");
            }
            else
            {
                sb.AppendLine("*STATIC" + (solver is { Length: > 0 } ? $", SOLVER={solver}" : ""));
                sb.AppendLine("*DLOAD");
                var dir = acc.Normalized();
                for (int b = 0; b < fes.Count; b++)
                    if (fes[b].Kept && !fes[b].Fixed) sb.AppendLine($"B{b}, GRAV, {F(acc.Norm)}, {F(dir.X)}, {F(dir.Y)}, {F(dir.Z)}");
                sb.AppendLine("*NODE PRINT, NSET=NFIX, TOTALS=ONLY");
                sb.AppendLine("RF");
                sb.AppendLine("*NODE FILE");
                sb.AppendLine("U");
                sb.AppendLine("*EL FILE");
                sb.AppendLine("S");
                // 바디별 적분점 응력(.dat) → 99 % 백분위(설계 _fea_station.py 와 같은 정의)
                for (int b = 0; b < fes.Count; b++)
                    if (fes[b].Kept && !fes[b].Fixed) { sb.AppendLine($"*EL PRINT, ELSET=B{b}"); sb.AppendLine("S"); }
            }
            sb.AppendLine("*END STEP");
            var path = Path.Combine(ccxDir, job + ".inp");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        log($"CalculiX: 절점 {nodeCount:N0}, 요소 {elemCount:N0}, 재료 {mats.Count}, TIE {ties.Count} …");
        var r = CcxRunner.Solve(inst, sv => Write(sv, "simpkg", false), ccxDir, "simpkg", TimeSpan.FromHours(4), threads: threads);
        if (!r.Ok) throw new InvalidOperationException($"CalculiX 실패: {r.Error}");
        log($"CalculiX 완료 {r.Seconds:F0} s{(r.SolverNote.Length > 0 ? " — " + r.SolverNote : "")}");

        // ---- 결과 ----
        var disp = r.Displacement();
        var vm = r.VonMises();
        var coords = r.Frd!.Coords;
        var ipVm = ReadElementVonMises(Path.Combine(ccxDir, "simpkg.dat"));
        var rows = new List<JsonObject>();
        double vmMax = 0, uMax = 0; string vmBody = "", uBody = ""; Vec3 vmAt = Vec3.Zero;
        for (int b = 0; b < fes.Count; b++)
        {
            var f = fes[b];
            var row = new JsonObject
            {
                ["body"] = f.Match.Body.BodyName, ["path"] = f.Match.Body.Name, ["step"] = f.Match.Note, ["material"] = f.Material.Name,
                ["status"] = f.Status, ["fixed"] = f.Fixed, ["thickness_m"] = f.Thick, ["meshSize_m"] = f.Size, ["elementsThroughThickness"] = f.Thick / f.Size,
                ["elements"] = f.Mesh?.Elements.Count ?? 0, ["feMass_kg"] = f.Mass, ["extraMass_kg"] = f.Extra, ["movedTo"] = f.MovedTo, ["movedMass_kg"] = f.MovedMass,
                ["straightenedElements"] = f.Straightened, ["stepMass_kg"] = f.Match.Part!.Volume * f.Material.Density, ["yield_Pa"] = Fin(f.Material.Yield),
            };
            if (f.Kept)
            {
                double bv = 0, bu = 0; Vec3 at = Vec3.Zero;
                for (int n = 0; n < f.Mesh!.Nodes.Count; n++)
                {
                    int id = f.Offset + n + 1;
                    if (vm.TryGetValue(id, out var v) && v > bv) { bv = v; at = coords.TryGetValue(id, out var c) ? c : f.Mesh.Nodes[n]; }
                    if (disp.TryGetValue(id, out var u)) bu = Math.Max(bu, u.Norm);
                }
                row["maxVonMises_Pa"] = bv; row["maxVonMisesAt_m"] = J.ToJson(at); row["maxDisplacement_m"] = bu;
                row["safetyFactor"] = Fin(bv > 0 ? f.Material.Yield / bv : double.NaN);
                if (ipVm.TryGetValue($"B{b}", out var list) && list.Count > 0)
                {
                    double p99 = Percentile(list, 0.99);
                    row["p99VonMises_Pa"] = p99; row["ipMaxVonMises_Pa"] = list.Max(); row["integrationPoints"] = list.Count;
                    row["safetyFactorP99"] = Fin(p99 > 0 ? f.Material.Yield / p99 : double.NaN);
                }
                if (!f.Fixed)
                {
                    if (bv > vmMax) { vmMax = bv; vmBody = f.Match.Body.BodyName; vmAt = at; }
                    if (bu > uMax) { uMax = bu; uBody = f.Match.Body.BodyName; }
                }
            }
            rows.Add(row);
        }
        double freeMass = kept.Where(f => !f.Fixed).Sum(f => f.Mass + f.Extra), fixedMass = kept.Where(f => f.Fixed).Sum(f => f.Mass);
        double pkgMass = fes.Where(f => !f.Fixed).Sum(f => f.Match.Part!.Volume * f.Material.Density);
        var expected = acc * freeMass;
        var rf = r.Dat.ReactionTotal ?? throw new InvalidOperationException("반력 합(*NODE PRINT TOTALS)이 .dat 에 없습니다.");
        double errPct = (rf + expected).Norm / expected.Norm * 100;
        double covPct = (rf + acc * pkgMass).Norm / (acc * pkgMass).Norm * 100;
        log($"평형: RF = ({rf.X:G5}, {rf.Y:G5}, {rf.Z:G5}) N, 해석 질량(이관 포함) {freeMass:G5} kg × a = {expected.Norm:G5} N → 오차 {errPct:G3} %; 패키지 모델 질량 {pkgMass:G5} kg 대비 {covPct:G3} %");
        var unmatchedFree = matches.Where(m => m.Part is null && !fixedIds.Contains(m.Body.Id)).Select(m => m.Body.BodyName).ToList();

        var vtu = Path.Combine(outDir, "result.vtu");
        WriteVtu(vtu, fes.Where(f => f.Kept).Select(f => (f.Mesh!, f.Offset, fes.IndexOf(f))).ToList(), disp, vm);

        static JsonNode? Fin(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
        static JsonArray Strs(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        JsonObject? MinBy(string key)
        {
            var best = rows.Where(x => !x["fixed"]!.GetValue<bool>() && x[key] is JsonValue).MinBy(x => x[key]!.GetValue<double>());
            return best is null ? null : new JsonObject { ["value"] = best[key]!.GetValue<double>(), ["body"] = best["body"]!.GetValue<string>(), ["material"] = best["material"]!.GetValue<string>() };
        }
        var summary = new JsonObject
        {
            ["schema"] = "mns.simpkg.summary/2",
            ["package"] = s.Package, ["project"] = s.ProjectName, ["study"] = s.StudyName, ["type"] = s.StudyType, ["status"] = "solved",
            ["acceleration_mps2"] = J.ToJson(acc),
            ["mesh"] = new JsonObject
            {
                ["rule"] = $"h = {rule.Scale:G3} × clamp(t/{nThick}, hMin, hMax), t = 2V/A" + (rule.ScaleOnly is { } so2 ? $" (배율은 바디 {so2.Count}개만)" : ""), ["scale"] = rule.Scale,
                ["scaleOnly"] = rule.ScaleOnly is { } so3 ? Strs(so3.Order()) : null, ["throughThickness"] = nThick,
                ["hMax_m"] = hMax, ["hMin_m"] = hMin, ["fusionAverage_m"] = hFusion, ["sizeFraction"] = frac, ["minSizeFraction"] = minFrac, ["modelDiagonal_m"] = diag,
                ["element"] = order == 2 ? "C3D10" : "C3D4", ["nodes"] = nodeCount, ["elements"] = elemCount,
                ["bodiesBelowRule"] = Strs(fes.Where(f => f.Mesh is not null && f.Thick / f.Size < nThick - 1e-9).Select(f => $"{f.Match.Body.BodyName}(t {f.Thick * 1000:G3} mm / h {f.Size * 1000:G3} mm)")),
            },
            ["ties"] = new JsonObject
            {
                ["bondedPairs"] = pairs.Count, ["tied"] = ties.Count, ["bothFixed"] = bothFixed, ["missingGeometry"] = missing,
                ["tolerance_m"] = tol, ["maxTieTolerance_m"] = ties.Count > 0 ? ties.Max(t => t.Tol) : tol, ["notWithinTolerance"] = Strs(weak),
            },
            ["solver"] = new JsonObject
            {
                ["ccx"] = inst.Ccx, ["seconds"] = r.Seconds, ["note"] = r.SolverNote, ["pastixRefinementError"] = r.RefinementError, ["deck"] = r.InpPath,
            },
            ["equilibrium"] = new JsonObject
            {
                ["reaction_N"] = J.ToJson(rf), ["expected_N"] = J.ToJson(-expected), ["freeMass_kg"] = freeMass, ["fixedMass_kg"] = fixedMass,
                ["movedMass_kg"] = givers.Sum(g => g.MovedMass), ["fusionMassMatched_kg"] = fes.Sum(f => f.Match.Part!.Volume * f.Material.Density),
                ["packageFreeMass_kg"] = pkgMass, ["errorPct"] = errPct, ["coverageErrorPct"] = covPct, ["unmatchedFreeBodies"] = Strs(unmatchedFree),
                ["note"] = "RF = 고정 바디 절점 반력 합. 기대값 = −(고정 아닌 해석 바디 FE 질량 + 이관 질량) × 가속도(평형 오차). 패키지 모델 질량 = 고정 아닌 매칭 바디 STEP 부피 × 패키지 밀도(반력 오차 = |RF + m·a| / |m·a|). 고정 바디 자체 관성은 구속에 바로 흡수.",
            },
            ["maxVonMises"] = new JsonObject { ["Pa"] = vmMax, ["body"] = vmBody, ["at_m"] = J.ToJson(vmAt) },
            ["maxDisplacement"] = new JsonObject { ["m"] = uMax, ["body"] = uBody },
            ["minSafetyFactor"] = MinBy("safetyFactor"),
            ["minSafetyFactorP99"] = MinBy("safetyFactorP99"),
            ["bodies"] = new JsonArray(rows.ToArray()),
            ["top10"] = new JsonArray(rows.Where(x => x["maxVonMises_Pa"] is not null && !x["fixed"]!.GetValue<bool>()).OrderByDescending(x => x["maxVonMises_Pa"]!.GetValue<double>()).Take(10)
                .Select(x => x.DeepClone()).ToArray()),
            ["top10P99"] = new JsonArray(rows.Where(x => x["safetyFactorP99"] is JsonValue && !x["fixed"]!.GetValue<bool>()).OrderBy(x => x["safetyFactorP99"]!.GetValue<double>()).Take(10)
                .Select(x => x.DeepClone()).ToArray()),
            ["unmatched"] = Strs(matches.Where(m => m.Part is null).Select(m => m.Body.Name)),
            ["vtu"] = vtu,
            ["warnings"] = Strs(warn),
        };

        if (modes > 0)
        {
            log($"CalculiX 고유진동 {modes}개 …");
            var rm = CcxRunner.Solve(inst, sv => Write(sv, "modal", true), ccxDir, "modal", TimeSpan.FromHours(4), threads: threads);
            if (!rm.Ok) { warn.Add($"고유진동 실패: {rm.Error}"); summary["warnings"] = Strs(warn); }
            else
            {
                var arr = new JsonArray();
                for (int k = 0; k < rm.Dat.FrequenciesHz.Count; k++)
                {
                    // 그 모드에서 변위가 가장 큰 바디(모드 모양의 주인)
                    var d = rm.Displacement(k + 1);
                    string who = ""; double best = -1;
                    foreach (var f in kept.Where(f => !f.Fixed))
                    {
                        double mx = 0;
                        for (int n = 0; n < f.Mesh!.Nodes.Count; n++) if (d.TryGetValue(f.Offset + n + 1, out var u)) mx = Math.Max(mx, u.Norm);
                        if (mx > best) { best = mx; who = f.Match.Body.BodyName; }
                    }
                    arr.Add(new JsonObject { ["mode"] = k + 1, ["hz"] = rm.Dat.FrequenciesHz[k], ["dominantBody"] = who });
                }
                summary["modal"] = new JsonObject
                {
                    ["modes"] = arr, ["seconds"] = rm.Seconds, ["note"] = rm.SolverNote, ["deck"] = rm.InpPath,
                    ["boundary"] = "정적 해석과 같음: 패키지 고정 바디 전 절점 고정(결합 면 둘레 제외) + 결합 접촉 TIE, 질량 = FE 질량 + 이관 질량",
                };
                log($"고유진동: {string.Join(", ", rm.Dat.FrequenciesHz.Select(x => x.ToString("F1", Inv)))} Hz");
            }
        }
        return summary;
    }

    /// <summary>
    /// ccx .dat 의 *EL PRINT S 블록 → 집합 이름(대문자) → 적분점 von Mises 목록 [Pa].
    /// 블록 머리 " stresses (elem, integ.pnt.,sxx,syy,szz,sxy,sxz,syz) for set B12 and time  0.1000000E+01", 자료 줄 = 요소 번호 · 적분점 · 응력 6성분.
    /// </summary>
    public static Dictionary<string, List<double>> ReadElementVonMises(string datPath)
    {
        var r = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(datPath)) return r;
        List<double>? cur = null;
        var tmp = new double[6];
        foreach (var raw in File.ReadLines(datPath))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("stresses (elem", StringComparison.Ordinal))
            {
                int i = l.IndexOf(" for set ", StringComparison.Ordinal);
                var name = i < 0 ? "" : l[(i + 9)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!r.TryGetValue(name, out cur)) r[name] = cur = new List<double>();
                continue;
            }
            if (cur is null) continue;
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 8 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            {
                bool ok = true;
                for (int k = 0; k < 6 && ok; k++) ok = double.TryParse(p[k + 2], NumberStyles.Float, Inv, out tmp[k]);
                // .dat 순서 sxx syy szz sxy sxz syz → Tensor.VonMises 순서(xx yy zz xy yz zx)
                if (ok) { cur.Add(Tensor.VonMises([tmp[0], tmp[1], tmp[2], tmp[3], tmp[5], tmp[4]])); continue; }
            }
            cur = null; // 다른 출력 블록
        }
        return r;
    }

    /// <summary>백분위(0~1) — 선형 보간(numpy.percentile 기본과 같음: 위치 (n−1)·p).</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var a = values.OrderBy(x => x).ToArray();
        double pos = (a.Length - 1) * Math.Clamp(p, 0, 1);
        int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, a.Length - 1);
        return a[lo] + (a[hi] - a[lo]) * (pos - lo);
    }

    /// <summary>
    /// 곡면 2차 사면체(gmsh 는 중간 절점을 곡면 위에 둔다)는 요소가 형상보다 굵으면 적분점 야코비안이 음수가 되어 ccx 가 멈춘다(실측:
    /// 'nonpositive jacobian'). 그런 요소의 중간 절점을 변 중점으로 옮겨(곧은 변) 고친다 — 이웃 요소도 바뀌므로 없어질 때까지 반복. 고친 요소 수.
    /// </summary>
    public static int StraightenInvalid(FeMesh m)
    {
        int fixedCount = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            var bad = Enumerable.Range(0, m.Elements.Count).Where(e => m.Elements[e].Type == FeType.C3D10 && MinDetJ(m, m.Elements[e].N) <= 0).ToList();
            if (bad.Count == 0) return fixedCount;
            fixedCount += bad.Count;
            foreach (var e in bad)
            {
                var n = m.Elements[e].N;
                foreach (var (k, a, b) in Tet10Edges) m.Nodes[n[k]] = (m.Nodes[n[a]] + m.Nodes[n[b]]) * 0.5;
            }
        }
        return fixedCount;
    }

    private static readonly (int Mid, int A, int B)[] Tet10Edges = [(4, 0, 1), (5, 1, 2), (6, 2, 0), (7, 0, 3), (8, 1, 3), (9, 2, 3)];

    private const double GpA = 0.5854101966249685, GpB = 0.1381966011250105;
    /// <summary>ccx C3D10 적분점 4개(무게중심 좌표, 가중치 1/24).</summary>
    private static readonly double[][] Gauss4 = [[GpA, GpB, GpB, GpB], [GpB, GpA, GpB, GpB], [GpB, GpB, GpA, GpB], [GpB, GpB, GpB, GpA]];

    /// <summary>C3D10 야코비안 행렬식(무게중심 좌표 L, 모양 함수 모서리 L_i(2L_i−1) · 중간 4L_iL_j).</summary>
    private static double DetJ(FeMesh m, int[] n, double[] L)
    {
        // dN/dL_i 를 모아 dx/dL_i(i=0..3), 그 뒤 dx/dξ_j = dx/dL_{j+1} − dx/dL_0
        var dL = new Vec3[4];
        for (int i = 0; i < 4; i++) dL[i] = m.Nodes[n[i]] * (4 * L[i] - 1);
        foreach (var (k, p, q) in Tet10Edges)
        {
            dL[p] += m.Nodes[n[k]] * (4 * L[q]);
            dL[q] += m.Nodes[n[k]] * (4 * L[p]);
        }
        Vec3 j1 = dL[1] - dL[0], j2 = dL[2] - dL[0], j3 = dL[3] - dL[0];
        return Vec3.Dot(j1, Vec3.Cross(j2, j3));
    }

    /// <summary>야코비안 최솟값 — ccx 적분점 4개 + 중심.</summary>
    private static double MinDetJ(FeMesh m, int[] n) => Math.Min(Gauss4.Min(L => DetJ(m, n, L)), DetJ(m, n, [0.25, 0.25, 0.25, 0.25]));

    /// <summary>ccx 가 체적력(GRAV)을 적분하는 것과 같은 질량: C3D10 은 곡면 요소 4점 적분 Σ detJ/24 · ρ(굵은 곡면 요소는 곧은 변 부피와 수 % 다르다 — 실측 2 %), 그 밖은 곧은 변.</summary>
    public static double CcxMass(FeMesh m, double rho)
    {
        double v = 0;
        for (int e = 0; e < m.Elements.Count; e++)
            v += m.Elements[e].Type == FeType.C3D10 ? Gauss4.Sum(L => DetJ(m, m.Elements[e].N, L)) / 24 : m.ElementVolume(e);
        return v * rho;
    }

    /// <summary>C3D10 면(1 기반) → 모서리 + 중간 절점(요소 안 위치). C3D4 는 모서리만.</summary>
    private static readonly int[][] Tet10Faces = [[0, 1, 2, 4, 5, 6], [0, 3, 1, 7, 8, 4], [1, 3, 2, 8, 9, 5], [2, 3, 0, 9, 7, 6]];

    private static IEnumerable<int> FaceNodes(FeMesh m, FeFace f)
    {
        var el = m.Elements[f.Element];
        return el.Type == FeType.C3D10 ? Tet10Faces[f.Face - 1].Select(k => el.N[k]) : f.Corners;
    }

    /// <summary>슬레이브 외곽 절점 중 마스터 외곽 면(모서리 삼각형)에서 tol 안에 있는 수(그 좌표는 near 에).</summary>
    private static int NodesNear(Fe slave, Fe master, double tol, List<Vec3> near)
    {
        var mm = master.Mesh!;
        var ctr = master.Ext.Select(x => x.Center).ToList();
        double rad = master.Ext.Max(x => x.Corners.Max(c => (mm.Nodes[c] - x.Center).Norm));
        var hash = new SpatialHash(ctr, Math.Max(rad, tol));
        int n = 0;
        foreach (var node in slave.Ext.SelectMany(x => FaceNodes(slave.Mesh!, x)).Distinct())
        {
            var p = slave.Mesh!.Nodes[node];
            foreach (var k in hash.Within(p, rad + tol))
            {
                var c = master.Ext[k].Corners;
                if (PointTriangleDistance(p, mm.Nodes[c[0]], mm.Nodes[c[1]], mm.Nodes[c[2]]) > tol) continue;
                n++;
                near.Add(p);
                break;
            }
        }
        return n;
    }

    /// <summary>점-삼각형 최단 거리(Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static double PointTriangleDistance(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.Norm;
        var bp = p - b;
        double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.Norm;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).Norm;
        var cp = p - c;
        double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.Norm;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).Norm;
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).Norm;
        double den = 1 / (va + vb + vc);
        return (p - (a + ab * (vb * den) + ac * (vc * den))).Norm;
    }

    /// <summary>
    /// ParaView 용 .vtu(ASCII): 점 = 바디 메시 절점(전역 번호 순), 셀 = C3D10 → VTK_QUADRATIC_TETRA(24, 절점 순서 같음) · C3D4 → VTK_TETRA(10),
    /// 점 자료 U [m]·vonMises [Pa], 셀 자료 body(바디 번호 = summary bodies 순서).
    /// </summary>
    public static void WriteVtu(string path, IReadOnlyList<(FeMesh Mesh, int Offset, int Body)> parts, IReadOnlyDictionary<int, Vec3> disp, IReadOnlyDictionary<int, double> vm)
    {
        int np = parts.Sum(p => p.Mesh.Nodes.Count), nc = parts.Sum(p => p.Mesh.Elements.Count);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<VTKFile type=\"UnstructuredGrid\" version=\"0.1\" byte_order=\"LittleEndian\">");
        sb.AppendLine("<UnstructuredGrid>");
        sb.AppendLine($"<Piece NumberOfPoints=\"{np}\" NumberOfCells=\"{nc}\">");
        sb.AppendLine("<Points><DataArray type=\"Float64\" NumberOfComponents=\"3\" format=\"ascii\">");
        foreach (var (m, _, _) in parts) foreach (var p in m.Nodes) sb.Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').AppendLine(F(p.Z));
        sb.AppendLine("</DataArray></Points>");
        sb.AppendLine("<Cells><DataArray type=\"Int64\" Name=\"connectivity\" format=\"ascii\">");
        int bias = 0;
        foreach (var (m, _, _) in parts) { foreach (var e in m.Elements) sb.AppendLine(string.Join(' ', e.N.Select(n => n + bias))); bias += m.Nodes.Count; }
        sb.AppendLine("</DataArray><DataArray type=\"Int64\" Name=\"offsets\" format=\"ascii\">");
        long off = 0;
        foreach (var (m, _, _) in parts) foreach (var e in m.Elements) sb.AppendLine((off += e.N.Length).ToString(Inv));
        sb.AppendLine("</DataArray><DataArray type=\"UInt8\" Name=\"types\" format=\"ascii\">");
        foreach (var (m, _, _) in parts) foreach (var e in m.Elements) sb.AppendLine(e.Type == FeType.C3D10 ? "24" : "10");
        sb.AppendLine("</DataArray></Cells>");
        sb.AppendLine("<PointData Vectors=\"U\" Scalars=\"vonMises\"><DataArray type=\"Float64\" Name=\"U\" NumberOfComponents=\"3\" format=\"ascii\">");
        foreach (var (m, o, _) in parts)
            for (int n = 0; n < m.Nodes.Count; n++)
            {
                var u = disp.TryGetValue(o + n + 1, out var v) ? v : Vec3.Zero;
                sb.Append(F(u.X)).Append(' ').Append(F(u.Y)).Append(' ').AppendLine(F(u.Z));
            }
        sb.AppendLine("</DataArray><DataArray type=\"Float64\" Name=\"vonMises\" format=\"ascii\">");
        foreach (var (m, o, _) in parts) for (int n = 0; n < m.Nodes.Count; n++) sb.AppendLine(F(vm.TryGetValue(o + n + 1, out var v) ? v : 0));
        sb.AppendLine("</DataArray></PointData>");
        sb.AppendLine("<CellData Scalars=\"body\"><DataArray type=\"Int32\" Name=\"body\" format=\"ascii\">");
        foreach (var (m, _, b) in parts) foreach (var _ in m.Elements) sb.AppendLine(b.ToString(Inv));
        sb.AppendLine("</DataArray></CellData>");
        sb.AppendLine("</Piece></UnstructuredGrid></VTKFile>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
