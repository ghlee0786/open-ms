using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sim.App.Update;

/// <summary>새 릴리스: 버전 · 태그 · 이름 · 릴리스 노트 · 설치 프로그램 자산(+ 같은 릴리스의 .sha256 자산).</summary>
public sealed record ReleaseInfo(
    Version Version, string Tag, string Name, string Notes, string HtmlUrl,
    string InstallerName, string InstallerUrl, long InstallerSize, string? Sha256Url);

/// <summary>확인 결과의 종류. Skipped = 자동 확인에서 사용자가 '이 버전 건너뛰기' 한 버전.</summary>
public enum UpdateStatus { DevBuild, UpToDate, Available, Skipped, NoInstaller, Failed }

public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release, string Message);

/// <summary>내려받은 설치 프로그램의 SHA-256 이 릴리스의 .sha256 자산과 다르다 — 실행하지 않는다.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// GitHub Releases 자동 업데이트(참고: EdgeVoice UpdateChecker). 최신 릴리스(<c>/repos/{소유자}/{이름}/releases/latest</c>)가
/// 지금 버전보다 새것이고 설치 프로그램 <c>OpenMS-Setup-&lt;버전&gt;.exe</c> 자산이 있으면 알린다. 내려받은 뒤 같은 릴리스의
/// <c>.sha256</c> 자산으로 검증하고, 설치 프로그램을 /SILENT 로 실행한다(installer/OpenMS.iss — 앱을 닫고 깔고 다시 띄운다).
/// 오프라인 · 404(릴리스 없음) · 레이트리밋은 예외 없이 <see cref="UpdateStatus.Failed"/> + 로그. Avalonia 없음(시험 링크).
/// </summary>
public static class UpdateChecker
{
    /// <summary>릴리스를 올리는 GitHub 저장소(소유자/이름). 저장소 이름이 바뀌면 여기만 고친다.</summary>
    public const string Repository = "ghlee0786/open-ms";

    /// <summary>API 주소를 바꾸는 환경변수(가짜 릴리스 서버 시험용) — 비우면 <see cref="DefaultApi"/>.</summary>
    public const string ApiEnvVar = "MNS_UPDATE_API";
    public const string DefaultApi = "https://api.github.com";

    /// <summary>설치 프로그램 자산 이름의 앞부분(installer/OpenMS.iss OutputBaseFilename · release.yml 과 같다).</summary>
    public const string InstallerPrefix = "OpenMS-Setup-";

