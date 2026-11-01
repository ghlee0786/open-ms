using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Sim.App.Rendering;

namespace Sim.App.Theme;

/// <summary>
/// 테마 전환(참고: Ironwall ThemeService — 시작 시 복원 · 토글 · 저장 · Changed 이벤트로 비-XAML 경로 재색칠).
/// Avalonia 는 <see cref="Application.RequestedThemeVariant"/> 하나로 ThemeDictionaries 두 벌을 바꾸고 DynamicResource 가 다시 칠한다.
/// 직접 그리는 것(GL 3D 뷰 팔레트)은 <see cref="Changed"/> 를 받아 토큰을 다시 읽는다.
/// </summary>
public static class ThemeService
{
    private static ThemeStore? _store;
    private static bool _persist = true;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>테마가 바뀐 뒤(리소스 교체 후) — UI 스레드.</summary>
    public static event Action<AppTheme>? Changed;

    /// <summary>
    /// 시작 시 한 번: <c>--theme dark|light</c> 가 있으면 그것(그 실행에만, 저장 안 함), 없으면 사용자 설정 파일 → 기본 다크.
    /// 스모크(<c>--smoke</c>)는 사용자 설정을 쓰지 않는다.
    /// </summary>
    public static void Initialize(string[] args)
    {
        _store = ThemeStore.Default();
        int i = Array.IndexOf(args, "--theme");
        var forced = i >= 0 && i + 1 < args.Length ? ThemeStore.Parse(args[i + 1]) : null;
        bool smoke = args.Contains("--smoke");
        _persist = forced is null && !smoke;
        Apply(forced ?? (smoke ? null : _store.Load()) ?? AppTheme.Dark, save: false);
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        Current = theme;
        Changed?.Invoke(theme);
        if (save && _persist) _store?.Save(theme);   // 실패는 조용히(ThemeStore)
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>현재 테마의 토큰 하나(없으면 null).</summary>
    public static object? Find(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        var variant = Current == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        return app.TryGetResource(key, variant, out var v) ? v : null;
    }

    public static Color ColorOf(string key, Color fallback) => Find(key) switch
    {
        Color c => c,
        ISolidColorBrush b => b.Color,
        _ => fallback,
    };

    /// <summary>3D 뷰 팔레트(View*Color 토큰).</summary>
    public static ViewPalette ViewPalette()
    {
        var d = Rendering.ViewPalette.Dark;
        (float, float, float) C(string key, (float R, float G, float B) fb)
        {
            var c = ColorOf(key, new Color(255, (byte)(fb.R * 255), (byte)(fb.G * 255), (byte)(fb.B * 255)));
            return Rendering.ViewPalette.Rgb(c.R, c.G, c.B);
        }
        return new ViewPalette(C("ViewBgColor", d.Background), C("ViewGroundColor", d.Ground), C("ViewGridColor", d.Grid),
            C("ViewDividerColor", d.Divider), C("ViewActiveColor", d.Active), C("ViewMarkerColor", d.Marker), C("ViewRiserColor", d.Riser));
    }
}

/// <summary>코드로 만든 UI 가 토큰을 쓰는 도우미 — 바인딩이라 테마를 바꾸면 따라 바뀐다.</summary>
public static class Ui
{
    /// <summary>속성을 토큰에 묶는다(DynamicResource 와 같음).</summary>
    public static T Res<T>(this T c, AvaloniaProperty property, string key) where T : Control
    {
        c.Bind(property, c.GetResourceObservable(key));
        return c;
    }

    public static T Fg<T>(this T c, string key) where T : TextBlock => c.Res(TextBlock.ForegroundProperty, key);

    public static T Bg<T>(this T c, string key) where T : Border => c.Res(Border.BackgroundProperty, key);

    /// <summary>직접 그리는 컨트롤용 — 그 컨트롤의 테마로 지금 값을 찾는다(Render 에서 호출, 테마가 바뀌면 다시 그려진다).</summary>
    public static IBrush? Brush(this Control c, string key)
        => c.TryFindResource(key, c.ActualThemeVariant, out var v) ? v as IBrush : null;

    public static Color ColorRes(this Control c, string key, Color fallback)
        => c.TryFindResource(key, c.ActualThemeVariant, out var v) ? v switch { Color col => col, ISolidColorBrush b => b.Color, _ => fallback } : fallback;
}
