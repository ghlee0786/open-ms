using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sim.App.Help;

/// <summary>
/// 공용 "?" — 작은 원형 단추 + 말풍선(<see cref="HelpCallout"/>). 화면은 키(<see cref="HelpKey"/> = <see cref="HelpKeys"/> 상수)만 걸고
/// 문구는 <see cref="HelpCatalog"/> 에서 찾는다(참고: Ironwall HelpTip).
/// </summary>
/// <remarks>
/// <para><b>열고 닫기</b>: 클릭 · Enter · Space 로 열고, "?" 다시 누르기 · 바깥 클릭 · 휠 · <c>Esc</c> · 창 크기 변경 · 창 비활성 · 숨김으로 닫는다.
/// <c>Esc</c> 는 말풍선이 열려 있을 때만 먹는다(창 터널 처리기). 바깥 클릭은 아래 요소에도 그대로 간다.</para>
/// <para><b>한 번에 하나</b>: 새로 열면 앞의 것을 닫는다(<see cref="Current"/>).</para>
/// <para><b>어디에 뜨나</b>: 창의 OverlayLayer(창 안) — 배치는 순수 함수 <see cref="HelpCalloutPlacement"/>(아래 → 위 → 왼쪽 → 오른쪽, 창 밖으로 안 나감).
/// 창 안 요소라 창 캡처(RenderTargetBitmap)에도 찍힌다.</para>
/// <para><b>없는 키</b>: 단추를 끈다 — 죽지 않는다. 자동화: 단추 <c>Help.{키}</c>, 말풍선 몸 <c>Help.{키}.Body</c>.</para>
/// </remarks>
public sealed class HelpTip : ToggleButton
{
    private HelpCallout? _callout;
    private Canvas? _layer;
    private TopLevel? _top;
    private bool _closing;

    /// <summary>지금 열린 "?"(없으면 null).</summary>
    public static HelpTip? Current { get; private set; }

    /// <summary>말풍선의 "전체 도움말에서 보기" — 메인 창이 받아 도움말 창을 그 장으로 연다.</summary>
    public static event Action<string>? GuideRequested;

    public static readonly StyledProperty<string?> HelpKeyProperty = AvaloniaProperty.Register<HelpTip, string?>(nameof(HelpKey));
    public string? HelpKey { get => GetValue(HelpKeyProperty); set => SetValue(HelpKeyProperty, value); }

    protected override Type StyleKeyOverride => typeof(ToggleButton);

    public HelpTip()
    {
        Classes.Add("helptip");
        Content = "?";
        Focusable = true;
        IsTabStop = true;
    }

