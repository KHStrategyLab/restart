# KHStrategyLab Architecture Guide

작성 기준: 2026-05-25 / `Codex` 브랜치

이 문서는 개발자용 구조 문서다. 프로그램 시작 흐름, REST/WebSocket, 저장소, 그리드, 차트, 경량엔진, 주문 레이어, 종료 정리를 설명한다. 전략 판단 기준은 `KHStrategyLab_Strategy_Manual.md`를 본다.

---

## 1. 전체 구조

```text
WPF MainWindow
↓
Config / Storage / Logger
↓
Kiwoom REST Auth
↓
Account / StockInfo / Index / Chart REST
↓
Condition WebSocket
↓
Realtime 0B Router
↓
Grid 표시 + MinuteCache + Strategy Signal
↓
RiskGuard / LiveOrder Layer
↓
Telegram / Logs / Local Storage
```

현재 구현은 `MainWindow` partial class를 기능별 파일로 나눈 구조다.

```text
MainWindow.Api.*.cs
MainWindow.WebSocket.*.cs
MainWindow.Order.*.cs
MainWindow.Strategy*.cs
MainWindow.Storage.cs
MainWindow.Timers.cs
MainWindow.Cleanup.cs
```

---

## 2. 시작 흐름

`MainWindow.xaml.cs` 생성자 기준 흐름:

```text
InitializeComponent
→ Data/Storage 폴더 생성
→ LoadConfig
→ LoadWatchCandidates
→ LoadLiveOrderState
→ Grid ItemsSource 연결
→ MA 신호 컬럼 초기화
→ InitializeTimers
→ InitializeTrayIcon
→ Loaded 이벤트에서 AutoLoginAsync
```

`AutoLoginAsync` 이후:

```text
키움 토큰 발급
→ 계좌번호 확인
→ InitializeConditionWebSocketAsync
→ 조건00 후보 시장 재검증
→ TOP20 1회 조회
```

---

## 3. REST API 영역

주요 REST 기능:

```text
oauth2/token        토큰 발급
kt00005             잔고조회
ka10074             일자별 실현손익
ka10001             주식기본정보
ka10080             분봉
ka10081             일봉
ka10100             NXT/SOR 가능 여부
0198 계열           실시간 종목조회순위 TOP20
kt10000/kt10001     매수/매도 주문
```

REST 호출은 대부분 `_http` 또는 전용 static `HttpClient`를 사용한다. 응답 원문 저장은 과도한 파일 증가를 막기 위해 일반 Raw 저장은 비활성이고, 주문 원문은 `RawLogs`에 별도 저장한다.

---

## 4. WebSocket 구조

조건/실시간 WebSocket은 `MainWindow.WebSocket.Condition.cs`에서 초기화한다.

안정화 기준:

```text
중복 연결 초기화 방지
기존 MessageReceived/Reconnection 구독 정리
기존 WebsocketClient Dispose 후 재생성
종료 중 재연결/LOGIN 차단
```

수신 메시지 흐름:

```text
PING     → echo 전송
LOGIN    → 인증 성공 후 장상태/0B/조건목록 요청
CNSRLST  → 조건목록 처리
CNSRREQ  → 조건검색 결과 처리
REAL/0B  → 시장상태 갱신 + 실시간 체결 처리
```

0B 등록 대상은 보유종목과 조건00 추적 후보 중심이다. TOP20 전용 종목은 조회 화면 전용이라 0B 등록 대상에서 제외한다.

---

## 5. 저장소 구조

실행 폴더 기준:

```text
Storage/
  CandidateUniverse/
  program_live_order_state.json
  market_holidays.json

Data/
Logs/
RawLogs/
```

Git 제외 대상:

```text
config.json
Storage/
Data/
Logs/
RawLogs/
*.db
*.sqlite
```

후보 저장은 조건00 추적 후보 기준이다. 후보는 6거래일 보관하고 7거래일차에 삭제/만료 기록을 남긴다.

---

## 6. 그리드 구조

주요 그리드:

```text
GridLeading  조건00 추적 후보
GridBalance  보유종목
GridRank     TOP20
```

각 행은 `StockGridRow` 또는 파생 클래스이며 `INotifyPropertyChanged`로 값 변경을 화면에 반영한다.

