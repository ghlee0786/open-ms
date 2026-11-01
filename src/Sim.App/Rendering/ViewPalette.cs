namespace Sim.App.Rendering;

/// <summary>
/// 3D 뷰(OpenGL) 바탕 팔레트 — 배경 · 지면 · 격자 · 분할 구분선 · 조작 화면 테두리 · 선택 표식 · 라이저.
/// 테마 토큰(App.axaml 의 View*Color)에서 ThemeService 가 만들어 넘긴다. 데이터 색(물체 · 컬러맵 · 방위 · 축 · MC)은 여기 없다.
/// </summary>
public readonly record struct ViewPalette(
    (float R, float G, float B) Background,
    (float R, float G, float B) Ground,
    (float R, float G, float B) Grid,
    (float R, float G, float B) Divider,
    (float R, float G, float B) Active,
    (float R, float G, float B) Marker,
    (float R, float G, float B) Riser)
{
    /// <summary>다크 기본값(토큰을 읽기 전 · 시험) — 예전 고정값과 같다.</summary>
    public static ViewPalette Dark { get; } = new(
        (0.07f, 0.09f, 0.12f), (0.085f, 0.105f, 0.135f), (0.4f, 0.6f, 0.45f),
        (0.2f, 0.24f, 0.3f), (0.13f, 0.72f, 0.85f), (1f, 1f, 1f), (0.85f, 0.85f, 0.85f));

    /// <summary>바이트 색 → 0–1.</summary>
    public static (float, float, float) Rgb(byte r, byte g, byte b) => (r / 255f, g / 255f, b / 255f);
}
