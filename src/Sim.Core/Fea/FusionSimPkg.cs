using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Fea;

/// <summary>패키지 바디: Geometry.Name("조립체 &gt; 컴포넌트 &gt; 바디"), Description JSON 의 bodyName·componentNames, 지정 재료 id.</summary>
public sealed record PkgBody(string Id, string Name, string BodyName, string[] Components, string? MaterialId);

/// <summary>재료(SI): 등방 탄성 + 금속 항복·인장 강도. Elastoplastic = MaterialPhysical.advanced(탄소성 경화) 속성 그대로(없으면 null).</summary>
public sealed record PkgMaterial(string Id, string Name, double Density, double E, double Nu, double G, double Yield, double Tensile, JsonObject? Elastoplastic);

/// <summary>
/// 하중. Type = LinearAcceleration | Gravity | LinearVelocity(초기 속도) | 그 밖(원문 type).
/// 가속도·중력: Vector = 단위 방향, Magnitude [m/s²]. 속도: Vector = 속도 성분 [m/s], Magnitude = |v|. Bodies 비면 전역.
/// </summary>
public sealed record PkgLoad(string Id, string Type, string Name, string LoadCaseId, double Magnitude, Vec3 Vector, bool On, string[] Bodies);

/// <summary>구속: Dofs = UX..RZ → "Fixed"|"Free"…, Bodies = 지정 면이 속한 바디, Faces = 지정 면 수(SMT 면 번호라 STEP 면과 대응 불가).</summary>
public sealed record PkgConstraint(string Id, string Name, string Type, string LoadCaseId, IReadOnlyDictionary<string, string> Dofs, string CoordinateSystem, string[] Bodies, int Faces);

/// <summary>접촉: Type = Bonded | Separation | …, Primary/Secondary = 바디 id.</summary>
public sealed record PkgContact(string Id, string Name, string Type, string Penetration, double? Friction, string Primary, string Secondary);

/// <summary>
/// Fusion 해석 패키지(.adsksimpkg)에서 읽은 해석 정의 하나. 단위 SI. 좌표·방향은 Fusion 모델(설계) 좌표 그대로 —
/// Fusion STEP 내보내기와 같은 좌표라 변환 없이 쓴다(조립체 STEP 의 축과 하중 벡터가 같은 틀).
/// </summary>
public sealed class FusionStudy
{
    public string Package { get; init; } = "";
    public string Producer { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string StudyName { get; init; } = "";
    /// <summary>LinearStress | Explicit | …(ProblemDefinitionStudySimulation.Type).</summary>
    public string StudyType { get; init; } = "";
    public string FusionVersion { get; init; } = "";
    public string ActiveLoadCase { get; init; } = "";
    public List<(string Id, string Name)> LoadCases { get; } = new();
    public List<PkgBody> Bodies { get; } = new();
    public List<PkgMaterial> Materials { get; } = new();
    public List<PkgLoad> Loads { get; } = new();
    public List<PkgConstraint> Constraints { get; } = new();
    public List<PkgContact> Contacts { get; } = new();
    /// <summary>강체로 지정된 바디 id(Explicit RigidBody).</summary>
    public List<string> RigidBodies { get; } = new();
    /// <summary>MeshSettingsVariablesFEACS 속성(MeshSizeFraction, MinSizeFraction, AbsoluteSize, MeshElementOrderType …).</summary>
    public JsonObject Mesh { get; set; } = new();
    /// <summary>해석 매개변수: SimulationParameterType → 속성(EventSimulation, RequestedResults, SurfaceContactSettings, LinearStaticStress …).</summary>
    public JsonObject Parameters { get; } = new();
    /// <summary>모르는 type · 따라가지 못한 관계.</summary>
    public List<string> Warnings { get; } = new();

    public PkgBody Body(string id) => Bodies.First(b => b.Id == id);
    public PkgMaterial? MaterialOf(PkgBody b) => Materials.FirstOrDefault(m => m.Id == b.MaterialId);
    public double MeshNum(string k, double def) => Mesh[k] is JsonValue v && v.TryGetValue(out double d) ? d : def;
}

/// <summary>
/// .adsksimpkg 리더(ZIP: manifest.json + G-&lt;uuid&gt;/project.json + model.7z). model.7z(Autodesk SMT 형상)는 읽지 않는다 —
/// 바디는 이름으로 STEP 과 맞춘다(<see cref="SimPkgCcx"/>). project.json = JSON:API 그래프 {"data":[{type,id,attributes,relationships}]} 를
/// 관계를 따라 해석한다. 관계 키에 뒤 공백이 붙어 오는 것("attributes ")이 있어 키는 Trim 해 비교한다.
/// 재료·하중·구속·강체가 어느 바디에 붙는지는 AttributeAssignment(GeometryId = 바디) → AttributeGeometryAssignment(AttributeId = 대상).
/// </summary>
public static class FusionSimPkg
{
    private static readonly HashSet<string> Known =
    [
        "ProjectSimulation", "ProblemDefinitionStudySimulation", "AttributeAssignment", "AttributeGeometryAssignment", "GeometryReference",
        "Contact", "GeometryAssignment", "LinearStaticStress", "SurfaceContactSettings", "EventSimulation", "RequestedResults", "RigidBody",
        "LinearAcceleration", "LinearVelocity", "GravityLoad", "Vector3d", "LoadCase", "Constraint", "Material", "MaterialMechanical",
        "MaterialMechanicalBehaviorIsotropic", "MaterialThermal", "MaterialThermalBehaviorIsotropic", "MaterialSpecificMetal",
        "MaterialAppearance", "MaterialPhysical", "MaterialPhysicalElastoplastic", "MeshSettingsVariablesFEACS", "Geometry", "files",
        "ApplicationData", "ApplicationDataItem",
    ];

