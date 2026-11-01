using System.Text.Json.Nodes;
using Sim.Core.Scenario;

namespace Sim.Core.Airflow;

/// <summary>
/// 'OpenFOAM 재생' 모드: 미리 계산한 비정상 해석의 시간 시퀀스(단면 장 · 입자 · 유선)를 GPU 실시간과 같은 화면으로 재생.
///
/// 이 클래스가 **어댑터 한 곳**이다 — 시퀀스 생성 쪽(동시 진행 B 작업, tools/cfd)의 형식이 아래와 다르면 <see cref="Load"/> ·
/// <see cref="ReadFrame"/> 만 고치면 창(AirflowWindow)은 그대로 동작한다. 화면이 받는 것은 늘 <see cref="AirflowFrame"/>(풍동 좌표).
///
/// 기대 형식(schema mns.flow-sequence/1):
///   폴더/sequence.json = {"schema", "name", "solver", "method", "alpha", "beta", "speed", "mach", "note",
///                         "frames": [{"t": 초, "file": "frame_0001.mnsf"}, …]}
///   frame_*.mnsf = GPU 가상 풍동 IPC 와 같은 이진 메시지(schema mns.airflow-ipc/1, type "frame", 배열 slice_speed · slice_vort ·
///                  particles · lines · line_counts, 머리 slice{plane, origin, axisA, axisB}) — tools/airflow/protocol.pack 으로 쓸 수 있다.
/// 찾는 곳: results/cfd/**/sequence.json, config/aero_db/**/sequence.json (깊이 4).
/// </summary>
public sealed class FlowSequence
{
    public const string Schema = "mns.flow-sequence/1";
    public static readonly string[] SearchDirs = ["results/cfd", "config/aero_db"];

    public required string Name { get; init; }
    public required string Dir { get; init; }
    public string Solver { get; init; } = "";
    public string Method { get; init; } = "";
    public string Note { get; init; } = "";
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Speed { get; init; }
    public double Mach { get; init; }
    public required IReadOnlyList<(double T, string File)> Frames { get; init; }

    private readonly Dictionary<int, AirflowFrame> _cache = new();

    public string Label => $"{Name} — {Solver} {Method} α {Alpha:0.#}° ({Frames.Count} 프레임)";

    /// <summary>저장소에서 시퀀스 찾기(형식이 맞는 것만, 오류는 reasons 로).</summary>
    public static List<FlowSequence> Discover(string repoRoot, List<string>? reasons = null)
    {
        var res = new List<FlowSequence>();
        foreach (var d in SearchDirs)
        {
            var root = Path.Combine(repoRoot, d);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Find(root, "sequence.json", 4))
            {
                try { res.Add(Load(f)); }
                catch (Exception ex) { reasons?.Add($"{f}: {ex.Message}"); }
            }
        }
        return res.OrderBy(s => s.Name).ThenBy(s => s.Alpha).ToList();
    }

    private static IEnumerable<string> Find(string dir, string name, int depth)
    {
        var f = Path.Combine(dir, name);
        if (File.Exists(f)) yield return f;
        if (depth <= 0) yield break;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch { yield break; }
        foreach (var s in subs.OrderBy(x => x))
            foreach (var x in Find(s, name, depth - 1)) yield return x;
    }

    public static FlowSequence Load(string sequenceJson)
    {
        var o = J.ParseObject(File.ReadAllText(sequenceJson));
        if (J.Str(o, "schema", "") != Schema) throw new InvalidDataException($"schema {Schema} 이 아닙니다");
        var dir = Path.GetDirectoryName(Path.GetFullPath(sequenceJson))!;
        var frames = new List<(double, string)>();
        foreach (var n in o["frames"] as JsonArray ?? throw new InvalidDataException("frames 가 없습니다"))
        {
            var fo = n!.AsObject();
            frames.Add((J.NumReq(fo, "t", "frame"), Path.Combine(dir, J.Str(fo, "file", ""))));
        }
        if (frames.Count == 0) throw new InvalidDataException("프레임이 0 개입니다");
        return new FlowSequence
        {
            Name = J.Str(o, "name", Path.GetFileName(dir)), Dir = dir, Solver = J.Str(o, "solver", "OpenFOAM"), Method = J.Str(o, "method", ""),
            Note = J.Str(o, "note", ""), Alpha = J.Num(o, "alpha", 0), Beta = J.Num(o, "beta", 0), Speed = J.Num(o, "speed", 0), Mach = J.Num(o, "mach", 0),
            Frames = frames.OrderBy(f => f.Item1).ToList(),
        };
    }

    /// <summary>프레임 i(0 ≤ i &lt; Count) — 처음 읽을 때 파일에서, 이후 캐시.</summary>
    public AirflowFrame ReadFrame(int i)
    {
        i = Math.Clamp(i, 0, Frames.Count - 1);
        if (_cache.TryGetValue(i, out var f)) return f;
        var m = AirflowProtocol.Decode(File.ReadAllBytes(Frames[i].File));
        var src = AirflowFrame.FromMessage(m);
        // 시퀀스 머리값(자세·속력)이 프레임에 없으면 채운다
        f = new AirflowFrame
        {
            Source = string.IsNullOrEmpty(src.Source) ? "OpenFOAM" : src.Source, Device = src.Device, Step = src.Step, Frame = i,
            TimePhys = Frames[i].T, Alpha = src.Header.ContainsKey("alpha") ? src.Alpha : Alpha, Beta = src.Header.ContainsKey("beta") ? src.Beta : Beta,
            Speed = src.Speed > 0 ? src.Speed : Speed, Mach = src.Mach > 0 ? src.Mach : Mach, Re = src.Re, RePhys = src.RePhys,
            RefArea = src.RefArea, RefLength = src.RefLength, Coeffs = src.Coeffs, CoeffsMean = src.CoeffsMean,
            Slice = src.Slice, Particles = src.Particles, Lines = src.Lines, LineCounts = src.LineCounts, Header = src.Header,
        };
        if (_cache.Count > 64) _cache.Clear();
        _cache[i] = f;
        return f;
    }

    /// <summary>재생 시각 t[s] 에 해당하는 프레임 번호(구간 반복).</summary>
    public int IndexAt(double t)
    {
        if (Frames.Count == 1) return 0;
        double t0 = Frames[0].T, span = Frames[^1].T - t0;
        if (!(span > 0)) return 0;
        double tt = t0 + ((t - t0) % span + span) % span;
        int lo = 0;
        for (int k = 0; k < Frames.Count; k++) if (Frames[k].T <= tt) lo = k;
        return lo;
    }
}
