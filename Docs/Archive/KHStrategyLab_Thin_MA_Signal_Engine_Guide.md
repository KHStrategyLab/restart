# KHStrategyLab Thin MA Signal Engine Guide

## 목적

이 문서는 Thin 브랜치 기준으로 `조건00 추적/전략 후보`에 10분봉 MA5/MA20/MA60 신호등, 10분 60선 이탈·회복 상태머신, 5분봉 20봉 신고가 매수후보 로직을 붙이기 위한 기준 문서다.

핵심 목표는 실시간 TOP20에 쓰던 리소스를 줄이고, 조건00 추적후보의 매수 판단과 화면 신호 표시 쪽에 리소스를 집중하는 것이다.

---

## 역할 분리

### TOP20

TOP20은 조회/화면 표시용이다.

```text
TOP20 실시간 0B 등록 사용 안 함
TOP20 가격 실시간 보정 사용 안 함
TOP20은 MA 신호등, 5분봉 신고가 감시, 텔레그램 매수신호로 연결하지 않음
```

### 조건00

조건00은 저장/추적/전략 후보 역할이다.

```text
조건00 편입 종목 → _search00List 표시
조건00 후보 → _watchCandidates에 6일 보관
조건00 후보 → KRX/NXT 시장분리 후 각각의 분봉 기준으로 추적
조건00 후보 → 0B 실시간 체결 유지
조건00 후보 → 10분봉 MA5/MA20/MA60 숫자와 신호등 표시
조건00 후보 → 상태 흐름 통과 후 5분봉 20봉 신고가 돌파 시 텔레그램 매수신호
```

### 조건01

현재 Thin 기준에서는 조건01은 비활성이다.

```text
조건01은 저장/추적/전략 후보에서 제거
전략 후보 통로는 조건00으로 통합
```

---

## KRX/NXT 분봉 데이터 기준

### 시장분리 실패 처리

`ka10100` 조회 실패는 KRX 확정이 아니다. API 예외, 응답 없음, 파싱 실패, 네트워크 오류, 토큰/서버 일시 오류, 동시성 오류, 필드 누락, 알 수 없는 응답은 모두 시장확정 실패로 처리한다.

```text
ka10100 정상 응답 + NXT 지원 명확 → NXT 확정
ka10100 정상 응답 + NXT 미지원 명확 → KRX 확정
ka10100 실패/불명확 응답 → 기존 StrategyMarket 유지 또는 시장확정 보류
```

기존 후보에 `StrategyMarket=NXT`, `MinuteChartMarket=NXT`, `RealtimePriceMarket=NXT`, `DisplayMarket=NXT`, `NxtEnabled=true` 같은 저장값이 있으면 조회 실패 시에도 NXT를 유지한다. 기존 NXT 후보를 실패 경로에서 KRX로 덮지 않는다. 기존 KRX 후보도 조회 실패 시 KRX를 새로 확정하는 것이 아니라 저장된 KRX 값을 유지하는 흐름으로만 다룬다.

신규 조건00 후보가 시장확정에 실패했고 기존 시장값도 없으면 `PENDING` 상태로 둔다. 이 상태에서는 `MinuteCache` 로드, MA 신호등 초기 로드, 전략 상태머신 판단, 텔레그램 매수신호를 모두 차단한다. 재시도는 `MarketResolveStatus=RETRY_WAIT`, `LastMarketResolveAttemptAt`, `MarketResolveRetryCount`로 추적하며, 재시도 중에도 기존 시장값이 있으면 보존한다.

위험한 fallback은 금지한다.

```text
ka10100 실패 → KRX 확정 금지
ka10100 실패 → NxtEnabled=false 강제 금지
ka10100 실패 → StrategyMarket=KRX 강제 금지
ka10100 실패 → KRX 6자리 코드로 ka10080 분봉 로드 금지
```

### KRX

KRX 후보는 일반 6자리 종목코드로 `ka10080` 분봉을 조회한다.

```text
stk_cd = "039490"
tic_scope = "10" 또는 "5"
```

### NXT

NXT 후보는 종목코드 뒤에 `_NX`를 붙여 `ka10080` 분봉을 조회한다.

