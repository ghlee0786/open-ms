using System.Globalization;
using Avalonia;

namespace Sim.App;

internal static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        // 실모델 폴더(FR-AN-07): --real-models DIR → 시나리오 massProps "real:…" 해석(MNS_REAL_MODELS), --synthetic → 합성 대체 강제
        int ri = Array.IndexOf(args, "--real-models");
        if (ri >= 0 && ri + 1 < args.Length) System.Environment.SetEnvironmentVariable("MNS_REAL_MODELS", Path.GetFullPath(args[ri + 1]));
        else if (args.Contains("--synthetic")) System.Environment.SetEnvironmentVariable("MNS_REAL_MODELS", "synthetic");
        // 창 없는 보고서 일괄 모드(FR-AN-08)
        if (Array.IndexOf(args, "--opening-shock-report") is var oi and >= 0) return HeadlessReports.OpeningShock(args, oi);
        // 대화형 창은 하나만(자동 실행 --smoke 는 여러 개 허용)
        if (!args.Contains("--smoke") && !SingleInstance.Acquire()) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // 데스크톱 OpenGL(WGL) 우선 → ANGLE(GLES3) → 소프트웨어
                RenderingMode = [Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .LogToTrace();
}
