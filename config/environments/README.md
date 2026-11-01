# 3D 장면 환경 파일 형식 (`mns.environment/1`)

3D 화면(보기 → 장면 환경)의 하늘 · 지면 · 조명 한 벌을 데이터 파일로 정의한다. 앱은 시작할 때 이 폴더의
`<id>/environment.json` 을 모두 읽어 목록에 올린다(잘못된 파일은 로그에 경로 · 필드와 함께 경고하고 건너뜀).
로더 · 검증: `src/Sim.App/Rendering/SceneEnvironment.cs` (`EnvironmentLoader`) · 시험: `tests/Sim.Core.Tests/SceneEnvironmentTests.cs`.

## 차용한 구조 (Autodesk Fusion 렌더링 환경)

Fusion 환경 하나는 폴더 하나다: `<이름>.xml` + `Ground.json` · `Dome.json`(메시) + `Ground.png` · `Dome.png`(텍스처) +
`<이름>-Reflection.exr`(IBL, 사전 필터 `_Glossy.dds` · `_Irradiance.dds`) + `Thumbnail.png`. XML 의 뼈대는

- `<Features>` — `<Feature Shape="Ground.JSON" Texture="Ground.png" IsSurrounding="1" IsGroundPlane="1"/>` · `<Feature Shape="Dome.JSON" Texture="Dome.png" IsSurrounding="1"/>`
- `<IBLEnvironment ImageName="…-Reflection.exr"/>`
- `<OGSLighting><Light type="Directional" Color Gain Space="View" Direction="0.1 -0.55 -1.0" ShadowColor="-0.3 -0.3 -0.3"/></OGSLighting>`
- `<RRTLighting AmbientLightColor AmbientLightGain …>` + 보조 방향광(Back · Front Left · Front Right)
- `<Scaling FitTo="2" AutoScale="1" AutoScaleFactor="10"/>` · `<Background RGB>` · `<DisplayName><Name Language>`

메시 JSON 은 `{"Vertices":{"Positions":[x,y,z,…],"UVs":[u,v,…]},"Indices":[…]}`(지면 = ±2057 사각형 2 삼각형).
클라우드 카탈로그의 `CenterAngle`(환경 기본 정면 각), 장면 설정(brightness · lightAngle · backgroundType · isGroundDisplayed ·
isGroundFlattened · isGroundReflections · groundRoughness · groundOffset · cameraExposure) 도 같은 이름으로 옮겼다.

**구조와 설정 개념만 차용한다. Autodesk/Fusion 의 파일(HDR · exr · dds · png · json · xml)은 읽지도 복사하지도 않는다.**
내장 환경의 텍스처는 모두 코드로 만드는 절차적 정의다.

## 우리 형식 (변형)

XML 대신 JSON 한 파일, 텍스처는 이미지 대신 절차적 정의를 기본으로, 사전 필터 결과는 파일로 두지 않고 앱이 계산한다.

```jsonc
{
  "schema": "mns.environment/1",          // 필수 — 형식 버전
  "id": "grass",                           // 필수 — 영문 · 숫자 · - · _ (설정에 저장되는 이름)
  "displayName": { "ko": "풀밭", "en": "Field" },
  "style": "scene",                        // "scene" | "analysis"(이전 방식: 단색 배경 + 격자, 아래 항목 무시)
  "centerAngleDeg": 0,                     // 환경 기본 정면 각 — 장면 회전에 더함(하늘 · 태양 · 나무 함께)
  "scaling": { "fitTo": 2, "autoScale": true, "autoScaleFactor": 10 },
  "features": [
    { "kind": "ground", "shape": "plane",  // "plane" 또는 메시 JSON 상대 경로
      "isGroundPlane": true, "isSurrounding": true,
      "texture": { "procedural": { "kind": "grass", "seed": 1,
                                   "colors": [[0.17,0.30,0.10],[0.33,0.46,0.16],[0.43,0.36,0.24]] } } },
    { "kind": "dome", "shape": "hemisphere", "isSurrounding": true,
      "texture": { "procedural": { "kind": "sky", "seed": 1,
                                   "zenith": [0.25,0.45,0.78], "horizon": [0.72,0.80,0.88], "haze": [0.62,0.66,0.62],
                                   "trees": false } } }
      // 또는 "texture": { "image": "sky.hdr" }  — 정거원통 영상(.hdr, 앱에서는 .png · .jpg 도)
  ],
  "ibl": { "source": "procedural-sky", "exposureEV": 0 },   // 또는 영상 상대 경로
  "lights": [
    { "type": "directional", "label": "sun", "color": [0.88,0.84,0.75], "gain": 1.0,
      "space": "ground", "direction": [-0.33,0.58,-0.74], "shadowColor": [-0.3,-0.3,-0.3], "enabled": true },
    { "type": "hemisphere", "label": "sky/ground", "sky": [0.42,0.46,0.54], "ground": [0.20,0.22,0.15], "gain": 1.0 }
  ],
  "ambient": { "color": [0.25,0.25,0.25], "gain": 0 },
  "background": { "rgb": [0.1,0.1,0.1] },  // 선택 — 단색 배경 색(없으면 테마 배경)
  "soilMatch": ["farmland", "grass", "*"]   // 시나리오 environment.soil → 이 환경('자동'), "*" = 그 밖 모두
}
```

