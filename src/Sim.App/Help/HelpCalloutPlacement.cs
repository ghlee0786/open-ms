namespace Sim.App.Help;

/// <summary>Avalonia 없는 사각형(시험 링크용) — 왼쪽 위 + 크기.</summary>
public readonly record struct HRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(HRect o) => !IsEmpty && !o.IsEmpty && o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top;

    public HRect Intersect(HRect o)
    {
        double l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? new HRect(l, t, r - l, b - t) : default;
    }

    public HRect Deflate(double d) => new(X + d, Y + d, Math.Max(0, Width - 2 * d), Math.Max(0, Height - 2 * d));

    public bool Contains(HRect o) => o.Left >= Left - 1e-9 && o.Top >= Top - 1e-9 && o.Right <= Right + 1e-9 && o.Bottom <= Bottom + 1e-9;
}

/// <summary>말풍선이 "?" 의 어느 쪽에 뜨는가 — 꼬리는 그 반대쪽 모서리에서 "?" 를 가리킨다.</summary>
public enum CalloutSide { Below, Above, Left, Right }

/// <summary>배치 결과 — 말풍선 왼쪽 위(입력과 같은 좌표계) · 뜬 쪽 · 꼬리 중심의 자리(말풍선 안 좌표, 위/아래면 x · 좌/우면 y).</summary>
public readonly record struct CalloutPlacement(double X, double Y, CalloutSide Side, double TailOffset)
{
    public HRect Rect(double width, double height) => new(X, Y, width, height);
}

/// <summary>
/// 말풍선 배치 — 순수 함수(참고: Ironwall HelpCalloutPlacement). 아래 → 위 → 왼쪽 → 오른쪽 순으로 경계 안에 <b>통째로</b> 들어가는 쪽.
/// </summary>
/// <remarks>
/// <para>말풍선 크기는 꼬리 자리(<see cref="TailMargin"/>, 네 변 모두)를 포함한 바깥 크기 — 어느 쪽에 뜨든 크기가 같다.</para>
/// <para>들어가는 쪽이 없으면 위 · 아래 중 넓은 쪽에 두고 경계 안으로 민다(겹침은 피할 수 없다). 말풍선이 경계보다 크면 왼쪽 · 위에 붙인다 —
/// 그래서 화면(창) 밖으로는 나가지 않는다(호출자가 최대 크기를 경계로 줄인다).</para>
/// </remarks>
public static class HelpCalloutPlacement
{
    /// <summary>꼬리 자리 — 바깥 크기 네 변에 비워 둔다(꼬리 길이).</summary>
    public const double TailMargin = 8;

    /// <summary>"?" 와 꼬리 끝 사이.</summary>
    public const double Gap = 2;

    /// <summary>꼬리 중심이 카드 모서리(둥근 귀퉁이)에서 떨어져야 하는 최소 거리.</summary>
    public const double TailEdgeInset = 16;

    /// <summary>"?" 를 왼쪽에 두고 펼칠 때 꼬리 중심의 기본 자리(카드 왼쪽에서) — 말풍선이 "?" 에서 오른쪽으로 펼쳐진다.</summary>
    public const double PreferredTailInset = 20;

    /// <summary>창 우선 — 창(작업 영역과 겹친 부분) 안에 통째로 들어가면 그 안에, 아니면 작업 영역 기준.</summary>
    public static CalloutPlacement Resolve(HRect target, double width, double height, HRect window, HRect screen)
    {
        var inner = window.IsEmpty ? window : screen.IsEmpty ? window : window.Intersect(screen);
        if (!inner.IsEmpty && inner.Intersects(target) && TryFit(target, width, height, inner, out var inWindow)) return inWindow;
        return Resolve(target, width, height, screen.IsEmpty ? window : screen);
    }

    public static CalloutPlacement Resolve(HRect target, double width, double height, HRect bounds)
    {
        double below = target.Bottom + Gap, above = target.Top - Gap - height;
        if (bounds.IsEmpty || !bounds.Intersects(target))
            return Horizontal(CalloutSide.Below, below, target, width, bounds, clamp: false);
        if (TryFit(target, width, height, bounds, out var fitted)) return fitted;
        double roomBelow = bounds.Bottom - target.Bottom, roomAbove = target.Top - bounds.Top;
        var side = roomBelow >= roomAbove ? CalloutSide.Below : CalloutSide.Above;
        double y = Clamp(side == CalloutSide.Below ? below : above, bounds.Top, bounds.Bottom - height);
        return Horizontal(side, y, target, width, bounds, clamp: true);
    }

    private static bool TryFit(HRect target, double width, double height, HRect bounds, out CalloutPlacement result)
    {
        double below = target.Bottom + Gap, above = target.Top - Gap - height;
        double left = target.Left - Gap - width, right = target.Right + Gap;
        bool across = width <= bounds.Width, along = height <= bounds.Height;
        if (across && below + height <= bounds.Bottom) result = Horizontal(CalloutSide.Below, below, target, width, bounds, true);
        else if (across && above >= bounds.Top) result = Horizontal(CalloutSide.Above, above, target, width, bounds, true);
        else if (along && left >= bounds.Left) result = Vertical(CalloutSide.Left, left, target, height, bounds);
        else if (along && right + width <= bounds.Right) result = Vertical(CalloutSide.Right, right, target, height, bounds);
        else { result = default; return false; }
        return true;
    }

    private static CalloutPlacement Horizontal(CalloutSide side, double y, HRect target, double width, HRect bounds, bool clamp)
    {
        double anchor = target.Left + target.Width / 2;
        double x = anchor - (TailMargin + PreferredTailInset);
        if (clamp) x = Clamp(x, bounds.Left, bounds.Right - width);
        return new CalloutPlacement(x, y, side, TailFor(anchor - x, width));
    }

    private static CalloutPlacement Vertical(CalloutSide side, double x, HRect target, double height, HRect bounds)
    {
        double anchor = target.Top + target.Height / 2;
        double y = Clamp(anchor - (TailMargin + PreferredTailInset), bounds.Top, bounds.Bottom - height);
        return new CalloutPlacement(x, y, side, TailFor(anchor - y, height));
    }

    /// <summary>꼬리 중심을 카드의 곧은 모서리 위로 — 귀퉁이(둥근 곳)에 걸리지 않게 자른다.</summary>
    private static double TailFor(double desired, double length)
    {
        double min = TailMargin + TailEdgeInset, max = length - TailMargin - TailEdgeInset;
        return max < min ? length / 2 : Clamp(desired, min, max);
    }

    /// <summary>하한 우선 자르기 — 상자가 경계보다 크면 하한(왼쪽 · 위)에 붙인다.</summary>
    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(v, max));
}
