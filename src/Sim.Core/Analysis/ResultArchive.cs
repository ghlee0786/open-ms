using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>불러온 결과: 다시 그릴 수 있는 World(프레임·이벤트 복원) + 요약·부가 데이터.</summary>
public sealed class LoadedResult
{
    /// <summary>자기완결 시나리오(질량특성 명시, 메시는 압축 해제 폴더) — 재실행·표시에 사용.</summary>
    public required ScenarioDoc Doc { get; init; }
    /// <summary>저장 당시 원본 시나리오(참고용, 외부 파일 경로를 포함할 수 있음).</summary>
    public required ScenarioDoc OriginalDoc { get; init; }
    public required World World { get; init; }
    public required JsonObject Summary { get; init; }
    public required JsonObject Manifest { get; init; }
    public JsonObject? Structural { get; init; }
    public JsonObject? MonteCarlo { get; init; }
    /// <summary>FEA 결과(fea.json, schema mns.fea/1 — CalculiX·OpenCourant 물체별 면 값).</summary>
    public JsonObject? Fea { get; init; }
    public string Path { get; init; } = "";
}

/// <summary>
/// 시뮬레이션 결과 파일(.mnsr = zip, schema mns.result/1). 이 프로그램에서 다시 불러와 3D 재생·텔레메트리·그래프·
/// 이벤트·요약·응력·MC 를 그대로 본다(실행 없이). 구성:
///   result.json (manifest: 종료 상태·이벤트·경고·착지·물체 통계) · scenario.json (원본) ·
///   scenario.resolved.json (물체별 질량·CG·관성 명시, 메시 형상은 패키지 meshes/*.stl(동체 좌표 m) — 원본 STL·massprops 없이 로드) ·
///   frames.bin (전 프레임 double 이진) · summary.json · events.json · timeseries.csv · devices.csv · scene.json ·
///   (선택) structural.json · mc.json · fea.json(FR-FEA-05).
/// </summary>
public static class ResultArchive
{
    public const string Schema = "mns.result/1";
    public const string Extension = ".mnsr";
    private const uint FramesMagic = 0x464E534D; // "MSNF"
    private const int FramesVersion = 1;

