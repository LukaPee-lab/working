# Nexus Editor

에픽세븐 Nexus 시스템의 DB를 시각적으로 보고 수정하는 WPF/.NET 10 에디터입니다. 시작 허브에서 차원 탐사 이벤트와 연구 중 하나를 선택하며, 실행 중에도 `Window` 메뉴에서 편집기를 바꿀 수 있습니다.

이 저장소에는 에디터 소스만 둡니다. 실제 작업 DB는 작업자 로컬 DB 폴더에 둡니다. Event Editor는 `nexus_event 차원 탐사 이벤트.xlsx`, Research Editor는 `nexus_out_system 차원_탐사_아웃시스템.xlsx`와 `nexus_effect 차원 탐사 효과.xlsx`를 사용합니다. 배포 폴더의 `Data` 폴더는 실행/검증용 보조 데이터이며, 앱이 기본 DB로 자동 선택하지 않습니다.

## 시작 허브와 보호 경계

- 스플래시 화면이 끝나면 Event Editor와 Research Editor 중 하나를 고르는 허브가 열립니다.
- 열린 편집기에서 `Window > Event Editor` 또는 `Window > Research Editor`를 선택해 모드를 바꿀 수 있습니다.
- 변경한 내용이 남아 있으면 `취소`, `버리기`, `Export 후 전환` 중 하나를 선택해야 합니다.
- 전환 중에는 기존 창을 잠그며, 새 편집기가 정상적으로 열린 뒤에만 기존 창을 닫습니다.
- Research Editor의 창 `X`는 전환 창을 띄우지 않습니다. 변경이 없으면 종료 여부를 묻고, 변경이 있으면 `취소`, `저장하지 않고 종료`, `Export 후 종료` 중 하나를 선택합니다.
- Event와 Research는 화면의 색, 패널 배치, 조작 감각만 통일합니다. 모델, Undo, 검증, Export 상태는 서로 공유하지 않습니다.
- Event Editor 내부 구현은 보호 대상입니다. 시작 허브와 `Window` 전환을 제외한 Research 기능은 `Research*` 파일에서 별도로 구현합니다.

## 최신 반영 사항

- Research Editor에서 카테고리, 연구 노드, 대응 연구 효과를 한 화면에서 편집할 수 있습니다.
- 카테고리 또는 좌표를 바꾸면 node ID, effect ID, 선행 조건 참조를 한 작업으로 함께 바꿉니다.
- Research Export는 두 워크북을 사전 검사한 뒤 한 쌍으로 저장하고, 두 번째 파일 저장 실패 시 첫 번째 파일도 복구합니다.
- Research Export Preview에서 체크하지 않은 변경은 실제 저장본에서도 제외됩니다.
- Ruby exporter가 수식 셀을 바로 읽을 수 있도록 생성 수식과 cached value를 함께 저장하고 검증합니다.
- Battle과 Exit은 우클릭으로 만드는 별도 객체가 아니라 장면 노드의 `next_action` 상태입니다.
- 빈 캔버스 우클릭 메뉴에는 `장면 노드 추가`, `보상 노드 추가`만 표시합니다.
- 과거 레이아웃에 남아 있던 임시 Battle 객체는 그래프를 다시 그릴 때 정리합니다.
- Reward 노드는 독립 객체처럼 선택, 복사, 삭제, 이동됩니다. 클릭해도 앞 장면이나 선택지를 대신 선택하지 않습니다.
- 여러 선택지 branch가 같은 Reward 노드에 연결될 수 있습니다. 나중에 연결한 branch는 같은 reward type/amount와 reward 이후 경로를 사용합니다.
- Reward 노드의 출력은 다음 장면 또는 Exit 장면으로 연결합니다.
- `background`는 자동 기본값을 넣지 않습니다. 비어 있으면 validation error로 표시합니다.
- `background`에 섞인 Zero-Width Space 같은 보이지 않는 문자는 로드, 직접 입력, Export 시 자동 제거합니다.

## 프로젝트 구성

