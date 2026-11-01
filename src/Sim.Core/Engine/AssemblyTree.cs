using System.Globalization;
using Sim.Core.Geometry;
using Sim.Core.Models;

namespace Sim.Core.Engine;

public enum AssemblyNodeKind { Group, Carrier, Body, Shape, Aero, Device, Connector, Event }

/// <summary>어셈블리 트리 노드(UI 트리·보고서용, 헤드리스). BodyIndex ≥ 0 이면 World.Bodies 인덱스.</summary>
public sealed class AssemblyNode
{
    public required AssemblyNodeKind Kind { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public int BodyIndex { get; init; } = -1;
    public List<AssemblyNode> Children { get; } = new();

    public IEnumerable<AssemblyNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}

/// <summary>
/// 시나리오 요소(R절)의 어셈블리 구조: 운반체 → 물체(수납 계층) → 형상·공력·장치, 연결, 이벤트 규칙.
/// 수납 계층은 분리(World.Separate) 때 끊기므로 **적분 전** 월드로 만들어야 한다.
/// </summary>
public static class AssemblyTree
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static AssemblyNode Build(World w)
    {
        var root = new AssemblyNode { Kind = AssemblyNodeKind.Group, Name = "장면", Detail = $"물체 {w.Bodies.Count} · 연결 {w.Connectors.Count} · 이벤트 {w.Events.Count}" };
        foreach (var b in w.Bodies.Where(b => b.Parent is null)) root.Children.Add(BodyNode(b));

        if (w.Connectors.Count > 0)
        {
            var g = new AssemblyNode { Kind = AssemblyNodeKind.Group, Name = "연결", Detail = $"{w.Connectors.Count}" };
            foreach (var c in w.Connectors) g.Children.Add(new AssemblyNode { Kind = AssemblyNodeKind.Connector, Name = c.Name, Detail = Describe(c) });
            root.Children.Add(g);
        }
        if (w.Events.Count > 0)
        {
            var g = new AssemblyNode { Kind = AssemblyNodeKind.Group, Name = "이벤트 규칙", Detail = $"{w.Events.Count}" };
            foreach (var e in w.Events)
                g.Children.Add(new AssemblyNode
                {
                    Kind = AssemblyNodeKind.Event, Name = e.Name,
                    Detail = string.Join(", ", e.Actions.Select(a => a.Describe())),
                });
            root.Children.Add(g);
        }
        return root;
    }

    private static AssemblyNode BodyNode(Body b)
    {
        bool carrier = b.Mode == BodyMode.Kinematic;
        var n = new AssemblyNode
        {
            Kind = carrier ? AssemblyNodeKind.Carrier : AssemblyNodeKind.Body,
            Name = b.Name, BodyIndex = b.Index,
            Detail = carrier ? "운반체(규정 궤적)" : string.Format(Inv, "{0:F3} kg · {1}", b.OwnMass, b.MassSource),
        };
        n.Children.Add(ShapeNode(b));
        if (!carrier && b.Aero is { } aero)
            n.Children.Add(new AssemblyNode { Kind = AssemblyNodeKind.Aero, Name = "공력", Detail = Describe(aero, b) });
        foreach (var d in b.Devices) n.Children.Add(new AssemblyNode { Kind = AssemblyNodeKind.Device, Name = d.Name, Detail = Describe(d) });
        foreach (var c in b.Children) n.Children.Add(BodyNode(c));
        return n;
    }

    /// <summary>형상 노드 — Fusion 내보내기의 사용자 파라미터·조인트가 있으면 그 아래에 값 보기(정보 줄)로.</summary>
    private static AssemblyNode ShapeNode(Body b)
    {
        var shape = new AssemblyNode { Kind = AssemblyNodeKind.Shape, Name = "형상", Detail = Describe(b.Shape) };
        if (b.MassProps is not { } mp) return shape;
        if (mp.UserParameters.Count > 0)
        {
            var g = new AssemblyNode { Kind = AssemblyNodeKind.Shape, Name = "사용자 파라미터", Detail = $"{mp.UserParameters.Count}" };
            foreach (var p in mp.UserParameters) g.Children.Add(new AssemblyNode { Kind = AssemblyNodeKind.Shape, Name = p.Name, Detail = Describe(p) });
            shape.Children.Add(g);
        }
        if (mp.Joints.Count > 0)
        {
            var g = new AssemblyNode { Kind = AssemblyNodeKind.Shape, Name = "조인트", Detail = $"{mp.Joints.Count}" };
            foreach (var j in mp.Joints) g.Children.Add(new AssemblyNode { Kind = AssemblyNodeKind.Shape, Name = j.Name, Detail = Describe(j) });
            shape.Children.Add(g);
        }
        return shape;
    }

