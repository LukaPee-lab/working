# Dimension Event Editor

Epic Seven 차원 탐사 이벤트 DB를 노드 그래프로 보고 수정하는 WPF/.NET 10 툴입니다.

이 저장소에는 에디터 소스만 올립니다. `nexus_event` 엑셀 테이블은 회사 작업 데이터라서 Git에 포함하지 않고, 원드라이브 배포 폴더의 `Data` 폴더에 따로 둡니다.

기본 원드라이브 테이블 위치:

```text
C:\Users\lbh9517\OneDrive - Super Creative\EpicSeven - 문서\기획실\2_코어시스템팀\1. 이병연\1_작업중\175126 260917 신규 PVE 전투\dimension_event_editor_windows_release\Data\nexus_event 차원 탐사 이벤트.xlsx
```

로컬에서 빌드할 때 `DimensionEventEditor\Data\nexus_event 차원 탐사 이벤트.xlsx`가 있으면 출력물에 자동 복사됩니다. 이 `Data/*.xlsx` 파일은 `.gitignore`로 제외됩니다.

## 구성

- 앱 프로젝트: `DimensionEventEditor.csproj`
- 로컬 DB 복사본: `Data/nexus_event 차원 탐사 이벤트.xlsx` (Git 제외)
- 아이콘/스플래시: `Assets/`
- 주요 코드:
  - `MainWindow.xaml`, `MainWindow.xaml.cs`: 노드 에디터 UI와 조작
  - `EventWorkbookService.cs`: 엑셀 로드/저장/diff/validation
  - `PreviewWindow.cs`: Export Preview
  - `BackgroundImagePickerWindow.cs`: background 이미지 선택 팝업
  - `NexusPathResolver.cs`: 기본 DB, 플레이어, 로컬 설정 경로 탐색

## 필요한 환경

- Windows
- .NET 10 SDK
- NuGet 패키지: `ClosedXML`

빌드:

```powershell
dotnet build .\DimensionEventEditor.csproj -c Release
```

실행:

```powershell
dotnet run --project .\DimensionEventEditor.csproj
```

QA export:

```powershell
dotnet .\bin\Release\net10.0-windows\DimensionEventEditor.dll --qa ".\Data\nexus_event 차원 탐사 이벤트.xlsx" "$env:TEMP\dimension_event_editor_qa.xlsx"
```

정상 기준:

```text
QA diff entries: 0
QA reload errors: 0
```

## 기본 DB 탐색 순서

앱은 다음 순서로 `nexus_event` 엑셀을 찾습니다.

1. `%APPDATA%\SuperCreative\DimensionEventEditor\settings.json`에 저장된 `EventWorkbookPath`
2. 앱 실행 폴더의 `Data/nexus_event 차원 탐사 이벤트.xlsx`
3. 사용자가 설정한 `repos` 루트 아래 `design\DB\alpha`
4. `D:\repos\design\DB\alpha`
5. `%USERPROFILE%\repos\design\DB\alpha`

다른 PC에서는 원드라이브 배포 폴더의 `Data` 테이블을 쓰거나, 에디터에서 직접 DB 경로를 선택하면 됩니다.

## 테이블 구조

### `nexus_event_base`

이벤트 1개당 1행입니다.

- `id`: 이벤트 ID. 예: `s1_EVT_001`
- `event_name`: 이벤트명 TID
- `rarity`: 희귀도
- `weight`: 등장 가중치
- `first_group_id`: 시작 장면 그룹 ID
- `export_id`: Ruby exporter용 row export ID

### `nexus_event_choice_group`

장면 노드 1개당 1행입니다.

- `id`: 장면 ID
- `event_id`: 소속 이벤트 ID
- `background`: 장면 배경 리소스 키
- `npc_id`: 등장 NPC ID
- `situation_text`: 상황문 TID
- `next_action`: 장면 종료 방식. `choice`, `exit`, `battle`
- `stage_id`: `next_action=battle`일 때 연결 전투 스테이지 ID
- `export_id`: Ruby exporter용 row export ID

### `nexus_event_choice`

선택지 1개당 1행입니다.

- `group_id`: 선택지가 속한 장면 ID
- `seq`: 표시 순서
- `choice_text`: 선택지 TID
- `cost_type`, `cost_amount`: 선택 비용
- `success_rate`: 성공 확률. 값이 있을 때만 F 핀이 표시됩니다.
- `success_reward_type`, `success_reward_amount`: T 결과 보상
- `success_next_group_id`: T 결과 다음 장면
- `fail_reward_type`, `fail_reward_amount`: F 결과 보상
- `fail_next_group_id`: F 결과 다음 장면
- `export_id`: Ruby exporter용 row export ID

### `텍스트`

Export 시 TID와 실제 문장을 함께 갱신합니다.

- `nexus_event_base.event_name`
- `nexus_event_choice_group.situation_text`
- `nexus_event_choice.choice_text`

텍스트 `exportID` 기본값은 `251023.lbh9517_142227`입니다.

### `이벤트툴_레이아웃`

툴 전용 레이아웃 시트입니다. 숨김 시트가 아니라 일반 시트로 둡니다.

- `schema_version`
- `event_id`
- `group_id`: 장면 ID 또는 객체 노드 키
- `x`, `y`, `width`, `height`
- `updated_at`

객체 노드 키 예:

- `reward|{choice_id}|success`
- `reward|{choice_id}|fail`
- `exit|{group_id}`
- `battle|{group_id}`
- `group_reward|{group_id}`

## 노드 연결 규칙

### 장면 노드

장면 노드는 `nexus_event_choice_group` 1행입니다.

- 왼쪽 input 핀: 다른 선택지 T/F에서 들어오는 연결
- 오른쪽 회색 출력 핀: Battle / Reward / Exit 터미널 노드로 나가는 연결
- 선택지별 T/F 핀: 다음 장면 input으로 나가는 연결

