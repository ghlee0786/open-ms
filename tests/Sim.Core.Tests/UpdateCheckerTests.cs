using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sim.App.Update;

namespace Sim.Core.Tests;

/// <summary>
/// 자동 업데이트(GitHub Releases): 태그 파싱 · 새 버전 판정 · 건너뛴 버전 · 개발 빌드 · 자산 없음 · SHA-256 불일치 거부 · 오프라인,
/// 그리고 로컬 HttpListener 가짜 릴리스 API 로 확인 → 내려받기 → 검증 → (가짜) 설치 실행까지.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mns_update_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly Version V090 = new(0, 9, 0);

    private static JsonObject Release(string tag, bool draft = false, bool prerelease = false, string baseUrl = "https://github.com/o/r/releases/download", params string[] assets) => new()
    {
        ["tag_name"] = tag,
        ["name"] = "Open M&S " + tag,
        ["body"] = "## 바뀐 점\n- 무엇",
        ["draft"] = draft,
        ["prerelease"] = prerelease,
        ["html_url"] = "https://github.com/o/r/releases/tag/" + tag,
        ["assets"] = new JsonArray(assets.Select(n => (JsonNode)new JsonObject
        {
            ["name"] = n, ["size"] = 123, ["browser_download_url"] = $"{baseUrl}/{tag}/{n}",
        }).ToArray()),
    };

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("v1.2.3+build5", "1.2.3")]
    [InlineData(" v0.9.1 ", "0.9.1")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_tags(string? tag, string? expected) => Assert.Equal(expected, UpdateChecker.ParseTag(tag)?.ToString());

    [Fact]
    public void Newer_release_with_installer_is_available_and_picks_sha256_asset()
    {
        var r = UpdateChecker.Evaluate(Release("v0.9.1", assets: ["OpenMS-v0.9.1-win-x64.zip", "OpenMS-Setup-0.9.1.exe", "OpenMS-Setup-0.9.1.exe.sha256"]), V090, null, manual: false);
        Assert.Equal(UpdateStatus.Available, r.Status);
        Assert.Equal(new Version(0, 9, 1), r.Release!.Version);
        Assert.Equal("OpenMS-Setup-0.9.1.exe", r.Release.InstallerName);
        Assert.EndsWith("/OpenMS-Setup-0.9.1.exe", r.Release.InstallerUrl);
        Assert.EndsWith("/OpenMS-Setup-0.9.1.exe.sha256", r.Release.Sha256Url);
        Assert.Equal("## 바뀐 점\n- 무엇", r.Release.Notes);
    }

    [Fact]
    public void Same_older_draft_prerelease_or_non_version_tag_is_up_to_date()
    {
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(Release("v0.9.0", assets: ["OpenMS-Setup-0.9.0.exe"]), V090, null, true).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(Release("v0.8.5", assets: ["OpenMS-Setup-0.8.5.exe"]), V090, null, true).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(Release("v1.0.0", draft: true, assets: ["OpenMS-Setup-1.0.0.exe"]), V090, null, true).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(Release("v1.0.0", prerelease: true, assets: ["OpenMS-Setup-1.0.0.exe"]), V090, null, true).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(Release("nightly", assets: ["OpenMS-Setup-1.0.0.exe"]), V090, null, true).Status);
        Assert.Contains("최신 버전입니다", UpdateChecker.Evaluate(Release("v0.9.0"), V090, null, true).Message);
    }

    [Fact]
    public void Missing_installer_asset_is_reported_not_offered()
    {
        var r = UpdateChecker.Evaluate(Release("v0.9.1", assets: ["OpenMS-v0.9.1-win-x64.zip", "OtherApp-Setup-0.9.1.exe"]), V090, null, true);
        Assert.Equal(UpdateStatus.NoInstaller, r.Status);
        Assert.Null(r.Release);
        Assert.Equal(UpdateStatus.NoInstaller, UpdateChecker.Evaluate(Release("v0.9.1"), V090, null, true).Status);
    }

    [Fact]
    public void Skipped_version_is_quiet_on_auto_check_but_shown_on_manual_check_and_newer_one_is_offered()
    {
        var rel = Release("v0.9.1", assets: ["OpenMS-Setup-0.9.1.exe"]);
        Assert.Equal(UpdateStatus.Skipped, UpdateChecker.Evaluate(rel, V090, "0.9.1", manual: false).Status);
        Assert.Equal(UpdateStatus.Skipped, UpdateChecker.Evaluate(rel, V090, "v0.9.1", manual: false).Status);
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Evaluate(rel, V090, "0.9.1", manual: true).Status);
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Evaluate(Release("v0.9.2", assets: ["OpenMS-Setup-0.9.2.exe"]), V090, "0.9.1", manual: false).Status);
    }

    [Fact]
    public async Task Dev_build_never_checks()
    {
        Assert.True(UpdateChecker.IsDevBuild(new Version(0, 0, 0)));
        Assert.True(UpdateChecker.IsDevBuild(new Version(0, 0, 0, 0)));
        Assert.False(UpdateChecker.IsDevBuild(V090));
        Assert.Equal(UpdateStatus.DevBuild, UpdateChecker.Evaluate(Release("v9.9.9", assets: ["OpenMS-Setup-9.9.9.exe"]), new Version(0, 0, 0), null, true).Status);
        // 요청을 보내지 않는다(닫힌 주소여도 즉시 DevBuild)
        using var http = UpdateChecker.CreateClient(new Version(0, 0, 0));
        var r = await UpdateChecker.CheckAsync(http, "http://localhost:1/never", new Version(0, 0, 0), null, true);
        Assert.Equal(UpdateStatus.DevBuild, r.Status);
        var svc = new UpdateService(http, new UpdateSettingsStore(Path.Combine(_dir, "s.json")), new Version(0, 0, 0), "http://localhost:1/", _dir, _ => { });
        Assert.False(svc.ShouldAutoCheck(DateTime.UtcNow));
    }

    [Fact]
    public void Release_build_version_comes_from_the_assembly()
    {
        // 저장소 기본(Directory.Build.props)은 0.0.0 = 개발 빌드
        Assert.Equal(new Version(0, 0, 0), UpdateChecker.Current);
        Assert.Equal("OpenMS-Setup-1.2.3.exe", UpdateChecker.InstallerFileName(new Version(1, 2, 3, 0)));
        Assert.Equal("https://api.github.com/repos/ghlee0786/open-ms/releases/latest", UpdateChecker.LatestUrl(UpdateChecker.DefaultApi));
        Assert.Equal("http://localhost:5/repos/ghlee0786/open-ms/releases/latest", UpdateChecker.LatestUrl("http://localhost:5/"));
    }

    [Fact]
    public void Auto_check_runs_once_a_day_and_can_be_turned_off()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(new UpdateSettings().DueForAutoCheck(now));
        Assert.False(new UpdateSettings(LastCheckUtc: now.AddHours(-3)).DueForAutoCheck(now));
        Assert.True(new UpdateSettings(LastCheckUtc: now.AddHours(-25)).DueForAutoCheck(now));
        Assert.True(new UpdateSettings(LastCheckUtc: now.AddDays(3)).DueForAutoCheck(now));   // 시계가 뒤로 감
        Assert.False(new UpdateSettings(AutoCheck: false).DueForAutoCheck(now));
    }

    [Fact]
    public void Settings_roundtrip_under_update_key_and_keep_other_keys()
    {
        var path = Path.Combine(_dir, "MnS.Simulator", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "theme": "light", "scene": { "environmentId": "grass" } }""");
        var store = new UpdateSettingsStore(path);
        Assert.Equal(new UpdateSettings(), store.Load());
        var t = new DateTime(2026, 10, 31, 8, 30, 0, DateTimeKind.Utc);
        Assert.True(store.Save(new UpdateSettings(false, "0.9.1", t)));
        var back = store.Load();
        Assert.False(back.AutoCheck);
        Assert.Equal("0.9.1", back.SkippedVersion);
        Assert.Equal(t, back.LastCheckUtc);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("light", (string?)root["theme"]);
        Assert.Equal("grass", (string?)root["scene"]!["environmentId"]);
        Assert.False((bool)root["update"]!["autoCheck"]!);
        // 손상 · 없음 = 기본값(자동 확인 켬), 예외 없음
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new UpdateSettings(), store.Load());
        Assert.True(store.Save(new UpdateSettings(SkippedVersion: "1.0.0")));
        Assert.Equal("1.0.0", store.Load().SkippedVersion);
        Assert.Equal(new UpdateSettings(), new UpdateSettingsStore(Path.Combine(_dir, "none", "x.json")).Load());
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  OpenMS-Setup-1.0.0.exe\n", "OpenMS-Setup-1.0.0.exe")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 *OpenMS-Setup-1.0.0.exe", "OpenMS-Setup-1.0.0.exe")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "anything.exe")]
    [InlineData("1111111111111111111111111111111111111111111111111111111111111111  other.zip\r\nabcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  OpenMS-Setup-1.0.0.exe\r\n", "OpenMS-Setup-1.0.0.exe")]
    public void Parses_sha256_files(string text, string file)
        => Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", UpdateChecker.ParseSha256(text, file));

    [Fact]
    public void Sha256_file_without_hash_is_null() => Assert.Null(UpdateChecker.ParseSha256("no hash here\n", "x.exe"));

    [Fact]
    public void Notes_preview_trims_long_text()
    {
        Assert.Equal("(릴리스 노트 없음)", UpdateChecker.NotesPreview(null));
        var p = UpdateChecker.NotesPreview(new string('가', 3000), 100);
        Assert.Equal(102, p.Length);
        Assert.EndsWith("\n…", p);
    }

    // ---------------- 가짜 릴리스 API(로컬 HttpListener) ----------------

    /// <summary>
    /// http://localhost:{port}/ — /repos/{Repository}/releases/latest = 릴리스 JSON, /dl/{이름} = 자산 바이트.
    /// <see cref="LatestStatus"/> 로 404 · 403 응답을 흉내 낸다.
    /// </summary>
    private sealed class FakeReleaseServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public string Base { get; }
        public Dictionary<string, byte[]> Files { get; } = new();
        public JsonObject? Latest { get; set; }
        public HttpStatusCode LatestStatus { get; set; } = HttpStatusCode.OK;
        public List<string> UserAgents { get; } = new();

        public FakeReleaseServer()
        {
            int port = FreePort();
            Base = $"http://localhost:{port}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _loop = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                lock (UserAgents) UserAgents.Add(ctx.Request.UserAgent ?? "");
                byte[] body;
                if (path == $"/repos/{UpdateChecker.Repository}/releases/latest")
                {
                    ctx.Response.StatusCode = (int)LatestStatus;
                    if (LatestStatus == HttpStatusCode.Forbidden) ctx.Response.Headers["X-RateLimit-Reset"] = "1893456000";
                    body = Encoding.UTF8.GetBytes(LatestStatus == HttpStatusCode.OK ? Latest?.ToJsonString() ?? "{}" : """{"message":"x"}""");
                    ctx.Response.ContentType = "application/json";
                }
                else if (path.StartsWith("/dl/") && Files.TryGetValue(path[4..], out var f)) body = f;
                else { ctx.Response.StatusCode = 404; body = []; }
                ctx.Response.ContentLength64 = body.Length;
                try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>v{version} 릴리스: 설치 프로그램(+ 원하면 .sha256 — 맞는 값 또는 틀린 값).</summary>
        public byte[] Publish(string version, bool withSha = true, bool wrongSha = false)
        {
            var name = $"OpenMS-Setup-{version}.exe";
            var bytes = new byte[300_000];
            new Random(7).NextBytes(bytes);
            Files[name] = bytes;
            var assets = new JsonArray { Asset(name, bytes.Length) };
            if (withSha)
            {
                var hash = Convert.ToHexString(SHA256.HashData(wrongSha ? new byte[] { 1, 2, 3 } : bytes)).ToLowerInvariant();
                Files[name + ".sha256"] = Encoding.ASCII.GetBytes($"{hash}  {name}\n");
                assets.Add(Asset(name + ".sha256", 90));
            }
            Latest = new JsonObject { ["tag_name"] = "v" + version, ["name"] = "Open M&S " + version, ["body"] = "노트", ["draft"] = false, ["prerelease"] = false, ["assets"] = assets };
            return bytes;
        }

        private JsonObject Asset(string name, long size) => new() { ["name"] = name, ["size"] = size, ["browser_download_url"] = $"{Base}/dl/{name}" };

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
            try { _loop.Wait(2000); } catch { }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private (UpdateService Svc, List<string> Launched, UpdateSettingsStore Store, HttpClient Http) Service(FakeReleaseServer srv, Version current)
    {
        var launched = new List<string>();
        var store = new UpdateSettingsStore(Path.Combine(_dir, "settings.json"));
        var http = UpdateChecker.CreateClient(current);
        var svc = new UpdateService(http, store, current, UpdateChecker.LatestUrl(srv.Base), Path.Combine(_dir, "dl"), launched.Add);
        return (svc, launched, store, http);
    }

    [Fact]
    public async Task Fake_server_check_download_verify_and_launch_installer()
    {
        using var srv = new FakeReleaseServer();
        var bytes = srv.Publish("0.9.1");
        var (svc, launched, store, http) = Service(srv, V090);
        using var _ = http;
        Assert.True(svc.ShouldAutoCheck(DateTime.UtcNow));

        var r = await svc.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Available, r.Status);
        Assert.Equal(new Version(0, 9, 1), r.Release!.Version);
        Assert.NotNull(r.Release.Sha256Url);
        Assert.NotNull(store.Load().LastCheckUtc);
        Assert.False(svc.ShouldAutoCheck(DateTime.UtcNow));   // 하루 한 번
        Assert.Contains(srv.UserAgents, ua => ua.StartsWith("OpenMS-Updater/0.9.0"));

        var progress = new List<double>();
        var path = await svc.DownloadAndInstallAsync(r.Release, new SyncProgress(progress.Add));
        Assert.Equal(new[] { path }, launched);
        Assert.Equal("OpenMS-Setup-0.9.1.exe", Path.GetFileName(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(1.0, progress.Last());
        Assert.True(progress.Count >= 2, "진행률이 여러 번 보고되어야 한다");
    }

    [Fact]
    public async Task Sha256_mismatch_is_rejected_and_installer_not_launched()
    {
        using var srv = new FakeReleaseServer();
        srv.Publish("0.9.1", wrongSha: true);
        var (svc, launched, _, http) = Service(srv, V090);
        using var _h = http;
        var r = await svc.CheckAsync(manual: true);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => svc.DownloadAndInstallAsync(r.Release!));
        Assert.Contains("불일치", ex.Message);
        Assert.Empty(launched);
        Assert.False(File.Exists(Path.Combine(_dir, "dl", "OpenMS-Setup-0.9.1.exe")), "검증 실패한 파일은 지운다");
    }

    [Fact]
    public async Task Release_without_sha256_asset_still_installs()
    {
        using var srv = new FakeReleaseServer();
        srv.Publish("0.9.1", withSha: false);
        var (svc, launched, _, http) = Service(srv, V090);
        using var _h = http;
        var r = await svc.CheckAsync(manual: true);
        Assert.Null(r.Release!.Sha256Url);
        await svc.DownloadAndInstallAsync(r.Release);
        Assert.Single(launched);
    }

    [Fact]
    public async Task Up_to_date_and_skip_through_the_service()
    {
        using var srv = new FakeReleaseServer();
        srv.Publish("0.9.1");
        var (svc, _, store, http) = Service(srv, new Version(0, 9, 1));
        using var _h = http;
        Assert.Equal(UpdateStatus.UpToDate, (await svc.CheckAsync(manual: true)).Status);

        var (svc0, _, _, http0) = Service(srv, V090);
        using var _h0 = http0;
        var r = await svc0.CheckAsync(manual: false);
        svc0.Skip(r.Release!);
        Assert.Equal("0.9.1", store.Load().SkippedVersion);
        Assert.Equal(UpdateStatus.Skipped, (await svc0.CheckAsync(manual: false)).Status);
        Assert.Equal(UpdateStatus.Available, (await svc0.CheckAsync(manual: true)).Status);
        svc0.SetAutoCheck(false);
        Assert.False(store.Load().AutoCheck);
        Assert.Equal("0.9.1", store.Load().SkippedVersion);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.Forbidden, "한도")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Http_errors_fail_quietly_with_a_reason(HttpStatusCode code, string expect)
    {
        using var srv = new FakeReleaseServer { LatestStatus = code };
        var (svc, _, store, http) = Service(srv, V090);
        using var _h = http;
        var logs = new List<string>();
        var r = await UpdateChecker.CheckAsync(http, UpdateChecker.LatestUrl(srv.Base), V090, null, true, logs.Add);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Contains(expect, r.Message);
        Assert.Equal(new[] { r.Message }, logs);
        await svc.CheckAsync(manual: false);
        Assert.Null(store.Load().LastCheckUtc);   // 실패는 다음 시작에 다시
    }

    [Fact]
    public async Task Offline_fails_quietly()
    {
        using var http = UpdateChecker.CreateClient(V090);
        var r = await UpdateChecker.CheckAsync(http, $"http://localhost:{FreePort()}/repos/x/releases/latest", V090, null, false);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Contains("오프라인", r.Message);
    }

    [Fact]
    public async Task Garbage_json_fails_quietly()
    {
        using var srv = new FakeReleaseServer();
        srv.Latest = null;   // "{}" — tag 없음 = 최신으로
        using var http = UpdateChecker.CreateClient(V090);
        Assert.Equal(UpdateStatus.UpToDate, (await UpdateChecker.CheckAsync(http, UpdateChecker.LatestUrl(srv.Base), V090, null, true)).Status);
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(JsonNode.Parse("[1,2]"), V090, null, true).Status);
    }

    /// <summary>Progress&lt;T&gt; 는 동기화 문맥으로 보내 순서가 늦을 수 있어 시험은 바로 부른다.</summary>
    private sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