    /// <summary>열린 말풍선(진단 · 시험 · 캡처용).</summary>
    public HelpCallout? Callout => _callout;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HelpKeyProperty)
        {
            var key = HelpKey;
            var entry = HelpCatalog.Find(key);
            AutomationProperties.SetAutomationId(this, $"Help.{key}");
            AutomationProperties.SetName(this, entry is null ? "설명 없음" : $"설명 — {HelpText.Plain(entry.Title)}");
            ToolTip.SetTip(this, entry is null ? null : $"설명 — {HelpText.Plain(entry.Title)}");
            IsEnabled = entry is not null;
            IsVisible = !string.IsNullOrWhiteSpace(key);
        }
        else if (change.Property == IsVisibleProperty && !IsVisible) Close();
    }

    protected override void Toggle()
    {
        if (IsChecked == true) Close();
        else Open();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.Handled)
        {
            Toggle();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>연다(이미 열려 있으면 그대로). 키가 없거나 창에 붙지 않았으면 아무것도 하지 않는다.</summary>
    public void Open()
    {
        if (_callout is not null) return;
        if (HelpCatalog.Find(HelpKey) is not { } entry) return;
        var top = TopLevel.GetTopLevel(this);
        var layer = OverlayLayer.GetOverlayLayer(this);
        if (top is null || layer is null) return;
        Current?.Close();
        Current = this;
        _top = top;
        _layer = layer;
        _callout = new HelpCallout(entry, key => { var k = key; Close(); GuideRequested?.Invoke(k); });
        layer.Children.Add(_callout);
        Place();
        Hook();
        SetCurrentValue(IsCheckedProperty, true);
    }

    /// <summary>닫는다(닫혀 있으면 그대로).</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            Unhook();
            if (_callout is not null) _layer?.Children.Remove(_callout);
            _callout = null;
            _layer = null;
            _top = null;
            if (ReferenceEquals(Current, this)) Current = null;
            SetCurrentValue(IsCheckedProperty, false);
        }
        finally { _closing = false; }
    }

    public static void CloseCurrent() => Current?.Close();

    /// <summary>"?" 옆 자리 — 창(오버레이) 안, 가장자리 6 px 여유.</summary>
    private void Place()
    {
        if (_callout is null || _layer is null || _top is null) return;
        // 오버레이 층은 창 클라이언트 영역 전체(0,0 기준) — 층 자신의 Bounds 는 처음 열 때 아직 0 일 수 있어 창 크기를 쓴다
        var cs = _top.ClientSize;
        var bounds = new HRect(0, 0, cs.Width, cs.Height).Deflate(6);
        double m = HelpCalloutPlacement.TailMargin;
        _callout.MaxWidth = Math.Max(160, Math.Min(HelpCallout.MaxCardWidth + 2 * m, bounds.Width));
        _callout.MaxHeight = Math.Max(120, bounds.Height);
        _callout.Measure(new Size(_callout.MaxWidth, _callout.MaxHeight));
        var size = _callout.DesiredSize;
        var origin = this.TranslatePoint(new Point(0, 0), _top) ?? new Point(0, 0);
        var target = new HRect(origin.X, origin.Y, Bounds.Width, Bounds.Height);
        var r = HelpCalloutPlacement.Resolve(target, size.Width, size.Height, bounds);
        Canvas.SetLeft(_callout, r.X);
        Canvas.SetTop(_callout, r.Y);
        _callout.Side = r.Side;
        _callout.TailOffset = r.TailOffset;
    }

    private void Hook()
    {
        if (_top is null) return;
        _top.AddHandler(PointerPressedEvent, OnTopPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top.AddHandler(PointerWheelChangedEvent, OnTopWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top.AddHandler(KeyDownEvent, OnTopKeyDown, RoutingStrategies.Tunnel);
        _top.SizeChanged += OnTopSizeChanged;
        if (_top is Window w) w.Deactivated += OnDeactivated;
        DetachedFromVisualTree += OnDetached;
    }

    private void Unhook()
    {
        if (_top is null) return;
        _top.RemoveHandler(PointerPressedEvent, OnTopPointerPressed);
        _top.RemoveHandler(PointerWheelChangedEvent, OnTopWheel);
        _top.RemoveHandler(KeyDownEvent, OnTopKeyDown);
        _top.SizeChanged -= OnTopSizeChanged;
        if (_top is Window w) w.Deactivated -= OnDeactivated;
        DetachedFromVisualTree -= OnDetached;
    }

    private void OnTopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInside(e.Source as Visual)) return;   // "?" 자신(토글이 닫는다) · 말풍선 안
        Close();
    }

    private void OnTopWheel(object? sender, PointerWheelEventArgs e)
    {
        if (IsInside(e.Source as Visual)) return;
        Close();
    }

    private void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _callout is null) return;
        Close();
        e.Handled = true;   // 열려 있을 때만 먹는다
    }

    private void OnTopSizeChanged(object? sender, SizeChangedEventArgs e) => Close();
    private void OnDeactivated(object? sender, EventArgs e) => Close();
    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();

    private bool IsInside(Visual? v)
    {
        for (; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, this) || ReferenceEquals(v, _callout)) return true;
        return false;
    }
}