- `NexusEditor/`: 에디터 소스 폴더
- `NexusEditor.csproj`: WPF 프로젝트
- `bin/Release/net10.0-windows/Nexus Editor.exe`, `Nexus Editor.dll`: Release 빌드 산출물
- `NexusHubWindow.xaml`, `NexusHubWindow.xaml.cs`: 시작 모드 선택 허브
- `WorkspaceMode.cs`, `WorkspaceSwitchDialog.xaml`: 모드 전환과 미저장 변경 확인
- `MainWindow.xaml`, `MainWindow.xaml.cs`: Event Editor UI와 상호작용
- `EventWorkbookService.cs`: 엑셀 로드/저장, diff, validation, 자동 보정
- `Models.cs`: 이벤트, 장면, 선택지, 레이아웃 모델
- `PreviewWindow.cs`: Export Preview
- `EventInfoWindow.cs`: Tools > Event info 분석 창
- `ThemedMessageBox.cs`: Unity 스타일 확인 팝업
- `BackgroundImagePickerWindow.cs`: background 이미지 선택
- `ClickSoundCatalogService.cs`: DEV 전체 FMOD bank 색인, 경로 추론, 외부 WAV 캐시
- `ClickSoundPickerWindow.cs`: click_sound 검색, 미리듣기, 선택
- `NexusPathResolver.cs`: DB, 플레이어, 설정 경로 탐색
- `ResearchEditorWindow.xaml`, `ResearchEditorWindow.xaml.cs`: 독립 Research Editor UI
- `ResearchModels.cs`: 카테고리, 연구 노드, 연구 효과와 diff 모델
- `ResearchWorkbookService.cs`: 연구 워크북 쌍 로드/검증/diff/원자 저장
- `ResearchPathResolver.cs`, `ResearchPreferencesWindow.cs`: 연구 DB 쌍 탐색과 로컬 설정
- `ResearchExportPreviewWindow.cs`: 연구 변경 선택, Before/After, 연쇄 Revert와 Export
- `Tools/vgmstream/`: 선택한 bank subsong을 WAV로 디코딩하는 배포 도구
- `Assets/`: 앱 아이콘과 스플래시 리소스

## 실행 환경

- Windows
- .NET 10 SDK
- NuGet: `ClosedXML`

빌드:

```powershell
dotnet build .\NexusEditor.csproj -c Release
```

실행:

```powershell
dotnet run --project .\NexusEditor.csproj
```

실행 중인 앱 종료 후 Release 빌드 및 재실행:

```powershell
Get-Process 'Nexus Editor' -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\NexusEditor.csproj -c Release
Start-Process '.\bin\Release\net10.0-windows\Nexus Editor.exe'
```

## DB 탐색 순서

Event Editor는 다음 순서로 `nexus_event 차원 탐사 이벤트.xlsx`를 찾습니다.

1. `%APPDATA%\SuperCreative\NexusEditor\settings.json`에 저장된 `EventWorkbookPath`
2. 설정된 repos root 아래 `design\DB\alpha`
3. `D:\repos\design\DB\alpha`
4. `%USERPROFILE%\repos\design\DB\alpha`

상단 `Preference`의 `Asset Path` 탭에서 `DB Table Path`, `Client Path`, `Sound Cache Path`를 직접 설정할 수 있습니다. `DB Table Path`는 엑셀 파일 또는 해당 파일이 있는 폴더를 받을 수 있으며, 저장하면 즉시 새 DB를 로드합니다. `Sound Cache Path`는 DEV/SVN 밖의 폴더만 사용하며, 설정값은 로컬 settings에 저장됩니다.

배포 폴더의 `Data` 폴더는 자동 탐색 순서에 넣지 않습니다. DB 폴더가 우선입니다.

Research Editor는 아래 두 파일을 한 쌍으로 찾습니다.

- `nexus_out_system 차원_탐사_아웃시스템.xlsx`
- `nexus_effect 차원 탐사 효과.xlsx`

탐색 순서는 `%APPDATA%\SuperCreative\NexusEditor\research-settings.json`에 저장된 경로, 설정된 repos의 `design\DB\alpha`, `D:\repos\design\DB\alpha`, `%USERPROFILE%\repos\design\DB\alpha` 순서입니다. 두 파일은 서로 다른 폴더에 있어도 설정할 수 있습니다. 필수 시트를 실제로 읽는 데 성공한 뒤에만 새 경로를 기억하며, Research도 `Data` 폴더를 자동 선택하지 않습니다.

## Event Editor

이하 `테이블 구조`부터 `선택지 클릭 사운드`까지는 Event Editor의 데이터와 조작 규칙입니다. Research Editor는 뒤의 별도 장을 따릅니다.

## 테이블 구조

엑셀 DB의 모든 핵심 시트는 1-3행을 보존합니다.

- 1행: export/owner 메타데이터
- 2행: memo/comment
- 3행: 실제 필드 헤더
- 4행부터 데이터

### `nexus_event_base`

이벤트 1개당 1행입니다.

- `id`: 이벤트 ID. 예: `s1_evt_001`
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
- `bg_anim`: 장면 진입 시 한 번 재생할 배경 삽화 연출
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
- `click_sound`: 선택지를 눌렀을 때 재생할 FMOD event 경로
- `seq`: 선택지 표시 순서
- `choice_text`: 선택지 TID
- `cost_type`, `cost_amount`: 선택 비용
- `success_rate`: 성공 확률. 값이 있을 때 일반 선택지에 F 핀이 표시됩니다.
- `success_reward_type`, `success_reward_amount`: T 결과 보상
- `success_next_group_id`: T 결과 다음 장면
- `fail_reward_type`, `fail_reward_amount`: F 결과 보상
- `fail_next_group_id`: F 결과 다음 장면

