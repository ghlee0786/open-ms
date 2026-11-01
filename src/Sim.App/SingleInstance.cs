using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sim.App;

/// <summary>대화형 앱은 한 번에 하나만 — 이미 떠 있으면 그 창을 앞으로 가져오고 새 프로세스는 끝낸다.
/// 자동 실행(--smoke)은 테스트·하네스 작업이 여러 개 동시에 돌리므로 대상이 아니다.</summary>
internal static class SingleInstance
{
    const string MutexName = @"Local\MnS.Simulator.SingleInstance";
    static Mutex? _mutex;

    /// <summary>true = 이 프로세스가 첫 인스턴스(계속 실행), false = 기존 창을 앞으로 가져왔으니 끝낼 것.</summary>
    public static bool Acquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew) return true;
        _mutex.Dispose();
        _mutex = null;
        ActivateExisting();
        return false;
    }

    static void ActivateExisting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p)
            {
                if (p.Id == self.Id) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, SwRestore);
                SetForegroundWindow(h);
                return;
            }
        }
    }

    const int SwRestore = 9;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}
