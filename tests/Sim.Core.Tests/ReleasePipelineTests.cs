using System.Text.RegularExpressions;
using Sim.App.Update;

namespace Sim.Core.Tests;

/// <summary>
/// 릴리스 파이프라인(.github/workflows · installer/OpenMS.iss · scripts/build-installer.ps1)이 앱의 자동 업데이트와 맞물리는지 —
/// 태그 트리거 · 설치 프로그램 / .sha256 자산 이름 · 사용자 설치 · 설치 후 재실행. YAML 은 줄 단위로 읽는다(외부 패키지 없음).
/// </summary>
public class ReleasePipelineTests
{
    private static string Repo(params string[] p) => Path.Combine([TestUtil.RepoRoot, .. p]);
    private static string Text(params string[] p) => File.ReadAllText(Repo(p));

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void Workflow_yaml_is_well_formed(string file)
    {
        var lines = File.ReadAllLines(Repo(".github", "workflows", file));
        Assert.Contains(lines, l => l.StartsWith("name: "));
        Assert.Contains(lines, l => l == "on:");
        Assert.Contains(lines, l => l == "jobs:");
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            Assert.False(l.Contains('\t'), $"{file}:{i + 1} 탭 문자(YAML 은 공백 들여쓰기)");
            int indent = l.Length - l.TrimStart(' ').Length;
            if (l.Trim().Length > 0) Assert.True(indent % 2 == 0, $"{file}:{i + 1} 들여쓰기 {indent}칸(2의 배수가 아님)");
            Assert.False(l.EndsWith(' '), $"{file}:{i + 1} 줄 끝 공백");
        }
        // 액션은 모두 버전 고정(@vN)
        foreach (Match m in Regex.Matches(string.Join("\n", lines), @"uses:\s*(\S+)"))
            Assert.Matches(@"^[\w.-]+/[\w.-]+@v\d+$", m.Groups[1].Value);
        // ${{ }} 식은 짝이 맞는다
        var all = string.Join("\n", lines);
        Assert.Equal(Regex.Matches(all, @"\$\{\{").Count, Regex.Matches(all, @"\}\}").Count);
    }

    [Fact]
    public void Ci_builds_and_tests_on_push_and_pull_request()
    {
        var ci = Text(".github", "workflows", "ci.yml");
        Assert.Contains("  push:", ci);
        Assert.Contains("  pull_request:", ci);
        Assert.Contains("dotnet build MnS.slnx", ci);
        Assert.Contains("dotnet test MnS.slnx", ci);
        Assert.Contains("8.0.x", ci);
    }

    [Fact]
    public void Release_on_version_tag_publishes_installer_and_sha256_that_the_updater_looks_for()
    {
        var rel = Text(".github", "workflows", "release.yml");
        Assert.Contains("tags: [\"v*\"]", rel);
        Assert.Contains("contents: write", rel);
        Assert.Contains("dotnet test MnS.slnx", rel);
        Assert.Contains("./scripts/build-installer.ps1 -Version ${{ steps.ver.outputs.version }}", rel);
        Assert.Contains("softprops/action-gh-release@v2", rel);
        // 앱이 찾는 이름: OpenMS-Setup-<버전>.exe + 같은 이름 .sha256
        var setup = $"installer/Output/{UpdateChecker.InstallerPrefix}${{{{ steps.ver.outputs.version }}}}.exe";
        Assert.Contains(setup + "\n", rel.Replace("\r\n", "\n"));
        Assert.Contains(setup + ".sha256", rel);
        Assert.Contains("Get-FileHash -Algorithm SHA256", rel);
        // 시험(test) 이 설치 프로그램보다 먼저
        Assert.True(rel.IndexOf("dotnet test", StringComparison.Ordinal) < rel.IndexOf("build-installer.ps1 -Version", StringComparison.Ordinal));
        // 시험판 태그(v1.2.3-beta)는 prerelease 로 — 업데이트가 건너뛴다
        Assert.Contains("prerelease: ${{ contains(github.ref_name, '-') }}", rel);
    }

    [Fact]
    public void Installer_script_installs_per_user_and_relaunches_after_silent_update()
    {
        var iss = Text("installer", "OpenMS.iss");
        Assert.Contains($"OutputBaseFilename={UpdateChecker.InstallerPrefix}{{#AppVersion}}", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\OpenMS", iss);
        Assert.Contains("#define AppName \"Open M&S\"", iss);
        Assert.Contains("CloseApplications=force", iss);
        Assert.Contains("{autoprograms}", iss);
        // 앱 실행 파일 이름 = Sim.App AssemblyName
        var asm = Regex.Match(Text("src", "Sim.App", "Sim.App.csproj"), "<AssemblyName>(.+)</AssemblyName>").Groups[1].Value;
        Assert.Contains($"#define AppExe \"{asm}.exe\"", iss);
        // 설치 후 실행 — /SILENT 업데이트 뒤에도 다시 띄우도록 skipifsilent 없음
        var run = iss.Split('\n').Single(l => l.StartsWith("Filename: \"{app}\\{#AppExe}\""));
        Assert.Contains("postinstall", run);
        Assert.DoesNotContain("skipifsilent", run);
        // 체크 상자 글의 "M&S" — & 를 && 로(안 하면 'Open MS' 로 보인다)
        Assert.Contains("StringChange(AppName, \"&\", \"&&\")", run);
        // 앱이 실행 폴더에서 찾는 데이터
        Assert.Contains(@"{#Repo}\config\*", iss);
        // 앱이 넘기는 인자와 짝
        Assert.Contains("/SILENT", UpdateChecker.InstallerArgs);
        Assert.Contains("/CLOSEAPPLICATIONS", UpdateChecker.InstallerArgs);
    }

    [Fact]
    public void Build_script_publishes_with_version_and_finds_iscc()
    {
        var ps = Text("scripts", "build-installer.ps1");
        Assert.Contains("\"-p:Version=$Version\"", ps);
        Assert.Contains("--self-contained true", ps);
        Assert.Contains(@"$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", ps);
        Assert.Contains(@"Inno Setup 6\ISCC.exe", ps.Replace("$env:LOCALAPPDATA", ""));
        Assert.Contains(@"installer\OpenMS.iss", ps);
        // 한글 주석 · 메시지 → Windows PowerShell 5.1 이 깨지지 않게 UTF-8 BOM
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        Assert.Equal(bom, File.ReadAllBytes(Repo("scripts", "build-installer.ps1"))[..3]);
        Assert.Equal(bom, File.ReadAllBytes(Repo("installer", "OpenMS.iss"))[..3]);
    }

    [Fact]
    public void Dev_version_is_zero_in_build_props()
        => Assert.Contains("<Version>0.0.0</Version>", Text("Directory.Build.props"));
}
