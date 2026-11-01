using Sim.Core.Engine;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>UI 어셈블리 트리(FR-GEN-05 트리 보기)의 헤드리스 구조 생성.</summary>
public class AssemblyTreeTests
{
    private static World Build03() => ScenarioDoc.Load(TestUtil.Scenario("03_container_drone_sensor_poc.json")).Build();

    private static AssemblyNode Find(AssemblyNode root, string name) =>
        root.Descendants().Single(n => n.BodyIndex >= 0 && n.Name == name);

    [Fact]
    public void Every_body_appears_exactly_once_with_its_index()
    {
        var w = Build03();
        var tree = AssemblyTree.Build(w);
        var bodies = tree.Descendants().Where(n => n.BodyIndex >= 0).ToList();
        Assert.Equal(w.Bodies.Count, bodies.Count);
        Assert.Equal(Enumerable.Range(0, w.Bodies.Count), bodies.Select(n => n.BodyIndex).OrderBy(i => i));
        foreach (var n in bodies) Assert.Equal(w.Bodies[n.BodyIndex].Name, n.Name);
    }

    [Fact]
    public void Stowed_hierarchy_follows_scenario_parents()
    {
        var w = Build03();
        var tree = AssemblyTree.Build(w);
        var carrier = tree.Children.Single(n => n.Kind == AssemblyNodeKind.Carrier);
        Assert.Equal("projectile", carrier.Name);
        var container = carrier.Children.Single(n => n.BodyIndex >= 0);
        Assert.Equal("container", container.Name);
        var expected = w.Bodies.Where(b => b.Parent?.Name == "container").Select(b => b.Name).ToList();
        Assert.Equal(new[] { "lid", "box1", "box2", "box3" }, expected.Take(4));
        Assert.Equal(expected, container.Children.Where(n => n.BodyIndex >= 0).Select(n => n.Name));

        // box1 → shell1 → drone1 → sensor1, 장치는 소유 물체 아래
        var chain = new[] { "box1", "shell1", "drone1", "sensor1" };
        for (int i = 0; i + 1 < chain.Length; i++)
            Assert.Contains(Find(tree, chain[i]).Children, c => c.Name == chain[i + 1] && c.BodyIndex >= 0);
        Assert.Contains(Find(tree, "box1").Children, c => c.Kind == AssemblyNodeKind.Device && c.Name == "chute");
        Assert.Contains(Find(tree, "drone1").Children, c => c.Kind == AssemblyNodeKind.Device && c.Name == "rotor");
        Assert.Equal(2, container.Children.Count(c => c.Kind == AssemblyNodeKind.Device));
        Assert.All(tree.Descendants().Where(n => n.Kind == AssemblyNodeKind.Body),
            n => Assert.Contains(n.Children, c => c.Kind == AssemblyNodeKind.Shape && c.Detail.Length > 0));
    }

    [Fact]
    public void Connectors_and_events_are_listed()
    {
        var w = Build03();
        var tree = AssemblyTree.Build(w);
        Assert.Equal(w.Connectors.Count, tree.Descendants().Count(n => n.Kind == AssemblyNodeKind.Connector));
        Assert.Equal(w.Events.Count, tree.Descendants().Count(n => n.Kind == AssemblyNodeKind.Event));
        Assert.True(w.Connectors.Count > 0);
    }

    [Fact]
    public void Tree_must_be_built_before_separation_flattens_hierarchy()
    {
        // 적분 후에는 분리로 Parent 가 끊겨 루트가 늘어난다 — UI 는 적분 전 트리를 써야 하는 이유(회귀 고정).
        var w = Build03();
        int before = AssemblyTree.Build(w).Children.Count(n => n.BodyIndex >= 0);
        w.Run();
        int after = AssemblyTree.Build(w).Children.Count(n => n.BodyIndex >= 0);
        Assert.Equal(1, before);
        Assert.True(after > before);
    }
}