### `텍스트`

- 에디터에서는 문장 안의 줄바꿈을 실제 줄바꿈으로 표시합니다.
- Export할 때 `Text` 컬럼의 실제 줄바꿈은 리터럴 `\n`으로 저장합니다.
- 다시 불러올 때 `Text` 컬럼의 리터럴 `\n`은 실제 줄바꿈으로 복원합니다.
- 이 변환은 `텍스트.Text` 컬럼에만 적용하며 DB 테이블의 memo 셀은 그대로 유지합니다.

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
- Reward 노드를 클릭하면 Reward 전용 Inspector가 열립니다.
- 여러 선택지 핀이 같은 Reward 노드에 연결될 수 있습니다.
- 기존 Reward 노드에 다른 선택지 핀을 연결하면, 연결한 branch에 reward type/amount와 reward 이후 경로가 복사됩니다.
- Reward 출력은 다음 장면 또는 Exit 장면으로 연결합니다.

### Battle 노드

Battle은 장면 노드 하나로 표현됩니다.

- Inspector에서 `next_action = Battle`을 선택합니다.
- `stage_id`가 필요합니다.
- Battle 장면에는 T/F 출력 핀이 항상 표시됩니다.
- Battle 결과는 `{battle_group_id}_battle_result` hidden choice row에 저장됩니다.
- Battle T 성공 보상은 선택 사항이며, 보상이 없어도 경고하지 않습니다.
- Battle 진입 전에는 반드시 도망/회피 선택지가 있어야 합니다.
- 우클릭 메뉴에는 Battle 노드 추가가 없습니다.

### Exit 장면

모든 이벤트는 Exit 장면으로 끝나야 합니다.

- `next_action = Exit`
- 보통 `{event_id}_g999_exit` ID를 사용합니다.
- Auto Layout에서 Exit 장면은 마지막 칼럼으로 밀립니다.
- Exit 장면에는 표시 선택지를 두지 않는 것이 원칙입니다.

## 주요 UX

- 앱은 실행 시 중앙 허브를 열고, 사용자가 선택한 편집기를 별도 창으로 시작합니다.
- Event Editor 진입 후 첫 이벤트가 선택되고 그래프가 즉시 렌더링됩니다.
- 상단 리모콘은 중앙에 위치하며 `▶`, `❚❚`, `■` 버튼을 사용합니다.
- 상단 Window 메뉴에서 `Event List`, `Scene`, `Hierarchy`, `Inspector`, `Console` 패널을 열고 닫을 수 있습니다.
- 상단 Tools 메뉴의 `Event info`에서 현재 DB의 보상, 비용, 난이도, 전투 정보를 표로 볼 수 있습니다.
- 닫은 패널과 크기는 로컬 설정에 저장됩니다.
- Console은 Unity 스타일 카운터 토글로 Noti/Warning/Error를 필터링합니다.
- Console 목록은 해결 우선순위에 맞춰 Error, Warning, Info 순서로 표시합니다.
- Console의 Error/Warning 항목을 클릭하면 해당 이벤트/장면/선택지 노드로 이동하고 하이라이트 애니메이션으로 위치를 알려줍니다.
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
- Export는 체크한 변경만 임시 저장 모델에 적용합니다. 체크하지 않은 변경은 현재 편집 상태에는 남지만 이번 엑셀 저장본에는 들어가지 않습니다.
- `Esc`로 Export Preview를 닫을 수 있습니다.

## Event info

`Tools > Event info`는 현재 열려 있는 이벤트 DB를 읽어서 밸런스 확인용 표를 만듭니다.

- `이벤트별 요약`: 이벤트별 장면 수, 선택지 수, 보상 수, 비용, 전투 여부를 봅니다.
- `보상 정보`: reward type별 횟수와 총량, 어느 이벤트에서 나오는지 봅니다.
- `비용 정보`: cost type별 횟수와 총량, 어느 이벤트에서 쓰이는지 봅니다.
- `이벤트 난이도`: 장면 수, 선택지 수, 확률 분기, 비용, 전투를 합쳐 복잡도를 봅니다.
- `전투 정보`: stage_id별 전투 연결 위치를 봅니다.
- `밸런스 체크`: 보상 없는 이벤트, 보상 과다 이벤트, 전투 포함 이벤트처럼 빠르게 확인할 항목을 봅니다.

위쪽 검색창은 현재 탭의 집계 표를 줄여서 보여줍니다. 아래쪽 검색창은 선택한 집계 행 안에서 세부 위치만 다시 줄여서 보여줍니다.

