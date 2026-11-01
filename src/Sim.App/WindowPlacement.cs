using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Sim.App;

/// <summary>창이 화면 밖으로 나가지 않게: 열릴 때 크기를 작업 영역(작업 표시줄 제외)에 맞추고 위치를 그 안으로 옮긴다.
/// 제목 표시줄 높이·Position 기준(바깥 틀/클라이언트)을 짐작하지 않고, Windows 에서는 실제로 보이는 틀(DWM 확장 틀 경계)을 재서 옮긴다.</summary>
public static class WindowPlacement
{
    public static void KeepOnScreen(this Window w)
    {
        w.Opened += (_, _) =>
        {
            var scr = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
            if (scr is null) return;
            var wa = scr.WorkingArea;
            double s = scr.Scaling;
            const double margin = 16;
            // 1) 대략 맞춤(제목 표시줄 32 가정) — 정확한 위치는 2) 에서 실제 틀로
            if (w.Width > wa.Width / s - margin) w.Width = wa.Width / s - margin;
            if (w.Height > wa.Height / s - 32 - margin) w.Height = wa.Height / s - 32 - margin;
            // 2) 배치가 끝난 뒤 실제로 보이는 틀을 재서 작업 영역 안으로
            Dispatcher.UIThread.Post(() => Fit(w, wa, s), DispatcherPriority.Background);
        };
    }

    private static void Fit(Window w, PixelRect wa, double s)
    {
        if (VisibleFrame(w) is not { } r) { FitFallback(w, wa, s); return; }
        // 틀이 작업 영역보다 크면 클라이언트를 그만큼 줄인다
        int over = r.Height - wa.Height;
        if (over > 0) { w.Height -= over / s; if (VisibleFrame(w) is { } r2) r = r2; }
        over = r.Width - wa.Width;
        if (over > 0) { w.Width -= over / s; if (VisibleFrame(w) is { } r3) r = r3; }
        int dx = 0, dy = 0;
        if (r.Right > wa.Right) dx = wa.Right - r.Right;
        if (r.X + dx < wa.X) dx = wa.X - r.X;
        if (r.Bottom > wa.Bottom) dy = wa.Bottom - r.Bottom;
        if (r.Y + dy < wa.Y) dy = wa.Y - r.Y;
        if (dx != 0 || dy != 0) w.Position = new PixelPoint(w.Position.X + dx, w.Position.Y + dy);
    }

    /// <summary>Windows 가 아니면: 제목 표시줄 높이를 FrameSize 로 어림해 맞춘다.</summary>
    private static void FitFallback(Window w, PixelRect wa, double s)
    {
        double frameH = Math.Max(0, (w.FrameSize?.Height ?? w.Height) - w.Height);
        if (frameH < 1) frameH = 32;
        int pw = (int)(w.Width * s), ph = (int)(w.Height * s), top = wa.Y + (int)(frameH * s);
        int x = Math.Clamp(w.Position.X, wa.X, Math.Max(wa.X, wa.Right - pw));
        int y = Math.Clamp(w.Position.Y, top, Math.Max(top, wa.Bottom - ph));
        w.Position = new PixelPoint(x, y);
    }

    /// <summary>화면에 실제로 보이는 창 틀(그림자·보이지 않는 크기 조절 테두리 제외) — 물리 픽셀.</summary>
    private static PixelRect? VisibleFrame(Window w)
    {
        if (!OperatingSystem.IsWindows() || w.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return null;
        if (DwmGetWindowAttribute(h, DwmwaExtendedFrameBounds, out var rc, Marshal.SizeOf<Rect>()) != 0) return null;
        return new PixelRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    private const int DwmwaExtendedFrameBounds = 9;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
}