    /// <summary>설치 프로그램 인자: 마법사 없이 진행 막대만, 실행 중인 앱은 닫는다. 설치가 끝나면 설치 프로그램이 앱을 다시 띄운다.</summary>
    public const string InstallerArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    /// <summary>이 앱의 버전(릴리스 빌드는 태그에서 -p:Version= 으로 넣는다. 0.0.0 = 개발 빌드).</summary>
    public static Version Current { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>개발 빌드(소스에서 실행)는 확인하지 않는다 — 릴리스로 덮어쓰지 않게.</summary>
    public static bool IsDevBuild(Version v) => Normalize(v) == new Version(0, 0, 0);

    public static string ApiBase => (System.Environment.GetEnvironmentVariable(ApiEnvVar) is { Length: > 0 } e ? e : DefaultApi).TrimEnd('/');

    public static string LatestUrl(string apiBase) => $"{apiBase.TrimEnd('/')}/repos/{Repository}/releases/latest";

    public static string InstallerFileName(Version v) => $"{InstallerPrefix}{Normalize(v).ToString(3)}.exe";

    /// <summary>'v1.2.3' · '1.2.3-beta' · '1.2' → 1.2.3 · 1.2.3 · 1.2.0. 버전 꼴이 아니면 null.</summary>
    public static Version? ParseTag(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(text, out var v) && v.Major >= 0 ? Normalize(v) : null;
    }

    /// <summary>
    /// 최신 릴리스 응답 하나를 판정한다. draft · prerelease · 태그가 버전이 아님 · 같거나 옛 버전 = 최신(UpToDate),
    /// 설치 프로그램 자산 없음 = NoInstaller, 자동 확인에서 건너뛴 버전 = Skipped. 수동 확인(<paramref name="manual"/>)은 건너뛰기를 무시한다.
    /// </summary>
    public static UpdateCheckResult Evaluate(JsonNode? release, Version current, string? skippedVersion, bool manual)
    {
        if (IsDevBuild(current)) return new(UpdateStatus.DevBuild, null, "개발 빌드(0.0.0)는 업데이트를 확인하지 않습니다.");
        if (release is not JsonObject r) return new(UpdateStatus.Failed, null, "릴리스 응답이 JSON 객체가 아닙니다.");
        current = Normalize(current);
        var tag = Str(r, "tag_name");
        if (Bool(r, "draft") || Bool(r, "prerelease"))
            return new(UpdateStatus.UpToDate, null, $"최신 릴리스 {tag} 는 초안 · 시험판이라 건너뜁니다 — 현재 {current.ToString(3)}.");
        if (ParseTag(tag) is not { } version)
            return new(UpdateStatus.UpToDate, null, $"릴리스 태그 '{tag}' 가 버전 꼴(v1.2.3)이 아닙니다 — 현재 {current.ToString(3)}.");
        if (version <= current)
            return new(UpdateStatus.UpToDate, null, $"최신 버전입니다 (현재 {current.ToString(3)} · 최신 릴리스 {version.ToString(3)}).");

        var assets = (r["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(a => (Name: Str(a, "name"), Url: Str(a, "browser_download_url"), Size: a["size"] is JsonValue s && s.TryGetValue<long>(out var n) ? n : 0))
            .Where(a => a.Name.Length > 0 && a.Url.Length > 0).ToList();
        // 정확한 이름(OpenMS-Setup-1.2.3.exe) 우선, 없으면 접두사 · .exe 로
        var installer = assets.FirstOrDefault(a => a.Name.Equals(InstallerFileName(version), StringComparison.OrdinalIgnoreCase));
        if (installer.Name is null)
            installer = assets.FirstOrDefault(a => a.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (installer.Name is null)
            return new(UpdateStatus.NoInstaller, null, $"새 버전 {version.ToString(3)} 릴리스에 설치 프로그램({InstallerFileName(version)})이 없습니다.");
        var sha = assets.FirstOrDefault(a => a.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (sha.Name is null) sha = assets.FirstOrDefault(a => a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));

        var info = new ReleaseInfo(version, tag, Str(r, "name") is { Length: > 0 } nm ? nm : tag, Str(r, "body"), Str(r, "html_url"),
            installer.Name, installer.Url, installer.Size, sha.Name is null ? null : sha.Url);
        if (!manual && ParseTag(skippedVersion) is { } skipped && skipped == version)
            return new(UpdateStatus.Skipped, info, $"새 버전 {version.ToString(3)} 은 건너뛰기로 한 버전입니다.");
        return new(UpdateStatus.Available, info, $"새 버전 {version.ToString(3)} 이 있습니다 (현재 {current.ToString(3)}).");
    }

    /// <summary>GitHub API 는 User-Agent 가 없으면 거절한다. 내려받기까지 같은 클라이언트(시간 제한 10 분 — 확인은 따로 짧게).</summary>
    public static HttpClient CreateClient(Version current)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenMS-Updater/{Normalize(current).ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>최신 릴리스를 받아 판정한다. 네트워크 · HTTP · JSON 오류는 예외 없이 Failed(이유는 메시지 · 로그).</summary>
    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http, string latestUrl, Version current, string? skippedVersion, bool manual,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (IsDevBuild(current))
        {
            var dev = Evaluate(null, current, skippedVersion, manual);
            log?.Invoke(dev.Message);
            return dev;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        UpdateCheckResult result;
        try
        {
            using var resp = await http.GetAsync(latestUrl, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                result = new(UpdateStatus.Failed, null, $"릴리스가 없습니다 (404 — 저장소 {Repository} 가 없거나 아직 릴리스를 올리지 않음).");
            else if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                string reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out var epoch)
                    ? $" — {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm} 이후 다시 시도" : "";
                result = new(UpdateStatus.Failed, null, $"GitHub API 요청 한도에 걸렸습니다 ({(int)resp.StatusCode}){reset}.");
            }
            else if (!resp.IsSuccessStatusCode)
                result = new(UpdateStatus.Failed, null, $"릴리스 확인 실패: HTTP {(int)resp.StatusCode}.");
            else
            {
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(timeout.Token));
                result = Evaluate(json, current, skippedVersion, manual);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new(UpdateStatus.Failed, null, "릴리스 확인 시간 초과(20 s) — 오프라인이거나 서버가 응답하지 않습니다.");
        }
        catch (HttpRequestException e)
        {
            result = new(UpdateStatus.Failed, null, $"릴리스 확인 실패(오프라인?): {e.Message}");
        }
        catch (JsonException e)
        {
            result = new(UpdateStatus.Failed, null, $"릴리스 응답을 읽지 못했습니다: {e.Message}");
        }
        log?.Invoke(result.Message);
        return result;
    }

    /// <summary>
    /// .sha256 자산 내용에서 해시를 꺼낸다 — <c>sha256sum</c> 꼴(<c>해시  파일이름</c>, 여러 줄이면 그 파일 줄) 또는 해시만.
    /// 없으면 null. 소문자 16진수 64자로 돌려준다.
    /// </summary>
    public static string? ParseSha256(string text, string fileName)
    {
        string? first = null;
        foreach (var raw in text.Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^([0-9A-Fa-f]{64})(?:\s+\*?(.+))?$");
            if (!m.Success) continue;
            var hash = m.Groups[1].Value.ToLowerInvariant();
            if (m.Groups[2].Success && Path.GetFileName(m.Groups[2].Value.Trim()).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return hash;
            first ??= hash;
        }
        return first;
    }

    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// 설치 프로그램을 <paramref name="dir"/> 에 내려받고(진행률 0–1), .sha256 자산이 있으면 검증한다. 불일치 = 파일을 지우고
    /// <see cref="UpdateVerificationException"/>. .sha256 자산이 없으면 검증 없이(로그) 넘긴다. 내려받은 파일 경로를 돌려준다.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, string dir, IProgress<double>? progress = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(release.InstallerName));
        var part = path + ".part";
        using (var resp = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.InstallerSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(part))
            {
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)got / total));
                }
            }
        }
        File.Move(part, path, overwrite: true);
        progress?.Report(1.0);
        log?.Invoke($"내려받음: {path} ({new FileInfo(path).Length:N0} B)");

