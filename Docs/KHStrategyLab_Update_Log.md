# KHStrategyLab Update Log

## 2026-05-26 / Codex / 기준일 분리 D1 점수화 및 MinuteCache 재로드 루프 차단

### 코드 변경
```text
기준봉 점수 저장에 FollowupD1 블록 추가 (기준일 BaseDate 단위로만 계산, 날짜 혼합 금지)
GridLeading 기준봉 표기를 D0 단일값에서 D0+D1 형식으로 확장 (예: A1 A13)
D1 점수 1차 항목 추가: 전일종가 대비 다음날 종가, 거래대금 소화율, 윗꼬리율, 다음날 종가 위치
MA신호등 분봉 복구에서 LastRealtimeAt 과거값 덮어쓰기 방지
REALTIME_GAP_RELOAD 예약 시점의 현재시각을 캐시에 반영해 재로드 반복 루프 차단
```

### 검증
```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

## 2026-05-26 / Codex / 조건00 종가정리(15분 재실행 차단) 및 차트 현재가 표시 정리

### 코드 변경
```text
조건00 후보를 장중 임시로 유지하되 종가 이후(16:00+) 기준봉 확정 정리를 1회 실행하도록 추가
종가정리 시 오늘 유입분만 대상으로 기준봉 실패 종목(종가등락률 20% 미만, 윗꼬리율 35% 초과, 전일종가/OHLC 미충족)을 목록에서 제거
종가정리 상태를 Storage/candidate_finalize_state.json에 저장하고 동일 기준일 재실행 차단
재시작/재로그인 반복 실행 방지를 위해 마지막 종가정리 시각 기준 15분 쿨다운 적용
차트 footer의 종가/현재가 분리 표시는 원래 단일 현재가 형태로 복귀
차트 세로축 현재가 마커(가이드선/라벨) 색을 고정 빨강 대신 종목의 등락 색(PriceColor)으로 변경
5분봉 표시 개수는 300개로 유지
```

### 검증
```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

## 2026-05-26 / Codex / 수동매도 잔고동기화 및 분봉차트 redraw 안정화

### 코드 변경
```text
수동매도처럼 프로그램 주문 레이어를 거치지 않은 매도에서도 ka10074 실현손익 변경을 감지하면 kt00005 보유잔고를 강제 재조회하도록 보완
실현손익, 수수료, 세금 중 하나라도 변경되면 잔고동기화 로그를 남기고 보유잔고 화면을 서버 기준으로 다시 반영
startup 직후에는 기존 로그인 직후 잔고조회가 있으므로 중복 잔고조회는 하지 않음
5분/10분/30분 분봉 차트 실시간 표시에서 틱마다 캔들은 갱신하되 MA 재계산과 차트 redraw는 1초 단위 또는 새 분봉 시작 시점으로 제한
차트 실시간 갱신은 화면에 선택된 단일 분봉 차트에만 적용하며 전략, 주문, MinuteCache 판단에는 관여하지 않음
```

### 확인 로그
```text
수동매도 후 ka10074 실현손익 갱신 확인
실현손익 변경 감지 후 kt00005 보유잔고 재조회 흐름 확인
보유잔고 화면 반영 확인
```

### 검증
```text
dotnet build KHStrategyLab.csproj -p:Platform=x64
빌드 성공 / 경고 0개 / 오류 0개
```

이 문서는 2026-05-25 이후 변경 기록을 남기는 문서다. 과거 개발 중 기록은 `Archive/` 폴더의 문서를 본다.

---

## 2026-05-25 / Codex / 기준봉 등급·비중 2단계 재정리

### 코드 변경

```text
기준봉 점수 로그와 주문 사전점검의 등급 표기를 GridLeading 화면 기준과 통일
기존 4단계 A/B/C/D 등급 표시는 테스트 기준에서 제외
현재 테스트 기준은 2단계만 사용
70% 이상은 A, 70% 미만은 B
화면과 로그 표기는 등급+최종순위 형식 사용: A1, A2, B3, B4
주문 비중은 기존 확정 기준 유지: 70% 이상 100%, 70% 미만 50%
점수 파일 누락, 조회 실패, 매칭 실패 시 50% fallback
주문 사전점검 로그도 저장 파일의 과거 Grade 값을 그대로 쓰지 않고 ScorePercent+FinalRank로 A2/B3를 재계산
```

