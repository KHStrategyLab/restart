namespace KHStrategyLab.Models;

public enum MarketKind
{
    Unknown = 0,
    Krx = 1,
    Nxt = 2,
    Sor = 3
}

public enum StrategySignalType
{
    None = 0,
    Buy = 1,
    Sell = 2,
    Alert = 3
}

public enum EntrySource
{
    Unknown = 0,
    Manual = 1,
    Strategy = 2,
    TelegramApproved = 3
}

public enum CandidateStatus
{
    Watching = 0,
    WaitingSignal = 1,
    SignalUsed = 2,
    Expired = 3,
    Excluded = 4
}