표에서 행을 선택하면 아래에 관련 이벤트와 노드 위치가 표시됩니다. 세부 위치를 더블클릭하면 해당 이벤트를 열고 Scene에서 그 노드를 하이라이트합니다.

별도 저장 버튼은 없습니다. 기존 `Export Preview`에서 적용하면 정보 표가 엑셀에 한글 시트로 함께 저장됩니다. 저장 후 시트 순서는 핵심 테이블, 정보 시트, `이벤트툴_레이아웃` 순서이며 레이아웃 시트는 항상 맨 끝에 둡니다.

## Runtime Player

상단 Play 버튼을 누르면 Godot Event Player 또는 실행 중인 Epic Seven DEV 클라이언트를 선택합니다.

- Godot은 현재 편집 중인 데이터를 임시 runtime workbook으로 저장한 뒤 `Dimension Exploration Event Player.exe`를 실행합니다.
- Epic Seven DEV는 선택한 클라이언트의 console에 `#ct:nexus_run_event( '<event_id>' )`를 전송하고 해당 게임 창을 맨 앞으로 올립니다.
- DEV가 여러 개 실행 중이면 창 제목, PID, 시작 시각을 보고 실행할 클라이언트를 선택합니다. STOVE 라이브 클라이언트는 목록에서 제외합니다.
- DEV 탐색은 `Preference > Asset Path > Client Path`에 저장한 DEV 루트를 우선 사용합니다. 해당 루트의 `game\bin\release` 아래 실행 파일을 동적으로 찾으므로 드라이브 문자, 사용자명, 설치 폴더가 달라도 됩니다.
- 기본 `ur.exe`와 `EpicSeven.exe`를 모두 지원합니다. 실행 파일 경로를 권한 문제로 읽지 못할 때는 DEV console 입력창이 실제로 있는 프로세스만 허용하므로 창 제목이 달라져도 찾을 수 있고 STOVE 라이브는 섞이지 않습니다.
- DEV console에 `nexus_run_event needs a server active run`이 출력되면 에디터 Console에 `차원 탐사 인게임에 진입한 상태에서 재생해야 합니다.` 오류를 함께 표시합니다.
- Godot 실행 중에는 DB 편집 UI를 잠급니다.
- Stop 또는 다시 Play를 누르면 플레이어를 종료하고 편집 UI를 복구합니다.
- Pause는 Godot 플레이어 프로세스를 suspend/resume합니다.

플레이어 탐색 순서:

1. settings의 `PlayerExePath`
2. 에디터 publish 폴더 주변의 `dimension_event_player_windows_release`
3. workbook 경로 상위 폴더의 `dimension_event_player_windows_release`

## 배경 이미지

장면의 `background`는 직접 입력하거나 picker 버튼으로 선택할 수 있습니다.

- 이미지 홈은 저장된 DEV 위치, 마지막 이미지 위치, 실행 중인 DEV 클라이언트, repos 후보 순서로 자동 탐색합니다.
- DB 또는 DEV 위치를 찾지 못하면 시작 시 `Preference > Asset Path`가 열립니다. `Client Path`에는 `dev` 폴더 또는 그 상위 `repos` 폴더를 지정할 수 있습니다.
- 선택한 DEV 위치는 settings의 `DevRoot`에 저장되며, 이미지 홈은 `{DevRoot}\game\Resources\res\nexus`를 사용합니다.
- Inspector 하단에 background preview가 표시됩니다.
- 이미지가 없으면 `Preview not found`로 표시됩니다.
- `background`가 비어 있으면 자동으로 기본 배경을 넣지 않고 validation error를 표시합니다.
- `background`의 Unicode format 문자(U+200B 등)는 로드 직후 제거되어 Export Preview 변경 내역에 표시되며, 저장 직전에도 다시 정리합니다.

## 배경 삽화 연출

장면 Inspector의 `bg_anim`은 목록에서 선택하거나 직접 값을 입력할 수 있습니다. 기본값은 `none`입니다.

- 카메라: `zoom_in`, `shake_light`, `shake_strong`, `walk_bob`, `look_left`, `look_right`, `look_up`, `look_down`
- UI 이펙트: `bright_pulse`, `dark_pulse`, `bad_end_darken`
- 목록에 없는 신규 연출 키도 편집형 입력란에 직접 작성할 수 있습니다.
- 구형 workbook에 `bg_anim` 열이 없으면 첫 export 때 `background` 다음 열로 추가합니다. 이미 열이 있으면 현재 위치를 찾아 그대로 읽고 씁니다.

## 선택지 클릭 사운드

선택지 Inspector의 `click_sound`는 직접 입력하거나 오른쪽 picker 버튼으로 선택할 수 있습니다.

