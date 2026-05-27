#nullable disable

using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DispatcherTimer _uiTimer;
        private DispatcherTimer _kiwoomRealtimeRankTimer;
        private DispatcherTimer _kiwoomIndexTimer;
        private DispatcherTimer _kiwoomRealizedProfitTimer;

        private void InitializeTimers()
        {
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) =>
            {
                // 상태 표시용 타이머
            };
            _uiTimer.Start();

            // 키움 0198 실시간종목조회순위 TOP20.
            // qry_tp=1은 1분 기준이며, 화면 갱신은 60초마다 한 번만 요청한다.
            _kiwoomRealtimeRankTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _kiwoomRealtimeRankTimer.Tick += async (s, e) =>
            {
                await RefreshKiwoomRealtimeTop20Async();
                if (IsMarketClosedDate(DateTime.Now.Date)) return;
            };
            _kiwoomRealtimeRankTimer.Start();

            // KOSPI/KOSDAQ 지수는 REST 비동기 조회로 장중/장전/장후 모두 화면을 갱신한다.
            // API 호출 과다를 막기 위해 30초 간격으로 제한한다.
            _kiwoomIndexTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _kiwoomIndexTimer.Tick += async (s, e) =>
            {
                await RefreshMarketIndexesAsync();
            };
            _kiwoomIndexTimer.Start();

            // 당일 실현손익은 kt00005 잔고 응답에서 안정적으로 채워지는 값이 아니다.
            // 매도 후 잔고 종목은 줄어들어도 상단 실현손익이 그대로인 문제를 막기 위해
            // ka10074(일자별실현손익요청)를 가볍게 별도 갱신한다.
            _kiwoomRealizedProfitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _kiwoomRealizedProfitTimer.Tick += (s, e) =>
            {
                AccountRequestTodayRealizedProfitRefresh("timer", force: false);
            };
            _kiwoomRealizedProfitTimer.Start();
            AccountRequestTodayRealizedProfitRefresh("startup", force: true);

            // 조건00 기준봉 OHLC 저장은 후보 편입 때 하지 않는다.
            // 장마감 후 일봉이 완성된 뒤 1회만 저장해서 이후 전략 계산에 사용한다.
            InitializeStrategyCandidateBaseCandleSnapshotTimer();

            TryInitializeOptionalTimer("InitializeBaseCandleScoreTimer");

            // 추적중인 매수후보 10분봉 MA5/20/60 신호등 표시 엔진.
            // 파일이 없으면 조용히 넘어가므로 빌드 의존성이 생기지 않는다.
            TryInitializeOptionalTimer("InitializeLeadingMaSignalEngine");

            // 추적리스트 거래대금/등락률/회전율 자동 보정.
            // 시작 직후 복원된 후보가 "조회중" 상태로 남지 않도록 종목정보를 천천히 채운다.
            TryInitializeOptionalTimer("InitializeStrategyCandidateStockInfoAutoRefreshTimer");

            // 조건00 매수신호 점검 파일이 적용되어 있으면 자동 연결한다.
            // 파일이 아직 없으면 조용히 넘어가므로 빌드 의존성이 생기지 않는다.
            TryInitializeOptionalTimer("InitializeStrategyCandidateBuySignalCheckTimer");

            TryInitializeOptionalTimer("InitializePrevLimitBodyOpenRecoveryTimer");
            TryInitializeOptionalTimer("InitializeLivePositionExitTimer");

            // 중요:
            // KHStrategyLab 시작 기준에서는 전략01/전략02 전용 타이머를 만들지 않는다.
            // 00번 검색식 유입과 전략 파일 호출만 연결한다.
        }

        private void LogMarketHolidayAutoRefreshPausedOnce()
        {
            if (_marketHolidayAutoRefreshNoticeLogged) return;
            _marketHolidayAutoRefreshNoticeLogged = true;
            string reason = GetMarketClosedReason(DateTime.Now.Date);
            Log($"⏸ [휴장일] 0B 주기등록 보류: {DateTime.Now:yyyy-MM-dd} {reason}");
        }

        private void TryInitializeOptionalTimer(string methodName)
        {
            try
            {
                MethodInfo method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                method?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [타이머 선택연결 오류] {methodName} / {ex.Message}");
            }
        }
    }
}