선택 안정화 기준:

```text
TOP20 갱신 시 Clear/Add 금지
기존 RankStock 행 객체를 유지하고 값만 업데이트
실시간 체결/종목정보/MA 신호 갱신 때 불필요한 전체 Refresh 금지
자동 선택 복원 중 SelectionChanged 차트 재조회 차단
ScrollIntoView/Keyboard.Focus 강제 호출 금지
```

---

## 7. 차트 구조

차트는 `MainWindow.Api.Placeholders.cs`에 구현되어 있다.
실시간 진행봉 표시는 `MainWindow.ChartRealtime.cs`에 분리되어 있다.

일봉:

```text
RequestDailyChartCandlesAsync
→ DailyChartStoreCandleCount = 180
→ DailyChartVisibleCandleCount = 100
→ DrawDailyChartCanvas
```

분봉:

```text
RequestMinuteChartCandlesForStrategyAsync
→ ka10080 / tic_scope=5,10,30
→ MinuteChartStoreCandleCount = 1000
→ MinuteChartVisibleCandleCount = 1000
→ DrawDailyChartCanvas 재사용
```

분봉 버튼:

```text
일봉 / 5분 / 10분 / 30분
```

실시간 차트 표시:

```text
기존 WebSocket 0B 주식체결 수신 재사용
신규 WebSocket 연결 없음
0B 값 중 체결시간(20), 현재가(10), 체결량(15)을 차트 진행봉 갱신에 사용
현재 화면에 열린 차트 종목 1개만 갱신
5분봉/10분봉/30분봉 상태에서만 갱신
일봉 상태에서는 실시간 차트 갱신 금지
차트용 진행봉과 이평선은 표시 전용
차트 계산값은 전략/주문/MinuteCache 판단으로 역류 금지
```

차트 하단 회전율은 `ka10001` 종목정보를 함께 조회해 표시한다.

---

## 8. 경량엔진 구조

경량엔진은 차트를 계속 다시 그리는 구조가 아니라, 후보별 숫자 캐시를 유지하고 필요한 값만 갱신하는 구조다.

현재 연결 흐름:

```text
조건00 후보 편입
→ KRX/NXT 시장분리
→ CandidateMinuteCache 생성/로드 예약
→ ka10080으로 10분봉/5분봉 seed 로드
→ 0B 현재가로 현재봉 갱신
→ MA5/20/60, High20 계산
→ GridLeading 숫자/신호 표시
→ 전략 상태머신 점검
→ LiveOrderSignal 생성
→ 주문 레이어 전달
```

경량엔진의 구조 원칙:

```text
TOP20 리소스보다 조건00 후보 판단 우선
0B 틱마다 REST 분봉 재조회 금지
0B 틱마다 전체 그리드 Refresh 금지
후보 행 위치 유지
KRX/NXT 분봉 캐시 분리
READY 전 매수 판단 차단
```

경량엔진의 세부 판단 기준은 전략 문서에 둔다.

---

## 9. 주문 레이어

주문 레이어는 전략 신호를 바로 주문으로 보내지 않고 리스크가드를 먼저 통과시킨다.

흐름:

```text
Strategy Signal
→ TryExecuteLiveBuyAsync
→ EvaluateLiveBuyRiskGuard
→ SendKiwoomOrderAsync
→ SaveLiveOrderState
→ SyncBalanceAfterOrderAsync
```

차단 기준:

```text
LiveOrderEnabled=OFF
시장 미확정
비정상 가격/수량
보유 중복
슬롯 초과
같은 날 같은 전략 재주문
주문 진행 중 중복
```

매도는 프로그램이 관리하는 포지션의 목표가/손절가를 기준으로 별도 모니터가 감시한다.

---

## 10. 종료 정리

완전 종료 시 `CleanupApplicationResources`를 호출한다.

정리 대상:

```text
UI/전략/계좌/지수/실현손익/포지션 타이머
조건 WebSocket 구독
조건 WebSocket 클라이언트
트레이 아이콘
HttpClient
```

종료 중에는 `_isShuttingDown=true`로 두어 WebSocket 재연결과 LOGIN 재전송을 막는다.

