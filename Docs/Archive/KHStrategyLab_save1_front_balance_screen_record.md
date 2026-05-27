# KHStrategyLab save1 — 잔고조회·화면표시·실시간 가격 반영 구현 기록

작성일: 2026-05-19  
기준 브랜치: `save1`  
기준 목적: 전략 파트 진입 전, 프로그램 앞부분(설정/화면/잔고/실시간 표시)의 확정 기준을 남긴다.

---

## 1. 현재 완료 범위 요약

현재 `save1` 기준 앞부분은 다음 범위까지 완성된 것으로 본다.

### 완료된 기능

- 설정값 저장/로드
  - 진입예산
  - 슬롯수
  - 자동매매 체크 상태
  - 조건식 번호 `ConditionSeq00`, `ConditionSeq01`
- 시작 버튼 실행 시 입력창 잠금
  - 감시 시작 시 입력창/체크박스 잠금
  - 감시 중지 시 입력 가능
- 보유종목 표시
  - `kt00005` KRX 단일 잔고조회 기준
  - MTS 손익률과 맞추기 위해 `pl_rt` 우선 사용
- 화면 종목 실시간 현재가 표시
  - 보유종목
  - 조건검색 00 표시종목
  - 조건검색 01 추적후보
  - 실시간 TOP20 종목
- 조건검색 00/01 역할 분리
  - 00번: 화면 표시 전용
  - 01번: 저장/추적/가상매수 후보
- NXT 시간외 잔고 평가 반영
  - NXT 시간외 가격이 들어오면 보유종목 평가금액/손익률/총잔고 재계산
  - 20:00 이후 다음날 07:00 전까지 마지막 NXT 평가가격 유지
- 20:00 이후 자동업무 제한
  - 신규 0B 실시간 등록/자동추적 업무는 중지
  - 잔고 보정, 마지막 NXT 가격 표시, 검색식 00 KRX 종가 보정은 유지
- 검색식 00 장외 KRX 종가 보정
  - 20:00 이후 신규 실시간 등록은 막지만, 00번 표시행은 KRX 종가/기본정보로 보정

---

## 2. 구현 흐름 전체 순서

현재 프로그램 시작 흐름은 다음 순서로 정리한다.

```text
프로그램 시작
→ config.json 설정 로드
→ 조건01 추적후보 파일 로드
→ REST 토큰 발급
→ 계좌번호 확인
→ kt00005 KRX 단일 잔고조회
→ 보유종목 Grid 반영
→ WebSocket 연결 및 LOGIN
→ TOP20 1회 조회 및 화면 표시
→ 화면 전체 종목 0B 등록대상 구성
→ 조건검색 목록 조회
→ 조건검색 00/01 실시간 요청
→ 00번은 화면 표시 전용 처리
→ 01번은 추적후보 저장/관리
→ 실시간 수신값으로 화면 현재가 갱신
→ 보유종목은 실시간/NXT 가격 기준으로 잔고 재계산
```

---

## 3. 설정 저장/입력창 잠금

### 적용 파일

```text
MainWindowParts/MainWindow.Config.cs
MainWindowParts/MainWindow.Events.cs
```

### 적용 내용

`config.json`에 다음 값을 저장/로드한다.

```json
{
  "Budget": 100000,
  "MaxSlots": 9,
  "AutoBuy": false,
  "ConditionSeq00": "0",
  "ConditionSeq01": "1"
}
```

시작 버튼 클릭 시 처리 순서:

```text
BtnStart_Click
→ 현재 입력값 검증
→ config.json 저장
→ 감시 ON
→ 입력창/체크박스 잠금
→ WebSocket 연결/LOGIN
→ 조건검색 요청
```

중지 버튼 클릭 시:

```text
감시 OFF
→ 입력창/체크박스 잠금 해제
```

### 기준 로그

```text
💾 [설정저장] 진입예산 100,000원 / 슬롯 9개 / 자동매매 OFF
▶ [감시] 시작 / 설정 입력창 잠금
■ [감시] 중지 / 설정 입력창 잠금 해제
```

