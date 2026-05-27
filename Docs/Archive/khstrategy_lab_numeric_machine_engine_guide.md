# KHStrategyLab NumericMachine 엔진 설계 가이드

## 결론

KHStrategyLab 새 전략 엔진은 **차트를 그리는 프로그램**이 아니라, **차트의 원재료인 시고저종·거래량·거래대금·시간만 받아 적는 숫자 엔진**으로 설계한다.

핵심은 단순하다.

```txt
실시간 체결 수신
→ 시고저종 받아쓰기
→ 거래량/거래대금 누적
→ 기준봉/마디/이평 숫자 계산
→ 상태 체크포인트 기록
→ 조건 일치 시 주문
```

차트 이미지를 그리지 않는다.  
하지만 내부에는 보이지 않는 차트, 즉 `OHLCV 장부`가 존재한다.

---

## 1. 설계 기준

### 첫번째, 차트 렌더링 금지

실전 매매 중에는 여러 종목 차트를 그리지 않는다.

```txt
차트 그리기 ❌
시고저종 숫자 저장 ⭕
```

차트는 결국 아래 숫자의 시각화일 뿐이다.

```txt
시간
시가
고가
저가
종가
거래량
거래대금
```

실전 엔진은 이 숫자만 다룬다.

---

### 두번째, 5분봉 경량엔진 예시의 핵심만 채택

기존 5분봉 경량엔진 예시의 핵심은 다음이다.

```txt
틱 수신
→ 시간 버킷 계산
→ 현재 봉 시가/고가/저가/종가/거래량 갱신
→ 봉 완성 시 저장
→ 최근 N개만 유지
```

이 구조는 맞다.  
다만 KHStrategyLab에서는 5분봉 하나만 고정하지 않고, **기본 단위는 3분봉 또는 설정값으로 둔다.**

추천 기본값:

```txt
기본봉: 3분봉
보조봉: 6분 / 9분 / 15분
참고봉: 필요 시 5분 / 10분
```

즉, 5분봉 예시는 “경량 캔들 생성 방식”의 샘플이고, 실제 엔진은 `MultiTimeframe Numeric Candle Engine`으로 확장한다.

---

## 2. 전체 엔진 흐름

```txt
Kiwoom REST WebSocket REAL
↓
RealMessageRouter
↓
StockTrade0BHandler
↓
NumericCandleEngine
↓
RollingIndicatorCache
↓
ScenarioStateStore
↓
StrategyHub
↓
PositionAllocator
↓
TradeGuard
↓
OrderExecutor
```

주문체결, 장상태, 종목정보는 별도 흐름으로 분리한다.

```txt
00 주문체결
→ OrderTracker

0s 장시작시간
→ MarketSessionState

0g 종목정보
→ StockLimitInfoCache
```

---

## 3. WebSocket REG 수신 처리

실시간 REG 후 `trnm = REAL` 메시지에서 `type`별로 분리한다.

주요 type:

```txt
0B = 주식체결
00 = 주문체결
0s = 장시작시간
0g = 종목정보
0H = 예상체결
```

전략 판단의 핵심은 `0B 주식체결`이다.

`0B`에서 주로 사용할 값:

```txt
20 = 체결시간
10 = 현재가
15 = 단건 거래량 또는 체결량
13 = 누적거래량
14 = 누적거래대금, 제공 시 사용
16 = 당일 시가
17 = 당일 고가
18 = 당일 저가
228 = 체결강도, 제공 시 사용
311 = 시가총액, 제공 시 참고
```

중요한 구분:

```txt
키움 0B의 시가/고가/저가
= 당일 기준 참고값

우리 CandleEngine의 시고저종
= 전략 판단용 내부 분봉
```

전략은 반드시 내부에서 만든 봉을 기준으로 판단한다.

---

## 4. NumericCandleEngine

### 역할

실시간 체결을 받아서 차트 없이 내부 봉을 만든다.

```txt
현재가가 들어오면
해당 시간 버킷의 시가/고가/저가/종가/거래량/거래대금을 갱신한다.
```

### CandleBar 구조

```txt
StockCode
TimeframeMinute
BucketStartTime
Open
High
Low
Close
Volume
TradingValue
FirstTickTime
LastTickTime
IsClosed
```

### 업데이트 방식

```txt
첫 가격 → 시가
가격이 기존 고가보다 높음 → 고가 갱신
가격이 기존 저가보다 낮음 → 저가 갱신
마지막 가격 → 종가
누적거래량 차이 → 거래량 더하기
누적거래대금 차이 → 거래대금 더하기
```

