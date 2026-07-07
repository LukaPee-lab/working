# Dimension Event Editor

에픽세븐 차원 탐사 `nexus_event` 엑셀 DB를 노드 그래프로 보고 수정하는 WPF/.NET 10 에디터입니다.

이 저장소에는 에디터 소스만 둡니다. 실제 작업 DB인 `nexus_event 차원 탐사 이벤트.xlsx`는 작업자 로컬 또는 배포 폴더의 `Data` 폴더에 따로 둡니다.

## 프로젝트 구성

- `DimensionEventEditor.csproj`: WPF 프로젝트
- `MainWindow.xaml`, `MainWindow.xaml.cs`: 노드 에디터 UI와 상호작용
- `EventWorkbookService.cs`: 엑셀 로드/저장, diff, validation, 자동 보정
- `Models.cs`: 이벤트, 장면, 선택지, 레이아웃 모델
- `PreviewWindow.cs`: Export Preview
- `ThemedMessageBox.cs`: Unity 스타일 확인 팝업
- `BackgroundImagePickerWindow.cs`: background 이미지 선택
- `NexusPathResolver.cs`: DB, 플레이어, 설정 경로 탐색
- `Assets/`: 앱 아이콘과 스플래시 리소스

## 실행 환경

- Windows
- .NET 10 SDK
- NuGet: `ClosedXML`

빌드:

```powershell
dotnet build .\DimensionEventEditor.csproj -c Release
```

실행:

```powershell
dotnet run --project .\DimensionEventEditor.csproj
```

실행 중인 앱 종료 후 Release 빌드 및 재실행:

```powershell
Get-Process DimensionEventEditor -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\DimensionEventEditor.csproj -c Release
Start-Process .\bin\Release\net10.0-windows\DimensionEventEditor.exe
```

## DB 탐색 순서

앱은 다음 순서로 `nexus_event 차원 탐사 이벤트.xlsx`를 찾습니다.

1. `%APPDATA%\SuperCreative\DimensionEventEditor\settings.json`에 저장된 `EventWorkbookPath`
2. 앱 실행 폴더의 `Data\nexus_event 차원 탐사 이벤트.xlsx`
3. 설정된 repos root 아래 `design\DB\alpha`
4. `D:\repos\design\DB\alpha`
5. `%USERPROFILE%\repos\design\DB\alpha`

직접 파일을 열려면 상단 `Open DB`를 사용합니다.

## 테이블 구조

엑셀 DB의 모든 핵심 시트는 1-3행을 보존합니다.

- 1행: export/owner 메타데이터
- 2행: memo/comment
- 3행: 실제 필드 헤더
- 4행부터 데이터

### `nexus_event_base`

이벤트 1개당 1행입니다.

- `id`: 이벤트 ID. 예: `s1_EVT_001`
- `memo`: 에디터에서 보이는 이벤트명
- `export_id`: 기본값 `manmo2429_175126`
- `event_name`: 이벤트명 TID
- `rarity`: `Common`, `Rare`, `Epic`
- `weight`: 등장 가중치
- `first_group_id`: 시작 장면 ID

### `nexus_event_choice_group`

장면 노드 1개당 1행입니다.

- `id`: 장면 ID
- `memo`: 장면 문구/상황문
- `export_id`: 기본값 `manmo2429_175126`
- `event_id`: 소속 이벤트 ID
- `background`: 배경 리소스 키
- `npc_id`: NPC ID
- `situation_text`: 상황문 TID
- `next_action`: `choice`, `exit`, `battle`
- `stage_id`: `next_action=battle`일 때 전투 스테이지 ID

### `nexus_event_choice`

선택지 1개당 1행입니다. Battle 결과는 숨김 choice row로도 저장합니다.

- `id`: 선택지 ID
- `memo`: 선택지 문구
- `export_id`: 기본값 `manmo2429_175126`
- `group_id`: 소속 장면 ID
- `seq`: 선택지 표시 순서
- `choice_text`: 선택지 TID
- `cost_type`, `cost_amount`: 선택 비용
- `success_rate`: 성공 확률. 값이 있을 때 일반 선택지에 F 핀이 표시됩니다.
- `success_reward_type`, `success_reward_amount`: T 결과 보상
- `success_next_group_id`: T 결과 다음 장면
- `fail_reward_type`, `fail_reward_amount`: F 결과 보상
- `fail_next_group_id`: F 결과 다음 장면

## 노드 모델

### 장면 노드

