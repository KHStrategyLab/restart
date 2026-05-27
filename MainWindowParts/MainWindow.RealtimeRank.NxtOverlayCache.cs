#nullable disable

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        // Thin 기준:
        // 0198 TOP20은 조회 결과 그대로 표시한다.
        // 장중 KRX 0B 실시간 보정, 장전/장후 NXT 0B 오버레이 보정, 리로드 후 메모리 재적용을 하지 않는다.
        //
        // 이 파일은 기존 NXT 오버레이 캐시 훅을 명시적으로 비워 두기 위한 호환용 파일이다.
        // TOP20 가격 표시를 다시 보정형으로 되돌릴 때는 save1의 MainWindow.RealtimeRank.NxtOverlayCache.cs를 기준으로 복원하면 된다.

        private void EnsureRankNxtOverlayHooked()
        {
        }

        private void ScheduleRankNxtOverlayAfterTop20Reload(string reason)
        {
        }

        private int ApplyRankNxtOverlayMemoryToTop20Rows(string reason)
        {
            return 0;
        }
    }
}