### 선택지 T/F 핀

선택지 핀은 장면 노드 input에만 연결합니다.

- T 핀 연결 -> `success_next_group_id`
- F 핀 연결 -> `fail_next_group_id`
- F 핀은 `success_rate`가 있을 때만 표시됩니다.
- T/F 핀에서 Battle / Reward / Exit 터미널로 직접 연결하지 않습니다.

### Battle / Reward / Exit 터미널 노드

세 노드는 모두 장면을 끝내는 터미널 노드입니다. 나가는 핀이 없습니다.

- 장면 출력 -> Battle:
  - 연결된 장면의 `next_action = battle`
  - 연결된 장면의 `stage_id`를 사용
  - 새 장면 그룹을 만들지 않습니다.
- 장면 출력 -> Reward:
  - 연결된 장면의 `next_action = exit`
  - 보상을 받고 이벤트를 종료하는 의미의 시각 노드입니다.
- 장면 출력 -> Exit:
  - 연결된 장면의 `next_action = exit`

즉, Battle/Reward/Exit는 “다음 장면”이 아니라 “현재 장면의 종료 방식”을 시각화합니다.

## 현재 QA로 확인한 예시

`s1_EVT_001`의 일부 흐름:

```text
s1_EVT_001_G1
  T(C1) -> s1_EVT_001_G1_C1_S1 -> s1_EVT_001_G1_C1_S2 -> Reward(relic x1)
  T(C2) -> s1_EVT_001_G1_C2_S1 -> s1_EVT_001_G1_C2_S2 -> Exit(event end)
```

검증한 노드:

```text
s1_EVT_001_G1_C2_S2
next_action = exit

[장면 노드 오른쪽 출력 핀] -> [Exit 노드 input 핀]
```

확인 결과:

- 선택지 T 핀은 다음 장면 input으로 들어갑니다.
- Exit는 선택지 핀에서 직접 붙지 않고 장면 출력 핀에서 붙습니다.
- Exit 노드는 outgoing 핀이 없습니다.
- 해당 row는 `next_action=exit`입니다.
- QA export/reload 통과: `50 events / 298 groups / 362 choices`, reload error 0.

## Export Preview

- 변경 내역은 이벤트 단위로 묶어 보여줍니다.
- Before/After diff와 노드 프리뷰를 같이 봅니다.
- 체크박스는 체크박스 영역을 눌렀을 때만 토글됩니다.
- 이름 영역 클릭은 선택입니다.
- `Shift+Click`: 범위 선택
- `Ctrl+Click`: 개별 선택 추가/해제
- Export Preview에서 입력한 export ID는 변경되었거나 추가된 row에만 적용됩니다.
- row 순서가 밀린 것만으로는 export ID를 바꾸지 않습니다.

## 조작

- 마우스 휠: 확대/축소
- 우클릭 드래그: 캔버스 이동
- 좌클릭 드래그: 노드 선택 박스
- 빈 공간 클릭: 선택 해제
- `Del`: 선택 노드 삭제
- `Ctrl+Z`: 되돌리기
- `Ctrl+C`, `Ctrl+V`: 노드 복사/붙여넣기
- 우클릭 메뉴: 장면/보상/전투/Exit 노드 생성

## Runtime Player 연동

상단 Play 버튼은 선택한 이벤트를 `Dimension Exploration Event Player.exe`로 실행합니다.

- 실행 전 현재 편집 중인 데이터를 임시 runtime workbook으로 저장합니다.
- 플레이어에는 `event_id`와 runtime workbook 경로를 넘깁니다.
- 실행 중에는 DB 수정 UI를 잠급니다.
- Stop 또는 다시 Play를 누르면 플레이어를 종료하고 편집 UI를 복구합니다.
- Pause는 Godot 플레이어 프로세스를 suspend/resume합니다.

플레이어 탐색 순서:

1. settings의 `PlayerExePath`
2. 에디터 publish 폴더 주변의 `dimension_event_player_windows_release`
3. workbook 경로 상위 폴더의 `dimension_event_player_windows_release`

## 배경 이미지 선택

장면 노드의 `background`는 직접 입력하거나 picker 버튼으로 선택할 수 있습니다.

- 기본 이미지 루트: `D:\repos\dev\game\Resources\res\nexus`
- picker에서 홈 버튼을 누르면 기본 이미지 루트로 이동합니다.
- 인스펙터 하단에는 Unity Inspector 스타일의 배경 미리보기가 표시됩니다.

## 콘솔 / 검증

하단 Console / Validation 영역은 Unity Console처럼 분류됩니다.

- Noti
- Warning
- Error

검색과 필터링을 지원합니다.

## Publish

```powershell
$publishDir = "C:\Users\lbh9517\OneDrive - Super Creative\EpicSeven - 문서\기획실\2_코어시스템팀\1. 이병연\1_작업중\175126 260917 신규 PVE 전투\dimension_event_editor_windows_release"
Get-Process DimensionEventEditor -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish .\DimensionEventEditor.csproj -c Release -r win-x64 --self-contained false -o $publishDir
```

Publish 후 QA:

```powershell
dotnet "$publishDir\DimensionEventEditor.dll" --qa ".\Data\nexus_event 차원 탐사 이벤트.xlsx" "$env:TEMP\dimension_event_editor_release_qa.xlsx"
```

## 커밋 범위

이 저장소에 포함해야 하는 것:

- `DimensionEventEditor` 소스
- `Assets`
- `Data/nexus_event 차원 탐사 이벤트.xlsx`
- `README.md`

포함하지 않는 것:

- `bin/`
- `obj/`
- QA export 산출물
- publish 결과물