    /// <summary>패키지 읽기 — manifest 의 첫 작업 모델·첫 해석(그 밖의 해석이 있으면 경고).</summary>
    public static FusionStudy Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        JsonObject Entry(string name)
        {
            var e = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/') == name) ?? throw new InvalidDataException($"{Path.GetFileName(path)}: '{name}' 이 없습니다.");
            using var r = new StreamReader(e.Open());
            return J.ParseObject(r.ReadToEnd());
        }
        var man = Entry("manifest.json");
        var wm = man["workingModels"]?.AsArray().FirstOrDefault() as JsonObject ?? throw new InvalidDataException("manifest.json 에 workingModels 가 없습니다.");
        var studies = wm["studies"]?.AsArray() ?? [];
        var st = studies.FirstOrDefault() as JsonObject ?? throw new InvalidDataException("manifest.json 에 해석(studies)이 없습니다.");
        var s = Parse(Entry($"{J.Str(wm, "folder", "")}/{J.Str(st, "file", "project.json")}"), Path.GetFileName(path), J.Str(man, "producer", ""));
        if (studies.Count > 1) s.Warnings.Add($"해석이 {studies.Count}개 — 첫 번째('{J.Str(st, "name", "")}')만 읽음");
        if (man["workingModels"]!.AsArray().Count > 1) s.Warnings.Add("작업 모델이 여럿 — 첫 번째만 읽음");
        return s;
    }

    private static Encoding? _cp949;

    /// <summary>
    /// 이중 인코딩된 한글 복원. 실측(20261001-2031_Explicit_8cfe5590): 이름 "BatteryCase:1 &gt; ÄÉÀÌ½º:1" 의 원문 바이트는 C3 84 C3 89 …
    /// = CP949 바이트(C4 C9 C0 CC BD BA = "케이스")를 Latin-1 문자로 읽어 UTF-8 로 다시 쓴 꼴이다(같은 이름의 "본체1" 은 정상 UTF-8).
    /// U+0080~U+00FF 문자만으로 된 짝수 길이 연속 구간을 Latin-1 바이트로 되돌려 CP949 로 엄격히 풀고, 결과가 모두 한글 음절일 때만 바꾼다
    /// (°·× 같은 단독 Latin-1 기호는 그대로).
    /// </summary>
    private static bool IsLatin1High(char c) => c is >= (char)0x80 and <= (char)0xFF;