    /// <summary>예: "250 mm = 0.25 m · 암 길이" (SI 값이 없으면 Fusion 내부값).</summary>
    public static string Describe(CadParameter p)
    {
        var parts = new List<string>();
        string val = p.ValueSI is { } si ? string.Format(Inv, "{0:G6}{1}", si, string.IsNullOrEmpty(p.SiUnit) ? "" : " " + p.SiUnit)
            : p.Value is { } v ? string.Format(Inv, "{0:G6} (Fusion 내부 단위)", v) : "";
        parts.Add(p.Expression.Length > 0 && val.Length > 0 ? $"{p.Expression} = {val}" : p.Expression + val);
        if (!string.IsNullOrWhiteSpace(p.Comment)) parts.Add(p.Comment!);
        return string.Join(" · ", parts.Where(s => s.Length > 0));
    }

    /// <summary>예: "Revolute · arm:1 ↔ (루트) · 원점 (0.1, 0, 0.05) m".</summary>
    public static string Describe(CadJoint j)
    {
        static string Occ(string s) => s.Length == 0 ? "(루트)" : s;
        var s = $"{(j.Type.Length > 0 ? j.Type : "?")} · {Occ(j.OccurrenceOne)} ↔ {Occ(j.OccurrenceTwo)}";
        if (j.Origin is { } o) s += string.Format(Inv, " · 원점 ({0:G4}, {1:G4}, {2:G4}) m", o.X, o.Y, o.Z);
        if (j.Source == "asBuiltJoint") s += " · as-built";
        if (j.Suppressed) s += " · 억제됨";
        return s;
    }

    public static string Describe(Shape? s) => s is null ? "없음(구 대체 표시)" : s.Kind switch
    {
        ShapeKind.Box => string.Format(Inv, "상자 {0:G4}×{1:G4}×{2:G4} m", s.Size.X, s.Size.Y, s.Size.Z),
        ShapeKind.Cylinder => string.Format(Inv, "원통 Ø{0:G4}×{1:G4} m ({2}축)", 2 * s.Radius, s.Length, "xyz"[Math.Clamp(s.Axis, 0, 2)]),
        ShapeKind.Sphere => string.Format(Inv, "구 Ø{0:G4} m", 2 * s.Radius),
        ShapeKind.ShellSegment => string.Format(Inv, "외피 조각 R{0:G4}×t{1:G3}×L{2:G4} m · {3:G3}° @ {4:G3}°", s.Radius, s.Thickness, s.Length, s.SpanDeg, s.CenterAngleDeg),
        _ => string.Format(Inv, "메시 {0:N0} 삼각형 {1:G4}×{2:G4}×{3:G4} m{4}", s.Mesh?.TriangleCount ?? 0, s.Size.X, s.Size.Y, s.Size.Z,
            s.MeshPath is { } p ? " · " + Path.GetFileName(p) : ""),
    };

    private static string Describe(AeroModel a, Body b)
    {
        var parts = new List<string> { a.Fidelity };
        if (a.Cd is { } cd) parts.Add(string.Format(Inv, "Cd {0:G3}", cd));
        if (a.Area is { } area) parts.Add(string.Format(Inv, "S {0:G3} m²", area));
        if (b.StaticMargin is { } sm) parts.Add(string.Format(Inv, "정적여유 {0:P1}", sm));
        return string.Join(" · ", parts);
    }

    private static string Describe(Device d) => d switch
    {
        Parachute p => string.Format(Inv, "낙하산 D₀ {0:G3} m · Cd {1:G3}", p.D0, p.Cd0),
        Multirotor m => string.Format(Inv, "멀티로터 최대추력 {0:G4} N", m.MaxThrust),
        _ => d.Type,
    };

    private static string Describe(Connector c) => c switch
    {
        Tether t => string.Format(Inv, "테더 {0} ↔ {1} · L {2:G3} m", t.A.Name, t.B.Name, t.Length),
        Rail r => string.Format(Inv, "레일 {0} → {1} · {2:G3} m", r.Parent.Name, r.Child.Name, r.Length),
        _ => c.Type,
    };
}