핵심 공식:

```txt
현재 틱 거래량 = 현재 누적거래량 - 이전 누적거래량
현재 틱 거래대금 = 현재 누적거래대금 - 이전 누적거래대금
```

단건 거래량 `15`보다 누적거래량 `13` 차분을 우선한다.

---

## 5. 봉 기준

### 기본 운영안

```txt
3분봉 = 진입 판단 기본봉
6분봉 = 3분봉 2개 묶음
9분봉 = 3분봉 3개 묶음
15분봉 = 3분봉 5개 묶음
```

5분/10분은 꼭 버리는 것이 아니라, 전략별 옵션으로 둔다.

```txt
상한가 D+1/D+2 전략
→ 5분/10분/15분 옵션 가능

경량 실전 모드
→ 3분/9분/15분 권장
```

중요한 건 봉 종류가 아니라 **고가·저가·종가·거래량·거래대금이 조건에 도달했는가**다.

---

## 6. RollingIndicatorCache

이평선은 전체 재계산하지 않는다.

### MA60 예시

```txt
MA60 = 최근 60개 종가 합 / 60
```

새 봉이 완성되면:

```txt
sum60 = sum60 + newClose - oldClose
ma60 = sum60 / 60
```

즉, 이평선도 더하기 빼기다.

관리할 값:

```txt
MA5
MA10
MA20
MA60
MA100
MA240, 일봉 기준
```

분봉 이평은 후보 종목에만 적용한다.

일봉 이평은 전체 종목 600일 데이터를 기준으로 장후/장전 계산한다.

---

## 7. 일봉 기반 후보 준비

분봉은 오래 들고 있을 필요가 없다.

```txt
일봉 600일 데이터
→ 전체 종목 메모리 보관 가능

분봉 데이터
→ 후보 종목의 필요한 구간만 생성/조회/추적
```

장후 또는 장전에는 일봉으로 먼저 자리를 찾는다.

예시 앵커:

```txt
상한가 발생일
D+1 / D+2 후보
일봉 20선 터치일
기준봉 발생일
전고점 돌파일
거래대금 급증일
중심가 지지일
```

이 앵커를 기준으로 다음날 추적할 후보파일을 만든다.

---

## 8. CandidateUniverse 파일

장 끝난 뒤 또는 장 시작 전 후보파일을 만든다.

예시:

```json
{
  "tradeDate": "2026-05-18",
  "createdAt": "2026-05-17 16:30:00",
  "candidates": [
    {
      "stockCode": "123456",
      "stockName": "예시종목",
      "route": "KRX",
      "source": ["AfterMarketScanner", "Condition01"],
      "strategyCode": "D20_BASE_REBREAK",
      "grade": "A",
      "anchorDate": "2026-05-17",
      "anchorType": "DailyMa20Touch",
      "baseHigh": 5430,
      "baseLow": 4980,
      "baseMid": 5205,
      "baseClose": 5400,
      "baseVolume": 1200000,
      "baseTradingValue": 105000000000,
      "triggerPrice": 5430,
      "maxOrderAmount": 1500000,
      "enabled": true
    }
  ]
}
```

장중에는 이 파일에 있는 종목만 우선 REG 등록한다.

검색식은 필수가 아니라 보조 레이더다.

```txt
메인 후보 = 장후 후보파일
보조 후보 = 장중 검색식 편입
최종 판단 = NumericMachine
```

---

## 9. ScenarioStateStore

전략은 매초 차트를 훑지 않고, 체크포인트만 저장한다.

예시 상태:

```txt
SearchMatched
DailyAnchorReady
BaseCandleDetected
BaseHighSaved
VolumeFollowPassed
MadiEfficiencyPassed
PullbackHeld
RebreakBaseHigh
TrapSuspected
BuySignalFired
```

예시 흐름:

```txt
일봉 자리 확인
↓
장중 기준봉 발생
↓
기준봉 고가/중심가/거래량/거래대금 저장
↓
3분 추적봉이 기준봉 거래량의 60% 이상 도달
↓
가격이 기준봉 고가 근처 도달
↓
기준봉 고가 재돌파
↓
트랩 체크 통과
↓
매수 신호
```

---

## 10. 기준봉 추적

기준봉은 전략의 중심이다.

저장값:

```txt
BaseOpen
BaseHigh
BaseLow
BaseClose
BaseMid
BaseVolume
BaseTradingValue
BaseTime
```

기준봉 이후 추적:

```txt
현재 3분봉 거래량 / 기준봉 거래량
현재 3분봉 거래대금 / 기준봉 거래대금
현재가 / 기준봉 고가
현재가 / 기준봉 중심가
```

