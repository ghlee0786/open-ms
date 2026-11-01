using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Sim.Core.Geometry;

namespace Sim.Core.Airflow;

/// <summary>앱이 서버에 보내는 전체 설정(cmd = config). 서버는 바뀐 항목만 보고 격자 재생성 · 재복셀화 · ω 변경을 고른다.</summary>
public sealed record AirflowSettings
{
    public string Mesh { get; init; } = "";
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Speed { get; init; } = 168;
    public double Altitude { get; init; } = 1000;
    /// <summary>계산 Re(기준 길이 기준). 0 이하 = 격자가 안정하게 풀 수 있는 최대.</summary>
    public double Re { get; init; }
    public string Resolution { get; init; } = "low";
    public double RefArea { get; init; }
    public double RefLength { get; init; }
    /// <summary>xz(피치면, 받음각 평면) | xy(요잉면, 옆미끄럼 평면).</summary>
    public string Plane { get; init; } = "xz";
    public double SliceOffset { get; init; }
    public bool Particles { get; init; } = true;
    public bool Lines { get; init; } = true;
    public bool Paused { get; init; }

    public JsonObject ToJson() => new()
    {
        ["cmd"] = "config", ["mesh"] = Mesh, ["alpha"] = Alpha, ["beta"] = Beta, ["speed"] = Speed, ["altitude"] = Altitude, ["re"] = Re,
        ["resolution"] = Resolution, ["refArea"] = RefArea, ["refLength"] = RefLength, ["plane"] = Plane, ["sliceOffset"] = SliceOffset,
        ["particles"] = Particles, ["lines"] = Lines, ["paused"] = Paused,
    };

    public static readonly string[] Resolutions = ["low", "medium", "high"];
}

/// <summary>파이썬 서버 위치(저장소 안 가상환경). 없으면 설치 안내.</summary>
public static class AirflowEnvironment
{
    public const string ServerScript = "tools/airflow/server.py";
    public const string VenvPython = "tools/airflow/.venv/Scripts/python.exe";

    public static string SetupHint =>
        "GPU 가상 풍동 파이썬 환경이 없습니다. 저장소 루트에서:\n" +
        "  python -m venv tools/airflow/.venv\n" +
        "  tools/airflow/.venv/Scripts/pip.exe install -r tools/airflow/requirements.txt\n" +
        "(NVIDIA GPU · 드라이버 필요. 다른 파이썬을 쓰려면 환경변수 MNS_AIRFLOW_PYTHON)";

    /// <summary>(파이썬 실행 파일, 서버 스크립트) — 없으면 null 과 이유.</summary>
    public static (string Python, string Script)? Find(string repoRoot, out string reason)
    {
        var script = Path.Combine(repoRoot, ServerScript);
        var py = System.Environment.GetEnvironmentVariable("MNS_AIRFLOW_PYTHON") is { Length: > 0 } env ? env : Path.Combine(repoRoot, VenvPython);
        if (!File.Exists(script)) { reason = $"서버 스크립트가 없습니다: {script}"; return null; }
        if (!File.Exists(py)) { reason = $"파이썬이 없습니다: {py}\n{SetupHint}"; return null; }
        reason = "";
        return (py, script);
    }

    /// <summary>동체 좌표 메시를 이진 STL(m)로 — 서버가 읽어 복셀화한다.</summary>
    public static void WriteStl(TriMesh mesh, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(new byte[80]);
        w.Write((uint)mesh.TriangleCount);
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            for (int k = 0; k < 3; k++) w.Write(0f);
            for (int k = 0; k < 3; k++)
            {
                var v = mesh.Vertices[3 * t + k];
                w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z);
            }
            w.Write((ushort)0);
        }
    }
}

/// <summary>
/// GPU 가상 풍동 서버와의 연결(별도 프로세스 + 로컬 TCP). 받은 메시지는 백그라운드에서 읽어 이벤트로 넘긴다(UI 스레드 아님).
/// Start: 서버 실행 → 표준출력 "AIRFLOW_PORT n" → 접속. Connect: 이미 떠 있는 서버(시험용). Dispose: quit → 2 s 뒤 강제 종료.
/// </summary>
public sealed class AirflowClient : IDisposable
{
    private Process? _proc;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;

