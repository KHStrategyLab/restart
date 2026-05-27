using KHStrategyLab.Models;

namespace KHStrategyLab.Services;

public sealed class CandidateStore
{
    private const string BaseCandidatesFile = "base_candidates.json";
    private const string AccumulationSetupsFile = "accumulation_setups.json";
    private const string UsedSignalsFile = "used_signals.json";

    private readonly JsonFileStore _store;

    public CandidateStore(JsonFileStore store)
    {
        _store = store;
    }

    public List<SavedBaseCandleCandidate> LoadBaseCandidates()
    {
        return _store.LoadList<SavedBaseCandleCandidate>(BaseCandidatesFile);
    }

    public void SaveBaseCandidates(IEnumerable<SavedBaseCandleCandidate> items)
    {
        var now = DateTime.Now;
        var alive = items.Where(x => x.IsValidNow(now)).ToList();
        _store.SaveList(BaseCandidatesFile, alive);
    }

    public void UpsertBaseCandidate(SavedBaseCandleCandidate candidate)
    {
        var items = LoadBaseCandidates();
        items.RemoveAll(x => x.Code == candidate.Code && x.BaseDate.Date == candidate.BaseDate.Date);
        items.Add(candidate);
        SaveBaseCandidates(items);
    }

    public List<AccumulationCandleSetup> LoadAccumulationSetups()
    {
        return [.. _store.LoadList<AccumulationCandleSetup>(AccumulationSetupsFile).Where(x => x.IsActive(DateTime.Now))];
    }

    public void SaveAccumulationSetups(IEnumerable<AccumulationCandleSetup> items)
    {
        var now = DateTime.Now;
        _store.SaveList(AccumulationSetupsFile, items.Where(x => x.IsActive(now)).ToList());
    }

    public HashSet<string> LoadUsedSignalKeys()
    {
        return _store.LoadList<string>(UsedSignalsFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSignalUsed(string lockKey)
    {
        if (string.IsNullOrWhiteSpace(lockKey)) return false;
        return LoadUsedSignalKeys().Contains(lockKey);
    }

    public void MarkSignalUsed(string lockKey)
    {
        if (string.IsNullOrWhiteSpace(lockKey)) return;

        var set = LoadUsedSignalKeys();
        set.Add(lockKey);
        _store.SaveList(UsedSignalsFile, set.OrderBy(x => x));
    }
}
