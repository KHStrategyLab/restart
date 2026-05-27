#nullable disable

using System;

using System.IO;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

namespace KHStrategyLab

{

 public partial class MainWindow : Window

 {

 public MainWindow()

 {

 InitializeComponent();

 // 실행 로그/점검용 원문은 파일로 저장하지 않는다.

 // 화면 로그창 최신 500줄만 유지한다.

 Directory.CreateDirectory(_dataDir);

 Directory.CreateDirectory(_storageDir);

 LoadConfig();

 LoadWatchCandidates();

 LoadLiveOrderState();

 GridBalance.ItemsSource = _balance;
 GridLeading.ItemsSource = _search00List;

 GridRank.ItemsSource = _rankList;

 // 같은 행을 다시 클릭해도 차트가 다시 적용되도록 보완한다.
 // DataGrid SelectionChanged는 이미 선택된 행을 다시 누르면 발생하지 않는다.
 // 그래서 다른 그리드를 본 뒤 보유종목의 기존 선택행을 다시 누르는 경우를 PreviewMouseLeftButtonDown에서 처리한다.
 GridBalance.PreviewMouseLeftButtonDown += OnStockGridPreviewMouseLeftButtonDown;
 GridLeading.PreviewMouseLeftButtonDown += OnStockGridPreviewMouseLeftButtonDown;
 GridRank.PreviewMouseLeftButtonDown += OnStockGridPreviewMouseLeftButtonDown;

 // Thin + 날개:

 // XAML 전체를 흔들지 않고, 조건00/추적 후보 그리드에

 // 10분봉 5/20/60선과 신호등 컬럼만 런타임으로 붙인다.

 InitializeLeadingMaSignalGridColumns();

 Log(" [시스템] KHStrategyLab 시작");

 InitializeTimers();

 InitializeTrayIcon();

 Loaded += async (s, e) =>

 {

 await AutoLoginAsync();

 await RefreshMarketIndexesAsync(force: true);

 await LoadDefaultDailyChartAsync();

 // MA 초기로드는 로그인 직후 일일 시장구분 재검증이 끝난 뒤 시작한다.
 // 재검증 루틴이 없거나 대상이 없을 때만 안전망 타이머로 풀린다.

 DelayLeadingMaSignalBootstrap(TimeSpan.FromSeconds(30), "WAIT_DAILY_MARKET_RECHECK");

 };

 Closing += (s, e) =>

 {

 if (!_isForceClose)

 {

 e.Cancel = true;

 Hide();

 }

 else

 {

 CleanupApplicationResources();

 }

 };

 }

 }

}