    public event Action<AirflowFrame>? FrameReceived;
    public event Action<string, string>? Status;              // (phase, message)
    public event Action<string, bool, double?>? Error;         // (message, diverged, newRe)
    public event Action<string>? Log;                          // 서버 stderr 한 줄
    public event Action<string>? Disconnected;

    public JsonObject? Hello { get; private set; }
    public bool Connected => _tcp?.Connected == true;
    public long FramesReceived { get; private set; }

    public static async Task<AirflowClient> StartAsync(string python, string script, string workDir, TimeSpan timeout, IEnumerable<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        foreach (var a in extraArgs ?? []) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var c = new AirflowClient();
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"서버를 시작하지 못했습니다: {python}");
        c._proc = p;
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } l) c.Log?.Invoke(l); };
        p.BeginErrorReadLine();
        var portTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("AIRFLOW_PORT ", StringComparison.Ordinal)) return int.Parse(line[13..].Trim(), CultureInfo.InvariantCulture);
            return -1;
        });
        if (await Task.WhenAny(portTask, Task.Delay(timeout)) != portTask || portTask.Result < 0)
        {
            c.Dispose();
            throw new TimeoutException(p.HasExited ? $"서버가 시작 중 종료됨(코드 {p.ExitCode})" : "서버 포트 대기 시간 초과");
        }
        // 이후 표준출력(라이브러리 메시지)은 로그로
        _ = Task.Run(async () => { while (await p.StandardOutput.ReadLineAsync() is { } l) c.Log?.Invoke(l); });
        await c.ConnectAsync(portTask.Result);
        return c;
    }

    public async Task ConnectAsync(int port)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync("127.0.0.1", port);
        _stream = _tcp.GetStream();
        _reader = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        string why = "연결 종료";
        try
        {
            while (!_cts.IsCancellationRequested && await AirflowProtocol.ReadAsync(_stream!, _cts.Token) is { } m)
            {
                switch (m.Type)
                {
                    case "hello": Hello = m.Header; Status?.Invoke("hello", $"서버 연결 — {m.Header["backend"]} {m.Header["versions"]?.ToJsonString()}"); break;
                    case "status": Status?.Invoke(m.Header["phase"]?.GetValue<string>() ?? "", m.Header["message"]?.GetValue<string>() ?? ""); break;
                    case "error":
                        double? newRe = m.Header["newRe"] is JsonValue nv && nv.TryGetValue<double>(out var r) ? r : null;
                        Error?.Invoke(m.Header["message"]?.GetValue<string>() ?? "?", m.Header["diverged"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d, newRe);
                        break;
                    case "frame":
                        FramesReceived++;
                        FrameReceived?.Invoke(AirflowFrame.FromMessage(m));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { why = "닫힘"; }
        catch (Exception ex) { why = ex.Message; }
        Disconnected?.Invoke(why);
    }

    public async Task SendAsync(JsonObject header)
    {
        if (_stream is null) throw new InvalidOperationException("서버에 연결되지 않았습니다");
        await _sendLock.WaitAsync();
        try { await AirflowProtocol.WriteAsync(_stream, header, null, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    public Task SendConfigAsync(AirflowSettings s) => SendAsync(s.ToJson());

    public Task ResetAsync() => SendAsync(new JsonObject { ["cmd"] = "reset" });

    public void Dispose()
    {
        try
        {
            if (_stream is not null && _tcp?.Connected == true)
                SendAsync(new JsonObject { ["cmd"] = "quit" }).Wait(1000);
        }
        catch { /* 이미 끊김 */ }
        try { if (_proc is { HasExited: false } p && !p.WaitForExit(2000)) p.Kill(entireProcessTree: true); } catch { /* 이미 종료 */ }
        _cts.Cancel();
        try { _stream?.Dispose(); _tcp?.Dispose(); } catch { /* 무시 */ }
        _proc?.Dispose();
    }
}