---

## 4. 잔고조회 기준

### 적용 파일

```text
MainWindowParts/MainWindow.Api.Account.cs
MainWindowParts/MainWindow.Api.Account.RealtimeBalance.cs
```

### 키움 적용 코드

| 목적 | 키움 코드/ID | 현재 사용 방식 |
|---|---:|---|
| 계좌번호 확인 | `ka00001` | 로그인 후 계좌번호 확인 |
| 잔고조회 기준 | `kt00005` | KRX 단일 호출만 사용 |
| 잔고 손익률 | 응답 필드 `pl_rt` | MTS 손익률 기준으로 우선 사용 |
| 보유수량 | 응답 필드 `stk_cntr_remn` | 보유수량 기준값으로 사용 |

### 잔고조회 원칙

- 통합 잔고는 `kt00005`를 KRX 단일 호출로 조회한다.
- KRX+NXT 이중 잔고조회는 하지 않는다.
- 수익률은 `pl_rt`를 우선 사용한다.
- 수수료/세금은 참고 계산값으로 사용하되, MTS 기준 손익률을 덮어쓰는 원본값으로 쓰지 않는다.
- 이후 NXT 시간외 가격이 들어오면 실시간 평가용 손익률/총잔고는 별도로 재계산한다.

### 기준 로그

```text
📌 [kt00005] 통합 잔고는 KRX 단일 호출로 조회 / KRX+NXT 이중호출 금지
📥 [kt00005] KRX page=1 / rows=3 / cont-yn=N / next-key=-
📊 [잔고집계] 종목명(코드) / 시장=통합 / 수량=... / 수익률=... / 기준=kt00005.pl_rt
✅ [계좌동기화] kt00005 잔고 반영 완료
```

---

## 5. 잔고 실시간 평가/NXT 마지막 가격 유지

### 적용 파일

```text
MainWindowParts/MainWindow.Api.Account.RealtimeBalance.cs
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
```

### 저장 파일

```text
Storage/nxt_last_prices.json
```

### 적용 내용

보유종목에 실시간 가격이 들어오면 다음 순서로 재계산한다.

```text
보유수량 × 실시간 현재가
→ 평가금액 계산
→ 매입금액 대비 손익금액 계산
→ 수수료/세금 참고 계산
→ 손익률 계산
→ 총잔고 요약 갱신
```

NXT 시간외 가격 처리:

```text
NXT 시간외 가격 수신
→ 해당 보유종목 현재가 덮어쓰기
→ nxt_last_prices.json 저장
→ 20:00 이후 다음날 07:00 전까지 저장 가격 복원 표시
```

### 시간 기준

```text
08:00~09:00     장전 NXT 가격 가능 구간
09:00~15:40     장중 기본 KRX 0B 구간
15:40~20:00     장후 NXT 가격 가능 구간
20:00~익일 07:00 마지막 NXT 평가가격 유지 표시
07:00 이후       전일 NXT 가격 적용 중지
```

### 기준 로그

```text
💹 [잔고실시간] 0B/NXT종가 기준 총잔고 재계산
🌙 [NXT잔고복원] 마지막 NXT 평가가격 적용
🌙 [NXT잔고보정] 보유 NXT 종가조회 적용 완료
```

---

## 6. 화면 표시 종목 묶음

현재 0B 실시간 등록대상은 화면에 표시되는 전체 종목 묶음에서 만든다.

### 등록대상 묶음

```text
보유종목
+ 조건검색 00 표시종목
+ 조건검색 01 추적후보
+ 실시간 TOP20 종목
→ 종목코드 중복 제거
→ 실시간 등록대상 구성
```

### 적용 파일

```text
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
Models/StockGridRow.cs
```

### StockGridRow 보완

화면 바인딩 갱신을 위해 다음 속성들에 변경 알림을 적용했다.

```text
CurrentPrice
VolumeText
TradingValueText
ChangeRateText
ProfitRateText
ProfitColor
Name
Rank
```

### 기준 로그

