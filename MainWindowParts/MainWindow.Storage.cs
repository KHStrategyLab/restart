#nullable disable
using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const int StrategyCandidateKeepBusinessDays = 6;
        private DateTime _lastWatchCandidateSaveLogAt = DateTime.MinValue;
        private DateTime _lastWatchCandidatePruneLogAt = DateTime.MinValue;

        private string CandidateUniverseDir => Path.Combine(_storageDir, "CandidateUniverse");
        private string CandidateUniverseActivePath => Path.Combine(CandidateUniverseDir, "candidate_universe_active.json");
        private string CandidateUniverseDailyPath(DateTime now) => Path.Combine(CandidateUniverseDir, $"candidate_universe_{now:yyyyMMdd}.json");
        private string CandidateUniverseExpiredPath(DateTime now) => Path.Combine(CandidateUniverseDir, $"candidate_universe_expired_{now:yyyyMMdd}.json");

        private void LoadWatchCandidates()
        {
            try
            {
                Directory.CreateDirectory(_storageDir);
                Directory.CreateDirectory(CandidateUniverseDir);

                string sourcePath = File.Exists(CandidateUniverseActivePath)
                    ? CandidateUniverseActivePath
                    : _watchPath;

                if (!File.Exists(sourcePath)) return;

                string json = File.ReadAllText(sourcePath);
                var data = ReadStrategyCandidateMapFromFile(sourcePath);

                if (data.Count == 0)
                {
                    Log("ℹ️ [감시복원] 복원할 조건00 추적후보가 없습니다.");
                    return;
                }

                DateTime now = DateTime.Now;
                var active = new Dictionary<string, WatchCandidate>();
                var expired = new List<WatchCandidate>();

                foreach (var candidate in data.Values)
                {
                    WatchCandidate normalized = NormalizeStrategyCandidate(candidate, now);
                    if (normalized == null) continue;

                    if (IsStrategyCandidateExpired(normalized, now))
                    {
                        expired.Add(normalized);
                        continue;
                    }

                    string code = NormalizeStockCode(normalized.Code);
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    if (!active.ContainsKey(code))
                    {
                        active[code] = normalized;
                    }
                    else
                    {
                        WatchCandidate old = active[code];
                        // 중복 저장 파일이 있어도 최초 편입일은 더 이른 쪽을 유지한다.
                        if (normalized.FirstSeen < old.FirstSeen)
                            old.FirstSeen = normalized.FirstSeen;
                        if (normalized.LastSeen > old.LastSeen)
                            old.LastSeen = normalized.LastSeen;
                        if (IsUsableResolvedName(normalized.Name, normalized.Code))
                            old.Name = normalized.Name;
                        if (normalized.LastPrice > 0)
                            old.LastPrice = normalized.LastPrice;
                        MergeStrategyCandidateMarketTag(old, normalized);
                    }
                }

                if (expired.Count > 0)
                    ArchiveExpiredStrategyCandidates(expired, now);

                WriteStrategyCandidateActiveFiles(active, now);

                _watchCandidates.Clear();
                _history00.Clear();
                _search00List.Clear();

                foreach (var candidate in active.Values.OrderByDescending(x => x.LastSeen))
                {
                    string code = NormalizeStockCode(candidate.Code ?? "");
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    string name = candidate.Name ?? "";
                    if (string.IsNullOrWhiteSpace(name) || name == code)
                        name = "종목명조회중";

                    candidate.Code = code;
                    candidate.Name = name;
                    candidate.Sources = "조건00";
                    EnsureStrategyCandidateMarketDefaults(candidate);

                    _watchCandidates[code] = candidate;
                    _history00.Add(code);
                    string marketText = string.IsNullOrWhiteSpace(candidate.StrategyMarket) ? "PENDING" : candidate.StrategyMarket;
                    _search00List.Add(new HoldingStock
                    {
                        Code = code,
                        Name = name,
                        CurrentPrice = candidate.LastPrice,
                        VolumeText = $"조건00추적/{marketText}",
                        TradingValueText = $"T+{GetStrategyCandidateTrackingDay(candidate, now)} / 6거래일보관",
                        TurnoverRateText = marketText == "PENDING" ? "시장확인중" : $"{marketText}전략"
                    });
                }

                ApplyBaseCandleScoreToLeadingGrid();
                Log($"✅ [감시복원] 조건00 추적후보 {_watchCandidates.Count}개 로드 / 만료삭제 {expired.Count}개 / GridLeading 반영");
            }
            catch (Exception ex)
            {
                Log($"❌ [감시복원 오류] {ex.Message}");
            }
        }

        private void SaveWatchCandidates()
        {
            try
            {
                Directory.CreateDirectory(_storageDir);
                Directory.CreateDirectory(CandidateUniverseDir);

                DateTime now = DateTime.Now;
                PruneExpiredStrategyCandidates(now, writeFile: false, reason: "저장 전 정리");

                var saveData = _watchCandidates.Values
                    .Select(x => NormalizeStrategyCandidate(x, now))
                    .Where(x => x != null && !IsStrategyCandidateExpired(x, now))
                    .GroupBy(x => NormalizeStockCode(x.Code))
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            // 같은 코드가 중복으로 섞여 있으면 최초 편입일은 가장 이른 값으로 고정한다.
                            var ordered = g.OrderBy(x => x.FirstSeen).ToList();
                            WatchCandidate keep = ordered.First();
                            WatchCandidate latest = ordered.OrderByDescending(x => x.LastSeen).First();
                            keep.LastSeen = latest.LastSeen;
                            if (latest.LastPrice > 0) keep.LastPrice = latest.LastPrice;
                            if (IsUsableResolvedName(latest.Name, latest.Code)) keep.Name = latest.Name;
                            keep.Sources = "조건00";
                            MergeStrategyCandidateMarketTag(keep, latest);
                            EnsureStrategyCandidateMarketDefaults(keep);
                            return keep;
                        });

                WriteStrategyCandidateActiveFiles(saveData, now);

                if ((DateTime.Now - _lastWatchCandidateSaveLogAt).TotalSeconds >= 10)
                {
                    _lastWatchCandidateSaveLogAt = DateTime.Now;
                    Log($"💾 [감시저장] 조건00 추적후보 {saveData.Count}개 저장 / 01번 비활성 / 6거래일 보관");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [감시저장 오류] {ex.Message}");
            }
        }

        private void PruneExpiredStrategyCandidates(DateTime now, bool writeFile, string reason)
        {
            try
            {
                var expired = _watchCandidates.Values
                    .Where(x => x != null)
                    .Select(x => NormalizeStrategyCandidate(x, now))
                    .Where(x => x != null && IsStrategyCandidateExpired(x, now))
                    .GroupBy(x => NormalizeStockCode(x.Code))
                    .Select(g => g.OrderByDescending(x => x.LastSeen).First())
                    .ToList();

                if (expired.Count == 0)
                    return;

                foreach (var item in expired)
                {
                    string code = NormalizeStockCode(item.Code);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    _watchCandidates.Remove(code);

                    var rows = _search00List
                        .Where(x => NormalizeStockCode(x.Code) == code)
                        .Where(x => (x.VolumeText ?? "").Contains("조건00"))
                        .ToList();
                    foreach (var row in rows)
                        _search00List.Remove(row);
                }

                ArchiveExpiredStrategyCandidates(expired, now);

                if (writeFile)
                    SaveWatchCandidates();

                if ((DateTime.Now - _lastWatchCandidatePruneLogAt).TotalSeconds >= 10)
                {
                    _lastWatchCandidatePruneLogAt = DateTime.Now;
                    Log($"🧹 [조건00 후보정리] 7거래일차 추적목록 삭제 {expired.Count}개 / 사유={reason}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [조건00 후보정리 오류] {ex.Message}");
            }
        }

        private bool IsStrategyCandidateExpired(WatchCandidate candidate, DateTime now)
        {
            if (candidate == null) return true;
            DateTime firstDate = candidate.FirstSeen == default ? now.Date : candidate.FirstSeen.Date;
            // 편입 당일을 1거래일차로 보고 6거래일차까지 보관, 7거래일차 시작 시 삭제한다.
            return CountBusinessDaysInclusive(firstDate, now.Date) > StrategyCandidateKeepBusinessDays;
        }

        private int GetStrategyCandidateTrackingDay(WatchCandidate candidate, DateTime now)
        {
            if (candidate == null || candidate.FirstSeen == default) return 1;
            int day = CountBusinessDaysInclusive(candidate.FirstSeen.Date, now.Date);
            if (day < 1) day = 1;
            return day;
        }

        private int CountBusinessDaysInclusive(DateTime startDate, DateTime endDate)
        {
            DateTime start = startDate.Date;
            DateTime end = endDate.Date;
            if (end < start) return 0;

            int count = 0;
            for (DateTime date = start; date <= end; date = date.AddDays(1))
            {
                if (!IsMarketClosedDate(date))
                    count++;
            }

            return count;
        }

        private WatchCandidate NormalizeStrategyCandidate(WatchCandidate candidate, DateTime now)
        {
            if (candidate == null) return null;

            string code = NormalizeStockCode(candidate.Code ?? "");
            if (string.IsNullOrWhiteSpace(code)) return null;

            if (candidate.FirstSeen == default)
                candidate.FirstSeen = now;
            if (candidate.LastSeen == default)
                candidate.LastSeen = candidate.FirstSeen;

            candidate.Code = code;
            if (string.IsNullOrWhiteSpace(candidate.Name))
                candidate.Name = "종목명조회중";
            candidate.Sources = "조건00";
            EnsureStrategyCandidateMarketDefaults(candidate);

            return candidate;
        }

        private void MergeStrategyCandidateMarketTag(WatchCandidate target, WatchCandidate source)
        {
            if (target == null || source == null) return;

            string market = (source.StrategyMarket ?? "").Trim().ToUpperInvariant();
            if (market != "KRX" && market != "NXT") return;

            bool isNxt = market == "NXT";
            ApplyStrategyCandidateMarketTag(
                target,
                isNxt,
                string.IsNullOrWhiteSpace(source.MarketResolveSource) ? "MERGE" : source.MarketResolveSource,
                updateResolvedTime: false);

            target.MarketResolvedAt = source.MarketResolvedAt ?? target.MarketResolvedAt;
        }

        private bool WasStrategyCandidateExpiredToday(string code, DateTime now)
        {
            try
            {
                code = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(code)) return false;

                string path = CandidateUniverseExpiredPath(now);
                if (!File.Exists(path)) return false;

                var data = ReadStrategyCandidateMapFromFile(path);
                if (data == null) return false;

                return data.ContainsKey(code);
            }
            catch
            {
                return false;
            }
        }

        private void ArchiveExpiredStrategyCandidates(IEnumerable<WatchCandidate> expired, DateTime now)
        {
            try
            {
                Directory.CreateDirectory(CandidateUniverseDir);

                string path = CandidateUniverseExpiredPath(now);
                var archive = File.Exists(path)
                    ? ReadStrategyCandidateMapFromFile(path)
                    : [];

                archive ??= [];

                foreach (var item in expired)
                {
                    WatchCandidate normalized = NormalizeStrategyCandidate(item, now);
                    if (normalized == null) continue;

                    string code = NormalizeStockCode(normalized.Code);
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    normalized.Sources = "조건00_만료삭제";
                    archive[code] = normalized;
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(archive, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log($"⚠️ [조건00 만료보관 오류] {ex.Message}");
            }
        }


        private Dictionary<string, WatchCandidate> ReadStrategyCandidateMapFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return [];

            string json = File.ReadAllText(path);
            return ReadStrategyCandidateMapFromJson(json);
        }

        private Dictionary<string, WatchCandidate> ReadStrategyCandidateMapFromJson(string json)
        {
            var result = new Dictionary<string, WatchCandidate>();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            JToken root = JToken.Parse(json);
            ReadStrategyCandidateToken(root, result, fallbackCode: null, depth: 0);
            return result;
        }

        private void ReadStrategyCandidateToken(JToken token, Dictionary<string, WatchCandidate> result, string fallbackCode, int depth)
        {
            if (token == null || token.Type == JTokenType.Null || depth > 4)
                return;

            if (token is JArray arr)
            {
                foreach (JToken child in arr)
                    ReadStrategyCandidateToken(child, result, fallbackCode: null, depth: depth + 1);
                return;
            }

            if (!(token is JObject obj))
                return;

            // 새 후보 저장 파일이 Version/GeneratedAt/Candidates 같은 포장(envelope) 형태일 수 있다.
            // 이 경우 Version 문자열을 WatchCandidate로 변환하려다 감시복원 오류가 나므로,
            // 후보 컬렉션으로 보이는 속성만 먼저 찾아서 읽는다.
            string[] containerNames = ["Candidates", "Items", "Active", "Data", "Rows", "Values", "WatchCandidates"];
            bool consumedContainer = false;
            foreach (string name in containerNames)
            {
                JToken child = obj[name];
                if (child == null) continue;
                ReadStrategyCandidateToken(child, result, fallbackCode: null, depth: depth + 1);
                consumedContainer = true;
            }

            if (LooksLikeWatchCandidateObject(obj))
            {
                TryAddStrategyCandidate(obj, result, fallbackCode);
                return;
            }

            if (consumedContainer)
                return;

            foreach (JProperty prop in obj.Properties())
            {
                if (IsCandidateEnvelopeMetadata(prop.Name))
                    continue;

                if (prop.Value is JObject childObj)
                {
                    string nextFallback = NormalizeStockCode(prop.Name ?? "");
                    ReadStrategyCandidateToken(childObj, result, nextFallback, depth + 1);
                }
                else if (prop.Value is JArray childArr)
                {
                    ReadStrategyCandidateToken(childArr, result, fallbackCode: null, depth: depth + 1);
                }
            }
        }

        private bool LooksLikeWatchCandidateObject(JObject obj)
        {
            if (obj == null) return false;
            return obj["Code"] != null
                || obj["code"] != null
                || obj["종목코드"] != null
                || obj["Name"] != null
                || obj["name"] != null
                || obj["FirstSeen"] != null
                || obj["firstSeen"] != null
                || obj["LastSeen"] != null
                || obj["lastSeen"] != null
                || obj["LastPrice"] != null
                || obj["lastPrice"] != null;
        }

        private bool IsCandidateEnvelopeMetadata(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            return string.Equals(name, "Version", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GeneratedAt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "UpdatedAt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "CreatedAt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Schema", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Count", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Description", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Memo", StringComparison.OrdinalIgnoreCase);
        }

        private void TryAddStrategyCandidate(JObject obj, Dictionary<string, WatchCandidate> result, string fallbackCode)
        {
            try
            {
                WatchCandidate candidate = obj.ToObject<WatchCandidate>();
                if (candidate == null) return;

                string code = NormalizeStockCode(candidate.Code ?? "");
                if (string.IsNullOrWhiteSpace(code))
                    code = NormalizeStockCode(fallbackCode ?? "");
                if (string.IsNullOrWhiteSpace(code))
                    return;

                candidate.Code = code;
                if (string.IsNullOrWhiteSpace(candidate.Name))
                    candidate.Name = "종목명조회중";

                if (result.TryGetValue(code, out WatchCandidate old))
                {
                    if (candidate.FirstSeen != default && (old.FirstSeen == default || candidate.FirstSeen < old.FirstSeen))
                        old.FirstSeen = candidate.FirstSeen;
                    if (candidate.LastSeen != default && candidate.LastSeen > old.LastSeen)
                        old.LastSeen = candidate.LastSeen;
                    if (candidate.LastPrice > 0)
                        old.LastPrice = candidate.LastPrice;
                    if (IsUsableResolvedName(candidate.Name, code))
                        old.Name = candidate.Name;
                    EnsureStrategyCandidateMarketDefaults(old);
                    return;
                }

                EnsureStrategyCandidateMarketDefaults(candidate);
                result[code] = candidate;
            }
            catch
            {
                // 깨진 후보 한 건 때문에 전체 감시복원이 실패하면 안 된다.
            }
        }

        private void WriteStrategyCandidateActiveFiles(Dictionary<string, WatchCandidate> active, DateTime now)
        {
            Directory.CreateDirectory(CandidateUniverseDir);

            active ??= [];

            // active 파일은 6거래일 보관 중인 전체 추적 후보 목록이다.
            // 어제 편입된 종목도 6거래일 보관 기간 안이면 여기에는 계속 남는다.
            File.WriteAllText(CandidateUniverseActivePath, JsonConvert.SerializeObject(active, Formatting.Indented));

            // 날짜별 파일은 "오늘 새로 편입된 조건00 후보"만 남긴다.
            // 예전처럼 active 전체를 오늘 날짜 파일에 다시 쓰면,
            // 어제/그제 후보가 오늘 다시 들어온 것처럼 보여 헷갈린다.
            var todayAdded = active
                .Where(kv => kv.Value != null)
                .Where(kv => (kv.Value.FirstSeen == default ? now.Date : kv.Value.FirstSeen.Date) == now.Date)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            File.WriteAllText(CandidateUniverseDailyPath(now), JsonConvert.SerializeObject(todayAdded, Formatting.Indented));
        }
    }
}