        if (release.Sha256Url is null)
        {
            log?.Invoke("릴리스에 .sha256 자산이 없어 해시 검증 없이 진행합니다.");
            return path;
        }
        var expected = ParseSha256(await http.GetStringAsync(release.Sha256Url, ct), release.InstallerName);
        var actual = Sha256Of(path);
        if (expected is null || expected != actual)
        {
            try { File.Delete(path); } catch { }
            throw new UpdateVerificationException(expected is null
                ? "릴리스의 .sha256 자산에서 해시를 읽지 못했습니다 — 설치하지 않습니다."
                : $"SHA-256 불일치 — 기대 {expected[..12]}… · 실제 {actual[..12]}… — 내려받은 파일을 지웠고 설치하지 않습니다.");
        }
        log?.Invoke($"SHA-256 확인: {actual}");
        return path;
    }

    /// <summary>릴리스 노트 앞부분(대화상자용) — 줄 끝 정리, 길면 잘라 '…'.</summary>
    public static string NotesPreview(string? notes, int max = 1500)
    {
        var t = (notes ?? "").Replace("\r\n", "\n").Trim();
        if (t.Length == 0) return "(릴리스 노트 없음)";
        return t.Length <= max ? t : t[..max].TrimEnd() + "\n…";
    }

    /// <summary>설치 프로그램을 조용히 실행한다. 실행하면 앱을 끝내야 한다(설치 프로그램이 다시 띄운다).</summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, InstallerArgs) { UseShellExecute = true });

    private static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>사용자 설정 settings.json 의 <c>"update"</c> 키: 자동 확인 · 건너뛴 버전 · 마지막 확인 시각(UTC).</summary>