```text
stk_cd = "039490_NX"
tic_scope = "10" 또는 "5"
```

NXT 후보에서 KRX 6자리 코드로 fallback하면 KRX 분봉이 섞일 수 있으므로, Thin MA 신호 엔진에서는 NXT는 `_NX`만 사용한다.

---

## 공통 분봉 캐시

조건00 후보의 10분봉/5분봉은 `종목코드|시장` 키로 공통 캐시에 저장한다.

```text
062970|KRX
356680|NXT
```

캐시는 KRX/NXT를 서로 다른 데이터로 취급한다. KRX 후보는 일반 6자리 코드로 `ka10080`을 요청하고, NXT 후보는 반드시 `종목코드_NX`로 요청한다. NXT `_NX` 요청이 실패해도 임의로 KRX 분봉으로 바꾸지 않는다.

공통 캐시에는 최소 아래 정보를 둔다.

```text
Code
Market
RequestCode10m
RequestCode5m
IsSeedReady
LoadStatus
LoadedAt
LastLoadAttemptAt
LastRealtimeAt
TenMinuteCompletedCandles
TenMinuteCurrentCandle
FiveMinuteCompletedCandles
FiveMinuteCurrentCandle
TenMinuteCompletedCloses
FiveMinuteCompletedCloses
FiveMinuteCompletedHighs
Ma5_10m
Ma20_10m
Ma60_10m
PrevMa5_10m
PrevMa20_10m
Ma20_5m
High20_5m
```

MA 신호등과 매수전략 상태머신은 같은 캐시를 공유한다.

```text
초기/복구/재연결/수동 새로고침/장마감 검증 → REST로 캐시 로드 허용
MA 신호등 표시 → 캐시 사용
60초 전략 점검 루프 → 캐시 사용
0B 실시간 수신 → 캐시의 현재 10분봉/5분봉 Close/High/Low 갱신
```

전략 점검 루프에서는 후보별 REST 재조회를 하지 않는다. 캐시가 `READY`가 아니면 매수 판단, 텔레그램 신호, 주문 연결은 모두 차단하고 분봉 로드만 예약한다.

```text
WAIT_MINUTE_LOAD 또는 LOAD_FAILED → 매수 판단 차단
READY → 완성봉 기준으로 상태머신 점검
```

신호등은 현재봉을 포함한 참고값을 표시할 수 있지만, 매수전략 확정 신호는 완성봉 기준 상태 흐름을 우선 사용한다. 공통 캐시와 전략별 상태값은 섞지 않는다.

---

## ka10080 응답 정렬 기준

`ka10080` 분봉 응답 리스트 `stk_min_pole_chart_qry`는 최신순이다.

```text
bars[0] = 최신봉
bars[1] = 바로 이전 봉
bars[-1] = 가장 오래된 봉
```

내부 계산에서는 반드시 과거에서 최신 순서로 정렬한다.

```text
API 응답: 최신 → 과거
내부 캐시: 과거 → 최신
```

---

## 현재봉 처리 기준

장중에 `ka10080`을 호출하면 최신봉은 미완성 현재봉일 수 있다.
따라서 내부에서는 아래처럼 나눈다.

```text
확정봉 배열 = 현재 10분/5분 버킷보다 이전 봉
현재봉 = 0B 현재가로 계속 갱신되는 진행 중 봉
```

화면 신호등은 현재봉 포함 실시간 참고값이다.

```text
10분봉 MA60 표시 = 최근 확정봉 59개 + 0B 현재가
10분봉 MA20 표시 = 최근 확정봉 19개 + 0B 현재가
10분봉 MA5 표시 = 최근 확정봉 4개 + 0B 현재가
```

전략 매수 판단은 봉완성 기준을 우선 사용한다.

```text
10분봉 MA 조건 = 마지막 확정 10분봉 기준
5분봉 20봉 신고가 = 완료된 5분봉 20개의 고가 기준 + 현재가 돌파
```

---

## 초기 로드 게이트