- `{DevRoot}\game\Resources\res\sound` 아래의 모든 `.bank`를 재귀 탐색합니다. UI/SFX/보이스/BGM/언어 bank를 별도로 제외하지 않습니다.
- bank 내부 FSB5 이름 테이블에서 `stream name`, `bank`, `subsong`을 색인합니다.
- FMOD 경로는 `master.strings.bank`, DB 텍스트, 스토리 CSD, bank 종류를 함께 사용해 추론합니다. 경로를 추론하지 못한 항목도 stream 이름으로 남아 검색, 미리듣기, 선택할 수 있습니다.
- picker에서 FMOD 경로, stream 이름, bank 이름을 검색할 수 있습니다. Space 또는 `Play`로 미리듣고 `Select`로 `click_sound`에 넣습니다.
- 실제 미리듣기는 `vgmstream-cli.exe -s {subsong}`으로 선택한 음원만 WAV로 디코딩합니다.
- WAV와 sound catalog는 `Sound Cache Path`에 저장합니다. DEV, SVN, DB 폴더에는 생성하지 않습니다.
- 기본 캐시 위치는 `%LOCALAPPDATA%\SuperCreative\NexusEditor\SoundCache`이며, Preference에서 Z 드라이브 같은 외부 위치로 바꿀 수 있습니다. 기존 버전의 설정과 캐시는 처음 실행할 때 자동으로 이전합니다.
- 첫 실행에서 전체 WAV를 미리 만들지 묻습니다. `예`는 스플래시 화면에서 전체 추출 후 에디터를 열고, `아니요`는 각 음원을 처음 재생할 때만 캐시합니다.
- bank 크기나 수정 시간이 바뀌면 해당 음원의 캐시 키가 달라져 새 WAV를 생성합니다.

## Research Editor

Research Editor는 연구 카테고리, 연구 노드, 그 노드에 대응하는 연구 효과를 하나의 독립된 작업 공간에서 편집합니다. Event Editor의 이벤트 모델이나 Undo 기록은 사용하지 않습니다.

### 편집 테이블

`nexus_node_category`에서는 다음 값을 다룹니다.

- `category`: 카테고리 ID입니다. Inspector에서 직접 바꿀 수 있으며, 적용 전에 영향 범위를 경고로 확인합니다. 변경하면 helper prefix/category key 조합도 새 ID와 일치하도록 정리됩니다.
- `export_id`: 변경 행을 내보낼 작업자 ID
- `helper prefix`, `category key`: 카테고리 ID의 구성 요소
- `index`: 카테고리 정렬 순서
- `node_name`: category key로 자동 생성되는 이름 TID

`nexus_node`에서는 다음 값을 다룹니다.

- `id`: `{theme_id}_node_{category}_{column}_{row}` 규칙으로 자동 생성
- `theme_id`, `category`, `image`, `node_permission`: 노드별로 직접 바꿀 수 있으며, 카테고리를 옮기면 관련 ID와 참조도 함께 갱신됩니다. `theme_id`는 `s1`에 고정하지 않고 소문자 ID 규칙을 따릅니다.
- `active_item_id`, `active_item_value`, `active_step`
- `column`, `row`: 그래프 좌표. row는 1~8을 사용합니다. row 1이 아래쪽이고 숫자가 커질수록 위로 올라가며, row 4의 카드 중심이 화면의 시각적 중앙입니다. 세로 위치는 `row 2 = 슬롯 1`, `row 3 = 슬롯 1.5`, `row 4 = 슬롯 2`처럼 숫자가 1 늘 때마다 반 슬롯씩 이동합니다. 하나의 STEP에는 최대 4개 노드만 둘 수 있습니다.
- `condition_node_1`~`condition_node_5`: 앞에서 열려 있어야 하는 노드 ID
- `category_name`, `node_effect_desc`, `nexus_effect_id`: 수식과 ID 규칙으로 자동 관리

연결된 `nexus_effect` 연구 행에서는 `export_id`, `parent_effect`, 메모, `type`, `condition`, `value`와 나머지 원본 칼럼을 편집합니다. Research Editor는 연구 노드가 참조하는 효과 행만 다루며, 같은 파일의 다른 효과 데이터는 보존합니다.

### 그래프와 Inspector