public sealed record UpdateSettings(bool AutoCheck = true, string? SkippedVersion = null, DateTime? LastCheckUtc = null)
{
    /// <summary>자동 확인 간격 — 하루 한 번.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>자동 확인을 켰고 마지막 확인이 하루 이상 전(또는 없음 · 시계가 뒤로 감)이면 지금 확인한다.</summary>
    public bool DueForAutoCheck(DateTime nowUtc) =>
        AutoCheck && (LastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc + TimeSpan.FromMinutes(5));
}

/// <summary>
/// <see cref="UpdateSettings"/> 를 <c>%APPDATA%/MnS.Simulator/settings.json</c> 의 <c>"update"</c> 에 저장 · 복원(ThemeStore 와 같은 파일 ·
/// 같은 규칙 — 다른 키 보존, 임시 파일 → 교체, 오류는 조용히).
/// </summary>
public sealed class UpdateSettingsStore(string path)
{
    private const string KeyName = "update";
    private readonly object _gate = new();
    public string FilePath { get; } = path;

    public static string DefaultPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "MnS.Simulator", "settings.json");

    public UpdateSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath))?[KeyName] is not JsonObject u) return new();
            bool auto = u["autoCheck"] is JsonValue a && a.TryGetValue<bool>(out var ab) ? ab : true;
            string? skipped = u["skippedVersion"] is JsonValue s && s.TryGetValue<string>(out var ss) && ss.Length > 0 ? ss : null;
            DateTime? last = u["lastCheckUtc"] is JsonValue l && l.TryGetValue<string>(out var ls)
                && DateTime.TryParse(ls, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var lt)
                ? lt : null;
            return new(auto, skipped, last);
        }
        catch { return new(); }
    }

    public bool Save(UpdateSettings s)
    {
        lock (_gate)
        {
            try
            {
                JsonObject root;
                try { root = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
                catch { root = new JsonObject(); }
                var u = new JsonObject { ["autoCheck"] = s.AutoCheck };
                if (s.SkippedVersion is { Length: > 0 } sv) u["skippedVersion"] = sv;
                if (s.LastCheckUtc is { } lc) u["lastCheckUtc"] = lc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                root[KeyName] = u;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch { return false; }
        }
    }

    public UpdateSettings Update(Func<UpdateSettings, UpdateSettings> change)
    {
        lock (_gate)
        {
            var s = change(Load());
            Save(s);
            return s;
        }
    }
}

/// <summary>
/// 확인 → (사용자 선택) → 내려받기 · 검증 → 설치 프로그램 실행 흐름(대화상자 없이 — 화면은 MainWindow.Update). 설치 실행은 주입(시험은 가짜).
/// </summary>
public sealed class UpdateService(HttpClient http, UpdateSettingsStore store, Version current, string latestUrl, string downloadDir,
    Action<string> launchInstaller, Action<string>? log = null)
{
    public Version Current { get; } = current;
    public UpdateSettingsStore Store { get; } = store;

    public bool ShouldAutoCheck(DateTime nowUtc) => !UpdateChecker.IsDevBuild(Current) && Store.Load().DueForAutoCheck(nowUtc);

    /// <summary>확인하고, 응답을 받았으면(Failed 가 아니면) 마지막 확인 시각을 남긴다 — 오프라인이면 다음 시작에 다시 확인.</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        var s = Store.Load();
        var r = await UpdateChecker.CheckAsync(http, latestUrl, Current, s.SkippedVersion, manual, log, ct);
        if (r.Status is not (UpdateStatus.Failed or UpdateStatus.DevBuild)) Store.Update(x => x with { LastCheckUtc = DateTime.UtcNow });
        return r;
    }

    public void Skip(ReleaseInfo r) => Store.Update(s => s with { SkippedVersion = r.Version.ToString(3) });

    public void SetAutoCheck(bool on) => Store.Update(s => s with { AutoCheck = on });

    /// <summary>내려받기 · 검증 뒤 설치 프로그램 실행. 실행한 설치 프로그램 경로를 돌려준다(호출자는 앱을 닫는다).</summary>
    public async Task<string> DownloadAndInstallAsync(ReleaseInfo r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = await UpdateChecker.DownloadAsync(http, r, downloadDir, progress, log, ct);
        log?.Invoke($"설치 프로그램 실행: {Path.GetFileName(path)} {UpdateChecker.InstallerArgs}");
        launchInstaller(path);
        return path;
    }
}