0B는 현재가만 준다. 0B 자체가 이평선 데이터를 주는 것은 아니다.
그러므로 프로그램 시작, 장중 재시작, 인터넷 끊김 후 재연결, WebSocket 재접속 뒤에는 후보별 분봉 데이터를 1회 REST로 다시 로드해야 한다.

```text
초기 1회 로드 필요 데이터
- 10분봉 최소 60개 이상
- 5분봉 최소 20개 이상
```

초기 로드 전에는 매수 판단을 차단한다.

```text
분봉 로드 성공 전 → 화면은 로딩/대기, 매수신호 차단
분봉 로드 성공 후 → MA 신호등 표시, 상태 추적, 5분봉 신고가 감시, 매수후보 작동
```

장중 0B가 일정 시간 끊겼다가 다시 들어오면 내부 봉 배열이 실제 시장과 어긋날 수 있으므로 다시 분봉 로드를 예약한다.

---

## MA 신호등 기준

신호등은 매수신호가 아니라 현재 10분봉 이평 배열 상태를 보여주는 계기판이다.
신호등은 후보 순위를 바꾸지 않는다. 기존 행 위치를 유지한 채 표시만 갱신한다.

```text
강     = MA5 > MA20 > MA60
가능   = MA5 > MA20 && MA5 > MA60
약     = MA60 > MA5 && MA60 > MA20
관망   = 나머지 전부
```

예시:

```text
MA5 > MA60 && MA20 > MA60 && MA5 < MA20 → 애매한 상태이므로 관망
```

---

## 매수전략 상태머신 기준

단순히 현재 순간의 녹색 신호와 5분봉 신고가만으로는 매수신호를 내지 않는다.
반드시 기준봉 이후 눌림과 회복 흐름을 통과해야 한다.

필수 흐름은 아래와 같다.

```text
기준봉 발생
→ 다음날 또는 며칠 뒤 눌림 대기
→ 10분봉 60선 아래로 이탈 확인
→ 다시 10분봉 60선 회복
→ 10분 5선이 20선과 60선을 위로 돌파
→ 10분봉 녹색 조건
→ 5분봉 20봉 신고가 돌파
→ 현재가 > 기준가
→ 초기 매수신호
```

후보 JSON에는 아래 상태값을 저장한다.

```text
BuyStage
HasBrokenBelowMa60
BrokenBelowMa60At
HasRecoveredMa60
RecoveredMa60At
HasGreenReady
GreenReadyAt
BuyStageChangedAt
BuyStageMemo
```

상태 단계는 아래처럼 본다.

```text
WAIT_PULLBACK       = 기준봉 이후 눌림 대기
BELOW_10M_MA60      = 10분 60선 아래 이탈 확인
RECOVERED_10M_MA60  = 10분 60선 회복 확인
GREEN_READY         = 10분 5선이 20선과 60선 위로 올라온 상태
BUY_SIGNAL          = 최종 트리거 발생
```

중요 기준:

```text
10분봉 60선 아래 이탈 이력이 없으면 매수 대상 아님
60선 회복 이력이 없으면 매수 대상 아님
녹색 조건만 갑자기 뜬 종목은 바로 매수 아님
기준봉 발생 당일 추격매수 방지를 위해 상태 추적은 기준봉 다음날부터 시작
```

---

## 최종 신규 초기 매수 후보 기준

상태머신을 통과한 후보만 아래 최종 조건을 검사한다.

### 10분봉 기준

```text
현재가 > 60선
5선 > 20선
5선 > 60선
5선 상승 중
20선이 급락 중은 아님
```

강한 상태는 아래와 같이 별도 표시한다.

```text
5선 > 20선 > 60선 → 강
```

### 5분봉 기준

```text
현재가 > 20선
5분봉 20봉 신고가 돌파
```

### 기준가 기준

기준가 위 조건은 `기준봉 발생 전일 종가` 기준이다.

```text
현재가 > 기준봉 발생 전일 종가
```

`BasePrice`, `PreviousClose`, 또는 기존 저장분의 `BaseHalfPrice` 복원값을 사용한다.
전일종가를 알 수 없으면 매수신호를 내지 않는다.

---

## 전일 상한가봉 몸통 눌림 시가회복 전략

