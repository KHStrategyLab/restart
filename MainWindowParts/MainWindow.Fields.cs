#nullable disable
using Hardcodet.Wpf.TaskbarNotification;
using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Websocket.Client;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private string _appKey = "";
        private string _secretKey = "";
        private string _telegramToken = "";
        private string _telegramChatId = "";

        private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string _configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string _rawLogDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RawLogs");
        private readonly string _logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private readonly string _dataDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        private readonly string _storageDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
        private readonly string _watchPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "watch_candidates.json");
        private readonly string _marketHolidayPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "market_holidays.json");
        private readonly string _baseCandleScorePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "base_candle_scores.json");
        private readonly string _candidateFinalizeStatePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage", "candidate_finalize_state.json");

        private readonly ObservableCollection<HoldingStock> _balance = [];
        private readonly ObservableCollection<HoldingStock> _search00List = [];
        private readonly ObservableCollection<RankStock> _rankList = [];
        private readonly HashSet<string> _history00 = [];
        private readonly Dictionary<string, WatchCandidate> _watchCandidates = [];
        private readonly Dictionary<string, bool> _nxtEnableCache = [];
        private readonly Dictionary<string, string> _nxtProbeLastLoggedState = [];
        private readonly Dictionary<string, DateTime> _nxtProbeLastLoggedAt = [];
        private readonly SemaphoreSlim _nxtEnableRequestGate = new(1, 1);
        private readonly SemaphoreSlim _condition00MarketResolveGate = new(1, 1);
        private readonly SemaphoreSlim _strategyMinuteChartRequestGate = new(1, 1);
        private DateTime _lastNxtEnableRequestAt = DateTime.MinValue;
        private readonly object _nxtProbeLogLock = new();
        private DateTime _lastStrategyMinuteChartRequestAt = DateTime.MinValue;
        private readonly HttpClient _http = new();

        private string _token = "";
        private string _accNo = "";
        private string _actualSeq00 = "";
        private string _targetConditionSeq00 = "0"; // 조건검색 0번 사용
        private long _entryBudget = 300000;
        private int _maxSlots = 3;
        private bool _isHunting = false;
        private bool _isWsAuthenticated = false;
        private bool _isForceClose = false;
        private bool _dailyMarketRecheckStarted = false;
        private bool _liveOrderEnabled = false;
        private bool _oneShareLiveOrderTestMode = false;
        private bool _marketHolidayAutoRefreshNoticeLogged = false;
        private HashSet<string> _marketHolidayDates = null;
        private DateTime _marketHolidayDatesLoadedAt = DateTime.MinValue;
        private string _latestMarketSessionCode = "";
        private string _latestMarketSessionTime = "";
        private DateTime _latestMarketSessionReceivedAt = DateTime.MinValue;
        private string _lastLoggedMarketSessionCode = "";
        private DateTime _lastMarketStateLogAt = DateTime.MinValue;
        private WebsocketClient _ws;
        private IDisposable _wsMessageSubscription;
        private IDisposable _wsReconnectionSubscription;
        private readonly SemaphoreSlim _conditionWebSocketConnectGate = new(1, 1);
        private TaskbarIcon _notifyIcon;
        private bool _isShuttingDown = false;
        private bool _isRestoringGridSelection = false;
        private string _manualChartRealtimeCode = "";
        private static readonly object _logLock = new();

        [GeneratedRegex("[^0-9]+")]
        private static partial Regex NumberOnlyRegex();


        // 조건00 기준봉 OHLC 저장용: 장마감 후 하루 1회만 후보 목록에 기준봉 데이터를 채운다.
        private DispatcherTimer _strategyCandidateBaseCandleTimer;
        private DispatcherTimer _baseCandleScoreTimer;
        private bool _strategyCandidateBaseCandleSnapshotRunning = false;
        private bool _baseCandleScoreRunning = false;
        private string _lastBaseCandleScoreRunKey = "";
        private string _lastBaseCandleFollowupRunKey = "";

        private sealed class WatchCandidate
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string Sources { get; set; } = "";

            // 조건00 편입 후 ka10100 NXT 가능 여부를 확인해 전략 시장을 분리한다.
            // KRX/NXT 전략은 같은 조건00 후보에서 출발하더라도 서로 다른 전략코드로 추적한다.
            public string StrategyMarket { get; set; } = "PENDING"; // PENDING / KRX / NXT
            public string ConditionMarket { get; set; } = "조건00";
            public string MinuteChartMarket { get; set; } = "PENDING"; // KRX / NXT
            public string RealtimePriceMarket { get; set; } = "PENDING";
            public string DisplayMarket { get; set; } = "PENDING";
            public bool NxtEnabled { get; set; } = false;
            public string StrategyCode { get; set; } = "PENDING_CONDITION00";
            public string StrategyGroup { get; set; } = "CONDITION00";
            public string MarketResolveSource { get; set; } = "PENDING";
            public string MarketResolveStatus { get; set; } = "PENDING";
            public int MarketResolveRetryCount { get; set; }
            public DateTime? LastMarketResolveAttemptAt { get; set; }
            public DateTime? MarketResolvedAt { get; set; }

            public long LastPrice { get; set; }
            public double? StockInfoChangeRatePercent { get; set; }
            public double? StockInfoTurnoverRatePercent { get; set; }
            public string StockInfoMarket { get; set; } = "";
            public string StockInfoRequestCode { get; set; } = "";
            public DateTime? StockInfoCapturedAt { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public DateTime? LastAlert { get; set; }

            // 조건00 기준봉 스냅샷.
            // 후보 편입 당시에는 비워두고, 장마감 후 1회 저장한다.
            public string BaseCandleDate { get; set; } = "";
            public long BaseOpen { get; set; }
            public long BaseHigh { get; set; }
            public long BaseLow { get; set; }
            public long BaseClose { get; set; }
            public long PreviousClose { get; set; }
            public double BaseCloseChangeRatePercent { get; set; }
            public long BaseVolume { get; set; }
            public long BaseTradingValue { get; set; }

            // 기준봉 중간값 참고 필드. 현재 매수신호 전략 기준선은 BaseHalfPrice가 아니라 BaseLow(기준봉 저가)이다.
            // 몸통 중간값도 함께 저장해 이후 전략에서 선택 가능하게 둔다.
            public long BaseHalfPrice { get; set; }
            public long BaseBodyHalfPrice { get; set; }
            public string BaseCandleMarket { get; set; } = "";
            public string BaseCandleRequestCode { get; set; } = "";
            public string BaseCandleSource { get; set; } = "";
            public DateTime? BaseCandleSavedAt { get; set; }

            // Thin MA Signal용 기준가 필드.
            // 현재 기준가 위 조건은 전일 종가(PreviousClose) 기준으로 본다.
            // BasePrice는 전일 종가를 복사해 매수신호 필터에서 우선 사용한다.
            public long BasePrice { get; set; }
            public string BasePriceSource { get; set; } = "";
            public DateTime? BasePriceSavedAt { get; set; }

            // 장마감 MA 스냅샷 저장용 필드.
            public long CloseSnapshotPrice { get; set; }
            public double Ma5_10m { get; set; }
            public double Ma20_10m { get; set; }
            public double Ma60_10m { get; set; }
            public string MaSignal { get; set; } = "";
            public long High20_5m { get; set; }
            public string MaSnapshotMarket { get; set; } = "";
            public string MaSnapshotSource { get; set; } = "";
            public DateTime? MaSnapshotAt { get; set; }

            // 조건00 회복 초입 매수 상태머신 저장용 필드.
            // 단순히 현재 녹색 신호만 보고 매수하지 않고,
            // 기준봉 이후 10분 60선 이탈 → 회복 → 녹색 전환 순서를 통과했는지 저장한다.
            public string BuyStage { get; set; } = "WAIT_PULLBACK";
            public bool HasBrokenBelowMa60 { get; set; }
            public DateTime? BrokenBelowMa60At { get; set; }
            public bool HasRecoveredMa60 { get; set; }
            public DateTime? RecoveredMa60At { get; set; }
            public bool HasGreenReady { get; set; }
            public DateTime? GreenReadyAt { get; set; }
            public DateTime? BuyStageChangedAt { get; set; }
            public string BuyStageMemo { get; set; } = "";

            // 전일 상한가봉 몸통 안 눌림 후 시가 회복 테스트 전략 전용 상태.
            // 기존 MA 회복초입 전략의 BuyStage/LastAlert와 분리해서 저장한다.
            public string PrevLimitBodyOpenRecoveryStage { get; set; } = "WAIT_BODY_PULLBACK";
            public long PrevLimitBodyPullbackLow { get; set; }
            public long PrevLimitTodayOpen { get; set; }
            public long PrevLimitEscapeCandleHigh { get; set; }
            public long PrevLimitEscapeCandleLow { get; set; }
            public string PrevLimitEscapeCandleTime { get; set; } = "";
            public DateTime? PrevLimitEscapeDetectedAt { get; set; }
            public DateTime? PrevLimitBodyOpenRecoveryAlertAt { get; set; }
            public string PrevLimitBodyOpenRecoveryMemo { get; set; } = "";
        }
    }
}
