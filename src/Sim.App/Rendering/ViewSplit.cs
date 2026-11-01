namespace Sim.App.Rendering;

/// <summary>3D 뷰 분할(값 = 분할 화면 수).</summary>
public enum SplitLayout { Single = 1, Dual = 2, Quad = 4 }

/// <summary>
/// 분할 화면 배치 수학(GL·Avalonia 없음 — 테스트 프로젝트가 이 소스를 링크한다).
/// 1분할 = 전체, 2분할 = 좌|우, 4분할 = 2×2(0 왼쪽 위 · 1 오른쪽 위 · 2 왼쪽 아래 · 3 오른쪽 아래).
/// 좌표는 왼쪽 위 원점(DIP 또는 픽셀 — 호출자가 같은 단위로 넘김).
/// </summary>
public static class ViewSplit
{
    public const int MaxPanes = 4;

    public static int PaneCount(SplitLayout l) => l switch { SplitLayout.Dual => 2, SplitLayout.Quad => 4, _ => 1 };

    public static (int Rows, int Cols) Grid(SplitLayout l) => l switch { SplitLayout.Dual => (1, 2), SplitLayout.Quad => (2, 2), _ => (1, 1) };

    /// <summary>분할 화면 수(1·2·4, 그 밖의 값은 가까운 쪽)를 배치로.</summary>
    public static SplitLayout FromCount(int n) => n >= 3 ? SplitLayout.Quad : n == 2 ? SplitLayout.Dual : SplitLayout.Single;

    /// <summary>분할 화면 i 의 사각형(왼쪽 위 원점). 칸 경계는 반올림해 이웃 칸과 빈틈·겹침 없이 맞닿는다.</summary>
    public static (double X, double Y, double W, double H) Rect(SplitLayout l, int pane, double width, double height)
    {
        var (rows, cols) = Grid(l);
        int r = pane / cols, c = pane % cols;
        double x0 = Edge(width, c, cols), x1 = Edge(width, c + 1, cols);
        double y0 = Edge(height, r, rows), y1 = Edge(height, r + 1, rows);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static double Edge(double size, int k, int n) => k >= n ? size : Math.Round(size * k / n);

    /// <summary>점 (x, y) 가 들어 있는 분할 화면(밖이면 가장 가까운 칸).</summary>
    public static int PaneAt(SplitLayout l, double width, double height, double x, double y)
    {
        var (rows, cols) = Grid(l);
        int c = width > 0 && double.IsFinite(x) ? Math.Clamp((int)(x / width * cols), 0, cols - 1) : 0;
        int r = height > 0 && double.IsFinite(y) ? Math.Clamp((int)(y / height * rows), 0, rows - 1) : 0;
        return r * cols + c;
    }
}