이 전략은 기존 10분 60선 회복초입 전략과 섞지 않고 별도 파일에서 동작한다.

```text
MainWindowParts/MainWindow.Strategy.PrevLimitBodyOpenRecovery.cs
```

전략 코드는 KRX/NXT를 분리한다.

```text
KRX_PREV_LIMIT_BODY_ESCAPE_OPEN_RECOVERY
NXT_PREV_LIMIT_BODY_ESCAPE_OPEN_RECOVERY
```

전략 목적은 전일 상한가급 양봉의 몸통 안으로 눌린 종목이 5분 5선을 회복한 첫 양봉의 고가를 돌파할 때, 오늘 시가까지의 짧은 회복 후보 신호를 내는 것이다.

기준봉 생성과 28% 이상 종가등락률 판정은 KRX 일봉 OHLC와 KRX 전일종가를 기준으로 고정한다. NXT 가능 종목이어도 전일 상한가급 기준봉 자체는 KRX 정규장 종가 기준으로 저장한다. 이후 분봉 추적, MA 계산, 현재가 추적, 전략코드, 주문 시장 판단은 기존처럼 `StrategyMarket`에 따라 KRX/NXT로 분리한다.

필수 조건은 아래 순서를 유지한다.

```text
1. 전일 양봉 몸통 상승률 >= 28%
2. 전일 시가 <= 현재가 < 전일 종가
3. 현재가 < TodayOpen
4. 최근 2개 5분봉에서 더 낮은 저가가 나오지 않음
5. 5분봉 종가 > 5분봉 시가
6. 5분봉 종가 > 5분 MA5
7. 위 4~6을 처음 동시에 만족한 봉을 탈출봉으로 저장
8. 실시간 현재가 > EscapeCandleHigh 이면 매수후보 신호
```

탈출봉 기준값은 아래처럼 저장한다.

```text
EscapeCandleHigh = 5분 5선 회복 첫 양봉 고가
EscapeCandleLow = 5분 5선 회복 첫 양봉 저가
BodyPullbackLow = 참고 기록
```

주문 설계 기준은 아래와 같다.

```text
목표가 = TodayOpen
손절가 = EscapeCandleLow
손절가는 BodyPullbackLow가 아니라 탈출봉 저가
```

신호 발생 시점에는 손익비를 같이 계산한다.

```text
진입가 = 현재가
목표여유 = TodayOpen - 현재가
손절폭 = 현재가 - EscapeCandleLow
손익비 = 목표여유 / 손절폭
```

텔레그램과 로그에는 목표여유, 손절폭, 손익비를 함께 남긴다. 신호 발생 뒤 실제 주문 여부는 전략 파일이 아니라 주문 레이어의 `LiveOrderEnabled`, 리스크 가드, 중복주문 방지, 사용자 설정 예산 기준 수량 계산을 통과한 경우에만 판단한다.

정상 흐름 로그는 아래와 같다. 장중 실행 로그에서 이 순서를 확인한 뒤 실전 조건을 추가한다.

```text
🧩 [전일상한가 시가회복] 탈출봉 저장
🧩 [KRX 전일상한가 시가회복 신호]
🧩 [NXT 전일상한가 시가회복 신호]
ℹ️ [전략집합] 전일상한가 시가회복 / 화면후보 n개 / 전략대상 n개 / KRX n개 / NXT n개
📌 [전략] 전일상한가 몸통눌림 시가회복 점검: 전체 n개 / KRX n개 / NXT n개 / 28%이상 종가등락률 n개(KRX n개 / NXT n개) / 신규 매수신호 n개 / 주문없음
```

실전 주문 연결 전 체크리스트는 아래 항목을 기준으로 본다.

```text
현재가가 오늘 시가 아래인가?
시가까지 최소 0.8~1% 이상 여유가 있는가?
손절가는 가까운가?
탈출봉 고가를 돌파했는가?
탈출봉 저가를 손절 기준으로 쓸 수 있는가?
5분 5선을 회복했는가?
체결강도가 95~100 이상인가?
호가 공백이 크지 않은가?
현재가가 이미 너무 튀지 않았는가?
미체결 주문을 오래 방치하지 않는가?
```