장면 노드는 `nexus_event_choice_group` row입니다. Inspector의 `next_action` 라디오로 역할이 결정됩니다.

- `Choice`: 선택지를 가진 일반 장면
- `Exit`: 이벤트 종료 장면
- `Battle`: 전투 장면

Battle과 Exit은 별도 테이블 노드가 아닙니다. 둘 다 장면 노드 상태입니다.

### 선택지 T/F 핀

- T 핀은 `success_next_group_id`로 저장됩니다.
- F 핀은 `success_rate`가 있는 일반 선택지에서 표시되고 `fail_next_group_id`로 저장됩니다.
- Battle 결과는 항상 T/F가 있으며 hidden choice row에 저장됩니다.
- 같은 장면 자기 자신으로 연결하는 self-link는 금지됩니다.

### Reward 노드

Reward 노드는 선택지 row의 reward 컬럼을 시각화하는 보조 노드입니다.

- T Reward는 `success_reward_type`, `success_reward_amount`를 수정합니다.
- F Reward는 `fail_reward_type`, `fail_reward_amount`를 수정합니다.
- 노드 안의 `reward_type`, `reward_amount` 필드를 더블 클릭해서 바로 수정할 수 있습니다.
- 여러 선택지 핀이 같은 Reward 노드에 연결될 수 있습니다.
- 공유 Reward 노드의 값을 바꾸면 같은 위치를 공유하는 branch들의 reward 값이 같이 갱신됩니다.
- Reward 출력은 다음 장면 또는 Exit 장면으로 연결합니다.

### Battle 노드

Battle은 장면 노드 하나로 표현됩니다.

- Inspector에서 `next_action = Battle`을 선택합니다.
- `stage_id`가 필요합니다.
- Battle 장면에는 T/F 출력 핀이 항상 표시됩니다.
- Battle 결과는 `{battle_group_id}_BATTLE_RESULT` hidden choice row에 저장됩니다.
- Battle 진입 전에는 반드시 도망/회피 선택지가 있어야 합니다.
- 우클릭 메뉴에는 Battle 노드 추가가 없습니다.

### Exit 장면

모든 이벤트는 Exit 장면으로 끝나야 합니다.

- `next_action = Exit`
- 보통 `{event_id}_G999_EXIT` ID를 사용합니다.
- Auto Layout에서 Exit 장면은 마지막 칼럼으로 밀립니다.
- Exit 장면에는 표시 선택지를 두지 않는 것이 원칙입니다.

## 주요 UX

- 앱은 실행 시 전체 화면으로 시작합니다.
- 첫 로드 시 첫 이벤트가 선택되고 그래프가 즉시 렌더링됩니다.
- 상단 리모콘은 중앙에 위치하며 `▶`, `❚❚`, `■` 버튼을 사용합니다.
- 상단 Window 메뉴에서 `Event List`, `Scene`, `Hierarchy`, `Inspector`, `Console` 패널을 열고 닫을 수 있습니다.
- 닫은 패널과 크기는 로컬 설정에 저장됩니다.
- Console은 Unity 스타일 카운터 토글로 Noti/Warning/Error를 필터링합니다.
- 스크롤바와 팝업은 어두운 Unity 스타일 테마를 사용합니다.

## 조작

- 마우스 휠: 줌
- 우클릭 드래그: 캔버스 팬
- 좌클릭 드래그: 노드 선택 박스
- 빈 캔버스 우클릭: `장면 노드 추가`, `보상 노드 추가`
- 노드 드래그: 위치 이동
- 여러 노드 선택 후 드래그: 함께 이동
- 핀 드래그: 연결 생성
- 핀 클릭: 연결 라인 강조
- 핀 선택 후 `Del` 또는 우클릭 메뉴: 연결 해제
- `Del`: 선택 노드 삭제
- `Ctrl+Z`: 되돌리기
- `Ctrl+C`, `Ctrl+V`: 노드 복사/붙여넣기
- 장면 문구 더블 클릭: 장면 memo 수정
- 선택지 문구 더블 클릭: 선택지 memo 수정
- Reward 필드 더블 클릭: reward type/amount 수정

## 단축키

- `Ctrl+Shift+L`: 현재 이벤트 Auto Layout
- `Ctrl+Shift+E`: Export Preview
- Export Preview에서 `Esc`: 창 닫기

버튼의 단축키는 버튼 내부 두 번째 줄에 작은 글씨로 표시합니다.

## Inspector

장면 Inspector에는 Unity 배열 스타일의 `Choices` 목록이 있습니다.

