using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Sim.Core.Engine;
using Sim.Core.Models;

namespace Sim.App;

public enum LogLevel { Info, Warn, Error }

/// <summary>로그 패널 한 줄. 색은 수준 클래스(warn · err) → 토큰(MainWindow.axaml 템플릿).</summary>
public sealed class LogItem
{
    public required DateTime Time { get; init; }
    public required LogLevel Level { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }

    public string LevelText => Level switch { LogLevel.Warn => "경고", LogLevel.Error => "오류", _ => "정보" };
    public string Text => $"{Time:HH:mm:ss.fff}  {LevelText}  [{Source}]  {Message}";
    public bool IsWarn => Level == LogLevel.Warn;
    public bool IsError => Level == LogLevel.Error;
    public override string ToString() => Text;
}

/// <summary>오른쪽 이벤트 목록 한 줄(클릭 → 그 시각으로 이동).</summary>
public sealed record EventItem(double Time, string Name, string Detail)
{
    public string TimeText => Time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " s";
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public string Text => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,8:F3}s  {1}", Time, Name);
    public override string ToString() => Text;
}

/// <summary>어셈블리 트리 항목(3D 위 브라우저 TreeView 바인딩).</summary>
public sealed class AsmItem : INotifyPropertyChanged
{
    /// <summary>숨긴 행(과 숨긴 물체의 정보 줄)의 흐림 정도.</summary>
    public const double DimOpacity = 0.45;

    private bool _shown = true, _expanded = true;
    private string _state = "";
    private BodyMode _mode = BodyMode.Stowed;