### 검증

```text
dotnet build KHStrategyLab.csproj
dotnet build KHStrategyLab.csproj -p:Platform=x64
빌드 성공 / 경고 0개 / 오류 0개
게시 프로필 Properties/PublishProfiles/FolderProfile.pubxml은 로컬 게시 설정으로 보고 백업 제외
```

---

## 2026-05-25 / Codex / 5분·10분·30분 차트와 분봉 실시간 표시 추가

### 코드 변경

```text
차트 버튼을 일봉/5분/10분/30분 구조로 확장
분봉 차트 조회를 공통 함수로 정리하고 tic_scope=5/10/30을 전달
기존 WebSocket 0B 주식체결 수신값을 재사용해 현재 화면 분봉 차트의 진행봉 갱신
차트 실시간 갱신은 현재 표시 중인 종목 1개와 분봉 차트 상태에서만 작동
일봉 상태에서는 실시간 차트 갱신을 하지 않음
차트용 실시간 진행봉과 이평선은 표시 전용이며 전략/주문/MinuteCache 판단으로 역류하지 않음
차트 redraw 제한은 장중 체감 테스트를 위해 일단 적용하지 않음
```

### API 사용 내역

```text
초기 분봉 조회: ka10080 / endpoint=/api/dostk/chart / stk_cd=KRX 6자리 또는 NXT _NX / tic_scope=5,10,30
초기 일봉 조회: ka10081 / endpoint=/api/dostk/chart / KRX, NXT(_NX), SOR(_AL) 기존 흐름 유지
실시간 차트 갱신: 기존 WebSocket 0B 주식체결 수신 재사용
0B 사용 필드: 20(체결시간), 10(현재가), 15(체결량/거래량)
차트용 WebSocket 신규 연결 없음
전략 점검용 REST 반복 조회 없음
```

### 검증

```text
dotnet build KHStrategyLab.csproj
dotnet build KHStrategyLab.csproj -p:Platform=x64
빌드 성공 / 경고 0개 / 오류 0개
장중 0B 실시간 차트 체감은 다음 장중 확인 예정
```

---

## 2026-05-25 / Codex / GridLeading 기준봉 등급 표시

### 코드 변경

```text
조건00 추적후보 GridLeading에 기준봉 등급 컬럼 추가
저장된 base_candle_scores.json을 읽어 A1, A2, B4 같은 형식으로 표시
70점 이상은 A, 70점 미만은 B로 단순 표시
숫자는 기준봉 점수 최종 순위를 사용
점수 파일이 없거나 평가 대기 상태이면 '-'로 표시
앱 시작 시 저장 점수를 반영하고, 새 기준봉 점수 생성 후에도 화면에 즉시 반영
기준봉 점수 조회 실패 시 주문 비중 fallback을 50%로 보정
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

---

## 2026-05-25 / Codex / 기준봉 점수 비중 정책 단순화

### 코드 변경

```text
기준봉 점수 기반 권장비중을 2단계로 단순화
70점 이상은 종목당 진입예산의 100%
70점 미만은 종목당 진입예산의 50%
기존 점수 파일에 0% 이하 비중이 남아 있어도 주문 단계에서는 50%로 보정
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

---

## 2026-05-25 / Codex / MA 회복초입 동적 청산 규칙 적용

### 코드 변경

```text
조건00 MA 회복초입 매수신호에 전용 청산모드(TEN_MIN_CLOSE_BELOW_MA60_TRAILING_5_2_80) 추가
MA 회복초입은 TargetPrice/StopPrice 고정값이 없어도 리스크가드 통과 가능하게 분기
손절 기준을 최신 완성 10분봉 종가 < 최신 MA60으로 적용
진입가 대비 +5% 도달 시 트레일링 활성화
트레일링 활성 후 관측 고가 대비 -2% 하락 시 보유수량 80% 부분매도
부분매도 후 잔량은 프로그램 관리 포지션에 유지
주문 사전점검 로그에 청산모드 표시
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
MA 회복초입의 TargetPrice=0 / StopPrice=0 차단 문제는 전용 청산모드 분기로 해결
전일상한가 몸통눌림 시가회복 전략은 기존 고정 목표/손절 구조 유지
```