- `Size`는 현재 visible choice 수입니다.
- `Element N` 또는 왼쪽 핸들을 드래그하면 선택지 순서를 바꿀 수 있습니다.
- 드래그 중 들어갈 위치가 행 위/아래 파란 라인으로 표시됩니다.
- 선택지 추가 시 새 선택지 상세로 자동 이동하지 않고, 장면 Inspector에 목록으로 추가됩니다.

한글 라벨도 수동 도움말 주석을 찾도록 매핑합니다.

- `희귀도` -> `rarity`
- `가중치` -> `weight`
- `메모/이벤트명` -> `event_name`
- `메모/상황문` -> `situation_text`
- `메모/선택지 문구` -> `choice_text`

## Auto Layout

- 현재 이벤트 Auto Layout과 모든 이벤트 Auto Layout을 지원합니다.
- 모든 이벤트 Auto Layout은 확인 팝업 후 로딩 팝업을 띄우고 처리합니다.
- Exit 장면은 마지막 칼럼으로 배치합니다.
- Reward 후 Exit으로 가는 선이 되돌아가지 않도록 Reward보다 Exit이 오른쪽에 오게 정렬합니다.

## Export Preview

- 변경 내용을 이벤트 단위로 묶어서 보여줍니다.
- Before/After 그래프 미리보기를 제공합니다.
- 체크박스 영역과 row 선택 영역을 구분합니다.
- `Shift+Click`으로 범위 선택, `Ctrl+Click`으로 개별 추가/해제할 수 있습니다.
- Revert Checked는 선택된 diff만 되돌립니다.
- `Esc`로 Export Preview를 닫을 수 있습니다.

## Runtime Player

상단 Play 버튼은 현재 편집 중인 데이터를 임시 runtime workbook으로 저장한 뒤 `Dimension Exploration Event Player.exe`를 실행합니다.

- 실행 중에는 DB 편집 UI를 잠급니다.
- Stop 또는 다시 Play를 누르면 플레이어를 종료하고 편집 UI를 복구합니다.
- Pause는 Godot 플레이어 프로세스를 suspend/resume합니다.

플레이어 탐색 순서:

1. settings의 `PlayerExePath`
2. 에디터 publish 폴더 주변의 `dimension_event_player_windows_release`
3. workbook 경로 상위 폴더의 `dimension_event_player_windows_release`

## 배경 이미지

장면의 `background`는 직접 입력하거나 picker 버튼으로 선택할 수 있습니다.

- 기본 이미지 루트: `D:\repos\dev\game\Resources\res\nexus`
- Inspector 하단에 background preview가 표시됩니다.
- 이미지가 없으면 `Preview not found`로 표시됩니다.

## QA와 검증

QA export:

```powershell
dotnet .\bin\Release\net10.0-windows\DimensionEventEditor.dll --qa ".\Data\nexus_event 차원 탐사 이벤트.xlsx" "$env:TEMP\dimension_event_editor_qa.xlsx"
```

정상 기준:

```text
QA diff entries: 0
QA reload errors: 0
```

스킬 패키지의 검증 스크립트:

```powershell
python C:\Users\lbh95\.codex\skills\dimension-exploration-event-designer\scripts\validate_nexus_event_tables.py --event-file "C:\Users\lbh95\Downloads\nexus_event 차원 탐사 이벤트.xlsx"
```

## 이벤트 작성 스킬

차원 탐사 이벤트 작성용 Codex 스킬은 다음 위치에 둡니다.

```text
C:\Users\lbh95\.codex\skills\dimension-exploration-event-designer
```

배포용 zip:

```text
C:\Users\lbh95\Downloads\dimension-exploration-event-skill.zip
```

스킬은 다음 규칙을 Codex에게 알려줍니다.

- Battle은 장면의 `next_action=battle`
- Exit은 장면의 `next_action=exit`
- 모든 이벤트는 Exit 장면으로 종료
- Battle 진입 전 도망 선택지 필수
- Reward는 choice row의 reward 컬럼
- hidden battle result choice row 규칙

## Publish

```powershell
$publishDir = "C:\Users\lbh95\Desktop\dimension_event_editor_windows_release"
Get-Process DimensionEventEditor -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish .\DimensionEventEditor.csproj -c Release -r win-x64 --self-contained false -o $publishDir
```

## 커밋 포함 범위

포함:

- 소스 코드
- `Assets`
- `README.md`

제외:

- `bin/`
- `obj/`
- `.vs/`
- 실제 작업 DB
- QA/export 결과물
- publish 결과물