    public required AssemblyNodeKind Kind { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public int BodyIndex { get; init; } = -1;
    public AsmItem? Parent { get; init; }
    public List<AsmItem> Children { get; } = new();
    /// <summary>물체 고유 색(시나리오 데이터 색 — 테마와 무관). 물체가 아니면 null(투명).</summary>
    public IBrush? Swatch { get; init; }
    /// <summary>브라우저 종류 아이콘 키(Controls.BrowserIcon): scene · assembly · body-mesh/box/cyl/sphere/shell · carrier · chute · rotor · pulse · device · tether · rail · hinge · swing · link · shape · aero · event · folder.</summary>
    public string IconKind { get; init; } = "folder";

    public bool IsBody => BodyIndex >= 0;
    /// <summary>보이기 토글(눈)이 있는 행 — 물체와 물체를 가진 묶음(장면).</summary>
    public bool HasEye => IsBody || Children.Any(c => c.IsBody);
    /// <summary>형상 · 공력 정보 줄(흐린 글자).</summary>
    public bool IsInfo => Kind is AssemblyNodeKind.Shape or AssemblyNodeKind.Aero;
    public bool HasState => State.Length > 0;
    /// <summary>숨김이면 흐리게 — 눈이 있는 행은 자기 표시, 정보 줄은 위 물체의 표시를 따른다.</summary>
    public double RowOpacity => (HasEye ? _shown : Parent is null || Parent.RowOpacity >= 1) ? 1 : DimOpacity;
    /// <summary>트리에 보이는 짧은 이름(넘치면 … 처리). 물체가 아닌 노드는 '이름 · 세부'.</summary>
    public string Label => IsBody || Detail.Length == 0 ? Name : $"{Name} · {Detail}";
    /// <summary>마우스를 올리면 보이는 전체 정보.</summary>
    public string Tip => Detail.Length == 0 ? Name : $"{Name}\n{Detail}{(State.Length > 0 ? $"\n상태: {State}" : "")}";
    public FontWeight Weight => IsBody || Kind == AssemblyNodeKind.Group ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>3D 뷰 표시(체크). 끄면 하위 물체도 함께 숨김.</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            OnChanged();
            OnChanged(nameof(RowOpacity));
            NotifyInfoRows(this);
            foreach (var c in Children.Where(c => c.IsBody)) c.Shown = value;

            static void NotifyInfoRows(AsmItem it)
            {
                foreach (var c in it.Children.Where(c => !c.HasEye)) { c.OnChanged(nameof(RowOpacity)); NotifyInfoRows(c); }
            }
            ShownChanged?.Invoke(this);
        }
    }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; OnChanged(); } }
    }

    public string State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnChanged(); OnChanged(nameof(HasState)); } }
    }

    /// <summary>상태 색 클래스(free · landed, 그 밖 = 수납 색) — 색은 토큰 StateFree/Landed/StowedBrush.</summary>
    public bool IsFree => _mode == BodyMode.Free;
    public bool IsLanded => _mode == BodyMode.Landed;

    public void SetMode(BodyMode m)
    {
        State = m switch
        {
            BodyMode.Stowed => "수납",
            BodyMode.Free => "비행",
            BodyMode.Landed => "착지",
            _ => "운반",
        };
        if (_mode == m) return;
        _mode = m;
        OnChanged(nameof(IsFree));
        OnChanged(nameof(IsLanded));
    }

    public event Action<AsmItem>? ShownChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public IEnumerable<AsmItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }

    /// <summary>헤드리스 트리 → UI 항목. 물체 아래 정보 노드는 기본 접힘(물체 계층만 펼침).</summary>
    public static AsmItem From(AssemblyNode n, World w, AsmItem? parent = null)
    {
        IBrush? swatch = null;
        if (n.BodyIndex >= 0 && Color.TryParse(w.Bodies[n.BodyIndex].Color, out var bodyColor))
            swatch = new SolidColorBrush(bodyColor);
        var item = new AsmItem { Kind = n.Kind, Name = n.Name, Detail = n.Detail, BodyIndex = n.BodyIndex, Parent = parent, Swatch = swatch, IconKind = IconOf(n, w, parent) };
        foreach (var c in n.Children) item.Children.Add(From(c, w, item));
        item._expanded = n.Kind == AssemblyNodeKind.Group ? n.Name == "장면" : item.Children.Any(c => c.IsBody);
        if (n.BodyIndex >= 0) item.SetMode(w.Bodies[n.BodyIndex].Mode);
        return item;
    }

    /// <summary>종류 아이콘 키 — 물체는 하위 물체가 있으면 어셈블리, 없으면 형상 종류, 장치 · 연결은 실제 형식으로.</summary>
    public static string IconOf(AssemblyNode n, World w, AsmItem? parent) => n.Kind switch
    {
        AssemblyNodeKind.Group => parent is null ? "scene" : "folder",
        AssemblyNodeKind.Carrier => "carrier",
        AssemblyNodeKind.Body when n.Children.Any(c => c.BodyIndex >= 0) => "assembly",
        AssemblyNodeKind.Body => w.Bodies[n.BodyIndex].Shape?.Kind switch
        {
            Sim.Core.Geometry.ShapeKind.Mesh => "body-mesh",
            Sim.Core.Geometry.ShapeKind.Cylinder => "body-cyl",
            Sim.Core.Geometry.ShapeKind.Sphere => "body-sphere",
            Sim.Core.Geometry.ShapeKind.ShellSegment => "body-shell",
            _ => "body-box",
        },
        AssemblyNodeKind.Shape => "shape",
        AssemblyNodeKind.Aero => "aero",
        AssemblyNodeKind.Event => "event",
        AssemblyNodeKind.Device => (parent is { BodyIndex: >= 0 } p ? w.Bodies[p.BodyIndex].Devices.FirstOrDefault(d => d.Name == n.Name) : null) switch
        {
            Parachute => "chute",
            Multirotor => "rotor",
            Pulse => "pulse",
            _ => "device",
        },
        AssemblyNodeKind.Connector => w.Connectors.FirstOrDefault(c => c.Name == n.Name) switch
        {
            Rail => "rail",
            Hinge => "hinge",
            Swing => "swing",
            Tether => "tether",
            _ => "link",
        },
        _ => "folder",
    };
}
