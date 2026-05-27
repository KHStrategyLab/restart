#nullable disable

using System;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private void CleanupApplicationResources()
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            _isHunting = false;
            _isWsAuthenticated = false;

            StopTimer(ref _uiTimer);
            StopTimer(ref _kiwoomRealtimeRankTimer);
            StopTimer(ref _kiwoomIndexTimer);
            StopTimer(ref _kiwoomRealizedProfitTimer);
            StopTimer(ref _strategyCandidateBaseCandleTimer);
            StopTimer(ref _baseCandleScoreTimer);
            StopTimer(ref _leadingMaSignalBootstrapTimer);
            StopTimer(ref _strategyCandidateBuySignalCheckTimer);
            StopTimer(ref _prevLimitBodyOpenRecoveryTimer);
            StopTimer(ref _livePositionExitTimer);

            DisposeConditionWebSocket();
            _notifyIcon?.Dispose();
            _notifyIcon = null;

            _http?.Dispose();
        }

        private static void StopTimer(ref DispatcherTimer timer)
        {
            if (timer == null)
                return;

            timer.Stop();
            timer = null;
        }

        private void DisposeConditionWebSocket()
        {
            _wsMessageSubscription?.Dispose();
            _wsMessageSubscription = null;

            _wsReconnectionSubscription?.Dispose();
            _wsReconnectionSubscription = null;

            _ws?.Dispose();
            _ws = null;
        }
    }
}