- 왼쪽에서 카테고리를 선택하면 해당 카테고리의 노드만 Scene과 Hierarchy에 표시됩니다.
- `node_permission` 필터는 현재 카테고리에 실제로 존재하는 권한 번호를 동적으로 표시합니다. 숨긴 노드는 선택 상태에서도 빠집니다.
- STEP 위의 `PERMISSION N` 막대는 해당 권한 구역이 차지하는 STEP 범위를 보여줍니다. 경계 화살표로 STEP을 옆 구역에 넘기거나 가져올 수 있습니다.
- 마지막 `+ 권한 구역` 버튼은 마지막 구역의 STEP 하나를 새 권한 구역으로 분리합니다. 마지막 구역의 `-` 버튼은 그 구역을 앞 구역에 합칩니다. 권한 번호는 1부터 필요한 만큼 계속 만들 수 있으며 구역마다 STEP을 하나 이상 남깁니다.
- 가장 오른쪽의 `+ STEP` 버튼은 마지막 STEP 형식을 바탕으로 유효한 노드·효과·자동 ID를 만들고, 직전 STEP에서 이어지는 조건을 연결합니다. 새 STEP은 마지막 권한 구역에 들어갑니다.
- 노드를 클릭하면 Inspector에서 노드와 연결된 효과를 함께 편집합니다. 클릭만으로 Scene이 자동 이동하지 않으며, 선택 노드를 화면 중앙에서 찾으려면 `F`를 누릅니다.
- 연구 노드를 좌클릭한 채 위아래로 끌면 같은 STEP 안에서 `row 1~8` 중 가장 가까운 칸으로 이동합니다. 여러 노드가 선택된 상태에서 그중 하나를 끌면 선택 노드가 같은 row 간격으로 함께 움직입니다. 이미 다른 노드가 있는 칸이거나 범위를 벗어나는 이동은 적용하지 않으며, 이동 전체를 `Ctrl+Z` 한 번으로 되돌릴 수 있습니다.
- 노드를 한 개 선택하면 카드 옆에 `빠른 노드 편집` 창이 열립니다. `노드 설정` 탭에서는 자주 바꾸는 `image`, `active_item_id`, `active_item_value`, `active_step`을 수정하고, `효과 설정` 탭에서는 연결된 `nexus_effect`의 Export ID, 타입, 조건, 값과 나머지 원본 칼럼을 바로 수정합니다. `memo`는 `type + condition + value`가 같은 기존 연구 효과 문구를 우선 찾아 현재 카테고리·권한 구역·좌표에 맞게 자동 작성합니다. 연결 효과가 없는 노드는 같은 탭에서 효과 행을 생성할 수 있으며, Inspector에는 전체 칼럼이 계속 표시됩니다.
- 비어 있는 노드 슬롯을 짧게 우클릭한 뒤 `연구 노드 추가`를 누르면 선택한 `STEP / row` 좌표에 정확히 새 노드와 연결 효과를 만듭니다. 빈 슬롯 좌클릭은 노드를 만들지 않고 STEP 블록 선택에만 사용합니다. 주변 노드의 연결은 자동으로 바꾸지 않습니다.
- 연구 노드를 우클릭하면 해당 노드만 삭제할 수 있습니다. 다중 선택 중에는 선택한 노드 전체를 삭제하며, 참조 중인 조건선과 연결 효과만 함께 정리합니다.
- 기존 자동 연결은 그대로 유지됩니다. 직접 연결을 편집할 때는 출력 핀을 좌클릭한 채 드래그해 바로 다음 STEP의 입력 핀에 놓습니다.
- 입력 핀을 우클릭하면 해당 노드로 들어오는 연결을, 출력 핀을 우클릭하면 해당 노드에서 나가는 연결을 해제합니다.
- 이 조작은 기존 condition 값만 편집하며 DB 스키마와 Export 형식은 바꾸지 않습니다.
- DEV의 `roadmap_line_*.png` 경로 리소스는 9-slice로 그려 길이가 달라져도 선과 모서리 두께가 일정합니다.
- 마우스 휠은 확대/축소, 우클릭 드래그는 이동입니다. 연구 노드 위에서 시작해도 우클릭 드래그가 우선하며, 움직이지 않고 우클릭을 놓았을 때만 노드 삭제 메뉴가 열립니다. Scene의 빈 곳이나 빈 슬롯에서 좌클릭 드래그하면 선택 사각형과 겹친 STEP 블록을 한꺼번에 선택합니다. `Ctrl+클릭`으로 STEP 선택을 더하거나 뺄 수도 있습니다.
- STEP 블록을 하나 이상 선택하고 `Ctrl+C`를 누르면 묶음으로 복사합니다. 같은 수의 대상 STEP을 선택하거나 붙여넣기 시작 STEP 하나를 선택한 뒤 `Ctrl+V`를 누르면 순서대로 붙여넣습니다. 대상 STEP이 부족하면 오른쪽 `+ STEP`으로 먼저 늘립니다. 선택한 STEP 블록에서 `Delete`를 누르면 블록 안의 연구 노드를 한 번에 삭제하며, `Ctrl+Z` 한 번으로 모두 되돌릴 수 있습니다.
- 연구 노드 선택에는 기존 `Ctrl+C`, `Ctrl+V`, `Delete`, `Ctrl+Z`, `F`를 그대로 지원합니다. Inspector 입력칸에 포커스가 있으면 글자 편집 단축키를 가로채지 않습니다.
- 여러 노드를 함께 옮길 때 최종 좌표, STEP당 최대 4개, row 1~8 범위를 먼저 검사한 뒤 ID·effect ID·condition을 한 번에 갱신합니다.
- Console은 Scene 아래 분리선으로 높이를 조절할 수 있으며 최소 높이를 유지합니다. Hierarchy와 Inspector도 각각 독립적으로 폭을 조절하고 Window 메뉴에서 다시 열 수 있습니다.
- Scene 오른쪽 분리선은 우측 도크 전체 폭을, Hierarchy와 Inspector 사이 분리선은 두 패널의 상대 폭만 조절합니다. 패널을 닫으면 최소 폭과 분리선까지 함께 접히며, 다시 열면 마지막으로 사용한 폭을 복원합니다.
- 우클릭 메뉴로 만든 슬롯 노드와 우클릭으로 삭제한 노드는 `Ctrl+Z`로 되돌릴 수 있습니다.