| 필드 | 뜻 · 기본값 |
|---|---|
| `features[].texture.procedural.kind` | 지면: `grass` 흙 자국 · `soil` 마른 갈라짐 · `sand` 바람 물결 · `rock` 균열 · `snow` 푸른 그늘 · `forestFloor` 낙엽 / 돔: `sky` |
| `colors` | 지면 [바탕 어두운 색, 밝은 색, 무늬 색] (0–1 선형 RGB) |
| `zenith` · `horizon` · `haze` | 천정 · 지평선 · 지평선 아래 색. `trees: true` = 지평선에 침엽수 실루엣 |
| `direction` | **빛이 나아가는 방향**(Fusion 과 같음), 지면 공간 `[동, 북, 위]`, 위 성분 &lt; 0. 첫 `enabled` directional 이 태양(그림자) |
| `space` | `ground`(지면 고정). `view`(카메라 고정)는 받아들이되 지금은 ground 와 같게 그린다 |
| `scaling.autoScale` · `autoScaleFactor` | 켬: 지면 무늬 타일 = 화면 범위 × 2.5 / 배율(10 → 1/4). 끔 + 메시 지면: 평탄화를 끄면 메시 크기만큼의 지면 |
| `ibl.exposureEV` | 하늘 · 조명 노출 [EV] |

빠진 선택 필드는 위 기본값, 타입이 틀리거나 허용되지 않는 값은 `"<파일 경로>: '<필드>' — <이유>"` 오류로 거부한다.

### 메시 JSON

Fusion 과 같은 `Vertices.Positions` · `Vertices.UVs` · `Indices` 구조에 우리 필드 `units`(`m` · `cm` · `mm` · `in` · `ft`, 기본 m) ·
`up`(`z` · `y`, 기본 z)을 덧붙인다. 정점 3 개 이상 · UV 는 정점마다 2 개 · 인덱스는 3 의 배수이고 정점 범위 안.

### 사용자 환경 (보기 → 장면 환경 → 사용자 환경 불러오기…)

- 위 형식의 `environment.json`(또는 그 폴더)을 고르면 그대로 목록에 추가.
- 정거원통 영상 한 장(`.hdr`, `.png`, `.jpg`; 가로 = 방위 0–360°(북 → 동), 세로 = 천정 → 천저)을 고르면
  돔 · IBL = 그 영상, 하늘 · 땅 환경광 = 위 · 아래 반구 평균(확산 사전 필터 근사), 태양 = 위 반구에서 가장 밝은 방향,
  지면 = 아래 반구 평균 색의 흙 무늬인 환경 `user-<파일 이름>` 을 만든다.
- 불러온 경로는 사용자 설정(`%APPDATA%/MnS.Simulator/settings.json` 의 `scene.userEnvironments`)에 남아 다음 실행에 다시 읽는다.

## 내장 환경

`analysis`(기본 · 처음 값) · `grass` · `dirt` · `sand` · `rock` · `snow` · `forest-edge`.
