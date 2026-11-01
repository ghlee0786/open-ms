using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sim.App.Theme;

/// <summary>앱 테마(밝기). 색 토큰 두 벌(App.axaml ThemeDictionaries)과 3D 뷰 팔레트가 이것을 따른다.</summary>
public enum AppTheme { Dark, Light }

/// <summary>
/// 테마 선택을 사용자 설정 파일(<c>%APPDATA%/MnS.Simulator/settings.json</c> 의 <c>"theme"</c>)에 저장 · 복원한다
/// (참고: Dotnet.Monitoring.Solution AppSettingsThemeStore — 시작 시 복원, 토글 시 저장).
/// 모든 I/O 오류는 조용히 넘긴다(테마는 보조 기능 — 시작 · 토글을 막지 않는다). 다른 키는 그대로 둔다. Avalonia 없음(시험 링크).
/// </summary>
public sealed class ThemeStore(string path)
{
    private const string KeyName = "theme";
    private readonly object _gate = new();

    public string FilePath { get; } = path;

    /// <summary>기본 위치: 사용자 앱 데이터 폴더(실행 폴더는 빌드 때 지워지고 쓰기 권한이 없을 수 있다).</summary>
    public static string DefaultPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "MnS.Simulator", "settings.json");

    public static ThemeStore Default() => new(DefaultPath);

    /// <summary>"dark" · "light"(대소문자 무시) → 테마. 그 밖은 null.</summary>
    public static AppTheme? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "dark" or "다크" => AppTheme.Dark,
        "light" or "라이트" => AppTheme.Light,
        _ => null,
    };

    /// <summary>저장된 테마(없음 · 손상 · 오류 = null → 호출자가 기본값).</summary>
    public AppTheme? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            return Parse(root?[KeyName]?.GetValue<string>());
        }
        catch { return null; }
    }

    /// <summary>테마를 기록(다른 키 보존, 임시 파일 → 교체로 원자적). 실패해도 예외 없음 — 성공 여부만 돌려준다.</summary>
    public bool Save(AppTheme theme)
    {
        lock (_gate)
        {
            try
            {
                JsonObject root;
                try { root = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
                catch { root = new JsonObject(); }   // 손상된 파일은 새로
                root[KeyName] = theme == AppTheme.Light ? "light" : "dark";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch { return false; }
        }
    }
}