### 자동 ID와 참조 갱신

- category, category key 또는 helper prefix를 바꾸면 해당 카테고리의 node ID, effect ID, TID, 모든 condition 참조와 `parent_effect` 참조를 한 작업으로 함께 바꿉니다. 실제 변경 전에는 경고 창이 한 번 표시되며, 적용 뒤에도 `Ctrl+Z`로 되돌릴 수 있습니다.
- 노드의 category, column, row, theme를 바꾸면 node ID와 effect ID, 이 노드를 가리키는 condition을 함께 바꿉니다.
- 현재 DB처럼 노드 좌표와 효과 ID 좌표가 한 칸 어긋난 레거시 데이터도 category, column, 정렬 순서로 안정적으로 짝지어 표시합니다. 사용자가 해당 노드를 이동하거나 카테고리를 바꿀 때만 새 좌표 규칙으로 효과 ID를 정규화합니다.
- 노드 ID는 소문자 영문, 숫자, 밑줄 규칙으로 정리됩니다.
- ID를 바꾸는 연쇄 작업은 Export Preview에서도 하나의 묶음으로 Revert됩니다.
- 참조 중인 노드나 노드가 남은 카테고리는 실수로 단독 삭제되지 않습니다. 연쇄 삭제를 선택해야 관련 참조를 함께 정리합니다.

### Research 검증

항상 확인하는 항목은 다음과 같습니다.

- 필수 값, 소문자 ID, 중복 category/index/node/effect ID
- 카테고리 안의 중복 좌표와 `node_permission` 1 이상 여부
- `active_item_value` 형식과 수량 합계
- 필수 이미지
- condition 최대 5개, 같은 카테고리, 앞 column 연결, 순환 참조 금지
- 노드당 대응 연구 효과 정확히 1개, 고아 연구 효과 금지
- 자동 수식과 생성 ID 일치

원본 DB에 이미 있던 오류는 Console에 표시합니다. Export는 편집 때문에 새로 생기거나 개수가 늘어난 오류를 차단하며, 기존 오류를 몰래 일괄 보정하지 않습니다.

### Research Export

- Export Preview는 category, node, effect 변경을 Before/After로 보여줍니다.
- 체크한 변경만 실제 저장용 복사본에 적용합니다. 체크하지 않은 변경은 에디터에는 남고 이번 저장본에서는 원본 값으로 돌아갑니다.
- 부분 Export 뒤에는 저장된 행만 새 기준으로 다시 연결합니다. 체크하지 않은 변경과 Undo 기록은 그대로 남아 다음 Export에서 이어서 작업할 수 있습니다.
- 카테고리/좌표 변경처럼 ID가 연쇄 변경된 행은 하나를 Revert해도 관련 category, node, effect, condition을 함께 되돌립니다.
- `nexus_out_system`과 `nexus_effect`를 먼저 임시 파일에 쓰고 모두 검증한 뒤 대상 파일을 교체합니다.
- 대상 파일이 있으면 `<파일명>.backup.yyyyMMdd_HHmmss.xlsx` 백업을 만듭니다.
- 두 번째 파일 교체가 실패하면 첫 번째 파일도 복구하며, 복구에 실패한 파일이 있으면 복구본을 삭제하지 않습니다.
- `nexus_out_system`의 자동 수식 셀은 수식과 cached value를 함께 기록합니다. 저장 직후 XML 캐시값을 다시 읽어 Ruby exporter가 바로 사용할 값을 확인합니다.
- `nexus_out_system`에서는 `nexus_node_category`, `nexus_node`만 편집합니다. 그 밖의 시트는 원본 수식과 cached value를 복원한 뒤 모든 데이터 셀을 원본과 비교하며, 하나라도 달라지면 대상 파일을 교체하기 전에 Export를 중단합니다.
- `nexus_effect`에서는 연구 노드에 연결된 `nexus_effect` 행만 편집합니다. 같은 워크북의 다른 시트 데이터도 동일한 방식으로 원본과 비교해 보존합니다.
- 로드한 뒤 다른 프로그램에서 원본 파일이 바뀌었다면 Export를 중단하고 Reload를 안내합니다. 오래된 화면 상태로 동료나 Excel의 변경을 덮어쓰지 않습니다.