---

## 2026-05-25 / Codex / 기준봉 점수 기반 비중조절기 주문 반영

### 코드 변경

```text
D0 기준봉 점수 저장 파일(base_candle_scores.json)을 주문 사전점검에서 조회
조건00/전일상한가 계열 매수신호에 기준봉 날짜를 전달
종목당 진입예산에 SuggestedBudgetPercent를 적용해 주문 수량 계산
점수 매칭 실패 시 조건00 계열은 보수적으로 50% 비중 적용
실주문 사전점검 로그에 원예산, 적용비중, 점수출처, 등급, 점수율 표시
TOP20은 휴장일에도 1분 REST 조회를 유지하고, 휴장일 0B 등록만 중지
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

---

## 2026-05-25 / Codex / 매수신호 파일명 정리 및 기준봉 문서 추가

### 코드 변경

```text
MainWindow.Strategy.BuySignalTest.cs를 MainWindow.Strategy.BuySignalCheck.cs로 변경
런타임 타이머/정리 루틴의 BuySignalTest 명칭을 BuySignalCheck로 정리
```

### 문서 변경

```text
기준봉(상한가) 매매시 고려할 사항.MD 추가
상한가 기준봉의 질, 거래대금, 매물소화, 접근-반응-확인 매수 프레임 정리
아카이브 문서의 매수신호 파일명 참조를 BuySignalCheck로 갱신
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
```

---

## 2026-05-25 / Codex / 휴장일 및 분봉 요청 안정화

### 코드 변경

```text
차트/로그에서 불필요한 봉 개수 표시 제거
MA신호등 표현을 관찰에서 관망으로 변경
휴장일 판정 보강: 2026-05-25 내장 휴장일 fallback 추가
휴장일 TOP20 자동 갱신 중지: 로그인 시 1회 표시 후 주기 갱신 보류
휴장일 지수는 최종값 1회 표시 후 자동 갱신 대기 흐름 확인
전략 분봉 요청을 직렬화하고 1초 5회 제한 기준에 맞춰 최소 210ms 간격 적용
429 발생 시 임의 즉시 재시도 제거, 기존 재시도 주기에 맡기도록 정리
10분봉 차트 제목에서 요청코드= 문구 제거, 실제 요청 코드와 _NX 구분을 바로 표시
```

### 문서 변경

```text
Strategy Manual 및 Archive 문서의 관찰 표현을 관망으로 정리
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
휴장일 실행 로그에서 TOP20 자동 갱신 중지 및 0B 등록 보류 확인
분봉 초기 로드가 429 없이 순차 완료되는 흐름 확인
```

---

## 2026-05-25 / Codex / 안정화 및 문서 정리

### 코드 변경

```text
WebSocket 중복 연결 방지
WebSocket 구독/클라이언트 정리 루틴 추가
완전 종료 시 타이머/WebSocket/트레이/HttpClient 정리
차트 선택 시 종목정보 조회 병행
회전율 직접 응답 또는 거래량/상장주식수 기반 계산
TOP20 갱신 시 Clear/Add 제거
TOP20 기존 행 객체 유지 후 값만 갱신
실시간 체결/종목정보/MA 신호 갱신 시 불필요한 전체 Refresh 제거
자동 선택 복원 중 차트 재조회 차단
```

### 문서 변경

```text
기존 개발 중 문서 6개를 Docs/Archive로 이동
현재 기준 사용자 매뉴얼 작성
현재 기준 개발자 구조 문서 작성
현재 기준 전략 매뉴얼 작성
이후 업데이트 전용 로그 문서 작성
```

### 검증

```text
dotnet build KHStrategyLab.csproj
빌드 성공 / 경고 0개 / 오류 0개
config.json, Storage, Logs, DB류 Git 추적 없음
```