아직 보류한 조건은 체결강도, 호가 공백, 미체결 관리, 비중 조절이다. 이 조건들은 테스트 신호 로그가 안정적으로 확인된 뒤 주문 엔진 연결 단계에서 추가한다.

---

## 첫 눌림 후 고가 돌파 조건

첫 눌림 후 고가는 10분봉 5선이 60선 위로 올라갈 때 만들어진 작은 언덕의 고가를 의미한다.
이 조건은 현재 버전에서는 당장 강제 적용하지 않는다.
향후 상태값과 로그 검증 후 아래처럼 추가할 수 있다.

```text
10분봉 MA5가 MA60 위로 올라가며 작은 언덕 형성
→ 이후 눌림
→ 그 언덕 고가 재돌파
→ 5분봉 20봉 신고가와 함께 강화 조건으로 사용
```

현재 버전에서는 `60선 이탈 → 회복 → 녹색 확인` 상태흐름을 먼저 저장하고, 최종 트리거는 `5분봉 20봉 신고가 돌파`로 본다.

---

## 장마감 이후 처리

장마감 이후에는 0B가 더 이상 정상적으로 들어오지 않을 수 있다.
이 경우 마지막 확정 분봉 기준으로 MA 신호를 고정 표시한다.

```text
장중 → 확정봉 59개 + 0B 현재가로 실시간 표시
장마감 후 → 마지막 확정 60개 봉 기준으로 고정 표시
```

15:20 동시호가, 15:30 장마감, 15:40 이후 조회값은 실제 응답 차이가 있을 수 있으므로 로그로 비교한다.

확인 포인트:

```text
15:30 직후 10분봉 최신봉
15:40 이후 10분봉 최신봉
마지막 확정봉 cntr_tm
MA5/MA20/MA60 값 차이
```

---

## 적용 파일

현재 Thin MA Signal 엔진의 핵심 적용 파일은 아래와 같다.

```text
MainWindowParts/MainWindow.Fields.cs
MainWindowParts/MainWindow.Storage.cs
MainWindowParts/MainWindow.WebSocket.Condition.cs
MainWindowParts/MainWindow.StrategyCandidate.BaseCandle.cs
MainWindowParts/MainWindow.LeadingMaSignal.cs
MainWindowParts/MainWindow.MinuteCache.cs
MainWindowParts/MainWindow.Strategy.BuySignalCheck.cs
Docs/KHStrategyLab_Thin_MA_Signal_Engine_Guide.md
```

### 내부 이름 기준

외부 조건검색 번호와 화면 로그는 실제 키움 조건식 기준을 따라 `조건00`을 유지한다.

```text
ConditionRoleTrack00
_targetConditionSeq00
_actualSeq00
[조건00편입]
[조건00 후보등록]
```

조건00에서 들어온 뒤 저장, 기준봉, 시장분리, MA 캐시, 매수 상태까지 관리되는 내부 후보는 `StrategyCandidate` 역할로 부른다. 이전 구현의 01번 후보 계열 함수명, 기준봉 파일명, 저장소 보조 함수명은 사용하지 않는다.

```text
MainWindowParts/MainWindow.StrategyCandidate.BaseCandle.cs
NormalizeStrategyCandidate
PruneExpiredStrategyCandidates
MergeStrategyCandidateMarketTag
QueueResolveStrategyCandidateMarket
ApplyResolvedStrategyCandidateMarket
EnsureStrategyCandidateMarketDefaults
ApplyStrategyCandidateMarketTag
InitializeStrategyCandidateBaseCandleSnapshotTimer
InitializeStrategyCandidateBuySignalCheckTimer
```

단, 저장 호환성을 위해 `WatchCandidate` JSON 필드명, `Storage/CandidateUniverse` 폴더명, `candidate_universe_active.json`, `candidate_universe_yyyyMMdd.json`, `candidate_universe_expired_yyyyMMdd.json`, `Storage/watch_candidates.json` fallback은 변경하지 않는다.

