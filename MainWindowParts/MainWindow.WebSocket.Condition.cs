#nullable disable
using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Websocket.Client;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const string ConditionRoleTrack00 = "00_TRACK";
        private const string ConditionRoleIgnored01 = "01_IGNORED";

        private string _ignoredSeq01 = "";
        private string _ignoredConditionSeq01 = "1";

        private readonly object _conditionSeqLock = new();
        private readonly Queue<string> _pendingConditionSeqQueue = new();
        private readonly Dictionary<string, string> _conditionRoleBySeq = [];

        private async Task InitializeConditionWebSocketAsync()
        {
            if (_isShuttingDown)
                return;

            if (_ws != null && _isWsAuthenticated)
                return;

            if (!await _conditionWebSocketConnectGate.WaitAsync(0))
            {
                Log("⏭️ [WS] 연결 시도 생략 / 이전 연결 초기화 진행 중");
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_token))
                {
                    Log("❌ [WS] 토큰 없음");
                    return;
                }

                DisposeConditionWebSocket();
                _isWsAuthenticated = false;

                var client = new WebsocketClient(new Uri("wss://api.kiwoom.com:10000/api/dostk/websocket"));
                client.ReconnectTimeout = TimeSpan.FromSeconds(30);
                client.ErrorReconnectTimeout = TimeSpan.FromSeconds(10);
                _ws = client;

                _wsMessageSubscription = client.MessageReceived.Subscribe(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg.Text))
                        HandleConditionWebSocketMessage(msg.Text);
                });

                _wsReconnectionSubscription = client.ReconnectionHappened.Subscribe(info =>
                {
                    if (_isShuttingDown)
                        return;

                    _isWsAuthenticated = false;
                    Log($"🔌 [WS] 연결/재연결: {info.Type}");

                    string loginJson = JsonConvert.SerializeObject(new
                    {
                        trnm = "LOGIN",
                        token = _token
                    });

                    client.Send(loginJson);
                    Log("🔑 [WS] LOGIN 전송");
                });

                await client.Start();
            }
            catch (Exception ex)
            {
                Log($"❌ [WS 연결 오류] {ex.Message}");
            }
            finally
            {
                _conditionWebSocketConnectGate.Release();
            }
        }

        private void HandleConditionWebSocketMessage(string text)
        {
            try
            {
                JObject res = JObject.Parse(text);
                string trnm = res["trnm"]?.ToString() ?? "";

                if (trnm != "REAL" && trnm != "PING" && trnm != "0B")
                    _ = SaveRawAsync($"websocket_{trnm}", text);

                if (trnm == "PING")
                {
                    _ws?.Send(text);
                    return;
                }

                if (trnm == "LOGIN")
                {
                    string code = res["return_code"]?.ToString() ?? "";
                    string msg = res["return_msg"]?.ToString() ?? "";

                    if (code == "0")
                    {
                        _isWsAuthenticated = true;
                        Log("✅ [WS] LOGIN 성공");

                        RegisterMarketSessionStatus();
                        RegisterVisibleScreenRowsForRealtimeTrade();
                        ReRegisterRealtimeTrades();
                        RequestConditionList();
                    }
                    else
                    {
                        Log($"❌ [WS] LOGIN 실패: {code} / {msg}");
                    }
                    return;
                }

                if (trnm == "CNSRLST")
                {
                    HandleConditionList(res);
                    return;
                }

                if (trnm == "CNSRREQ")
                {
                    HandleConditionResult(res);
                    return;
                }

                if (trnm == "REAL" || trnm == "0B")
                {
                    UpdateMarketSessionStateFromRealtime(res);
                    HandleRealtimeTradeMessage(res);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [WS 메시지 처리 오류] {ex.Message}");
                Log($"[WS 원문] {text}");
            }
        }

        private void HandleConditionList(JObject res)
        {
            try
            {
                JArray data = res["data"] as JArray;
                if (data == null)
                {
                    Log("⚠️ [조건목록] data 없음");
                    return;
                }

                _actualSeq00 = "";
                _ignoredSeq01 = "";

                lock (_conditionSeqLock)
                {
                    _pendingConditionSeqQueue.Clear();
                    _conditionRoleBySeq.Clear();
                }

                foreach (JToken item in data)
                {
                    string seq = ReadConditionListValue(item, 0, "seq");
                    string name = ReadConditionListValue(item, 1, "name");

                    if (string.IsNullOrWhiteSpace(seq) && string.IsNullOrWhiteSpace(name))
                        continue;

                    Log($"🔎 [조건식] seq={seq} / name={name}");

                    if (seq == _targetConditionSeq00)
                        _actualSeq00 = seq;

                    if (seq == _ignoredConditionSeq01)
                        _ignoredSeq01 = seq;
                }

                if (!string.IsNullOrWhiteSpace(_actualSeq00))
                {
                    // 2차 경량화: 사용자가 전략 검색식을 00번으로 옮겼으므로
                    // 이제 00번이 매일 후보 저장/6거래일 추적/전략 알림의 유일한 조건검색 통로다.
                    RequestConditionRealtime(_actualSeq00, ConditionRoleTrack00);
                }
                else
                {
                    Log($"❌ [조건식] {_targetConditionSeq00}번 전략 후보 조건식을 찾지 못함. config.json ConditionSeq00 확인 필요");
                }

                if (!string.IsNullOrWhiteSpace(_ignoredSeq01))
                {
                    Log($"ℹ️ [조건01] 비활성화 / seq={_ignoredSeq01} / 00번 전략후보로 역할 이관 완료");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [조건목록 처리 오류] {ex.Message}");
            }
        }

        private string ReadConditionListValue(JToken item, int arrayIndex, params string[] keys)
        {
            if (item == null) return "";

            if (item is JObject obj)
            {
                foreach (string key in keys)
                {
                    JToken value = obj[key];
                    if (value == null)
                    {
                        JProperty prop = obj.Properties()
                            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                        value = prop?.Value;
                    }

                    string text = TokenToPlainText(value);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }

            if (item is JArray arr && arr.Count > arrayIndex)
            {
                string value = arr[arrayIndex]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private void RequestConditionList()
        {
            try
            {
                var req = new { trnm = "CNSRLST" };
                _ws.Send(JsonConvert.SerializeObject(req));
                Log("📤 [조건목록] CNSRLST 요청");
            }
            catch (Exception ex)
            {
                Log($"❌ [조건목록 요청 오류] {ex.Message}");
            }
        }

        private void RequestConditionRealtime(string seq, string role)
        {
            try
            {
                seq = (seq ?? "").Trim();
                if (string.IsNullOrWhiteSpace(seq)) return;

                lock (_conditionSeqLock)
                {
                    _conditionRoleBySeq[seq] = role;
                    _pendingConditionSeqQueue.Enqueue(seq);
                }

                var req = new
                {
                    trnm = "CNSRREQ",
                    seq = seq,
                    search_type = "1",
                    stex_tp = "K",
                    cont_yn = "N",
                    next_key = ""
                };

                _ws.Send(JsonConvert.SerializeObject(req));

                string roleText = role == ConditionRoleTrack00
                    ? "00 추적/전략후보"
                    : role == ConditionRoleIgnored01 ? "01 비활성" : "미사용";
                Log($"📡 [조건검색] 실시간 요청: seq={seq} / role={roleText} / stex_tp=K");
            }
            catch (Exception ex)
            {
                Log($"❌ [조건검색 요청 오류] {ex.Message}");
            }
        }

        private void HandleConditionResult(JObject res)
        {
            try
            {
                string returnCode = res["return_code"]?.ToString() ?? "";
                string returnMsg = res["return_msg"]?.ToString() ?? "";

                if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                {
                    Log($"❌ [조건검색] 응답 오류: code={returnCode} / msg={returnMsg}");
                    return;
                }

                JArray data = res["data"] as JArray;
                if (data == null || data.Count == 0)
                {
                    Log("ℹ️ [조건검색] 결과 없음");
                    return;
                }

                // 중요:
                // 키움 CNSRREQ 응답은 response/item에 seq가 빠지는 경우가 있다.
                // 이때 pending queue는 "응답 1건당 1번"만 사용해야 한다.
                // 기존처럼 item마다 ResolveConditionSeq(res, item)를 다시 호출하면
                // 00번 응답의 첫 종목이 pending queue의 다음 seq(01)로 오인되어
                // 조건 응답이 다른 역할로 오인되어 추적후보에 저장될 수 있다.
                string responseSeq = ResolveConditionSeq(res, null);
                string responseRole = ResolveConditionRole(responseSeq);

                foreach (JToken item in data)
                {
                    // item 자체에 seq가 있을 때만 item seq를 사용한다.
                    // item에 seq가 없으면 responseRole을 그대로 따른다.
                    // 여기서는 pending queue fallback을 절대 쓰지 않는다.
                    string itemSeq = ReadConditionSeqFromToken(item);
                    string role = string.IsNullOrWhiteSpace(itemSeq)
                        ? responseRole
                        : ResolveConditionRole(itemSeq);

                    if (string.IsNullOrWhiteSpace(role)) role = responseRole;
                    if (string.IsNullOrWhiteSpace(role)) role = ConditionRoleIgnored01;

                    string code = ReadConditionValue(item, 0,
                        "stk_cd", "stkCd", "code", "jmcode", "jm_code", "종목코드", "9001");
                    string name = ReadConditionValue(item, 1,
                        "stk_nm", "stkNm", "name", "jmname", "jm_name", "종목명");
                    string priceText = ReadConditionValue(item, 2,
                        "cur_prc", "curPrc", "now_prc", "price", "현재가", "10");

                    code = NormalizeStockCode(code);
                    long price = ParseLongSafe(priceText);

                    if (string.IsNullOrWhiteSpace(code))
                        continue;

                    AddOrUpdateConditionStock(code, name, price, role);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [조건검색 결과 처리 오류] {ex.Message}");
            }
        }

        private string ResolveConditionSeq(JObject response, JToken item)
        {
            string seq = ReadConditionSeqFromToken(item);
            if (!string.IsNullOrWhiteSpace(seq)) return seq;

            seq = ReadConditionSeqFromToken(response);
            if (!string.IsNullOrWhiteSpace(seq)) return seq;

            lock (_conditionSeqLock)
            {
                if (_pendingConditionSeqQueue.Count > 0)
                    return _pendingConditionSeqQueue.Dequeue();
            }

            return "";
        }

        private string ReadConditionSeqFromToken(JToken token)
        {
            if (token == null) return "";

            JObject values = token["values"] as JObject;
            string seq = ReadConditionValue(token, -1,
                "seq", "cond_seq", "condition_seq", "conditionSeq", "조건식번호", "조건식일련번호");
            if (!string.IsNullOrWhiteSpace(seq)) return seq.Trim();

            if (values != null)
            {
                seq = ReadConditionValue(values, -1,
                    "seq", "cond_seq", "condition_seq", "conditionSeq", "조건식번호", "조건식일련번호");
                if (!string.IsNullOrWhiteSpace(seq)) return seq.Trim();
            }

            return "";
        }

        private string ResolveConditionRole(string seq)
        {
            seq = (seq ?? "").Trim();
            if (string.IsNullOrWhiteSpace(seq)) return "";

            if (seq == _actualSeq00 || seq == _targetConditionSeq00)
                return ConditionRoleTrack00;

            if (seq == _ignoredSeq01 || seq == _ignoredConditionSeq01)
                return ConditionRoleIgnored01;

            lock (_conditionSeqLock)
            {
                if (_conditionRoleBySeq.TryGetValue(seq, out string role))
                    return role;
            }

            return "";
        }

        private string ReadConditionValue(JToken item, int arrayIndex, params string[] keys)
        {
            if (item == null) return "";

            if (item is JObject obj)
            {
                JObject values = obj["values"] as JObject;

                foreach (string key in keys)
                {
                    string value = ReadTokenValue(obj, key);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();

                    if (values != null)
                    {
                        value = ReadTokenValue(values, key);
                        if (!string.IsNullOrWhiteSpace(value))
                            return value.Trim();
                    }
                }
            }

            if (arrayIndex >= 0 && item is JArray arr && arr.Count > arrayIndex)
            {
                string value = arr[arrayIndex]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private string ReadTokenValue(JObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key)) return "";

            JToken value = obj[key];
            if (value == null)
            {
                JProperty prop = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                value = prop?.Value;
            }

            return TokenToPlainText(value);
        }

        private string TokenToPlainText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";

            if (token is JArray arr)
            {
                if (arr.Count == 0) return "";
                return arr[0]?.ToString() ?? "";
            }

            return token.ToString();
        }

        private void AddOrUpdateConditionStock(string code, string name, long price, string role)
        {
            bool isTrack00 = role == ConditionRoleTrack00;
            name = IsUsableResolvedName(name, code) ? name : "";

            // 2차 경량화: 01번은 사용하지 않고, 00번만 추적/저장/전략 후보로 처리한다.
            if (!isTrack00)
                return;

            Dispatcher.Invoke(() =>
            {
                HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                bool isNew = row == null;

                if (row == null)
                {
                    row = new HoldingStock
                    {
                        Code = code,
                        Name = string.IsNullOrWhiteSpace(name) ? "종목명조회중" : name,
                        CurrentPrice = price,
                        BuyPrice = 0,
                        VolumeText = "조건00추적",
                        TradingValueText = "조회중",
                        ChangeRateText = "-",
                        TurnoverRateText = "-"
                    };
                    _search00List.Insert(0, row);
                }
                else
                {
                    row.Name = string.IsNullOrWhiteSpace(name) ? row.Name : name;
                    if (price > 0)
                        row.CurrentPrice = price;

                    if (ShouldUseConditionTrackingVolumePlaceholder(row.VolumeText))
                        row.VolumeText = "조건00추적";
                }

                if (_search00List.Count > 80)
                    _search00List.RemoveAt(_search00List.Count - 1);

                RegisterRealtimeTrade(code);

                if (isTrack00)
                {
                    AddOrUpdateStrategyCandidateFromCondition00(code, row.Name, price);
                    QueueResolveStrategyCandidateMarket(code, row.Name);
                }

                bool isClosed = GetRealtimeMarketModeNow() == RealtimeMarketMode.Closed;
                bool shouldRefreshKrxClose = isNew || price <= 0 || isClosed;

                if (isNew)
                {
                    Log("🧭 [조건00] 저장/추적/전략알림 판단 ON");
                    Log($"🎯 [조건00편입] {row.Name}({code}) / 현재가 {price:N0} / 0B 실시간등록");
                }

                if (shouldRefreshKrxClose)
                {
                    _ = Task.Run(() => RefreshStockInfoAsync(code));
                }
            });
        }

        private void AddOrUpdateStrategyCandidateFromCondition00(string code, string name, long price)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return;
            name = IsUsableResolvedName(name, code) ? name : "";

            DateTime now = DateTime.Now;
            PruneExpiredStrategyCandidates(now, writeFile: true, reason: "조건00 편입 전 정리");

            if (WasStrategyCandidateExpiredToday(code, now))
            {
                Log($"⛔ [조건00 재추가차단] {code} / 오늘 7거래일차 삭제된 종목이라 재추가 안 함");
                return;
            }

            if (!_watchCandidates.ContainsKey(code))
            {
                _watchCandidates[code] = new WatchCandidate
                {
                    Code = code,
                    Name = string.IsNullOrWhiteSpace(name) ? "종목명조회중" : name,
                    Sources = "조건00",
                    StrategyMarket = "PENDING",
                    ConditionMarket = "조건00",
                    MinuteChartMarket = "PENDING",
                    RealtimePriceMarket = "PENDING",
                    DisplayMarket = "PENDING",
                    NxtEnabled = false,
                    StrategyCode = "PENDING_CONDITION00",
                    StrategyGroup = "CONDITION00",
                    MarketResolveSource = "PENDING",
                    MarketResolveStatus = "PENDING",
                    MarketResolveRetryCount = 0,
                    LastMarketResolveAttemptAt = null,
                    MarketResolvedAt = null,
                    LastPrice = price,
                    FirstSeen = now,
                    LastSeen = now
                };

                Log($"🆕 [조건00 후보등록] {(_watchCandidates[code].Name)}({code}) / 시장확인=PENDING / 최초편입={now:yyyy-MM-dd HH:mm:ss} / 6거래일 보관");
            }
            else
            {
                WatchCandidate candidate = _watchCandidates[code];

                if (candidate.FirstSeen == default)
                    candidate.FirstSeen = now;

                candidate.Name = string.IsNullOrWhiteSpace(name) ? candidate.Name : name;
                candidate.Sources = "조건00";
                EnsureStrategyCandidateMarketDefaults(candidate);
                if (price > 0) candidate.LastPrice = price;
                candidate.LastSeen = now;

                // 이미 추적 중인 종목은 오전/오후/다음날 다시 잡혀도 최초편입일을 유지한다.
                // 따라서 6거래일 보관 기간이 연장되지 않는다.
            }

            SaveWatchCandidates();
        }

        private void QueueResolveStrategyCandidateMarket(string code, string name)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return;

            _ = Task.Run(async () =>
            {
                await _condition00MarketResolveGate.WaitAsync();
                try
                {
                    MarkCondition00MarketResolveAttempt(code);

                    NxtResolveResult result = await TryResolveNxtEnabledAsync(code);
                    if (result.IsKnown)
                    {
                        ApplyResolvedStrategyCandidateMarket(code, name, result.IsNxtEnabled, "ka10100");
                        return;
                    }

                    HandleCondition00MarketResolveFailed(code, name, result.FailureReason);
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [조건00 시장분리 오류] {code} / {ex.Message}");
                    HandleCondition00MarketResolveFailed(code, name, "EXCEPTION");
                }
                finally
                {
                    _condition00MarketResolveGate.Release();
                }
            });
        }

        private void StartDailyTrackedMarketRecheck()
        {
            if (_dailyMarketRecheckStarted) return;
            _dailyMarketRecheckStarted = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RecheckTrackedCandidateMarketsOnceAsync();
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [시장구분 일일재검증 오류] {ex.Message}");
                    ReleaseLeadingMaSignalBootstrap("DAILY_MARKET_RECHECK_FAILED");
                }
            });
        }

        private async Task RecheckTrackedCandidateMarketsOnceAsync()
        {
            if (string.IsNullOrWhiteSpace(_token)) return;

            List<WatchCandidate> targets = [];

            void SnapshotTargets()
            {
                targets = [.. _watchCandidates.Values
                    .Where(x => x != null)
                    .Select(x => NormalizeStrategyCandidate(x, DateTime.Now))
                    .Where(x => x != null)
                    .GroupBy(x => NormalizeStockCode(x.Code))
                    .Select(g => g.OrderByDescending(x => x.LastSeen).First())];
            }

            if (Dispatcher.CheckAccess()) SnapshotTargets();
            else Dispatcher.Invoke(SnapshotTargets);

            if (targets.Count == 0)
            {
                ReleaseLeadingMaSignalBootstrap("DAILY_MARKET_RECHECK_NO_TARGETS");
                return;
            }

            Log($"🧭 [시장구분 일일재검증] 시작 / 대상={targets.Count}개 / 저장값 우선 사용 후 백그라운드 확인");

            int checkedCount = 0;
            int changedCount = 0;

            foreach (WatchCandidate candidate in targets)
            {
                string code = NormalizeStockCode(candidate.Code);
                if (string.IsNullOrWhiteSpace(code)) continue;

                checkedCount++;
                string beforeMarket = (candidate.StrategyMarket ?? "").Trim().ToUpperInvariant();
                NxtResolveResult result = await TryResolveNxtEnabledAsync(code, forceRefresh: true);
                if (!result.IsKnown)
                {
                    HandleCondition00MarketResolveFailed(code, candidate.Name, $"DAILY_RECHECK_{result.FailureReason}");
                    await Task.Delay(120);
                    continue;
                }

                bool isNxt = result.IsNxtEnabled;
                string afterMarket = isNxt ? "NXT" : "KRX";

                if (beforeMarket != afterMarket)
                {
                    changedCount++;
                    string beforeText = string.IsNullOrWhiteSpace(beforeMarket) ? "PENDING" : beforeMarket;
                    Log($"🔁 [시장구분 변경감지] {candidate.Name}({code}) / {beforeText} → {afterMarket}");
                    ApplyResolvedStrategyCandidateMarket(code, candidate.Name, isNxt, "ka10100_DAILY_RECHECK");
                }
                else
                {
                    Log($"✅ [시장구분 유지] {candidate.Name}({code}) / {afterMarket}");
                }

                await Task.Delay(120);
            }

            Log($"✅ [시장구분 일일재검증] 완료 / 확인={checkedCount}개 / 변경={changedCount}개");
            ReleaseLeadingMaSignalBootstrap("DAILY_MARKET_RECHECK_DONE");
        }

        private bool ShouldUseConditionTrackingVolumePlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;

            string text = value.Trim();
            if (text == "-" || text == "조회중" || text == "복원") return true;
            if (text.Contains("조건00", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private void ApplyResolvedStrategyCandidateMarket(string code, string name, bool isNxt, string source)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return;
            name = IsUsableResolvedName(name, code) ? name : "";

            void ApplyOnUi()
            {
                if (!_watchCandidates.TryGetValue(code, out WatchCandidate candidate)) return;

                candidate.Name = string.IsNullOrWhiteSpace(name) ? candidate.Name : name;
                candidate.Sources = "조건00";
                ApplyStrategyCandidateMarketTag(candidate, isNxt, source);

                HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                if (row != null)
                {
                    if (ShouldUseConditionTrackingVolumePlaceholder(row.VolumeText))
                        row.VolumeText = isNxt ? "조건00추적/NXT" : "조건00추적/KRX";

                    if (IsStrategyMarketPlaceholderTurnoverText(row.TurnoverRateText)) row.TurnoverRateText = ResolveStrategyCandidateTurnoverText(candidate);
                }

                SaveWatchCandidates();
                Log($"🧭 [조건00 시장분리] {candidate.Name}({code}) → {candidate.StrategyMarket} 확정 / source={candidate.MarketResolveSource}");
                _ = Task.Run(() => RefreshStockInfoAsync(code, "KRX"));
                if (isNxt && AccountShouldUseNxtCloseNow())
                    _ = RefreshSearch00NxtClosePricesAsync($"조건00 NXT 확정 후 장외 종가고정 / source={source}");
                QueueLoadLeadingMaSignalForResolvedCandidate(code, "MARKET_RESOLVED");
            }

            if (Dispatcher.CheckAccess()) ApplyOnUi();
            else Dispatcher.Invoke(ApplyOnUi);
        }

        private void EnsureStrategyCandidateMarketDefaults(WatchCandidate candidate)
        {
            if (candidate == null) return;

            if (string.IsNullOrWhiteSpace(candidate.ConditionMarket)) candidate.ConditionMarket = "조건00";
            if (string.IsNullOrWhiteSpace(candidate.StrategyGroup)) candidate.StrategyGroup = "CONDITION00";

            bool hasLegacyKrxFallbackSource = IsDangerousKrxFallbackSource(candidate.MarketResolveSource);
            string market = (candidate.StrategyMarket ?? "").Trim().ToUpperInvariant();
            if (hasLegacyKrxFallbackSource && market == "KRX" && !candidate.NxtEnabled)
                market = "PENDING";

            if (market != "KRX" && market != "NXT")
            {
                string minuteMarket = (candidate.MinuteChartMarket ?? "").Trim().ToUpperInvariant();
                string realtimeMarket = (candidate.RealtimePriceMarket ?? "").Trim().ToUpperInvariant();
                string displayMarket = (candidate.DisplayMarket ?? "").Trim().ToUpperInvariant();

                if (candidate.NxtEnabled) market = "NXT";
                else if (minuteMarket == "NXT" || realtimeMarket == "NXT" || displayMarket == "NXT") market = "NXT";
                else if (!hasLegacyKrxFallbackSource && (minuteMarket == "KRX" || realtimeMarket == "KRX" || displayMarket == "KRX")) market = "KRX";
                else market = "PENDING";
            }

            if (market == "NXT")
                ApplyStrategyCandidateMarketTag(candidate, isNxt: true, string.IsNullOrWhiteSpace(candidate.MarketResolveSource) ? "RESTORE" : candidate.MarketResolveSource, updateResolvedTime: false);
            else if (market == "KRX")
                ApplyStrategyCandidateMarketTag(candidate, isNxt: false, string.IsNullOrWhiteSpace(candidate.MarketResolveSource) ? "RESTORE" : candidate.MarketResolveSource, updateResolvedTime: false);
            else
            {
                candidate.StrategyMarket = "PENDING";
                candidate.MinuteChartMarket = "PENDING";
                candidate.RealtimePriceMarket = "PENDING";
                candidate.DisplayMarket = "PENDING";
                candidate.NxtEnabled = false;
                candidate.StrategyCode = string.IsNullOrWhiteSpace(candidate.StrategyCode) ? "PENDING_CONDITION00" : candidate.StrategyCode;
                candidate.StrategyGroup = "CONDITION00";
                candidate.MarketResolveStatus = string.IsNullOrWhiteSpace(candidate.MarketResolveStatus) ? "PENDING" : candidate.MarketResolveStatus;
                candidate.MarketResolveSource = IsDangerousKrxFallbackSource(candidate.MarketResolveSource) || string.IsNullOrWhiteSpace(candidate.MarketResolveSource)
                    ? "PENDING"
                    : candidate.MarketResolveSource;
            }
        }

        private void ApplyStrategyCandidateMarketTag(WatchCandidate candidate, bool isNxt, string source, bool updateResolvedTime = true)
        {
            if (candidate == null) return;

            string market = isNxt ? "NXT" : "KRX";
            candidate.StrategyMarket = market;
            candidate.ConditionMarket = "조건00";
            candidate.MinuteChartMarket = market;
            candidate.RealtimePriceMarket = market;
            candidate.DisplayMarket = market;
            candidate.NxtEnabled = isNxt;
            candidate.StrategyGroup = isNxt ? "NXT_CONDITION00" : "KRX_CONDITION00";
            candidate.StrategyCode = isNxt ? "NXT_CONDITION00_PULLBACK_BREAK" : "KRX_CONDITION00_PULLBACK_BREAK";
            candidate.MarketResolveSource = string.IsNullOrWhiteSpace(source) ? "UNKNOWN" : source;
            candidate.MarketResolveStatus = "RESOLVED";
            candidate.MarketResolveRetryCount = 0;
            candidate.LastMarketResolveAttemptAt = DateTime.Now;
            if (updateResolvedTime || candidate.MarketResolvedAt == null)
                candidate.MarketResolvedAt = DateTime.Now;
        }

        private void MarkCondition00MarketResolveAttempt(string code)
        {
            void ApplyOnUi()
            {
                if (!_watchCandidates.TryGetValue(code, out WatchCandidate candidate)) return;
                candidate.LastMarketResolveAttemptAt = DateTime.Now;
                candidate.MarketResolveStatus = "CHECKING";
            }

            if (Dispatcher.CheckAccess()) ApplyOnUi();
            else Dispatcher.Invoke(ApplyOnUi);
        }

        private void HandleCondition00MarketResolveFailed(string code, string name, string failureReason)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return;
            name = IsUsableResolvedName(name, code) ? name : "";

            bool shouldRetry = false;
            string retryName = name;

            void ApplyOnUi()
            {
                if (!_watchCandidates.TryGetValue(code, out WatchCandidate candidate)) return;

                candidate.Name = string.IsNullOrWhiteSpace(name) ? candidate.Name : name;
                retryName = candidate.Name;
                candidate.Sources = "조건00";
                candidate.ConditionMarket = "조건00";
                candidate.LastMarketResolveAttemptAt = DateTime.Now;
                candidate.MarketResolveRetryCount++;
                candidate.MarketResolveStatus = "RETRY_WAIT";

                if (TryGetKnownCondition00Market(candidate, out string existingMarket))
                {
                    PreserveCondition00ExistingMarket(candidate, existingMarket);
                    candidate.MarketResolveSource = $"ka10100_FAIL_KEEP_EXISTING_{existingMarket}";
                    SaveWatchCandidates();

                    Log($"⚠️ [조건00 시장분리] {candidate.Name}({code}) ka10100 실패 → 기존 {existingMarket} 유지 / KRX fallback 금지 / reason={failureReason}");
                    QueueLoadLeadingMaSignalForResolvedCandidate(code, "MARKET_RESOLVE_FAILED_KEEP_EXISTING");
                }
                else
                {
                    candidate.StrategyMarket = "PENDING";
                    candidate.MinuteChartMarket = "PENDING";
                    candidate.RealtimePriceMarket = "PENDING";
                    candidate.DisplayMarket = "PENDING";
                    candidate.NxtEnabled = false;
                    candidate.StrategyCode = "PENDING_CONDITION00";
                    candidate.StrategyGroup = "CONDITION00";
                    candidate.MarketResolveSource = "ka10100_FAIL_KEEP_PENDING";
                    SaveWatchCandidates();

                    HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                    if (row != null)
                    {
                        row.VolumeText = "조건00추적/시장대기";
                        row.TurnoverRateText = "-";
                        row.Ma5Text = "-";
                        row.Ma20Text = "-";
                        row.Ma60Text = "-";
                        row.MaSignalText = "시장대기";
                    }

                    Log($"⚠️ [조건00 시장분리] {candidate.Name}({code}) ka10100 실패 → 시장확정 보류 / 매수판단 차단 / reason={failureReason}");
                }

                shouldRetry = candidate.MarketResolveRetryCount < 5;
            }

            if (Dispatcher.CheckAccess()) ApplyOnUi();
            else Dispatcher.Invoke(ApplyOnUi);

            if (shouldRetry)
                QueueRetryCondition00MarketResolve(code, retryName);
        }

        private void QueueRetryCondition00MarketResolve(string code, string name)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                QueueResolveStrategyCandidateMarket(code, name);
            });
        }

        private bool TryGetKnownCondition00Market(WatchCandidate candidate, out string market)
        {
            market = "";
            if (candidate == null) return false;

            string[] values =
            [
                candidate.StrategyMarket,
                candidate.MinuteChartMarket,
                candidate.RealtimePriceMarket,
                candidate.DisplayMarket
            ];

            foreach (string value in values)
            {
                string normalized = (value ?? "").Trim().ToUpperInvariant();
                if (normalized == "NXT" || normalized == "KRX")
                {
                    market = normalized;
                    return true;
                }
            }

            if (candidate.NxtEnabled)
            {
                market = "NXT";
                return true;
            }

            return false;
        }

        private void PreserveCondition00ExistingMarket(WatchCandidate candidate, string market)
        {
            if (candidate == null) return;

            market = string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase) ? "NXT" : "KRX";
            candidate.StrategyMarket = market;
            candidate.ConditionMarket = "조건00";
            candidate.MinuteChartMarket = market;
            candidate.RealtimePriceMarket = market;
            candidate.DisplayMarket = market;
            candidate.NxtEnabled = market == "NXT";
            candidate.StrategyGroup = market == "NXT" ? "NXT_CONDITION00" : "KRX_CONDITION00";
            candidate.StrategyCode = market == "NXT" ? "NXT_CONDITION00_PULLBACK_BREAK" : "KRX_CONDITION00_PULLBACK_BREAK";
        }

        private bool IsDangerousKrxFallbackSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            return source.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase)
                && source.Contains("KRX", StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeStockCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            raw = raw.Trim().Replace("A", "").Replace("_AL", "");
            string digits = new([.. raw.Where(char.IsDigit)]);

            if (digits.Length >= 6)
                return digits.Substring(digits.Length - 6);

            return digits.PadLeft(6, '0');
        }

        private long ParseLongSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;

            value = value.Trim()
                .Replace(",", "")
                .Replace("+", "")
                .Replace("-", "");

            return long.TryParse(value, out long v) ? v : 0;
        }
    }
}
