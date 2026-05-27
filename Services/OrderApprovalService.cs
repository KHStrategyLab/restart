using KHStrategyLab.Models;

namespace KHStrategyLab.Services;

public sealed class OrderApprovalService
{
    private readonly CandidateStore _candidateStore;
    private readonly AppLogger _logger;

    public OrderApprovalService(CandidateStore candidateStore, AppLogger logger)
    {
        _candidateStore = candidateStore;
        _logger = logger;
    }

    public bool CanRequestBuyApproval(StrategySignal signal)
    {
        if (!signal.IsTriggered || signal.Type != StrategySignalType.Buy) return false;
        if (string.IsNullOrWhiteSpace(signal.LockKey)) return false;
        if (_candidateStore.IsSignalUsed(signal.LockKey)) return false;
        return true;
    }

    public void MarkApprovedOrOrdered(StrategySignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.LockKey)) return;

        _candidateStore.MarkSignalUsed(signal.LockKey);
        _logger.Order($"신호 사용 잠금: {signal.LockKey} / {signal.Code} {signal.Name} / {signal.SignalPrice:N0}");
    }
}