    public static void Save(string path, ScenarioDoc doc, World w, JsonObject? summary = null,
        JsonObject? structural = null, JsonObject? monteCarlo = null, string? gitHash = null, JsonObject? fea = null)
    {
        summary ??= Results.Summary(doc, w, gitHash);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var resolved = Resolve(doc, out var meshes);
            Text(zip, "result.json", Manifest(doc, w, summary, structural is not null, monteCarlo is not null).ToJsonString(J.Pretty));
            Text(zip, "scenario.json", doc.ToJson());
            Text(zip, "scenario.resolved.json", resolved.ToJsonString(J.Pretty));
            Text(zip, "summary.json", summary.ToJsonString(J.Pretty));
            Text(zip, "events.json", (summary["events"] ?? new JsonArray()).ToJsonString(J.Pretty));
            Text(zip, "scene.json", Results.Scene(w).ToJsonString(J.Pretty));
            if (structural is not null) Text(zip, "structural.json", structural.ToJsonString(J.Pretty));
            if (monteCarlo is not null) Text(zip, "mc.json", monteCarlo.ToJsonString());
            if (fea is not null) Text(zip, "fea.json", fea.ToJsonString());
            foreach (var (name, mesh) in meshes)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                var t = System.IO.Path.GetTempFileName();
                try { mesh.SaveBinaryStl(t); using var f = File.OpenRead(t); f.CopyTo(s); }
                finally { File.Delete(t); }
            }
            using (var s = zip.CreateEntry("frames.bin", CompressionLevel.Fastest).Open())
                WriteFrames(s, w);
            CsvEntry(zip, "timeseries.csv", p => Results.WriteTimeseriesCsv(w, p));
            CsvEntry(zip, "devices.csv", p => Results.WriteDevicesCsv(w, p));
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private static void Text(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var b = new UTF8Encoding(false).GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    private static void CsvEntry(ZipArchive zip, string name, Action<string> write)
    {
        var t = System.IO.Path.GetTempFileName();
        try
        {
            write(t);
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            using var f = File.OpenRead(t);
            f.CopyTo(s);
        }
        finally { File.Delete(t); }
    }

    private static string Safe(string name) => string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    /// <summary>
    /// 자기완결 시나리오: 초기 상태 월드를 새로 만들어(적분 전 질량특성) 물체별 mass/cg/inertia 를 명시하고
    /// 메시 형상은 동체 좌표 STL(m)로 패키지에 넣는다. modelFrame·noseAxis·massProps 는 이미 반영되어 제거한다.
    /// </summary>
    public static JsonObject Resolve(ScenarioDoc doc, out List<(string Entry, TriMesh Mesh)> meshes)
    {
        var list = new List<(string Entry, TriMesh Mesh)>();
        meshes = list;
        var w0 = doc.Build();
        var root = doc.Root.DeepClone().AsObject();
        root["resolvedFrom"] = doc.FilePath ?? doc.Name;
        void ShapeOut(JsonObject def, Body b)
        {
            if (b.Shape is { Kind: ShapeKind.Mesh } s)
            {
                var entry = $"meshes/{Safe(b.Name)}.stl";
                list.Add((entry, s.Mesh!));
                def["shape"] = new JsonObject { ["type"] = "mesh", ["file"] = entry, ["unit"] = "m" };
            }
        }
        if (root["carrier"] is JsonObject car && w0.BodyMap.TryGetValue(J.Str(car, "name", "carrier"), out var cb)) ShapeOut(car, cb);
        if (root["bodies"] is JsonArray arr)
            foreach (var d in arr.OfType<JsonObject>())
            {
                var b = w0.GetBody(J.StrReq(d, "name", "bodies[]"));
                foreach (var k in new[] { "massProps", "modelFrame", "modelToBody", "noseAxis", "density", "mass", "cg", "inertia", "massCorrection" }) d.Remove(k);
                d["mass"] = b.OwnMass;
                d["cg"] = J.ToJson(b.OwnCg);
                d["inertia"] = J.ToJson(b.OwnInertia);
                ShapeOut(d, b);
                if (d["structure"] is null && b.Structure is { Material: { } mat } st)
                {
                    var so = new JsonObject { ["material"] = mat.Key, ["section"] = st.Section, ["attachRadius"] = st.AttachRadius };
                    if (st.Thickness is { } th) so["thickness"] = th;
                    d["structure"] = so;
                }
            }
        return root;
    }

    private static JsonObject Manifest(ScenarioDoc doc, World w, JsonObject summary, bool hasStructural, bool hasMc)
    {
        var impacts = new JsonArray();
        var stats = new JsonArray();
        foreach (var b in w.Bodies)
        {
            stats.Add(new JsonObject
            {
                ["name"] = b.Name, ["maxLoadG"] = b.MaxLoadG, ["maxLoadTime"] = Parachute.Num(b.MaxLoadTime), ["releaseTime"] = Parachute.Num(b.ReleaseTime),
            });
            if (b.Impact is not { } i) continue;
            impacts.Add(new JsonObject
            {
                ["body"] = b.Name, ["time"] = i.Time, ["position"] = J.ToJson(i.Position), ["velocity"] = J.ToJson(i.Velocity),
                ["euler"] = J.ToJson(i.EulerDeg), ["noseAngle"] = i.NoseAngleDeg, ["depth"] = i.PenetrationDepth, ["g"] = i.PenetrationG,
                ["mass"] = i.Mass, ["aoa"] = i.AngleOfAttackDeg, ["vertDepth"] = i.PenetrationVerticalDepth, ["area"] = i.PenetrationArea,
                ["noseN"] = i.PenetrationNoseN, ["source"] = i.PenetrationSource,
            });
        }
        return new JsonObject
        {
            ["schema"] = Schema,
            ["scenario"] = doc.Name,
            ["scenarioFile"] = doc.FilePath,
            ["scenarioBaseDir"] = doc.BaseDir,
            ["scenarioHash"] = doc.Hash(),
            ["engine"] = "Sim.Core " + Results.EngineVersion,
            ["savedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            ["endTime_s"] = w.T,
            ["stopReason"] = w.StopReason,
            ["suspect"] = w.Suspect,
            ["steps"] = w.StepCount,
            ["bodies"] = w.Bodies.Count,
            ["frames"] = w.Recorder.Frames.Count,
            ["hasStructural"] = hasStructural,
            ["hasMonteCarlo"] = hasMc,
            ["eventLog"] = new JsonArray(w.EventLog.Select(e => (JsonNode)new JsonObject { ["t"] = e.Time, ["name"] = e.Name, ["message"] = e.Message }).ToArray()),
            ["warnings"] = new JsonArray(w.Warnings.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["impacts"] = impacts,
            ["bodyStats"] = stats,
        };
    }

    // ---------------- frames.bin ----------------

    public static void WriteFrames(Stream s, World w)
    {
        using var bw = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        var frames = w.Recorder.Frames;
        int nb = w.Bodies.Count;
        int nd = frames.Count > 0 ? frames[0].Devices.Length : w.Bodies.Sum(b => b.Devices.Count);
        int nc = frames.Count > 0 ? frames[0].Connectors.Length : w.Connectors.Count;
        bw.Write(FramesMagic); bw.Write(FramesVersion);
        bw.Write(nb); bw.Write(nd); bw.Write(nc); bw.Write(frames.Count);
        string[] dn = frames.Count > 0 ? frames[0].Devices.Select(d => d.Name).ToArray() : [];
        string[] dt = frames.Count > 0 ? frames[0].Devices.Select(d => d.Type).ToArray() : [];
        string[] cn = frames.Count > 0 ? frames[0].Connectors.Select(c => c.Name).ToArray() : [];
        string[] ct = frames.Count > 0 ? frames[0].Connectors.Select(c => c.Type).ToArray() : [];
        foreach (var x in dn.Concat(dt).Concat(cn).Concat(ct)) bw.Write(x ?? "");
        foreach (var f in frames)
        {
            if (f.Bodies.Length != nb || f.Devices.Length != nd || f.Connectors.Length != nc)
                throw new InvalidDataException("프레임마다 물체·장치·연결 수가 달라 저장할 수 없습니다.");
            bw.Write(f.T);
            foreach (var b in f.Bodies)
            {
                V(bw, b.Origin); Q(bw, b.Q); V(bw, b.Cg); V(bw, b.Velocity); V(bw, b.Omega);
                bw.Write(b.Airspeed); bw.Write(b.LoadG); V(bw, b.AirVelocity); bw.Write((byte)b.Mode);
            }
            foreach (var d in f.Devices)
            {
                bw.Write(d.Visible); bw.Write(d.Fill); V(bw, d.Anchor); V(bw, d.Tip); bw.Write(d.Size); bw.Write(d.Force);
            }
            foreach (var c in f.Connectors)
            {
                bw.Write(c.Visible); V(bw, c.A); V(bw, c.B); bw.Write(c.Tension);
            }
        }
    }

    public static List<Frame> ReadFrames(Stream s, World w)
    {
        using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != FramesMagic) throw new InvalidDataException("frames.bin 형식이 아닙니다.");
        int ver = br.ReadInt32();
        if (ver != FramesVersion) throw new InvalidDataException($"frames.bin 버전 {ver} 은 지원하지 않습니다.");
        int nb = br.ReadInt32(), nd = br.ReadInt32(), nc = br.ReadInt32(), nf = br.ReadInt32();
        if (nb != w.Bodies.Count) throw new InvalidDataException($"물체 수 불일치: 파일 {nb}, 시나리오 {w.Bodies.Count}");
        if (nf < 0 || nd < 0 || nc < 0) throw new InvalidDataException("frames.bin 머리글 손상");
        var dn = new string[nd]; var dt = new string[nd]; var cn = new string[nc]; var ct = new string[nc];
        for (int i = 0; i < nd; i++) dn[i] = br.ReadString();
        for (int i = 0; i < nd; i++) dt[i] = br.ReadString();
        for (int i = 0; i < nc; i++) cn[i] = br.ReadString();
        for (int i = 0; i < nc; i++) ct[i] = br.ReadString();
        var frames = new List<Frame>(nf);
        for (int k = 0; k < nf; k++)
        {
            double t = br.ReadDouble();
            var bodies = new BodySample[nb];
            for (int i = 0; i < nb; i++)
            {
                var o = RV(br); var q = RQ(br); var cg = RV(br); var v = RV(br); var om = RV(br);
                double air = br.ReadDouble(), lg = br.ReadDouble(); var av = RV(br); var mode = (BodyMode)br.ReadByte();
                bodies[i] = new BodySample(o, q, cg, v, om, air, lg, mode, av);
            }
            Recorder.WithRoots(bodies, w.Bodies);
            var devs = new DeviceSample[nd];
            for (int i = 0; i < nd; i++)
            {
                bool vis = br.ReadBoolean(); double fill = br.ReadDouble(); var an = RV(br); var tip = RV(br); double size = br.ReadDouble(), force = br.ReadDouble();
                devs[i] = new DeviceSample(dn[i], dt[i], vis, fill, an, tip, size, force);
            }
            var cons = new ConnectorSample[nc];
            for (int i = 0; i < nc; i++)
            {
                bool vis = br.ReadBoolean(); var a = RV(br); var b = RV(br); double ten = br.ReadDouble();
                cons[i] = new ConnectorSample(cn[i], ct[i], vis, a, b, ten);
            }
            frames.Add(new Frame { T = t, Bodies = bodies, Devices = devs, Connectors = cons });
        }
        return frames;
    }

    private static void V(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static void Q(BinaryWriter w, Quat q) { w.Write(q.W); w.Write(q.X); w.Write(q.Y); w.Write(q.Z); }
    private static Vec3 RV(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
    private static Quat RQ(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());

    // ---------------- 불러오기 ----------------

    private static JsonObject ReadJson(ZipArchive zip, string name, bool required = true)
    {
        var e = zip.GetEntry(name);
        if (e is null)
        {
            if (required) throw new InvalidDataException($"결과 파일에 {name} 이 없습니다.");
            return null!;
        }
        using var s = e.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return J.ParseObject(r.ReadToEnd());
    }

    /// <summary>결과 파일 불러오기. extractRoot: 메시·시나리오 압축 해제 위치(기본 %TEMP%/mns_results).</summary>
    public static LoadedResult Load(string path, string? extractRoot = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"결과 파일이 없습니다: {path}");
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"결과 파일(.mnsr, zip)이 아니거나 손상되었습니다: {ex.Message}"); }
        using (zip)
        {
            var manifest = ReadJson(zip, "result.json");
            var schema = J.Str(manifest, "schema", "");
            if (schema != Schema) throw new InvalidDataException($"지원하지 않는 결과 schema: '{schema}' (필요: {Schema})");
            var fi = new FileInfo(path);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
            var dir = System.IO.Path.Combine(extractRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mns_results"), key);
            Directory.CreateDirectory(dir);
            foreach (var e in zip.Entries)
            {
                if (!(e.FullName.StartsWith("meshes/", StringComparison.Ordinal) || e.FullName == "scenario.resolved.json") || e.FullName.EndsWith('/')) continue;
                var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(System.IO.Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"잘못된 항목 경로: {e.FullName}");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
            var resolvedPath = System.IO.Path.Combine(dir, "scenario.resolved.json");
            if (!File.Exists(resolvedPath)) throw new InvalidDataException("결과 파일에 scenario.resolved.json 이 없습니다.");
            var doc = ScenarioDoc.Load(resolvedPath);
            var world = doc.Build();
            List<Frame> frames;
            var fe = zip.GetEntry("frames.bin") ?? throw new InvalidDataException("결과 파일에 frames.bin 이 없습니다.");
            using (var fs = fe.Open())
            {
                try { frames = ReadFrames(fs, world); }
                catch (EndOfStreamException) { throw new InvalidDataException("frames.bin 이 잘렸습니다(손상)."); }
            }
            var log = (manifest["eventLog"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(e => new LogEntry(J.Num(e, "t", 0), J.Str(e, "name", ""), J.Str(e, "message", ""))).ToList();
            var warns = (manifest["warnings"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList();
            world.RestoreRecorded(frames, log, warns, J.Num(manifest, "endTime_s", frames.Count > 0 ? frames[^1].T : 0),
                J.Str(manifest, "stopReason", ""), J.Bool(manifest, "suspect", false), (long)J.Num(manifest, "steps", 0));
            foreach (var st in (manifest["bodyStats"] as JsonArray ?? []).OfType<JsonObject>())
                if (world.BodyMap.TryGetValue(J.Str(st, "name", ""), out var b))
                {
                    b.MaxLoadG = J.Num(st, "maxLoadG", 0);
                    b.MaxLoadTime = J.NumOpt(st, "maxLoadTime") ?? double.NaN;
                    b.ReleaseTime = J.NumOpt(st, "releaseTime") ?? double.NaN;
                }
            foreach (var i in (manifest["impacts"] as JsonArray ?? []).OfType<JsonObject>())
                if (world.BodyMap.TryGetValue(J.Str(i, "body", ""), out var b))
                    b.Impact = new ImpactRecord
                    {
                        Time = J.Num(i, "time", 0), Position = J.V3(i, "position", Vec3.Zero), Velocity = J.V3(i, "velocity", Vec3.Zero),
                        EulerDeg = J.V3(i, "euler", Vec3.Zero), NoseAngleDeg = J.Num(i, "noseAngle", 0), PenetrationDepth = J.Num(i, "depth", 0),
                        PenetrationG = J.Num(i, "g", 0), Mass = J.Num(i, "mass", 0), AngleOfAttackDeg = J.Num(i, "aoa", 0),
                        PenetrationVerticalDepth = J.Num(i, "vertDepth", 0), PenetrationArea = J.Num(i, "area", 0),
                        PenetrationNoseN = J.Num(i, "noseN", 0), PenetrationSource = J.Str(i, "source", ""),
                    };
            var origJson = zip.GetEntry("scenario.json") is { } oe ? new StreamReader(oe.Open(), Encoding.UTF8).ReadToEnd() : doc.ToJson();
            var origBase = J.StrOpt(manifest, "scenarioBaseDir") is { } ob && Directory.Exists(ob) ? ob : dir;
            var original = new ScenarioDoc { Root = J.ParseObject(origJson), BaseDir = origBase, FilePath = J.StrOpt(manifest, "scenarioFile") };
            return new LoadedResult
            {
                Doc = doc, OriginalDoc = original, World = world, Manifest = manifest, Path = System.IO.Path.GetFullPath(path),
                Summary = ReadJson(zip, "summary.json"),
                Structural = zip.GetEntry("structural.json") is null ? null : ReadJson(zip, "structural.json"),
                MonteCarlo = zip.GetEntry("mc.json") is null ? null : ReadJson(zip, "mc.json"),
                Fea = zip.GetEntry("fea.json") is null ? null : ReadJson(zip, "fea.json"),
            };
        }
    }
}