01번 조건식은 Thin 기준에서 비활성/무시 대상이다. 코드에서는 `_ignoredSeq01`, `_ignoredConditionSeq01`, `ConditionRoleIgnored01`처럼 무시 대상임을 드러내는 이름을 사용하고, 로그 문구 `조건01 비활성화`는 운용자가 상태를 알아보기 위한 설명으로만 유지한다.

---

## 동작 로그 기준

정상 동작 시 볼 수 있는 로그 예시는 아래와 같다.

```text
[MA신호등] 초기/복구 분봉 로드 예약
✅ [MA신호등] 초기/복구 분봉 로드 완료
[전략단계] 종목명(코드) / WAIT_PULLBACK → BELOW_10M_MA60
[전략단계] 종목명(코드) / BELOW_10M_MA60 → RECOVERED_10M_MA60
[전략단계] 종목명(코드) / RECOVERED_10M_MA60 → GREEN_READY
[전략] 조건00 MA신호 점검: 전체 n개 / KRX n개 / NXT n개 / 신규 매수신호 n개 / 주문없음
[KRX 매수신호 발생]
[NXT 매수신호 발생]
```

분봉이 부족하거나 점검 시간에는 아래 로그가 나올 수 있다.

```text
⚠️ [MA신호등] 10분봉 부족
⚠️ [전략분봉] HTTP 오류
⚠️ [전략분봉] 응답 오류
```

이 경우 매수신호는 차단된다.

---

## 현재 버전의 의도

이 버전은 조건00 후보 저장, KRX/NXT 시장분리, MinuteCache 기반 MA/전략 점검, 주문 레이어 연결까지 포함한다. 전략 파일은 매수 후보 신호까지만 만들고, 실제 주문 전송 여부는 주문 전용 레이어가 판단한다.

```text
LiveOrderEnabled=OFF → 신호만 발생 / 주문전송 없음
LiveOrderEnabled=ON  → 리스크가드 통과 시 사용자 설정 예산 기준 주문수량 계산 후 주문 전송
KRX 후보 → KRX 주문
NXT 가능 후보 → SOR 주문
```

PENDING 시장, 비정상 가격, 비정상 수량, 슬롯 초과, 보유 중복, 같은 날 같은 전략 재매수, 주문 진행 중 중복 요청은 주문 레이어에서 차단한다.

---

## 조건00 장외 가격 고정 기준

조건00 후보의 저장 기준봉과 전일 28% 이상 종가등락률 판정은 KRX 정규장 일봉으로 고정한다. 이 기준은 NXT 가능 종목이어도 바꾸지 않는다. 기준봉은 전략의 출발점이고, 시간외 NXT 종가는 다음날 갭 흐름으로만 해석한다.

화면 표시와 전략 추적은 시장확정 이후 역할을 분리한다.

```text
저장 기준봉/28% 판정 = KRX 정규장 기준
화면 기본 종목정보 = KRX 기준
NXT 시장확정 후 장외 화면 현재가 = NXT 종가 고정
NXT 전략/MA/MinuteCache = _NX 기준
```

20:00 이후부터 다음 거래일 07:00 전까지는 조건00 화면 행에서 NXT 후보를 KRX 종목정보 보정값으로 덮지 않는다. NXT 후보는 `ka10081` 일봉 조회에 `종목코드_NX`를 넣어 최신 일봉 종가를 읽고, 실패 시 NXT/SOR 종목정보 보정값을 fallback으로 사용한다.

정상 로그는 아래 흐름을 기준으로 확인한다.

```text
🧭 [조건00 시장분리] 종목명(코드) → NXT 확정 / source=ka10100
💾 [조건00 NXT종가고정] 화면 NXT 종가 반영 완료: 적용 n개 / 대상 n개 / source=조건00 NXT 확정 후 장외 종가고정
✅ [MinuteCache] 초기 로드 완료: 코드 / 시장=NXT / 10분=코드_NX / 5분=코드_NX
```

이 로그가 확인되면 화면 가격은 장외 NXT 종가 기준으로 고정되고, 전략 점검은 NXT MinuteCache를 사용한다. 단, TOP20 화면 종목은 참고용 조회 화면이므로 0B 실시간 보정이나 장외 NXT 종가 고정 대상이 아니다.