```text
📌 [실시간체결] 화면 종목 0B 등록대상 추가
📡 [실시간체결] LOGIN 이후 0B 재등록 완료
📈 [실시간체결] 화면 현재가 갱신 중
```

---

## 7. 실시간 WebSocket/0B 적용 구조

### 적용 파일

```text
MainWindowParts/MainWindow.WebSocket.Condition.cs
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
```

### 키움 WebSocket 기준

운영 WebSocket 도메인/경로:

```text
wss://api.kiwoom.com:10000
/api/dostk/websocket
```

### 현재 프로그램 구현 기준

현재 프로그램은 실시간 현재가/체결성 가격 갱신을 `0B` 등록 로그 기준으로 구현했다.

```text
📡 [실시간체결] 0B 등록: 090460 / 현재가 화면갱신 ON
```

주의:
- 키움 REST 가이드 화면 추출에서는 실시간 타입 목록 표기가 섞여 보이는 구간이 있다.
- 현재 코드에서는 실제 수신 로그와 화면 갱신 성공 기준으로 `0B`를 사용했다.
- 최종적으로 키움 가이드 AI에 “주식체결 실시간 타입 코드가 0A인지 0B인지, NXT/SOR 등록 코드 형식은 무엇인지”를 한 번 더 확인하는 것이 안전하다.

### KRX/NXT 실시간 가격 라우팅

사용자가 확정한 방향:

```text
기본은 KRX 6자리 코드로 전체 등록
NXT 가능 종목은 NXT 시간대에 한 번 더 등록
NXT 값이 오면 같은 화면 행을 덮어쓰기
```

시간대별 처리:

```text
08:00~09:00
→ KRX 기본 등록 + NXT 가능 종목 NXT overlay

09:00~15:40
→ 기존처럼 KRX 기본 6자리 등록

15:40~20:00
→ KRX 기본 등록 + NXT 가능 종목 NXT overlay

20:00 이후
→ 신규 0B 등록 중지
→ 화면 보정/잔고 보정은 유지
```

---

## 8. 20:00 이후 처리 기준

20:00 이후에는 모든 기능을 막는 것이 아니다.

### 중지 대상

```text
신규 0B 실시간 등록
자동추적 업무
불필요한 실시간 현재가 신규 등록
```

### 유지 대상

```text
잔고 화면 표시
마지막 NXT 평가가격 복원
검색식 00 KRX 종가 보정
config 설정 저장/로드
계좌조회
종목정보 조회
화면 표시 유지
```

### 로그 문구 의미

```text
🧭 [실시간체결 등록모드] 장외 CLOSED / 화면종목 신규등록 보류
```

위 로그는 화면종목을 지운다는 뜻이 아니다.  
20시 이후 새 실시간 WebSocket 등록만 하지 않는다는 뜻이다.

---

## 9. 조건검색 00/01 역할 분리

### 적용 파일

```text
MainWindowParts/MainWindow.WebSocket.Condition.cs
MainWindowParts/MainWindow.Storage.cs
MainWindowParts/MainWindow.Config.cs
```

### 키움 적용 코드

| 목적 | 키움 코드/명령 | 현재 사용 |
|---|---:|---|
| 조건검색 목록조회 | `ka10171` / `CNSRLST` | 조건식 번호/이름 조회 |
| 조건검색 일반 요청 | `ka10172` | 현재 메인 흐름에서는 보조 가능 |
| 조건검색 실시간 요청 | `ka10173` | 00/01 실시간 편입 수신 |
| 조건검색 실시간 해제 | `ka10174` | 추후 정리용 |
| 조건식 목록 응답 | `seq`, `name` | 배열/객체 응답 모두 처리 |

### 00번 조건식

```text
ConditionSeq00 = "0"
역할 = 화면 표시 전용
파일 저장 = OFF
가상매수 = OFF
전략추적 = OFF
0B 현재가 화면 갱신 = ON
장외 KRX 종가 보정 = ON
```

### 01번 조건식

```text
ConditionSeq01 = "1"
역할 = 저장/추적/가상매수 후보
파일 저장 = ON
가상매수 = ON
전략추적 = ON
0B 현재가 화면 갱신 = ON
```