    public static string FixMojibake(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(IsLatin1High)) return s;
        if (_cp949 is null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _cp949 = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            int j = i;
            while (j < s.Length && IsLatin1High(s[j])) j++;
            if (j == i) { sb.Append(s[i++]); continue; }
            var run = s[i..j];
            string? fixedRun = null;
            if (run.Length % 2 == 0)
            {
                try
                {
                    var t = _cp949.GetString(run.Select(c => (byte)c).ToArray());
                    if (t.Length > 0 && t.All(c => c is >= (char)0xAC00 and <= (char)0xD7A3)) fixedRun = t;
                }
                catch (DecoderFallbackException) { }
            }
            sb.Append(fixedRun ?? run);
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>그래프 안 모든 문자열 값(Description 안 JSON 문자열 포함)에 <see cref="FixMojibake"/> 적용 — 바꾼 수.</summary>
    private static int FixStrings(JsonNode? n)
    {
        int k = 0;
        switch (n)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue(out string? str))
                    {
                        var f = FixMojibake(str);
                        if (!ReferenceEquals(f, str) && f != str) { o[key] = f; k++; }
                    }
                    else k += FixStrings(o[key]);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue(out string? str))
                    {
                        var f = FixMojibake(str);
                        if (f != str) { a[i] = f; k++; }
                    }
                    else k += FixStrings(a[i]);
                }
                break;
        }
        return k;
    }

    /// <summary>project.json 그래프 → 해석 정의.</summary>
    public static FusionStudy Parse(JsonObject project, string package = "", string producer = "")
    {
        int recoded = FixStrings(project);
        var data = project["data"]?.AsArray().OfType<JsonObject>().ToList() ?? throw new InvalidDataException("project.json 에 data 가 없습니다.");
        var by = new Dictionary<string, JsonObject>();
        foreach (var x in data) by[J.Str(x, "id", "")] = x;
        static string Type(JsonObject x) => J.Str(x, "type", "");
        static JsonObject Attr(JsonObject x) => x["attributes"] as JsonObject ?? new JsonObject();
        static List<string> Rel(JsonObject x, string key)
        {
            var ids = new List<string>();
            if (x["relationships"] is not JsonObject rels) return ids;
            foreach (var (k, v) in rels)
            {
                if (k.Trim() != key || v?["data"] is not { } d) continue;
                foreach (var n in d is JsonArray a ? a.ToList() : [d])
                    if (n?["id"]?.GetValue<string>() is { } id) ids.Add(id);
            }
            return ids;
        }
        JsonObject? One(JsonObject x, string key) => Rel(x, key).Select(id => by.GetValueOrDefault(id)).FirstOrDefault(o => o is not null);
        static double Num(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
        Vec3 V(JsonObject? v) => v is null ? Vec3.Zero : new Vec3(Num(Attr(v), "X"), Num(Attr(v), "Y"), Num(Attr(v), "Z"));

        var ps = data.FirstOrDefault(x => Type(x) == "ProjectSimulation");
        var pd = data.FirstOrDefault(x => Type(x) == "ProblemDefinitionStudySimulation") ?? throw new InvalidDataException("ProblemDefinitionStudySimulation 이 없습니다.");
        var pda = Attr(pd);
        string active = "";
        if (pda["ExternalData"] is JsonObject ext)
            foreach (var (_, v) in ext) if (v?["ActiveLoadCase"]?.GetValue<string>() is { } lc) active = lc;
        var s = new FusionStudy
        {
            Package = package, Producer = producer, ProjectName = ps is null ? "" : J.Str(Attr(ps), "Name", ""),
            StudyName = J.Str(pda, "Name", ""), StudyType = J.Str(pda, "Type", ""), FusionVersion = J.Str(pda, "Version", ""), ActiveLoadCase = active,
        };
        if (recoded > 0) s.Warnings.Add($"이중 인코딩(CP949 바이트를 Latin-1 로 읽은 꼴)된 문자열 {recoded}개를 한글로 복원");
        foreach (var g in data.Where(x => Type(x) == "ProblemDefinitionStudySimulation").Skip(1)) s.Warnings.Add($"해석 정의가 여럿 — '{J.Str(Attr(g), "Name", "")}' 무시");
        foreach (var t in data.Select(Type).Where(t => !Known.Contains(t)).GroupBy(t => t))
            s.Warnings.Add($"모르는 type '{t.Key}' ×{t.Count()} — 무시");

        // 지정 대상(재료·하중·구속·강체 id) → (바디 id, 면 참조 수)
        var assigned = new Dictionary<string, List<(string Body, int Faces)>>();
        foreach (var aa in Rel(pd, "attributesassignment").Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var geom = J.Str(Attr(aa), "GeometryId", "");
            foreach (var aga in Rel(aa, "attributes").Select(by.GetValueOrDefault).OfType<JsonObject>())
            {
                var target = J.Str(Attr(aga), "AttributeId", "");
                int faces = Rel(aga, "geometryreferences").Count(id => by.TryGetValue(id, out var r) && J.Str(Attr(r), "Type", "") == "Surface");
                if (!assigned.TryGetValue(target, out var l)) assigned[target] = l = new();
                l.Add((geom, faces));
                if (!by.ContainsKey(target)) s.Warnings.Add($"지정 대상 {target} 이 그래프에 없습니다");
            }
        }
        string[] BodiesOf(string id) => assigned.TryGetValue(id, out var l) ? l.Select(x => x.Body).Distinct().ToArray() : [];

        foreach (var g in (ps is null ? data.Where(x => Type(x) == "Geometry").Select(x => J.Str(x, "id", "")) : Rel(ps, "geometry")).Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var a = Attr(g);
            string name = J.Str(a, "Name", ""), body = name.Split(" > ")[^1];
            string[] comps = [];
            try
            {
                if (J.StrOpt(a, "Description") is { Length: > 0 } desc && JsonNode.Parse(desc) is JsonObject d)
                {
                    body = J.Str(d, "bodyName", body);
                    comps = d["componentNames"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToArray() ?? [];
                }
            }
            catch (System.Text.Json.JsonException) { s.Warnings.Add($"바디 '{name}' Description 이 JSON 이 아님 — 이름 끝 토막을 바디 이름으로"); }
            s.Bodies.Add(new PkgBody(J.Str(g, "id", ""), name, body, comps, null));
        }

        foreach (var m in Rel(pd, "materials").Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var mid = J.Str(m, "id", "");
            var iso = One(One(m, "mechanical") ?? new JsonObject(), "behavior");
            if (iso is null || Type(iso) != "MaterialMechanicalBehaviorIsotropic")
                s.Warnings.Add($"재료 '{J.Str(Attr(m), "Name", "")}': 등방 탄성(MaterialMechanicalBehaviorIsotropic)이 아님 — 탄성 값 없음");
            var ia = iso is null ? new JsonObject() : Attr(iso);
            var sp = One(m, "specific");
            var ep = One(One(m, "physical") ?? new JsonObject(), "advanced");
            s.Materials.Add(new PkgMaterial(mid, J.Str(Attr(m), "Name", ""), Num(ia, "Density"), Num(ia, "YoungModulus"), Num(ia, "PoissonRatio"), Num(ia, "ShearModulus"),
                sp is null ? double.NaN : Num(Attr(sp), "YieldStrength"), sp is null ? double.NaN : Num(Attr(sp), "TensileStrength"),
                ep is not null && Type(ep) == "MaterialPhysicalElastoplastic" ? (JsonObject)Attr(ep).DeepClone() : null));
            foreach (var b in BodiesOf(mid))
            {
                int i = s.Bodies.FindIndex(x => x.Id == b);
                if (i < 0) { s.Warnings.Add($"재료 '{J.Str(Attr(m), "Name", "")}' 의 바디 {b} 가 Geometry 목록에 없습니다"); continue; }
                if (s.Bodies[i].MaterialId is not null && s.Bodies[i].MaterialId != mid) s.Warnings.Add($"바디 '{s.Bodies[i].Name}' 에 재료가 둘 — 나중 것 사용");
                s.Bodies[i] = s.Bodies[i] with { MaterialId = mid };
            }
        }
        foreach (var b in s.Bodies.Where(b => b.MaterialId is null)) s.Warnings.Add($"바디 '{b.Name}' 에 재료가 지정되지 않았습니다");

        foreach (var lc in Rel(pd, "loadcases").Select(by.GetValueOrDefault).OfType<JsonObject>())
            s.LoadCases.Add((J.Str(lc, "id", ""), J.Str(Attr(lc), "Name", "")));

        foreach (var l in Rel(pd, "loads").Concat(Rel(pd, "gravityload")).Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var a = Attr(l);
            string id = J.Str(l, "id", ""), t = Type(l), lcId = J.Str(a, "LoadCaseId", "");
            switch (t)
            {
                case "LinearAcceleration":
                    s.Loads.Add(new PkgLoad(id, t, J.Str(a, "Name", ""), lcId, Num(a, "Magnitude"), V(One(l, "directionunitvector")), true, BodiesOf(id)));
                    break;
                case "GravityLoad":
                    s.Loads.Add(new PkgLoad(id, "Gravity", J.Str(a, "Name", ""), lcId, Num(a, "Magnitude"), V(One(l, "directionunitvector")), J.Bool(a, "IsOn", true), BodiesOf(id)));
                    break;
                case "LinearVelocity":
                    var v = V(One(l, "velocitycomponents"));
                    s.Loads.Add(new PkgLoad(id, t, J.Str(a, "Name", ""), lcId, v.Norm, v, true, BodiesOf(id)));
                    break;
                default:
                    s.Warnings.Add($"모르는 하중 type '{t}'('{J.Str(a, "Name", "")}') — 해석에 쓰지 않음");
                    s.Loads.Add(new PkgLoad(id, t, J.Str(a, "Name", ""), lcId, Num(a, "Magnitude"), Vec3.Zero, true, BodiesOf(id)));
                    break;
            }
        }

        foreach (var c in Rel(pd, "constraints").Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var a = Attr(c);
            var id = J.Str(c, "id", "");
            var dofs = new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.ToDictionary(k => k, k => J.Str(a, k, ""));
            int faces = assigned.TryGetValue(id, out var l) ? l.Sum(x => x.Faces) : 0;
            s.Constraints.Add(new PkgConstraint(id, J.Str(a, "Name", ""), J.Str(a, "ConstraintType", ""), J.Str(a, "LoadCaseId", ""), dofs, J.Str(a, "CoordinateSystem", ""), BodiesOf(id), faces));
        }

        string GeomOf(JsonObject contact, string key) => One(contact, key) is { } ga ? J.Str(Attr(ga), "GeometryId", "") : "";
        foreach (var p in Rel(pd, "attributes").Select(by.GetValueOrDefault).OfType<JsonObject>())
        {
            var a = Attr(p);
            var t = Type(p);
            if (t == "Contact")
            {
                string pri = GeomOf(p, "primarygeometryassignments"), sec = GeomOf(p, "secondarygeometryassignments");
                if (pri == "" || sec == "") s.Warnings.Add($"접촉 '{J.Str(a, "Name", "")}': 바디 지정을 따라가지 못함");
                s.Contacts.Add(new PkgContact(J.Str(p, "id", ""), J.Str(a, "Name", ""), J.Str(a, "ContactType", ""), J.Str(a, "PenetrationType", ""), J.NumOpt(a, "Friction"), pri, sec));
            }
            else if (t == "RigidBody") s.RigidBodies.AddRange(BodiesOf(J.Str(p, "id", "")));
            else
            {
                var o = (JsonObject)a.DeepClone();
                o.Remove("Type");
                s.Parameters[J.Str(a, "SimulationParameterType", t)] = o;
            }
        }
        if (One(pd, "meshsettingsvariables") is { } ms) s.Mesh = (JsonObject)Attr(ms).DeepClone();
        if (s.LoadCases.Count > 1) s.Warnings.Add($"하중 조건이 {s.LoadCases.Count}개 — 해석은 활성 하중 조건만");
        return s;
    }

    /// <summary>
    /// 우리 해석 정의 형식 study.json(schema "mns.study/1") — Fusion 그래프를 차용하되 관계 대신 평평한 섹션 + id 참조:
    /// source{package,producer,project,study,fusionVersion} · analysis{type,activeLoadCase,parameters{SimulationParameterType→속성}} ·
    /// loadCases[{id,name}] · bodies[{id,name,bodyName,components[],material→materials.id}] ·
    /// materials[{id,name,density,E,nu,G,yield,tensile,elastoplastic?}] ·
    /// loads[{id,type,name,loadCase,magnitude,direction|velocity,on,bodies[]→bodies.id(비면 전역)}] ·
    /// constraints[{id,type,name,loadCase,dofs{UX..RZ},coordinateSystem,bodies[],faces}] ·
    /// contacts[{id,type,name,penetration,friction,primary,secondary→bodies.id}] · rigidBodies[→bodies.id] · mesh{Fusion 메시 설정} · warnings[].
    /// 단위 SI(m, kg, s, N, Pa), 좌표·방향은 Fusion 모델 좌표.
    /// </summary>
    public static JsonObject ToStudyJson(FusionStudy s)
    {
        static JsonNode? N(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
        static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        return new JsonObject
        {
            ["schema"] = "mns.study/1",
            ["units"] = "SI (m, kg, s, N, Pa)",
            ["source"] = new JsonObject { ["package"] = s.Package, ["producer"] = s.Producer, ["project"] = s.ProjectName, ["study"] = s.StudyName, ["fusionVersion"] = s.FusionVersion },
            ["analysis"] = new JsonObject { ["type"] = s.StudyType, ["activeLoadCase"] = s.ActiveLoadCase, ["parameters"] = s.Parameters.DeepClone() },
            ["loadCases"] = new JsonArray(s.LoadCases.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["name"] = l.Name }).ToArray()),
            ["bodies"] = new JsonArray(s.Bodies.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["name"] = b.Name, ["bodyName"] = b.BodyName, ["components"] = Ids(b.Components), ["material"] = b.MaterialId,
            }).ToArray()),
            ["materials"] = new JsonArray(s.Materials.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["name"] = m.Name, ["density"] = N(m.Density), ["E"] = N(m.E), ["nu"] = N(m.Nu), ["G"] = N(m.G),
                ["yield"] = N(m.Yield), ["tensile"] = N(m.Tensile), ["elastoplastic"] = m.Elastoplastic?.DeepClone(),
            }).ToArray()),
            ["loads"] = new JsonArray(s.Loads.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["type"] = l.Type, ["name"] = l.Name, ["loadCase"] = l.LoadCaseId, ["magnitude"] = N(l.Magnitude),
                [l.Type == "LinearVelocity" ? "velocity" : "direction"] = J.ToJson(l.Vector), ["on"] = l.On, ["bodies"] = Ids(l.Bodies),
            }).ToArray()),
            ["constraints"] = new JsonArray(s.Constraints.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = c.Type, ["name"] = c.Name, ["loadCase"] = c.LoadCaseId,
                ["dofs"] = new JsonObject(c.Dofs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["coordinateSystem"] = c.CoordinateSystem, ["bodies"] = Ids(c.Bodies), ["faces"] = c.Faces,
            }).ToArray()),
            ["contacts"] = new JsonArray(s.Contacts.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = c.Type, ["name"] = c.Name, ["penetration"] = c.Penetration, ["friction"] = c.Friction,
                ["primary"] = c.Primary, ["secondary"] = c.Secondary,
            }).ToArray()),
            ["rigidBodies"] = Ids(s.RigidBodies),
            ["mesh"] = s.Mesh.DeepClone(),
            ["warnings"] = Ids(s.Warnings),
        };
    }
}