## QA와 검증

QA export:

```powershell
dotnet '.\bin\Release\net10.0-windows\Nexus Editor.dll' --qa ".\Data\nexus_event 차원 탐사 이벤트.xlsx" "$env:TEMP\nexus_editor_qa.xlsx"
```

정상 기준:

```text
QA diff entries: 0
QA reload errors: 0
```

Research DB 쌍 왕복 QA:

```powershell
dotnet '.\bin\Release\net10.0-windows\Nexus Editor.dll' --research-qa `
  'D:\repos\design\DB\alpha\nexus_out_system 차원_탐사_아웃시스템.xlsx' `
  'D:\repos\design\DB\alpha\nexus_effect 차원 탐사 효과.xlsx'
```

네 번째 인자로 임시 폴더를 주면 `pass1`, `pass2` 산출물을 지우지 않고 남겨 Ruby exporter 호환성을 직접 확인할 수 있습니다.

```powershell
dotnet '.\bin\Release\net10.0-windows\Nexus Editor.dll' --research-qa '<out-system.xlsx>' '<effect.xlsx>' "$env:TEMP\NexusEditorResearchQa"
ruby -r roo -e "b=Roo::Excelx.new(ARGV[0]); s=b.sheet('nexus_node'); puts [s.cell(4,1),s.cell(4,20)].join(' | ')" "$env:TEMP\NexusEditorResearchQa\pass1\nexus_out_system 차원_탐사_아웃시스템.xlsx"
```

정상 기준:

```text
baseline/reload errors: 0 / 0
first-pass differences: 0
second-pass differences: 0
newly introduced errors: 0
mutation failures: 0
```

2026-07-20 실제 DB 검사에서는 Event DB를 두 번 연속 왕복해 두 차례 모두 diff 0, reload error 0을 확인했습니다. Research DB는 6 categories, 1,320 nodes, 1,320 effects를 읽었고, baseline/reload error 0/0, 1차/2차 diff 0, 신규 오류 0, mutation failure 0을 확인했습니다. 아웃시스템 원본/1차/2차의 수식 5,604개와 효과 파일 비대상 시트의 수식 2개도 cached value 누락 0개였습니다. 변이 QA에는 카테고리·노드·효과 생성/이동/삭제, STEP 블록 복사/붙여넣기, 연쇄 Revert, 부분 Export 후 미선택 변경·Undo 보존, 외부 파일 변경 충돌 차단이 포함됩니다. 이 숫자는 DB가 바뀌면 달라질 수 있으므로 성공 여부는 위 조건으로 판단합니다.

전체 FMOD 색인과 디코더 확인:

```powershell
dotnet '.\bin\Release\net10.0-windows\Nexus Editor.dll' --sound-probe "D:\repos\dev" "$env:TEMP\NexusEditorSoundProbe" --decode-first
```

현재 DEV 검증 결과는 62개 bank에서 96,469개 sound를 색인했고, 첫 항목 WAV 디코딩까지 성공했습니다. 두 번째 catalog 로드는 외부 색인을 재사용합니다.

스킬 패키지의 검증 스크립트:

```powershell
python C:\Users\lbh9517\.codex\skills\dimension-exploration-event-designer\scripts\validate_nexus_event_tables.py --event-file "C:\Users\lbh9517\Downloads\nexus_event 차원 탐사 이벤트.xlsx"
```

## 이벤트 작성 스킬

차원 탐사 이벤트 작성용 Codex 스킬은 다음 위치에 둡니다.

```text
C:\Users\lbh9517\.codex\skills\dimension-exploration-event-designer
```

배포용 zip:

```text
C:\Users\lbh9517\OneDrive - Super Creative\EpicSeven - 문서\기획실\2_코어시스템팀\1. 이병연\1_작업중\175126 260917 신규 PVE 전투\차원탐사_skill\dimension-exploration-event-skill.zip
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
$publishDir = "C:\Users\lbh9517\OneDrive - Super Creative\EpicSeven - 문서\기획실\2_코어시스템팀\1. 이병연\1_작업중\175126 260917 신규 PVE 전투\nexus_editor_windows_release"
Get-Process 'Nexus Editor' -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish .\NexusEditor.csproj -c Release -r win-x64 --self-contained true -o $publishDir
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
