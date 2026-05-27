#nullable disable

using KHStrategyLab.Models;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private void BtnStart_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            bool willStart = !_isHunting;
            if (willStart)
            {
                // 시작 버튼을 누르는 순간 현재 입력값을 먼저 파일에 저장한다.
                // 저장 실패 시에는 실수 주문/감시를 막기 위해 시작하지 않는다.
                if (!SaveTradingSettingsFromUi())
                {
                    ApplySettingsInputLock(false);
                    return;
                }
            }

            _isHunting = willStart;
            ApplySettingsInputLock(_isHunting);
            BtnStart.Content = _isHunting ? "감시 중지" : "시스템 시작";
            Log(_isHunting ? $"▶ [감시] 시작 / 진입예산 {GetCurrentBudgetTextForLog()}원 / 슬롯제한 {GetCurrentMaxSlotsTextForLog()}개 / 설정 입력창 잠금" : "■ [감시] 중지 / 설정 입력창 잠금 해제");
            Log(_liveOrderEnabled ? "[실주문상태] LiveOrderEnabled=ON / KRX=KRX 주문 / NXT가능종목=SOR 주문" : "[실주문상태] LiveOrderEnabled=OFF / 신호만 발생 / 주문전송 없음");

            if (_isHunting)
            {
                // 조건검색 WebSocket은 로그인 직후 이미 시작된다.
                // 시작 버튼은 감시 상태만 ON/OFF 하고, 조건00/01을 다시 불러오지 않는다.
                if (!_isWsAuthenticated)
                {
                    Log("⚠️ [감시] WS 미인증, 조건검색 WebSocket 1회 재연결 시도");
                    _ = Task.Run(async () => await InitializeConditionWebSocketAsync());
                }
                else
                {
                    Log("ℹ️ [감시 시작] 0B 연결 유지");
                }
            }
        }

        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = NumberOnlyRegex().IsMatch(e.Text);
        }

        private string GetCurrentBudgetTextForLog()
        {
            string text = (InputAmount?.Text ?? "").Replace(",", "").Trim();
            return long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out long budget) ? budget.ToString("N0") : text;
        }

        private string GetCurrentMaxSlotsTextForLog()
        {
            string text = (InputMaxSlots?.Text ?? "").Replace(",", "").Trim();
            return int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int maxSlots) ? maxSlots.ToString("N0") : text;
        }

        private void OnStockSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringGridSelection) return;

            DataGrid grid = sender as DataGrid;
            if (grid == null) return;

            if (grid.SelectedItem is StockGridRow row && !string.IsNullOrWhiteSpace(row.Code))
                ApplySelectedStockToChart(row);
        }

        private void OnStockGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isRestoringGridSelection) return;

            DataGrid grid = sender as DataGrid;
            if (grid == null) return;

            if (e.OriginalSource is not DependencyObject source)
                return;

            DataGridRow clickedRow = FindVisualParent<DataGridRow>(source);
            if (clickedRow?.Item is not StockGridRow row)
                return;

            // 이미 선택된 행을 다시 누르면 SelectionChanged가 발생하지 않는다.
            // 이때만 직접 차트를 다시 적용한다. 새 선택은 기존 OnStockSelected가 처리한다.
            if (!ReferenceEquals(grid.SelectedItem, row))
                return;

            if (string.IsNullOrWhiteSpace(row.Code))
                return;

            ApplySelectedStockToChart(row);
        }

        private void ApplySelectedStockToChart(StockGridRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Code))
                return;

            ApplyChartFooterPriceColor(row.PriceColor);
            SetManualChartRealtimeCode(row.Code);
            RegisterVisibleScreenRowsForRealtimeTrade();

            _ = Task.Run(async () =>
            {
                await FetchAndDrawDailyChartAsync(row.Code, row.Name);
            });
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private Task ShouldProtectRankNxtQuoteOnSelectionAsync(StockGridRow row)
        {
            // Thin 기준에서는 TOP20 NXT/0B 표시값 보호를 사용하지 않는다.
            return Task.FromResult(false);
        }

        private bool IsNxtDisplayPreserveTimeNow()
        {
            // Thin 기준에서는 TOP20 NXT/0B 표시값 보호를 사용하지 않는다.
            return false;
        }

        private void KospiPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _ = Task.Run(() => FetchAndDrawIndexDailyChartAsync("001", "KOSPI"));
        }

        private void KosdaqPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _ = Task.Run(() => FetchAndDrawIndexDailyChartAsync("101", "KOSDAQ"));
        }

        private void BtnChartDaily_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _ = Task.Run(() => ReloadCurrentChartAsDailyAsync());
        }

        private void BtnChartFiveMinute_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _ = Task.Run(() => ReloadCurrentChartAsFiveMinuteAsync());
        }

        private void BtnChartTenMinute_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _ = Task.Run(() => ReloadCurrentChartAsTenMinuteAsync());
        }

        private void BtnChartThirtyMinute_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _ = Task.Run(() => ReloadCurrentChartAsThirtyMinuteAsync());
        }

        private void ChartCanvas_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            DrawDailyChartCanvas();
        }
    }
}