예시 조건:

```txt
3분 거래량 >= 기준봉 거래량 × 0.6
3분 거래대금 >= 기준봉 거래대금 × 0.6
현재가 >= 기준봉 고가 × 0.98
현재가 > 기준봉 고가
```

단, 60%는 매수 조건이 아니라 예열 조건이다.

최종 매수는 재돌파와 유지력이 필요하다.

---

## 11. 마디 효율

거래대금 자체보다 중요한 것은 거래대금으로 가격이 얼마나 전진했는가다.

```txt
마디 효율 = 마디 상승률 / 마디 거래대금억
```

예시:

```txt
마디 상승률 6%
마디 거래대금 120억
효율 = 6 / 120 = 0.05
→ 효율 좋은 마디

마디 상승률 1%
마디 거래대금 300억
효율 = 1 / 300 = 0.0033
→ 무거운 종목 또는 매물대 의심
```

관리값:

```txt
MadiStartPrice
MadiHighPrice
MadiLowPrice
MadiTradingValue
MadiRate
MadiEfficiency
PullbackDepth
RebreakSuccess
```

마디 판단 흐름:

```txt
거래대금 터짐
↓
마디 시작점 저장
↓
마디 고점 갱신
↓
상승률 계산
↓
거래대금 대비 효율 계산
↓
눌림 깊이 확인
↓
고점 재돌파 확인
```

---

## 12. 트랩 차단

트랩은 진짜 상승 시작과 모양이 비슷하다.

따라서 거래량만으로 매수하지 않는다.

트랩 의심 조건:

```txt
거래량은 큰데 가격 전진 실패
위꼬리 과다
고가 돌파 후 기준봉 고가 아래 복귀
종가 위치가 봉 중간 이하
바로 위 매물대 존재
```

숫자 조건 예시:

```txt
UpperWickRate >= 0.40
CloseLocation <= 0.50
CurrentPrice < BaseHigh
MadiEfficiency <= 기준값
```

트랩 의심 시:

```txt
BuyBlocked = true
다음 봉 재회복 확인 전까지 매수 금지
```

---

## 13. TradeGuard

주문 직전에는 반드시 안전장치를 통과해야 한다.

```txt
LatencyGuard
시세 지연 확인

BacklogGuard
큐 밀림 확인

DataGapGuard
체결시간 갭 확인

MarketSessionGuard
장 상태 확인

DuplicateOrderGuard
중복 주문 방지
```

초기 기준 예시:

```txt
수신→처리 지연 1000ms 이상
→ 신규매수 차단

주문 큐 길이 50 초과
→ 신규매수 차단

체결시간 갭 3초 이상
→ 해당 종목 매수 보류
```

밀린 데이터로 매수하지 않는다.

---

## 14. 파일 저장 구조

### 장후 저장

```txt
DailyBars.json 또는 DB
전체 종목 600일 일봉

CandidateUniverse.yyyyMMdd.json
다음날 추적 후보

AnchorEvents.yyyyMMdd.json
상한가일, 20선 터치일, 기준봉일 등
```

### 장중 저장

```txt
RealtimeStateSnapshot.json
현재 후보 상태

ScenarioLog.yyyyMMdd.log
상태 변화 로그

OrderLog.yyyyMMdd.log
주문 요청/응답/체결 로그

MadiLog.yyyyMMdd.jsonl
마디 발생, 효율, 재돌파 결과
```

### 복기 저장

```txt
TradeReview.yyyyMMdd.json
매수 당시 상태값
마디 효율
기준봉 정보
체결강도
지연시간
결과 수익률
```

로그는 가격이 변할 때마다 찍지 않는다.

```txt
현재가 로그 ❌
상태 변화 로그 ⭕
```

---

## 15. 관리 방식

### 설정파일

```txt
Strategies/User/grade.rules.json
Strategies/User/allocation.rules.json
Strategies/User/madi.rules.json
Strategies/User/performance.rules.json
Strategies/User/strategy.profiles.json
```

### 성능 모드

```txt
TradingMode
- 차트 OFF
- 상태표만 표시
- 주문 우선

MonitorMode
- 선택 종목 1개 차트만 느리게 표시

ReviewMode
- 복기용 차트/로그 허용
- 주문 없음
```

### 추적 종목 관리

```txt
S/A등급 후보 우선 REG
B등급은 여유 있을 때 등록
C등급은 관망만
D등급 제외
```

PC가 바쁘면 자동 축소한다.