### 기준 로그

```text
📡 [조건검색] 실시간 요청: seq=0 / role=00 화면표시 / stex_tp=K
📡 [조건검색] 실시간 요청: seq=1 / role=01 추적/가상매수 / stex_tp=K
ℹ️ [조건00] 화면표시 전용 / 파일저장 OFF / 가상매수 OFF
🧭 [조건01] 저장/추적/가상매수 판단 ON
```

---

## 10. 조건검색 목록조회가 반복되는 이유와 정리 방향

현재는 WebSocket LOGIN 후 `CNSRLST` 조건검색 목록조회를 다시 요청한다.

현재 흐름:

```text
시작 버튼
→ WebSocket LOGIN
→ CNSRLST 조건검색 목록조회
→ 00/01 조건식 확인
→ 조건검색 00/01 실시간 요청
```

현재 동작은 오류는 아니다.  
다만 사용자가 조건식 번호를 직접 관리하고 있으므로, 다음 안정화 단계에서는 다음처럼 정리할 수 있다.

```text
프로그램 최초 시작 때만 조건식 목록 1회 조회
시작 버튼 클릭 때는 config.json의 ConditionSeq00/01로 바로 실시간 요청
```

---

## 11. 검색식 00 KRX 종가 보정

### 적용 파일

```text
MainWindowParts/MainWindow.Api.StockInfo.Search00KrxClose.cs
MainWindowParts/MainWindow.WebSocket.Condition.cs
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
```

### 키움 적용 코드

| 목적 | 키움 코드 |
|---|---:|
| 주식기본정보요청 | `ka10001` |

### 적용 이유

20:00 이후에는 신규 0B 등록이 중지된다.  
그렇지만 00번 화면 표시행은 장외에도 KRX 종가/기본정보가 화면에 맞게 표시되어야 한다.

처리 흐름:

```text
장외 CLOSED
→ 00번 표시행 확인
→ 현재가 0이거나 보정 필요 시 ka10001 조회
→ KRX 종가/기본정보 화면 반영
```

기준 로그:

```text
🌙 [조건00 KRX종가보정] 표시행 N개 / 조회요청 N개
✅ [종목정보] 반영 완료: 종목명(코드) / 현재가 ...
```

---

## 12. TOP20 화면 표시

### 적용 파일

```text
MainWindowParts/MainWindow.Timers.cs
MainWindowParts/MainWindow.Api.StockInfo.cs
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
```

### 키움 적용 코드

| 목적 | 키움 코드 |
|---|---:|
| 실시간종목조회순위 | `ka00198` |

### 현재 기준

- TOP20 목록 자체는 `ka00198`로 주기 조회한다.
- TOP20에 표시된 종목의 현재가 변화는 WebSocket 실시간 가격으로 보강한다.
- TOP20을 15초마다 “움직이게” 하는 것이 목적이 아니라, 화면에 올라온 종목들의 가격이 실시간으로 변하도록 하는 것이 목적이다.

기준 로그:

```text
📌 [0198 TOP20] 키움 실시간종목조회순위 요청: qry_tp=1(1분)
✅ [0198 TOP20] 화면 표시 완료: 20개
```

---

## 13. 차트/종목정보 표시

### 적용 파일

```text
MainWindowParts/MainWindow.Api.StockInfo.cs
MainWindowParts/MainWindow.Api.Chart.cs
```

### 키움 적용 코드

| 목적 | 키움 코드 |
|---|---:|
| 주식기본정보요청 | `ka10001` |
| 주식일봉차트조회요청 | `ka10081` |

### 현재 기준

- 종목 클릭/초기 차트는 `ka10081` 일봉 조회를 사용한다.
- NXT 가능 종목은 차트 조회 시 `_AL` 요청코드를 사용한 흐름이 확인되었다.
- 검색식 00 KRX 종가 보정은 `ka10001`을 사용한다.

기준 로그:

```text
📌 [종목정보] 조회 요청: 090460
✅ [종목정보] 반영 완료: 비에이치(090460) / 현재가 ...
📈 [차트] 일봉 조회 요청: 삼성전자(005930) / 요청코드=005930_AL / 시장=SOR(_AL) / api-id=ka10081
```

---

## 14. 지수 자동조회

### 적용 파일

```text
MainWindowParts/MainWindow.Api.Index.cs
MainWindowParts/MainWindow.Timers.cs
```

### 현재 기준

- 로그인 직후 1회 조회는 시간 제한 없이 허용한다.
- 일반 자동조회는 18:00 이후 중지한다.
- 18:00 이후에도 화면에 이미 표시된 지수는 유지한다.

기준 로그:

```text
✅ [지수] KOSPI/KOSDAQ 화면 반영 완료
⏸ [지수] 자동 갱신 중지: 18:00 이후 / 로그인 시 1회 표시만 유지
```

---

## 15. 조건01 추적후보 6일 보관/7일차 삭제

이 부분은 전략 파트 입구로 작성되었다.

### 적용 파일

```text
MainWindowParts/MainWindow.Storage.cs
MainWindowParts/MainWindow.WebSocket.Condition.cs
```

### 저장 파일

```text
Storage/CandidateUniverse/candidate_universe_active.json
Storage/CandidateUniverse/candidate_universe_yyyyMMdd.json
Storage/CandidateUniverse/candidate_universe_expired_yyyyMMdd.json
```

### 적용 기준

```text
01번 조건식 최초 편입
→ 후보 저장
→ FirstSeenDate 유지
→ 오전/오후/다음날 다시 잡혀도 새로 추가하지 않음
→ LastSeenDate만 갱신
→ 6일간 보관
→ 7일차에 추적목록에서 삭제
```

예시:

```text
5월 19일 편입 = 1일차
5월 24일까지 보관
5월 25일 실행 시 삭제
```

### 기준 로그

```text
🆕 [조건01 후보등록] 종목명(코드) / 최초편입=... / 6일 보관
💾 [감시저장] 조건01 추적후보 N개 저장 / 00번 표시종목은 저장 안 함 / 6일 보관
🧹 [조건01 후보정리] 7일차 추적목록 삭제 N개
⛔ [조건01 재추가차단] 코드 / 오늘 7일차 삭제된 종목이라 재추가 안 함
```

---

## 16. 패치 적용 순서 기록

앞부분은 다음 순서로 만들어졌다.

```text
1. 설정 저장/로드 + 시작 버튼 입력창 잠금
2. WebSocket 0B 실시간 현재가 화면 갱신
3. CNSRLST 배열 응답 파싱 수정
4. StockGridRow 바인딩 변경 알림 보강
5. 검색식 00 저장 차단
6. 검색식 00 가상매수 차단
7. 조건식 00/01 역할 분리
8. 20시 이후 신규 실시간 등록 중지 방향 확정
9. KRX 기본 + NXT overlay 실시간 가격 구조 작성
10. 보유종목 NXT 시간외 가격 잔고 반영
11. 마지막 NXT 평가가격 다음날 07:00 전까지 유지
12. 검색식 00 장외 KRX 종가 보정
13. 조건01 후보 6일 보관/7일차 삭제 저장소 작성
14. WebSocket 중복 연결 방지 및 종료 정리 루틴 추가
15. 차트 종목정보 회전율 조회/계산 보강
16. TOP20/보유/추적 그리드 선택 포인트 안정화
```

---

## 17. 다음 단계로 넘길 기준

앞부분 안정 기준:

```text
설정 저장/로드 정상
입력창 잠금/해제 정상
kt00005 잔고조회 정상
보유종목 화면 표시 정상
TOP20 표시 정상
검색식 00 표시 정상
검색식 01 추적후보 저장 정상
0B 실시간 가격 화면 갱신 정상
NXT 시간외 잔고 반영 정상
20시 이후 NXT 마지막 가격 유지 정상
검색식 00 장외 KRX 종가 보정 정상
```

다음 전략 파트 시작점:

```text
조건01 후보 저장소
→ 일봉 600일 조회
→ 기준봉 판정
→ 기준가 A 계산
→ 마디고가 B 계산
→ 마디저가 C 계산
→ 목표고가 = 마디저가 × (마디고가 / 기준가)
→ 목표하단밴드 계산
→ 예상진입가 기준 손익비 계산
→ 가상매수
```

---

## 18. 주의/추후 검증 필요

다음 항목은 추후 키움 가이드 AI 또는 실제 RawLogs로 한 번 더 확인한다.

```text
1. WebSocket 주식체결 실시간 타입 코드가 현재 구현처럼 0B가 맞는지
2. 키움 가이드 표기상 0A/0B가 섞여 보이는 부분의 최종 공식 의미
3. 장시작시간 실시간 타입 코드가 0s인지 0m인지
4. NXT/SOR 실시간 등록 시 _NX, _AL 형식 중 공식 권장값
5. 0s 또는 0m 장시작시간이 NXT 프리/메인/애프터 상태까지 내려주는지
```

현재 프로그램은 실제 수신 로그와 화면 정상 동작 기준으로 유지한다.  
전략 파트 진입 전, 위 항목은 “문서상 재검증 필요”로 남긴다.

---

## 19. 현재 안정 기준 한 줄 요약

```text
save1 앞부분은 설정/잔고/화면/실시간 표시/NXT 보정까지 완료.
이제 01번 조건식 후보 저장소를 기준으로 전략 계산 엔진으로 넘어간다.
```

---

## 20. 2026-05-25 Codex 브랜치 안정화 기록

이번 안정화는 전략 판단식 자체를 바꾸지 않고, 화면/연결/종료/차트 정보 표시의 흔들림을 줄이는 방향으로 적용했다.

### WebSocket 및 종료 안정화

적용 파일:

```text
MainWindow.xaml.cs
MainWindowParts/MainWindow.Fields.cs
MainWindowParts/MainWindow.WebSocket.Condition.cs
MainWindowParts/MainWindow.Cleanup.cs
```

적용 내용:

```text
WebSocket 연결 초기화 중복 시도 방지
기존 WebSocket 구독/클라이언트 정리 후 재생성
강제 종료 시 DispatcherTimer, WebSocket, 트레이 아이콘, HttpClient 정리
종료 중 재연결/LOGIN 재전송 차단
```

### 차트 회전율 표시 보강

적용 파일:

```text
MainWindowParts/MainWindow.Api.Placeholders.cs
MainWindowParts/MainWindow.Api.StockInfo.cs
```

적용 내용:

```text
일봉/10분봉 차트 조회 시 종목정보(ka10001) 조회 병행
거래량/거래대금/상장주식수 응답 필드 후보 확장
회전율 직접 응답이 없으면 거래량 / 상장주식수 × 100으로 계산
회전율 표시 실패 시 차트 하단은 --- 유지
```

### 그리드 선택 포인트 안정화

적용 파일:

```text
MainWindowParts/MainWindow.GridSelection.cs
MainWindowParts/MainWindow.Api.RealtimeRank.cs
MainWindowParts/MainWindow.Api.Account.cs
MainWindowParts/MainWindow.Api.Account.RealtimeBalance.cs
MainWindowParts/MainWindow.Api.StockInfo.Search00KrxClose.cs
MainWindowParts/MainWindow.Api.StockInfo.cs
MainWindowParts/MainWindow.Events.cs
MainWindowParts/MainWindow.LeadingMaSignal.cs
MainWindowParts/MainWindow.WebSocket.RealtimeTrade.cs
```

적용 내용:

```text
TOP20 갱신 시 _rankList.Clear/Add 제거
기존 TOP20 행 객체를 유지하고 값만 갱신
실시간 체결/종목정보/MA신호 갱신 때 불필요한 전체 Refresh 제거
자동 선택 복원 중 SelectionChanged 차트 재조회 차단
ScrollIntoView/Keyboard.Focus 강제 호출 제거
```

확인 결과:

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```
