#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DispatcherTimer _strategyCandidateStockInfoRefreshTimer;
        private bool _isStrategyCandidateStockInfoRefreshing;
        private DateTime _lastStrategyCandidateStockInfoAutoRefreshLogAt = DateTime.MinValue;

        private void InitializeStrategyCandidateStockInfoAutoRefreshTimer()
        {
            if (_strategyCandidateStockInfoRefreshTimer != null)
                return;

            _strategyCandidateStockInfoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20)
            };

            _strategyCandidateStockInfoRefreshTimer.Tick += async (s, e) =>
            {
                await RefreshStrategyCandidateStockInfosIfNeededAsync("timer");
            };

            _strategyCandidateStockInfoRefreshTimer.Start();

            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                await RefreshStrategyCandidateStockInfosIfNeededAsync("startup");
            });
        }

        private async Task RefreshStrategyCandidateStockInfosIfNeededAsync(string reason)
        {
            if (_isStrategyCandidateStockInfoRefreshing)
                return;

            if (string.IsNullOrWhiteSpace(_token))
                return;

            List<string> targets = [];

            try
            {
                _isStrategyCandidateStockInfoRefreshing = true;

                await Dispatcher.InvokeAsync(() =>
                {
                    targets = _search00List
                        .Where(row => row != null)
                        .Where(row => IsStrategyCandidateStockInfoRefreshNeeded(row))
                        .Select(row => NormalizeStockCode(row.Code))
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct()
                        .Take(25)
                        .ToList();

                    foreach (string code in targets)
                    {
                        HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                        if (row != null && IsPlaceholderTradingValueText(row.TradingValueText))
                            row.TradingValueText = "조회중";
                    }
                });

                if (targets.Count == 0)
                    return;

                if ((DateTime.Now - _lastStrategyCandidateStockInfoAutoRefreshLogAt).TotalSeconds >= 30)
                {
                    _lastStrategyCandidateStockInfoAutoRefreshLogAt = DateTime.Now;
                    Log($"ℹ️ [추적종목 종목정보] KRX 일거래대금/회전율 자동조회 {targets.Count}개 / {reason}");
                }

                foreach (string code in targets)
                {
                    // 추적리스트의 거래대금은 전략시장(NXT/KRX) 실시간 거래대금이 아니라
                    // 후보 비교용 KRX 일거래대금으로 고정한다.
                    await RefreshStockInfoAsync(code, "KRX");
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [추적종목 종목정보 자동조회 오류] {ex.Message}");
            }
            finally
            {
                _isStrategyCandidateStockInfoRefreshing = false;
            }
        }

        private bool IsStrategyCandidateStockInfoRefreshNeeded(StockGridRow row)
        {
            if (row == null)
                return false;

            string code = NormalizeStockCode(row.Code);
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string volumeText = row.VolumeText ?? "";
            if (!volumeText.Contains("조건00", StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsPlaceholderTradingValueText(row.TradingValueText))
                return true;

            if (string.IsNullOrWhiteSpace(row.TurnoverRateText) || row.TurnoverRateText == "-")
                return true;

            if (string.IsNullOrWhiteSpace(row.ChangeRateText) || row.ChangeRateText == "-")
                return true;

            return false;
        }

        private bool IsPlaceholderTradingValueText(string text)
        {
            text = (text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (text == "-" || text == "조회중" || text == "복원" || text == "시장확인중")
                return true;

            if (text.StartsWith("T+", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
