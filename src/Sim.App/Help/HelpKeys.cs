namespace Sim.App.Help;

/// <summary>
/// 섹션 "?" 의 키 — 화면(axaml · 코드)은 이 상수만 건다(오타가 컴파일에서 잡히게). 문구는 <see cref="AppHelp"/>.
/// 형식 <c>App.{창}.{섹션}</c>. 버튼 AutomationId = <c>Help.{키}</c>, 말풍선 몸 = <c>Help.{키}.Body</c>.
/// 시험(HelpCatalogTests): 상수마다 카탈로그 항목이 있고, 카탈로그의 App.* 키는 모두 상수이며, 상수는 모두 화면 소스에서 쓰인다.
/// </summary>
public static class HelpKeys
{
    // 메인 창 — 도구 막대 · 상태 표시줄
    public const string Toolbar = "App.Main.Toolbar";
    public const string Color = "App.Main.Color";
    public const string Split = "App.Main.Split";
    public const string Status = "App.Main.Status";
    // 셸 — 왼쪽 아이콘 레일 · 서랍 · 떠 있는 패널
    public const string Rail = "App.Main.Rail";
    // 왼쪽 입력
    public const string Scenario = "App.Main.Scenario";
    public const string Conditions = "App.Main.Conditions";
    public const string ImportOptions = "App.Main.ImportOptions";
    public const string MonteCarlo = "App.Main.MonteCarlo";
    public const string Json = "App.Main.Json";
    // 가운데
    public const string Tree = "App.Main.Tree";
    public const string View3D = "App.Main.View3D";
    public const string Playback = "App.Main.Playback";
    public const string Plot = "App.Main.Plot";
    public const string Log = "App.Main.Log";
    // 오른쪽 대시보드
    public const string Dashboard = "App.Main.Dashboard";
    public const string Devices = "App.Main.Devices";
    public const string Impact = "App.Main.Impact";
    public const string Structure = "App.Main.Structure";
    public const string Credibility = "App.Main.Credibility";
    public const string Events = "App.Main.Events";
    public const string Summary = "App.Main.Summary";
    // 보조 창 · 대화상자
    public const string ScenarioInfo = "App.ScenarioInfo.Body";
    public const string HelpSearch = "App.Help.Search";
    public const string WindTunnelControls = "App.WindTunnel.Controls";
    public const string WindTunnelStage = "App.WindTunnel.Stage";
    public const string AirflowControls = "App.Airflow.Controls";
    public const string AirflowCoeffs = "App.Airflow.Coeffs";
    public const string CredibilityWindow = "App.Credibility.Window";
    public const string Materials = "App.Materials.Window";
    public const string CcxOptions = "App.Ccx.Options";
    public const string Update = "App.Update.Window";
}
