using Sim.Core.Engine;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

internal static class TestUtil
{
    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "MnS.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("MnS.slnx 를 찾지 못했습니다.");
        }
    }

    /// <summary>
    /// 시나리오 파일: 엔진 검증용(01~10b, 테스트 전용 tests/Sim.Core.Tests/scenarios) → 앱 운용 시나리오(config/scenarios, 11~) 순으로 찾는다.
    /// </summary>
    public static string Scenario(string file)
    {
        var test = Path.Combine(TestScenarioDir, file);
        return File.Exists(test) ? test : Path.Combine(RepoRoot, "config", "scenarios", file);
    }

    public static string TestScenarioDir => Path.Combine(RepoRoot, "tests", "Sim.Core.Tests", "scenarios");

    /// <summary>
    /// 실제 설계 자료(config/models · config/aero_db · 설계 시나리오 09·10·11 — 공개 스냅숏에 없는 파일)를 쓰는 시험을 돌리는가.
    /// 환경변수 MNS_DESIGN_DATA = 1 이면 켬(자료가 없으면 그 시험은 실패), 0 이면 끔, 없으면 설계 자료 폴더 config/models 가 있을 때만 켬.
    /// </summary>
    public static bool DesignData => System.Environment.GetEnvironmentVariable("MNS_DESIGN_DATA") switch
    {
        "1" => true,
        "0" => false,
        _ => Directory.Exists(Path.Combine(RepoRoot, "config", "models")),
    };

    public const string DesignSkip = "실제 설계 자료가 필요한 시험 — 공개판에는 자료가 없어 건너뜀(MNS_DESIGN_DATA=1 로 강제)";

    /// <summary>설계 자료 파일을 이름 무늬로 찾는다(저장소 상대 폴더, 이름순 첫 파일). 없으면 무늬 그대로의 경로.</summary>
    public static string DesignFile(string relDir, string pattern)
    {
        var dir = Path.Combine(RepoRoot, relDir);
        return (Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null)
               ?? Path.Combine(dir, pattern);
    }

    public static World Build(string json) => ScenarioDoc.Parse(json, RepoRoot).Build();

    public static World Run(string json)
    {
        var w = Build(json);
        w.Run();
        return w;
    }
}

/// <summary>실제 설계 자료가 필요한 [Fact] — <see cref="TestUtil.DesignData"/> 가 거짓이면 건너뜀.</summary>
public sealed class DesignFactAttribute : FactAttribute
{
    public DesignFactAttribute() { if (!TestUtil.DesignData) Skip = TestUtil.DesignSkip; }
}

/// <summary>실제 설계 자료가 필요한 [Theory] — <see cref="TestUtil.DesignData"/> 가 거짓이면 건너뜀.</summary>
public sealed class DesignTheoryAttribute : TheoryAttribute
{
    public DesignTheoryAttribute() { if (!TestUtil.DesignData) Skip = TestUtil.DesignSkip; }
}