```txt
차트 갱신 중단
UI 갱신 주기 증가
신규 후보 편입 제한
신규 매수 차단
```

---

## 16. 폴더 구조 초안

```txt
Strategies
 ├─ Core
 │   ├─ NumericMachine.cs
 │   ├─ RealMessageRouter.cs
 │   ├─ NumericCandleEngine.cs
 │   ├─ RollingIndicatorCache.cs
 │   ├─ ScenarioStateStore.cs
 │   ├─ StrategyHub.cs
 │   ├─ PositionAllocator.cs
 │   ├─ TradeGuard.cs
 │   └─ StrategyLogger.cs
 │
 ├─ Models
 │   ├─ QuoteTick.cs
 │   ├─ CandleBar.cs
 │   ├─ CandidateProfile.cs
 │   ├─ ScenarioState.cs
 │   ├─ MadiState.cs
 │   ├─ StrategySignal.cs
 │   └─ AllocationResult.cs
 │
 ├─ AfterMarket
 │   ├─ DailyDataStore.cs
 │   ├─ AfterMarketScanner.cs
 │   ├─ CandidateUniverseBuilder.cs
 │   ├─ CandidateGrader.cs
 │   └─ CandidateExporter.cs
 │
 ├─ Krx
 │   ├─ D20_BaseRebreak_Krx.cs
 │   ├─ LimitUpAfter_Krx.cs
 │   └─ MadiEfficiency_Krx.cs
 │
 ├─ Nxt
 │   ├─ D20_BaseRebreak_Nxt.cs
 │   ├─ LimitUpAfter_Nxt.cs
 │   └─ MadiEfficiency_Nxt.cs
 │
 └─ User
     ├─ candidates.yyyyMMdd.json
     ├─ grade.rules.json
     ├─ allocation.rules.json
     ├─ madi.rules.json
     └─ performance.rules.json
```

---

## 17. 매매 방향과 맞는가

### 맞다

현재 설계는 사용자의 매매 방향과 잘 맞는다.

이유는 다음과 같다.

```txt
일봉으로 자리 먼저 본다.
분봉은 오래 저장하지 않는다.
필요한 앵커 날짜 이후만 추적한다.
차트 이미지는 그리지 않는다.
시고저종과 거래량/거래대금만 사용한다.
기준봉을 저장하고 재돌파를 본다.
거래량 자체보다 가격 전진 효율을 본다.
트랩은 거래량이 아니라 결과값으로 거른다.
장중에는 더하기 빼기만 한다.
```

이 구조는 “고수의 차트 해석”을 그림으로 흉내 내는 방식이 아니라, 그 해석을 숫자 규칙으로 바꾸는 방식이다.

---

## 18. 추적 방향

장중 추적은 아래만 보면 된다.

```txt
현재가
현재 3분봉 시고저종
현재 3분봉 거래량/거래대금
기준봉 고가/중심가
마디 고가/저가
마디 효율
이평 숫자
체결강도
시세 지연 여부
```

추적의 핵심은 이 문장이다.

```txt
돈이 들어왔을 때 가격이 얼마나 전진했고,
그 전진을 얼마나 지켰고,
다시 고점을 넘을 때 돈이 또 붙는가.
```

---

## 19. 파일 저장 방향

파일 저장은 세 단계로 나눈다.

```txt
장후 후보파일
→ 내일 볼 종목과 기준값 저장

장중 상태파일
→ 현재 체크포인트와 마디 상태 저장

복기 파일
→ 왜 매수했는지, 왜 스킵했는지 저장
```

특히 복기 파일에는 반드시 다음을 남긴다.

```txt
전략코드
기준봉 정보
마디 효율
거래대금
거래량 진행률
트랩 여부
지연시간
주문 여부
결과
```

이렇게 해야 전략을 감으로 고치는 것이 아니라 숫자로 개선할 수 있다.

---

## 20. 최종 정의

KHStrategyLab NumericMachine은 다음과 같이 정의한다.

```txt
차트의 그림을 버리고,
차트의 숫자만 남긴다.

실시간 체결을 받아
시고저종과 거래량/거래대금을 적고,
이평은 더하기 빼기로 갱신하고,
마디 효율로 종목의 힘을 판단하고,
상태 체크포인트가 모두 맞을 때만 주문하는
경량 전략 엔진이다.
```

한 줄 결론:

```txt
KHStrategyLab 새 엔진 = 시고저종 장부 + 마디 효율 + 상태머신 + 주문 안전장치
```

이 방향이면 현재 매매 방식과 맞고, PC 성능 문제도 줄일 수 있다.

