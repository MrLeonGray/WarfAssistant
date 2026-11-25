using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Fetches and caches live Warframe Worldstate (api.warframestat.us/pc?language=ru)
    /// plus browse.wf Arbitration rotation + ExportRegions fallback with offline disk snapshot support.
    /// Zero dependency on EE.log or local game client files.
    /// </summary>
    public sealed class WorldStateService
    {
        private static readonly Lazy<WorldStateService> _lazy = new(() => new WorldStateService());
        public static WorldStateService Instance => _lazy.Value;

        private const string RegionsUrl = "https://browse.wf/warframe-public-export-plus/ExportRegions.json";
        private const string ArbysUrl = "https://browse.wf/arbys.txt";
        private const string DictEnUrl = "https://browse.wf/warframe-public-export-plus/dict.en.json";
        private const string DictRuUrl = "https://browse.wf/warframe-public-export-plus/dict.ru.json";

        private readonly string _cacheDir;
        private readonly string _worldstateCachePath;
        private readonly string _regionsCachePath;
        private readonly string _arbysCachePath;
        private readonly string _enRuDictCachePath;

        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
        private Dictionary<string, (string NameEn, string SystemEn, string MissionEn, string FactionEn)>? _regionsMap;
        private readonly List<(long UnixTs, string NodeId)> _arbysSchedule = new();
        private Dictionary<string, string>? _enRuDict;
        private volatile bool _lastFetchWasLive;

        private WorldStateService()
        {
            string dataDir = Path.GetDirectoryName(StorageService.Instance.SaveDirectoryPath)
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            _cacheDir = Path.Combine(dataDir, "cache");
            try { Directory.CreateDirectory(_cacheDir); } catch { }

            _worldstateCachePath = Path.Combine(_cacheDir, "worldstate_last.json");
            _regionsCachePath = Path.Combine(_cacheDir, "export_regions.json");
            _arbysCachePath = Path.Combine(_cacheDir, "arbys.txt");
            _enRuDictCachePath = Path.Combine(_cacheDir, "en_ru_dict.json");
            LoadEnRuDictFromDisk();
        }

        public string? LoadCachedWorldstateOnly()
        {
            if (File.Exists(_worldstateCachePath))
            {
                try { return File.ReadAllText(_worldstateCachePath); }
                catch { }
            }
            return null;
        }

        public bool LastFetchWasLive
        {
            get => _lastFetchWasLive;
            private set => _lastFetchWasLive = value;
        }

        public async Task<string?> FetchOrLoadCachedWorldstateJsonAsync()
        {
            try
            {
                using var doc = await WarframeApiService.GetFullWorldstateAsync("ru").ConfigureAwait(false);
                if (doc != null)
                {
                    string raw = doc.RootElement.GetRawText();
                    if (raw.Length > 500)
                    {
                        LastFetchWasLive = true;
                        try
                        {
                            string tmp = _worldstateCachePath + ".tmp";
                            await File.WriteAllTextAsync(tmp, raw).ConfigureAwait(false);
                            File.Move(tmp, _worldstateCachePath, overwrite: true);
                        }
                        catch { }
                        return raw;
                    }
                }
            }
            catch { }

            LastFetchWasLive = false;
            if (File.Exists(_worldstateCachePath))
            {
                try { return await File.ReadAllTextAsync(_worldstateCachePath).ConfigureAwait(false); }
                catch { }
            }
            return null;
        }

        public async Task<ArbitrationModel?> ResolveCurrentArbitrationAsync(JsonElement root, DateTime utcNow)
        {
            // 1. Check live api.warframestat.us arbitration object first
            if (root.TryGetProperty("arbitration", out var arbEl) && arbEl.ValueKind == JsonValueKind.Object)
            {
                bool expired = arbEl.TryGetProperty("expired", out var exEl) && exEl.ValueKind == JsonValueKind.True;
                string node = GetStr(arbEl, "node");
                if (!expired && !string.IsNullOrEmpty(node) && !node.Equals("SolNode000", StringComparison.OrdinalIgnoreCase))
                {
                    var (n, p) = SplitAndTranslateNode(node);
                    DateTime exp = WarframeApiService.ParseIsoOrFallback(GetStr(arbEl, "expiry"), TimeSpan.FromMinutes(45));
                    if (exp > utcNow)
                    {
                        return new ArbitrationModel
                        {
                            Node = n,
                            Planet = p,
                            MissionType = TranslateMission(GetStr(arbEl, "type")),
                            Faction = TranslateFaction(GetStr(arbEl, "enemy")),
                            ExpiryUtc = exp,
                            SourceNote = "Live API"
                        };
                    }
                }
            }

            // 2. Reliable schedule fallback via browse.wf/arbys.txt + ExportRegions.json
            try
            {
                await EnsureArbitrationScheduleLoadedAsync().ConfigureAwait(false);
                if (_arbysSchedule.Count > 0 && _regionsMap != null)
                {
                    long nowUnix = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
                    long curHourUnix = nowUnix - (nowUnix % 3600);
                    string? nodeId = FindArbysNode(curHourUnix);
                    if (!string.IsNullOrEmpty(nodeId) && _regionsMap.TryGetValue(nodeId, out var reg))
                    {
                        DateTime hourStart = DateTimeOffset.FromUnixTimeSeconds(curHourUnix).UtcDateTime;
                        DateTime hourEnd = hourStart.AddHours(1);
                        return new ArbitrationModel
                        {
                            Node = reg.NameEn,
                            Planet = TranslatePlanet(reg.SystemEn),
                            MissionType = TranslateMission(reg.MissionEn),
                            Faction = TranslateFaction(reg.FactionEn),
                            ExpiryUtc = hourEnd,
                            SourceNote = "Расписание browse.wf"
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        public sealed class DeOfficialWorldFallback
        {
            public List<VoidFissureItem> Fissures { get; } = new();
            public double? FomorianPercent { get; set; }
            public double? RazorbackPercent { get; set; }

            public string? SortieBoss { get; set; }
            public string? SortieFaction { get; set; }
            public DateTime? SortieExpiryUtc { get; set; }
            public List<SortieStageModel> SortieStages { get; } = new();

            public string? ArchonRawBoss { get; set; }
            public string? ArchonBossRu { get; set; }
            public string? ArchonShardTitle { get; set; }
            public string? ArchonShardColor { get; set; }
            public DateTime? ArchonExpiryUtc { get; set; }
            public List<SortieStageModel> ArchonStages { get; } = new();

            public List<AlertItem> Alerts { get; } = new();
            public DarvoDealModel? DarvoDeal { get; set; }

            public string? TraderCharacter { get; set; }
            public string? TraderLocation { get; set; }
            public DateTime? TraderActivationUtc { get; set; }
            public DateTime? TraderExpiryUtc { get; set; }

            public List<GlobalBoosterModel> Boosters { get; } = new();

            public DateTime? NightwaveExpiryUtc { get; set; }
            public string? NightwaveTag { get; set; }
            public int? NightwavePhase { get; set; }
            public List<NightwaveChallenge> NightwaveChallenges { get; } = new();
        }

        public static string ResolveFissureRegion(string planet, string node, bool isStorm)
        {
            if (isStorm || planet.Contains("Проксима", StringComparison.OrdinalIgnoreCase) || planet.Contains("Proxima", StringComparison.OrdinalIgnoreCase))
                return "Рэйлджек (Проксима)";
            if (planet.Contains("Зариман", StringComparison.OrdinalIgnoreCase) || planet.Contains("Zariman", StringComparison.OrdinalIgnoreCase))
                return "Зариман";
            if (planet.Contains("Хольван", StringComparison.OrdinalIgnoreCase) || planet.Contains("Hollvania", StringComparison.OrdinalIgnoreCase) ||
                planet.Contains("Höllvania", StringComparison.OrdinalIgnoreCase) || planet.Contains("1999", StringComparison.OrdinalIgnoreCase))
                return "1999 (Хольвания)";
            if ((planet.Contains("Деймос", StringComparison.OrdinalIgnoreCase) || planet.Contains("Deimos", StringComparison.OrdinalIgnoreCase) || planet.Contains("Sanctum", StringComparison.OrdinalIgnoreCase)) &&
                (planet.Contains("Sanctum", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Cambire", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Munio", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Persto", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Armatus", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Nex", StringComparison.OrdinalIgnoreCase)))
                return "Санктум (Деймос)";
            if (planet.Equals("Луа", StringComparison.OrdinalIgnoreCase) || planet.Equals("Lua", StringComparison.OrdinalIgnoreCase))
                return "Луа";
            if (planet.Contains("Кув", StringComparison.OrdinalIgnoreCase) || planet.Contains("Kuva", StringComparison.OrdinalIgnoreCase))
                return "Крепость Кувы";
            if (planet.Equals("Бездна", StringComparison.OrdinalIgnoreCase) || planet.Equals("Void", StringComparison.OrdinalIgnoreCase))
                return "Бездна";

            string[] classicOrigin = { "Меркурий", "Венера", "Земля", "Марс", "Фобос", "Деймос", "Церера", "Юпитер", "Европа", "Сатурн", "Уран", "Нептун", "Плутон", "Седна", "Эрида" };
            if (!string.IsNullOrEmpty(planet) && !classicOrigin.Contains(planet, StringComparer.OrdinalIgnoreCase))
                return planet;

            return "Звёздная карта";
        }

        private (string NodeName, string Planet, string Mission, string Faction) ResolveDeNodeInfo(string nodeKey, bool isStorm = false)
        {
            string nodeName = nodeKey;
            string planet = isStorm ? "Проксима" : "Система";
            string mission = string.Empty;
            string faction = "Враги";

            if (_regionsMap != null && _regionsMap.TryGetValue(nodeKey, out var reg))
            {
                nodeName = reg.NameEn;
                planet = TranslatePlanet(reg.SystemEn);
                mission = TranslateMission(reg.MissionEn);
                faction = TranslateFaction(reg.FactionEn);
            }
            return (nodeName, planet, mission, faction);
        }

        public async Task<DeOfficialWorldFallback?> FetchOfficialDeFissuresAndThreatsAsync(
            DateTime utcNow,
            IReadOnlyList<CollectionItemModel>? catalog = null)
        {
            try
            {
                await EnsureArbitrationScheduleLoadedAsync().ConfigureAwait(false);
                string? raw = await WarframeApiService.GetStringAsync("https://api.warframe.com/cdn/worldState.php", timeoutSeconds: 10).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return null;

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var result = new DeOfficialWorldFallback();

                void ParseDeFissureArray(string propName, bool isStorm)
                {
                    if (!root.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                    foreach (var el in arr.EnumerateArray())
                    {
                        DateTime exp = ParseDeUnixExpiry(el) ?? utcNow.AddMinutes(30);
                        if (exp <= utcNow) continue;

                        string nodeKey = GetStr(el, "Node");
                        string modKey = isStorm ? GetStr(el, "ActiveMissionTier") : GetStr(el, "Modifier");
                        string tier = modKey switch
                        {
                            "VoidT1" => "Lith",
                            "VoidT2" => "Meso",
                            "VoidT3" => "Neo",
                            "VoidT4" => "Axi",
                            "VoidT5" => "Requiem",
                            "VoidT6" => "Omnia",
                            _ => "Lith"
                        };
                        bool isHard = el.TryGetProperty("Hard", out var hp) && hp.ValueKind == JsonValueKind.True;

                        var (nodeName, planet, mission, faction) = ResolveDeNodeInfo(nodeKey, isStorm);
                        if (el.TryGetProperty("MissionType", out var mtEl) && mtEl.ValueKind == JsonValueKind.String)
                        {
                            string ov = TranslateDeMissionCode(mtEl.GetString() ?? string.Empty);
                            if (!string.IsNullOrEmpty(ov)) mission = ov;
                        }
                        if (string.IsNullOrEmpty(mission))
                            mission = TranslateMission(GetStr(el, "MissionType"));

                        int order = tier switch { "Lith" => 1, "Meso" => 2, "Neo" => 3, "Axi" => 4, "Requiem" => 5, "Omnia" => 6, _ => 7 };
                        string color = tier switch { "Lith" => "#10B981", "Meso" => "#06B6D4", "Neo" => "#EAB308", "Axi" => "#F97316", "Requiem" => "#DC2626", "Omnia" => "#38BDF8", _ => "#EAB308" };

                        result.Fissures.Add(new VoidFissureItem
                        {
                            Node = nodeName,
                            Planet = planet,
                            SystemRegion = ResolveFissureRegion(planet, nodeName, isStorm),
                            MissionType = mission,
                            Tier = tier,
                            TierOrder = order,
                            TierColor = color,
                            EnemyFaction = faction,
                            ExpiryUtc = exp,
                            IsSteelPath = isHard,
                            IsVoidStorm = isStorm
                        });
                    }
                }

                ParseDeFissureArray("ActiveMissions", isStorm: false);
                ParseDeFissureArray("VoidStorms", isStorm: true);

                if (root.TryGetProperty("ProjectPct", out var pctArr) && pctArr.ValueKind == JsonValueKind.Array && pctArr.GetArrayLength() >= 2)
                {
                    if (pctArr[0].TryGetDouble(out double fom)) result.FomorianPercent = Math.Round(Math.Clamp(fom, 0, 100), 1);
                    if (pctArr[1].TryGetDouble(out double raz)) result.RazorbackPercent = Math.Round(Math.Clamp(raz, 0, 100), 1);
                }

                // Live Daily Sortie
                if (root.TryGetProperty("Sorties", out var sortiesEl) && sortiesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sEl in sortiesEl.EnumerateArray())
                    {
                        DateTime? sExp = ParseDeUnixExpiry(sEl);
                        if (!sExp.HasValue || sExp.Value <= utcNow) continue;
                        var (bossRu, factionRu) = TranslateDeSortieBoss(GetStr(sEl, "Boss"));
                        result.SortieBoss = bossRu;
                        result.SortieFaction = factionRu;
                        result.SortieExpiryUtc = sExp.Value;

                        if (sEl.TryGetProperty("Variants", out var vars) && vars.ValueKind == JsonValueKind.Array)
                        {
                            int idx = 1;
                            foreach (var v in vars.EnumerateArray())
                            {
                                var (nodeName, planet, _, _) = ResolveDeNodeInfo(GetStr(v, "node"));
                                string mType = TranslateDeMissionCode(GetStr(v, "missionType"));
                                var (modName, modDesc) = TranslateDeSortieModifier(GetStr(v, "modifierType"));
                                result.SortieStages.Add(new SortieStageModel
                                {
                                    Index = idx++,
                                    Node = string.IsNullOrEmpty(planet) ? nodeName : $"{nodeName} ({planet})",
                                    MissionType = string.IsNullOrEmpty(mType) ? GetStr(v, "missionType") : mType,
                                    Modifier = modName,
                                    ModifierDesc = modDesc
                                });
                            }
                        }
                        break;
                    }
                }

                // Live Archon Hunt (LiteSorties)
                if (root.TryGetProperty("LiteSorties", out var liteEl) && liteEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var aEl in liteEl.EnumerateArray())
                    {
                        DateTime? aExp = ParseDeUnixExpiry(aEl);
                        if (!aExp.HasValue || aExp.Value <= utcNow) continue;
                        string rawBoss = GetStr(aEl, "Boss");
                        var (bossRu, shardRu, shardCol) = TranslateArchon(rawBoss);
                        result.ArchonRawBoss = rawBoss;
                        result.ArchonBossRu = bossRu;
                        result.ArchonShardTitle = shardRu;
                        result.ArchonShardColor = shardCol;
                        result.ArchonExpiryUtc = aExp.Value;

                        if (aEl.TryGetProperty("Missions", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                        {
                            int idx = 1;
                            foreach (var m in mArr.EnumerateArray())
                            {
                                var (nodeName, planet, _, _) = ResolveDeNodeInfo(GetStr(m, "node"));
                                string mType = TranslateDeMissionCode(GetStr(m, "missionType"));
                                result.ArchonStages.Add(new SortieStageModel
                                {
                                    Index = idx++,
                                    Node = string.IsNullOrEmpty(planet) ? nodeName : $"{nodeName} ({planet})",
                                    MissionType = string.IsNullOrEmpty(mType) ? GetStr(m, "missionType") : mType
                                });
                            }
                        }
                        break;
                    }
                }

                // Live Alerts
                if (root.TryGetProperty("Alerts", out var alertsEl) && alertsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var al in alertsEl.EnumerateArray())
                    {
                        DateTime? alExp = ParseDeUnixExpiry(al);
                        if (!alExp.HasValue || alExp.Value <= utcNow) continue;
                        if (!al.TryGetProperty("MissionInfo", out var mi) || mi.ValueKind != JsonValueKind.Object) continue;

                        var (nodeName, planet, mFallback, facFallback) = ResolveDeNodeInfo(GetStr(mi, "location"));
                        string mType = TranslateDeMissionCode(GetStr(mi, "missionType"));
                        if (string.IsNullOrEmpty(mType)) mType = mFallback;
                        string fac = TranslateDeFactionCode(GetStr(mi, "faction"));
                        if (string.IsNullOrEmpty(fac)) fac = facFallback;

                        string rewStr = "Кредиты + Награда";
                        if (mi.TryGetProperty("missionReward", out var mr) && mr.ValueKind == JsonValueKind.Object)
                        {
                            var parts = new List<string>();
                            if (mr.TryGetProperty("countedItems", out var ci) && ci.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var cItem in ci.EnumerateArray())
                                {
                                    string uid = GetStr(cItem, "ItemType");
                                    int cnt = cItem.TryGetProperty("ItemCount", out var cEl) ? cEl.GetInt32() : 1;
                                    string rName = catalog != null
                                        ? ResolveLocalizedCatalogName(uid, uid.Split('/').LastOrDefault(), catalog)
                                        : TranslateCommonReward(uid.Split('/').LastOrDefault() ?? "Награда");
                                    parts.Add(cnt > 1 ? $"{cnt:N0}× {rName}" : rName);
                                }
                            }
                            if (mr.TryGetProperty("items", out var itArr) && itArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var itEl in itArr.EnumerateArray())
                                {
                                    string uid = itEl.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(uid))
                                    {
                                        string rName = catalog != null
                                            ? ResolveLocalizedCatalogName(uid, uid.Split('/').LastOrDefault(), catalog)
                                            : TranslateCommonReward(uid.Split('/').LastOrDefault() ?? "Награда");
                                        parts.Add(rName);
                                    }
                                }
                            }
                            if (parts.Count > 0)
                                rewStr = string.Join(" + ", parts);
                            else if (mr.TryGetProperty("credits", out var crEl) && crEl.TryGetInt32(out int cr) && cr > 0)
                                rewStr = $"{cr:N0} Кредитов";
                        }

                        string descRaw = GetStr(mi, "descText").Split('/').LastOrDefault() ?? "Тревога";
                        string title = descRaw switch
                        {
                            "TennoUnitedAlert" => "Тревога: Тэнно Едины",
                            "GiftFromTheLotus" => "Дар Лотос",
                            _ => "Сигнал Тревоги"
                        };

                        result.Alerts.Add(new AlertItem
                        {
                            Title = title,
                            Node = nodeName,
                            Planet = planet,
                            MissionType = mType,
                            Faction = fac,
                            Reward = rewStr,
                            ExpiryUtc = alExp.Value
                        });
                    }
                }

                // Live Darvo Daily Deal
                if (root.TryGetProperty("DailyDeals", out var dealsEl) && dealsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in dealsEl.EnumerateArray())
                    {
                        DateTime? dExp = ParseDeUnixExpiry(d);
                        if (!dExp.HasValue || dExp.Value <= utcNow) continue;
                        string uid = GetStr(d, "StoreItem");
                        string name = catalog != null
                            ? ResolveLocalizedCatalogName(uid, uid.Split('/').LastOrDefault(), catalog)
                            : (uid.Split('/').LastOrDefault() ?? string.Empty);
                        result.DarvoDeal = new DarvoDealModel
                        {
                            ItemName = name,
                            OriginalPrice = d.TryGetProperty("OriginalPrice", out var op) ? op.GetInt32() : 0,
                            SalePrice = d.TryGetProperty("SalePrice", out var sp) ? sp.GetInt32() : 0,
                            DiscountPercent = d.TryGetProperty("Discount", out var dp) ? dp.GetInt32() : 0,
                            TotalCount = d.TryGetProperty("AmountTotal", out var tp) ? tp.GetInt32() : 0,
                            SoldCount = d.TryGetProperty("AmountSold", out var sop) ? sop.GetInt32() : 0,
                            ExpiryUtc = dExp.Value
                        };
                        break;
                    }
                }

                // Live Void Trader (Baro Ki'Teer)
                if (root.TryGetProperty("VoidTraders", out var vtEl) && vtEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in vtEl.EnumerateArray())
                    {
                        DateTime? bAct = ParseDeUnixTimestamp(b, "Activation");
                        DateTime? bExp = ParseDeUnixExpiry(b);
                        if (!bExp.HasValue) continue;
                        var (nodeName, planet, _, _) = ResolveDeNodeInfo(GetStr(b, "Node"));
                        result.TraderCharacter = "Баро Ки'Тиир";
                        result.TraderLocation = string.IsNullOrEmpty(planet) ? nodeName : $"{nodeName} ({planet})";
                        result.TraderActivationUtc = bAct ?? utcNow;
                        result.TraderExpiryUtc = bExp.Value;
                        break;
                    }
                }

                // Live Global Upgrades (Weekend Boosters)
                if (root.TryGetProperty("GlobalUpgrades", out var upgEl) && upgEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in upgEl.EnumerateArray())
                    {
                        DateTime? gExp = ParseDeUnixExpiry(g);
                        if (!gExp.HasValue || gExp.Value <= utcNow) continue;
                        string rawType = GetStr(g, "UpgradeType");
                        string label = rawType switch
                        {
                            "GAMEPLAY_PICKUP_AMOUNT" => "Дроп ресурсов",
                            "GAMEPLAY_DROP_RATE" or "GAMEPLAY_RESOURCE_DROP_CHANCE" => "Шанс дропа ресурсов",
                            "GAMEPLAY_KILL_XP_AMOUNT" => "Синтез (Опыт)",
                            "GAMEPLAY_MONEY_REWARD_AMOUNT" or "GAMEPLAY_MONEY_PICKUP_AMOUNT" => "Кредиты",
                            _ => rawType.Replace("GAMEPLAY_", "").Replace('_', ' ')
                        };
                        double val = g.TryGetProperty("Value", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetDouble() : 2;
                        result.Boosters.Add(new GlobalBoosterModel
                        {
                            Title = "Глобальный бустер",
                            EffectText = $"{label} ×{val:0.##}",
                            ExpiryUtc = gExp.Value
                        });
                    }
                }

                // Live Nightwave SeasonInfo
                if (root.TryGetProperty("SeasonInfo", out var nwEl) && nwEl.ValueKind == JsonValueKind.Object)
                {
                    result.NightwaveExpiryUtc = ParseDeUnixExpiry(nwEl);
                    result.NightwaveTag = GetStr(nwEl, "AffiliationTag");
                    result.NightwavePhase = nwEl.TryGetProperty("Phase", out var phEl) && phEl.ValueKind == JsonValueKind.Number ? phEl.GetInt32() : 0;

                    if (nwEl.TryGetProperty("ActiveChallenges", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ch in chArr.EnumerateArray())
                        {
                            DateTime? chExp = ParseDeUnixExpiry(ch);
                            if (!chExp.HasValue || chExp.Value <= utcNow) continue;
                            bool isDaily = ch.TryGetProperty("Daily", out var dEl) && dEl.ValueKind == JsonValueKind.True;
                            string path = GetStr(ch, "Challenge");
                            bool isElite = path.Contains("/WeeklyHard/", StringComparison.OrdinalIgnoreCase);
                            int rep = isDaily ? 1000 : (isElite ? 7000 : 4500);
                            var (titleRu, descRu) = TranslateDeSeasonChallengePath(path);
                            result.NightwaveChallenges.Add(new NightwaveChallenge
                            {
                                Title = titleRu,
                                Description = descRu,
                                Reputation = rep,
                                IsDaily = isDaily,
                                IsElite = isElite,
                                ExpiryUtc = chExp.Value
                            });
                        }
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseDeUnixExpiry(JsonElement el) => ParseDeUnixTimestamp(el, "Expiry");

        private static DateTime? ParseDeUnixTimestamp(JsonElement el, string propName)
        {
            if (el.TryGetProperty(propName, out var expEl) &&
                expEl.TryGetProperty("$date", out var dEl) &&
                dEl.TryGetProperty("$numberLong", out var nlEl))
            {
                string? raw = nlEl.ValueKind == JsonValueKind.String ? nlEl.GetString() : nlEl.GetRawText();
                if (long.TryParse(raw, out long ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }
            return null;
        }

        private static string TranslateDeMissionCode(string code) => code switch
        {
            "MT_SURVIVAL" => "Выживание",
            "MT_DEFENSE" => "Оборона",
            "MT_INTERCEPTION" or "MT_TERRITORY" => "Перехват",
            "MT_EXCAVATION" or "MT_EXCAVATE" => "Раскопки",
            "MT_DISRUPTION" or "MT_ARTIFACT" => "Сбой",
            "MT_EVACUATION" => "Перебежчики",
            "MT_INFESTED_SALVAGE" => "Сбор ресурсов",
            "MT_MOBILE_DEFENSE" => "Моб. Оборона",
            "MT_EXTERMINATION" or "MT_EXTERMINATE" => "Зачистка",
            "MT_RESCUE" => "Спасение",
            "MT_CAPTURE" => "Захват",
            "MT_SABOTAGE" => "Диверсия",
            "MT_INTEL" or "MT_COUNTER_INTEL" or "MT_ESPIONAGE" => "Шпионаж",
            "MT_HIVE" => "Улей",
            "MT_ASSASSINATION" => "Убийство",
            "MT_ALCHEMY" or "MT_ALCHEMIST" => "Алхимия",
            "MT_VOID_CASCADE" => "Каскад Бездны",
            "MT_VOID_FLOOD" => "Потоп Бездны",
            "MT_ARMAGEDDON" => "Армагеддон Бездны",
            "MT_MIRROR_DEFENSE" => "Зеркальная оборона",
            "MT_ASSAULT" => "Штурм",
            "MT_HIJACK" => "Угон",
            "MT_RAILJACK" => "Стычка (Рэйлджек)",
            "MT_RAILJACK_VOLATILE" => "Налёт (Рэйлджек)",
            "MT_RAILJACK_ORPHIX" => "Орфикс (Рэйлджек)",
            _ => string.Empty
        };

        private static string TranslateDeFactionCode(string code) => code switch
        {
            "FC_GRINEER" => "Гринир",
            "FC_CORPUS" => "Корпус",
            "FC_INFESTATION" => "Заражённые",
            "FC_OROKIN" => "Порабощённые",
            "FC_MITW" => "Шёпот",
            "FC_SENTIENT" => "Владеющие Разумом",
            _ => string.Empty
        };

        private static (string BossRu, string FactionRu) TranslateDeSortieBoss(string code) => code switch
        {
            "SORTIE_BOSS_CORRUPTED_VOR" => ("Порабощённый Вор", "Порабощённые"),
            "SORTIE_BOSS_ALAD" => ("Алад V", "Корпус"),
            "SORTIE_BOSS_INFALAD" => ("Муталист Алад V", "Заражённые"),
            "SORTIE_BOSS_AMBULAS" => ("Амбулас", "Корпус"),
            "SORTIE_BOSS_HEK" => ("Советник Вэй Хек", "Гринир"),
            "SORTIE_BOSS_HYENA" => ("Стая Гиен", "Корпус"),
            "SORTIE_BOSS_KELA" => ("Кела Де Тэйм", "Гринир"),
            "SORTIE_BOSS_KRIL" => ("Лейтенант Лех Крил", "Гринир"),
            "SORTIE_BOSS_VOR" => ("Капитан Вор и Лех Крил", "Гринир"),
            "SORTIE_BOSS_LEPHANTIS" => ("Лефантис", "Заражённые"),
            "SORTIE_BOSS_PHORID" => ("Форид", "Заражённые"),
            "SORTIE_BOSS_RAPTOR" => ("Раптор", "Корпус"),
            "SORTIE_BOSS_RUK" => ("Генерал Саргас Рук", "Гринир"),
            "SORTIE_BOSS_TYL" => ("Тил Регор", "Гринир"),
            "SORTIE_BOSS_JACKAL" => ("Шакал", "Корпус"),
            "SORTIE_BOSS_NEF" => ("Сержант", "Корпус"),
            _ => (code.Replace("SORTIE_BOSS_", "").Replace('_', ' '), "")
        };

        private static (string NameRu, string DescRu) TranslateDeSortieModifier(string code) => code switch
        {
            "SORTIE_MODIFIER_LOW_ENERGY" => ("Снижение энергии (−75%)", "Запас энергии снижен на 75%, восстановление замедлено."),
            "SORTIE_MODIFIER_FIRE" => ("Огненные очаги", "На миссии встречаются очаги огня, уменьшающие здоровье."),
            "SORTIE_MODIFIER_FREEZE" or "SORTIE_MODIFIER_COLD" => ("Утечка криогена", "На полу зоны заморозки, замедляющие движение."),
            "SORTIE_MODIFIER_RADIATION" => ("Радиационная опасность", "Облака радиации вызывают статус радиации."),
            "SORTIE_MODIFIER_MAGNETIC" => ("Электромагнитные аномалии", "Магнитные поля накладывают магнитный статус."),
            "SORTIE_MODIFIER_FOG" => ("Густой туман", "Ограниченная видимость на локации."),
            "SORTIE_MODIFIER_ARMOR" => ("Усиленная броня врагов", "Броня противников значительно увеличена."),
            "SORTIE_MODIFIER_SHIELDS" => ("Усиленные щиты врагов", "Щиты противников значительно увеличены."),
            "SORTIE_MODIFIER_EXIMUS" => ("Оплот Эксимусов", "Повышенное число врагов Эксимус."),
            "SORTIE_MODIFIER_HAZARD_FIRE" => ("Усиление стихией: Огонь", "Враги наносят дополнительный урон Огнём и имеют сопротивление ему."),
            "SORTIE_MODIFIER_HAZARD_ICE" or "SORTIE_MODIFIER_HAZARD_COLD" => ("Усиление стихией: Холод", "Враги наносят дополнительный урон Холодом и имеют сопротивление ему."),
            "SORTIE_MODIFIER_HAZARD_ELECTRICITY" => ("Усиление стихией: Электро", "Враги наносят дополнительный урон Электричеством."),
            "SORTIE_MODIFIER_HAZARD_TOXIN" => ("Усиление стихией: Токсин", "Враги наносят дополнительный урон Токсином."),
            "SORTIE_MODIFIER_HAZARD_RADIATION" => ("Усиление стихией: Радиация", "Враги наносят дополнительный урон Радиацией."),
            "SORTIE_MODIFIER_HAZARD_MAGNETIC" => ("Усиление стихией: Магнит", "Враги наносят дополнительный урон Магнитом."),
            "SORTIE_MODIFIER_HAZARD_VIRAL" => ("Усиление стихией: Вирус", "Враги наносят дополнительный урон Вирусом."),
            "SORTIE_MODIFIER_HAZARD_CORROSIVE" => ("Усиление стихией: Коррозия", "Враги наносят дополнительный урон Коррозией."),
            "SORTIE_MODIFIER_IMPACT" => ("Усиление физ. уроном: Удар", "Атаки врагов наносят повышенный ударный урон."),
            "SORTIE_MODIFIER_PUNCTURE" => ("Усиление физ. уроном: Пронзание", "Атаки врагов наносят повышенный пронзающий урон."),
            "SORTIE_MODIFIER_SLASH" => ("Усиление физ. уроном: Разрез", "Атаки врагов наносят повышенный режущий урон."),
            "SORTIE_MODIFIER_BOW_ONLY" => ("Только Луки", "В миссию можно взять только лук."),
            "SORTIE_MODIFIER_SNIPER_ONLY" => ("Только Снайперские винтовки", "В миссию можно взять только снайперскую винтовку."),
            "SORTIE_MODIFIER_SHOTGUN_ONLY" => ("Только Дробовики", "В миссию можно взять только дробовик."),
            "SORTIE_MODIFIER_RIFLE_ONLY" => ("Только Штурмовые винтовки", "В миссию можно взять только штурмовую винтовку."),
            "SORTIE_MODIFIER_PISTOL_ONLY" => ("Только Пистолеты", "В миссию можно взять только вторичное оружие."),
            "SORTIE_MODIFIER_MELEE_ONLY" => ("Только Ближний бой", "В миссию можно взять только оружие ближнего боя."),
            _ => (code.Replace("SORTIE_MODIFIER_", "").Replace('_', ' '), "")
        };

        private static (string TitleRu, string DescRu) TranslateDeSeasonChallengePath(string path)
        {
            string key = path.Split('/').LastOrDefault() ?? path;
            return key switch
            {
                "SeasonDailyTwoForOne" => ("Двое одним выстрелом", "Пронзите и убейте 2 или более врагов одним выстрелом из лука"),
                "SeasonDailyKillEnemiesWithGas" => ("Биологическая опасность", "Убейте 150 врагов уроном Газом"),
                "SeasonDailyKillEnemiesWithFire" => ("Поджигатель", "Убейте 150 врагов уроном Огнём"),
                "SeasonDailyKillEnemiesWithCold" => ("Глубокая заморозка", "Убейте 150 врагов уроном Холодом"),
                "SeasonDailyKillEnemiesWithElectricity" => ("Громобой", "Убейте 150 врагов уроном Электричеством"),
                "SeasonDailyKillEnemiesWithToxin" => ("Химик", "Убейте 150 врагов уроном Токсином"),
                "SeasonDailyKillEnemiesWithBlast" => ("Взрывотехник", "Убейте 150 врагов уроном Взрывом"),
                "SeasonDailyPlaceGlyph" => ("Уличный Художник", "Разместите глиф в любой миссии"),
                "SeasonWeeklyPermanentCompleteMissions16" or "SeasonWeeklyPermanentCompleteMissions" => ("Миссия Выполнена", "Завершите 15 миссий"),
                "SeasonWeeklyPermanentKillEximus16" or "SeasonWeeklyPermanentKillEximus" => ("Устранитель Эксимусов", "Убейте 30 врагов Эксимус"),
                "SeasonWeeklyPermanentKillEnemies16" or "SeasonWeeklyPermanentKillEnemies" => ("Без предупреждения", "Убейте 500 противников"),
                "SeasonWeeklyBeastSlayer" => ("Убийца чудовищ", "Убейте 15 врагов-зверей / существ или мини-боссов"),
                "SeasonWeeklyCompleteClemMission" => ("Хороший друг", "Помогите Клему выполнить его еженедельное задание"),
                "SeasonWeeklyHardFriendsSurvival" => ("Выживание", "Завершите миссию Выживания длительностью не менее 20 минут"),
                "SeasonWeeklyHardRailjackMissions" => ("Элитный исследователь", "Завершите 8 миссий Рэйлджека"),
                _ => (key.Replace("SeasonWeeklyHard", "").Replace("SeasonWeeklyPermanent", "").Replace("SeasonWeekly", "").Replace("SeasonDaily", ""), "Испытание Ночной Волны")
            };
        }

        private void LoadEnRuDictFromDisk()
        {
            if (_enRuDict != null || !File.Exists(_enRuDictCachePath)) return;
            try
            {
                string json = File.ReadAllText(_enRuDictCachePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed != null && parsed.Count > 100)
                    _enRuDict = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        public async Task EnsureEnRuDictionaryLoadedAsync()
        {
            bool fresh = File.Exists(_enRuDictCachePath) &&
                         (DateTime.UtcNow - File.GetLastWriteTimeUtc(_enRuDictCachePath)).TotalDays < 7;
            if (_enRuDict != null && fresh) return;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                LoadEnRuDictFromDisk();
                fresh = File.Exists(_enRuDictCachePath) &&
                        (DateTime.UtcNow - File.GetLastWriteTimeUtc(_enRuDictCachePath)).TotalDays < 7;
                if (_enRuDict != null && fresh) return;

                var enTask = WarframeApiService.GetStringAsync(DictEnUrl, timeoutSeconds: 6);
                var ruTask = WarframeApiService.GetStringAsync(DictRuUrl, timeoutSeconds: 6);
                await Task.WhenAll(enTask, ruTask).ConfigureAwait(false);

                if (string.IsNullOrEmpty(enTask.Result) || string.IsNullOrEmpty(ruTask.Result))
                    return;

                using var enDoc = JsonDocument.Parse(enTask.Result);
                using var ruDoc = JsonDocument.Parse(ruTask.Result);
                if (enDoc.RootElement.ValueKind != JsonValueKind.Object || ruDoc.RootElement.ValueKind != JsonValueKind.Object)
                    return;

                var ruByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in ruDoc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            ruByKey[prop.Name] = val.Trim();
                    }
                }

                var enToRu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in enDoc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    string? en = prop.Value.GetString()?.Trim();
                    if (string.IsNullOrEmpty(en) || en.Length > 220 || en.Contains('|')) continue;
                    if (en.StartsWith("[PH]", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ruByKey.TryGetValue(prop.Name, out string? ru)) continue;
                    if (ru.StartsWith("[PH]", StringComparison.OrdinalIgnoreCase) || en.Equals(ru, StringComparison.Ordinal))
                        continue;

                    string cleanEn = en.Replace("<LINE_SEPARATOR>", " ").Trim();
                    string cleanRu = ru.Replace("<LINE_SEPARATOR>", " ").Trim();
                    if (!enToRu.ContainsKey(cleanEn))
                        enToRu[cleanEn] = cleanRu;
                }

                if (enToRu.Count > 500)
                {
                    _enRuDict = enToRu;
                    try
                    {
                        string tmp = _enRuDictCachePath + ".tmp";
                        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(enToRu)).ConfigureAwait(false);
                        File.Move(tmp, _enRuDictCachePath, overwrite: true);
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _initLock.Release();
            }
        }

        public string TranslateEnToRu(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string clean = raw.Replace("[PH] ", "", StringComparison.OrdinalIgnoreCase).Trim();
            var dict = _enRuDict;
            if (dict != null)
            {
                if (dict.TryGetValue(clean, out string? ru)) return ru;
                if (clean.EndsWith(".") && dict.TryGetValue(clean[..^1], out string? ruNoDot)) return ruNoDot + ".";
            }
            return clean;
        }

        private async Task EnsureArbitrationScheduleLoadedAsync()
        {
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_regionsMap == null)
                {
                    string? regJson = null;
                    bool regFresh = File.Exists(_regionsCachePath) &&
                                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(_regionsCachePath)).TotalDays < 7;
                    if (regFresh)
                    {
                        try { regJson = await File.ReadAllTextAsync(_regionsCachePath).ConfigureAwait(false); } catch { }
                    }
                    if (string.IsNullOrEmpty(regJson))
                    {
                        regJson = await WarframeApiService.GetStringAsync(RegionsUrl, timeoutSeconds: 6).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(regJson))
                        {
                            try
                            {
                                string tmp = _regionsCachePath + ".tmp";
                                await File.WriteAllTextAsync(tmp, regJson).ConfigureAwait(false);
                                File.Move(tmp, _regionsCachePath, overwrite: true);
                            }
                            catch { }
                        }
                        else if (File.Exists(_regionsCachePath))
                        {
                            try { regJson = await File.ReadAllTextAsync(_regionsCachePath).ConfigureAwait(false); } catch { }
                        }
                    }
                    if (!string.IsNullOrEmpty(regJson))
                    {
                        ParseRegionsMap(regJson);
                    }
                }

                if (_arbysSchedule.Count == 0)
                {
                    string? arbText = null;
                    if (File.Exists(_arbysCachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(_arbysCachePath)).TotalDays < 7)
                    {
                        try { arbText = await File.ReadAllTextAsync(_arbysCachePath).ConfigureAwait(false); } catch { }
                    }
                    if (string.IsNullOrEmpty(arbText))
                    {
                        arbText = await WarframeApiService.GetStringAsync(ArbysUrl, timeoutSeconds: 6).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(arbText))
                        {
                            try
                            {
                                string tmp = _arbysCachePath + ".tmp";
                                await File.WriteAllTextAsync(tmp, arbText).ConfigureAwait(false);
                                File.Move(tmp, _arbysCachePath, overwrite: true);
                            }
                            catch { }
                        }
                        else if (File.Exists(_arbysCachePath))
                        {
                            try { arbText = await File.ReadAllTextAsync(_arbysCachePath).ConfigureAwait(false); } catch { }
                        }
                    }
                    if (!string.IsNullOrEmpty(arbText))
                    {
                        ParseArbysSchedule(arbText);
                    }
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        private void ParseRegionsMap(string json)
        {
            var dict = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var el = prop.Value;
                string namePath = GetStr(el, "name");
                string sysPath = GetStr(el, "systemName");
                string mType = GetStr(el, "missionType");
                string mNamePath = GetStr(el, "missionName");
                string fac = GetStr(el, "faction");

                string name = ExtractLocTail(namePath);
                name = prop.Name switch
                {
                    "SolNode715" => "Effervo",
                    "SolNode716" => "Nex",
                    "SolNode717" => "Persto",
                    "SolNode718" => "Cambire",
                    "SolNode719" => "Munio",
                    "SolNode720" => "Testudo",
                    "SolNode721" => "Armatus",
                    "SolNode230" => "Панорамная арка",
                    "SolNode231" => "Периметр Халако",
                    "SolNode232" => "Палаты Тувула",
                    "SolNode233" => "Мастерские Оро",
                    "SolNode235" => "Зелёный коридор",
                    "CrewBattleNode501" => "Кластер Мордо",
                    "CrewBattleNode502" => "Ущелье Совера",
                    "CrewBattleNode503" => "Эхо Бифроста",
                    "CrewBattleNode504" => "Вектор Арвы",
                    "CrewBattleNode509" => "Храм Йоты",
                    "CrewBattleNode511" => "Защитное Кольцо Маяка",
                    "CrewBattleNode512" => "Орвин-Хаарк",
                    "CrewBattleNode513" => "Ущелье Vesper",
                    "CrewBattleNode514" => "Падающее Величие",
                    "CrewBattleNode515" => "Невезучий простор",
                    "CrewBattleNode516" => "Рудники Ню-Гуа",
                    "CrewBattleNode518" => "Кластер Огал",
                    "CrewBattleNode519" => "Пояс Корма",
                    "CrewBattleNode521" => "Ледяной Затор Энкиду",
                    "CrewBattleNode522" => "Кластер Бендар",
                    "CrewBattleNode523" => "Проспект Маммона",
                    "CrewBattleNode524" => "Суверенная Хватка",
                    "CrewBattleNode525" => "Кластер Брома",
                    "CrewBattleNode526" => "Посланник Хуфу",
                    "CrewBattleNode527" => "Семь Сирен",
                    "CrewBattleNode528" => "Переезд Обол",
                    "CrewBattleNode529" => "Предел Прибыли",
                    "CrewBattleNode530" => "Перевал Казио",
                    "CrewBattleNode531" => "Поле Фентона",
                    "CrewBattleNode533" => "Разрыв Нодо",
                    "CrewBattleNode534" => "Перевал Люпэл",
                    "CrewBattleNode535" => "Кластер Ванд",
                    "CrewBattleNode536" => "Ось Сапсана",
                    "CrewBattleNode538" => "Калабаш",
                    "CrewBattleNode539" => "Нумина",
                    "CrewBattleNode540" => "Серебряная Дуга",
                    "CrewBattleNode541" => "Эрато",
                    "CrewBattleNode542" => "Лу-янь",
                    "CrewBattleNode543" => "Облако Сабмира",
                    "CrewBattleNode550" => "Локус Нсу",
                    "CrewBattleNode553" => "Флекса",
                    "CrewBattleNode554" => "Облако Н-2",
                    "CrewBattleNode555" => "Облако R-9",
                    "SolNode400" => "Teshub",
                    "SolNode401" => "Hepit",
                    "SolNode402" => "Taranis",
                    "SolNode403" => "Tiwaz",
                    "SolNode404" => "Stribog",
                    "SolNode405" => "Ani",
                    "SolNode406" => "Ukko",
                    "SolNode407" => "Oxomoco",
                    "SolNode408" => "Belenus",
                    "SolNode409" => "Mot",
                    "SolNode410" => "Aten",
                    "SolNode411" => "Marduk",
                    "SolNode412" => "Mithra",
                    "MercuryHUB" => "Реле Larunda",
                    "EarthHUB" => "Реле Strata",
                    "SaturnHUB" => "Реле Kronia",
                    "PlutoHUB" => "Реле Orcus",
                    _ => name
                };

                string sys = ExtractLocTail(sysPath);
                if (sys == "SolarMapDeimosName") sys = "Deimos";
                else if (sys.Equals("Venus_SPACE", StringComparison.OrdinalIgnoreCase)) sys = "Проксима Венеры";
                else if (sys.Equals("Earth_SPACE", StringComparison.OrdinalIgnoreCase)) sys = "Проксима Земли";
                else if (sys.Equals("Saturn_SPACE", StringComparison.OrdinalIgnoreCase)) sys = "Проксима Сатурна";
                else if (sys.Equals("Neptune_SPACE", StringComparison.OrdinalIgnoreCase)) sys = "Проксима Нептуна";
                else if (sys.Equals("Pluto_SPACE", StringComparison.OrdinalIgnoreCase)) sys = "Проксима Плутона";
                else if (sys.Equals("Veil_SPACE", StringComparison.OrdinalIgnoreCase) || sys.EndsWith("_SPACE", StringComparison.OrdinalIgnoreCase))
                    sys = "Проксима Вуали";

                string mission = mType switch
                {
                    "MT_SURVIVAL" => "Survival",
                    "MT_DEFENSE" => "Defense",
                    "MT_INTERCEPTION" => "Interception",
                    "MT_EXCAVATION" => "Excavation",
                    "MT_DISRUPTION" => "Disruption",
                    "MT_EVACUATION" => "Defection",
                    "MT_INFESTED_SALVAGE" => "Infested Salvage",
                    "MT_MOBILE_DEFENSE" => "Mobile Defense",
                    "MT_EXTERMINATION" => "Extermination",
                    "MT_RESCUE" => "Спасение",
                    "MT_CAPTURE" => "Захват",
                    "MT_SABOTAGE" => "Диверсия",
                    "MT_INTEL" => "Шпионаж",
                    "MT_HIVE" => "Улей",
                    "MT_ASSAULT" => "Штурм",
                    "MT_HIJACK" => "Угон",
                    "MT_RAILJACK" => mNamePath.Contains("Volatile", StringComparison.OrdinalIgnoreCase) ? "Налёт"
                        : (mNamePath.Contains("Orphix", StringComparison.OrdinalIgnoreCase) ? "Орфикс" : "Стычка"),
                    _ => TranslateDeMissionCode(mType) is { Length: > 0 } mapped ? mapped : mType.Replace("MT_", "")
                };
                string faction = fac switch
                {
                    "FC_GRINEER" => "Grineer",
                    "FC_CORPUS" => "Corpus",
                    "FC_INFESTATION" => "Infestation",
                    "FC_OROKIN" => "Corrupted",
                    "FC_MITW" => "The Murmur",
                    _ => fac.Replace("FC_", "")
                };
                dict[prop.Name] = (name, sys, mission, faction);
            }
            _regionsMap = dict;
        }

        private void ParseArbysSchedule(string text)
        {
            _arbysSchedule.Clear();
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (long.TryParse(line.AsSpan(0, comma), out long ts))
                {
                    string node = line[(comma + 1)..].Trim();
                    _arbysSchedule.Add((ts, node));
                }
            }
            _arbysSchedule.Sort((a, b) => a.UnixTs.CompareTo(b.UnixTs));
        }

        private string? FindArbysNode(long targetHourUnix)
        {
            int lo = 0, hi = _arbysSchedule.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                long ts = _arbysSchedule[mid].UnixTs;
                if (ts == targetHourUnix) return _arbysSchedule[mid].NodeId;
                if (ts < targetHourUnix) lo = mid + 1;
                else hi = mid - 1;
            }
            return null;
        }

        private static string ExtractLocTail(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            string tail = slash >= 0 ? path[(slash + 1)..] : path;
            if (tail.StartsWith("SolarMap", StringComparison.OrdinalIgnoreCase))
            {
                if (tail.Contains("Deimos")) return "Deimos";
                if (tail.Contains("Zariman")) return "Zariman";
            }
            return tail;
        }

        public static string GetStr(JsonElement el, string prop, string fallback = "") =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback
                : fallback;

        public static (string Node, string Planet) SplitAndTranslateNode(string rawNode)
        {
            if (string.IsNullOrWhiteSpace(rawNode)) return ("", "");
            int paren = rawNode.IndexOf('(');
            if (paren > 0 && rawNode.EndsWith(")"))
            {
                string node = rawNode[..paren].Trim();
                string planet = rawNode.Substring(paren + 1, rawNode.Length - paren - 2).Trim();
                return (node, TranslatePlanet(planet));
            }
            return (rawNode, "");
        }

        public static string TranslatePlanet(string en)
        {
            if (string.IsNullOrWhiteSpace(en)) return string.Empty;
            string direct = en switch
            {
                "Mercury" => "Меркурий",
                "Venus" => "Венера",
                "Earth" => "Земля",
                "Mars" => "Марс",
                "Phobos" => "Фобос",
                "Deimos" => "Деймос",
                "Ceres" => "Церера",
                "Jupiter" => "Юпитер",
                "Europa" => "Европа",
                "Saturn" => "Сатурн",
                "Uranus" => "Уран",
                "Neptune" => "Нептун",
                "Pluto" => "Плутон",
                "Sedna" => "Седна",
                "Eris" => "Эрида",
                "Void" => "Бездна",
                "Lua" or "Moon" or "Earth_Moon" => "Луа",
                "Kuva Fortress" or "Fortress" or "KuvaFortress" or "Kuva FortressRegionName" => "Крепость Кувы",
                "Zariman" or "ZarimanRegionName" or "Zariman Ten Zero" or "The Zariman" => "Зариман",
                "Höllvania" or "Hollvania" or "HollvaniaRegionName" => "Хольвания",
                "Earth Proxima" => "Земля Проксима",
                "Venus Proxima" => "Венера Проксима",
                "Saturn Proxima" => "Сатурн Проксима",
                "Neptune Proxima" => "Нептун Проксима",
                "Pluto Proxima" => "Плутон Проксима",
                "Veil Proxima" => "Вуаль Проксима",
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(direct)) return direct;
            if (en.Contains("Zariman", StringComparison.OrdinalIgnoreCase)) return "Зариман";
            if (en.Contains("Hollvania", StringComparison.OrdinalIgnoreCase) || en.Contains("Höllvania", StringComparison.OrdinalIgnoreCase)) return "Хольвания";
            if (en.Contains("Kuva", StringComparison.OrdinalIgnoreCase)) return "Крепость Кувы";
            return en;
        }

        public static string TranslateMission(string en)
        {
            if (string.IsNullOrWhiteSpace(en)) return string.Empty;
            string cleaned = en
                .Replace("D T_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("DT_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_", " ")
                .Trim();

            return cleaned.ToUpperInvariant() switch
            {
                "SURVIVAL" => "Выживание",
                "EXTERMINATION" or "EXTERMINATE" => "Зачистка",
                "CAPTURE" => "Захват",
                "DEFENSE" => "Оборона",
                "MOBILE DEFENSE" => "Моб. Оборона",
                "SABOTAGE" => "Диверсия",
                "HIVE SABOTAGE" or "HIVE" or "SABOTAGE HIVE" => "Улей",
                "SPY" => "Шпионаж",
                "RESCUE" => "Спасение",
                "INTERCEPTION" => "Перехват",
                "EXCAVATION" => "Раскопки",
                "HIJACK" => "Угон",
                "DEFECTION" => "Перебежчики",
                "ASSASSINATION" => "Убийство",
                "DISRUPTION" or "ARTIFACT" => "Сбой",
                "ALCHEMY" => "Алхимия",
                "MIRROR DEFENSE" => "Зеркальная Оборона",
                "VOID CASCADE" => "Каскад Бездны",
                "VOID FLOOD" => "Потоп Бездны",
                "VOID ARMAGEDDON" => "Армагеддон Бездны",
                "LEGACYTE HARVEST" => "Сбор Легацитов",
                "HELL-SCRUB" => "Зачистка (Хелл-Скраб)",
                "SKIRMISH" => "Стычка (Рейлджек)",
                "VOLATILE" => "Взрывчатка (Рейлджек)",
                "ORPHIX" => "Орфикс (Рейлджек)",
                "CROSSFIRE EXTERMINATE" => "Перекрёстный Огонь",
                "INFESTED SALVAGE" => "Заражённое Спасение",
                "CORRUPTION" or "SHRINE DEFENSE" => "Защита Святилища",
                "LOOT CREATURES" => "Сбор добычи",
                "DEEP MINES" => "Глубокие Шахты",
                _ => Instance.TranslateEnToRu(cleaned)
            };
        }

        public static string TranslateFaction(string en) => en switch
        {
            "Grineer" => "Гринир",
            "Corpus" => "Корпус",
            "Infestation" or "Infested" => "Заражённые",
            "Orokin" => "Орокин",
            "Corrupted" => "Порабощённые",
            "Narmer" => "Нармер",
            "Scaldra" => "Скальдра",
            "Techrot" => "Технорот",
            "The Murmur" or "Man in the Wall" => "Шёпот",
            "Crossfire" => "Перекрёстный",
            "Sentient" => "Владеющие Разумом",
            _ => en
        };

        public static (string BossRu, string ShardRu, string ShardColor) TranslateArchon(string rawBoss)
        {
            if (rawBoss.Contains("Boreal", StringComparison.OrdinalIgnoreCase))
                return ("Архонт Бореаль", "Лазурный Осколок Архонта (Щиты / Энергия / Броня)", "#38BDF8");
            if (rawBoss.Contains("Amar", StringComparison.OrdinalIgnoreCase))
                return ("Архонт Амар", "Багровый Осколок Архонта (Сила / Длительность / Крит)", "#EF4444");
            if (rawBoss.Contains("Nira", StringComparison.OrdinalIgnoreCase))
                return ("Архонт Нира", "Янтарный Осколок Архонта (Скорость каста / Паркур / Сферы)", "#F59E0B");
            return (rawBoss, "Осколок Архонта", "#F59E0B");
        }

        public static (string BaseUrl, string GlowUrl) GetArchonShardIconUrls(string rawBoss)
        {
            if (rawBoss.Contains("Boreal", StringComparison.OrdinalIgnoreCase))
                return (
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardBoreal.png!00_ghf9Qbj2Tca5VYRXNfLoxA",
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardBorealMythicGlow.png!00_WNJLrmvOGuvKkYAKfrP9uQ");
            if (rawBoss.Contains("Amar", StringComparison.OrdinalIgnoreCase))
                return (
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmar.png!00_rSQSghtWz+j0RYah4EmfVg",
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmarMythicGlow.png!00_PRyBTogo3mZHDWxAUViI8Q");
            if (rawBoss.Contains("Nira", StringComparison.OrdinalIgnoreCase))
                return (
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardNira.png!00_dn6QF0OanOjP8TYAHbXG5w",
                    "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardNiraMythicGlow.png!00_HvVayxayGKrg6RVvTV6sSQ");
            return (
                "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardGreen.png!00_3bL3gFazx0uV8jEjV9Nliw",
                "https://content.warframe.com/PublicExport/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardMythicGreenGlow.png!00_KYl-QUF92aMbufLIlsUl1A");
        }

        public static string GetArchonShardIconPath(string rawBoss) => GetArchonShardIconUrls(rawBoss).GlowUrl;

        public static string TranslateModifier(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string clean = raw.StartsWith("Enemy ", StringComparison.OrdinalIgnoreCase) ? raw["Enemy ".Length..] : raw;
            return clean switch
            {
                "Eximus Stronghold" => "Оплот Эксимусов",
                "Energy Reduction" => "Снижение энергии (−75%)",
                "Augmented Enemy Armor" or "Augmented Armor" => "Усиленная броня врагов",
                "Enhanced Enemy Shields" or "Enhanced Shields" => "Усиленные щиты врагов",
                "Elemental Enhancement: Heat" => "Усиление стихией: Огонь",
                "Elemental Enhancement: Cold" => "Усиление стихией: Холод",
                "Elemental Enhancement: Radiation" => "Усиление стихией: Радиация",
                "Elemental Enhancement: Magnetic" => "Усиление стихией: Магнит",
                "Elemental Enhancement: Viral" => "Усиление стихией: Вирус",
                "Elemental Enhancement: Toxin" => "Усиление стихией: Токсин",
                "Elemental Enhancement: Corrosive" => "Усиление стихией: Коррозия",
                "Elemental Enhancement: Electricity" => "Усиление стихией: Электро",
                "Physical Enhancement: Slash" => "Усиление физ. уроном: Разрез",
                "Physical Enhancement: Impact" => "Усиление физ. уроном: Удар",
                "Physical Enhancement: Puncture" => "Усиление физ. уроном: Пронзание",
                "Environmental Effect: Extreme Cold" => "Сильный холод (сниж. подвижность)",
                "Environmental Effect: Radiation Hazard" => "Радиационная опасность",
                "Environmental Hazard: Cryogenic Leakage" => "Утечка криогена",
                "Environmental Hazard: Dense Fog" => "Густой туман",
                "Environmental Hazard: Electromagnetic Anomalies" => "Электромагнитные аномалии",
                "Environmental Hazard: Fire" => "Огненные очаги",
                "Weapon Restriction: Bow Only" => "Только Луки",
                "Weapon Restriction: Sniper Only" => "Только Снайперские винтовки",
                "Weapon Restriction: Shotgun Only" => "Только Дробовики",
                "Weapon Restriction: Assault Rifle Only" => "Только Штурмовые винтовки",
                "Weapon Restriction: Pistol Only" => "Только Пистолеты",
                "Weapon Restriction: Melee Only" => "Только Ближний бой",
                _ => Instance.TranslateEnToRu(raw)
            };
        }

        public static string TranslateSteelPathReward(string en) => en switch
        {
            "Umbra Forma Blueprint" => "Чертёж: Форма Умбра",
            "50,000 Kuva" => "50 000 Кувы",
            "Kitgun Riven Mod" => "Мод Разлома: Китган",
            "3x Forma" => "3× Готовая Форма",
            "Zaw Riven Mod" => "Мод Разлома: Зо",
            "30,000 Endo" => "30 000 Эндо",
            "Rifle Riven Mod" => "Мод Разлома: Винтовка",
            "Shotgun Riven Mod" => "Мод Разлома: Дробовик",
            _ => en
        };

        public static string TranslateGlobalUpgrade(string rawUpgrade, string opSym, double val)
        {
            string title = rawUpgrade switch
            {
                "Resource Drop Amount" => "Дроп ресурсов",
                "Resource Drop Chance" => "Шанс дропа ресурсов",
                "Affinity" => "Синтез (Опыт)",
                "Credits" => "Кредиты",
                _ => rawUpgrade
            };
            return $"{title} {opSym}{val:0.##}";
        }

        public static string ResolveLocalizedCatalogName(
            string? uniqueName,
            string? rawEnName,
            IReadOnlyList<CollectionItemModel> catalog)
        {
            if (catalog.Count > 0)
            {
                string? normUid = string.IsNullOrEmpty(uniqueName)
                    ? null
                    : uniqueName
                        .Replace("/Lotus/Types/StoreItems/", "/Lotus/", StringComparison.Ordinal)
                        .Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal);

                foreach (var item in catalog)
                {
                    if (!string.IsNullOrEmpty(normUid) && item.UniqueName.Equals(normUid, StringComparison.OrdinalIgnoreCase))
                        return item.Name;
                }
                if (!string.IsNullOrEmpty(rawEnName))
                {
                    foreach (var item in catalog)
                    {
                        if (!string.IsNullOrEmpty(item.NameEn) && item.NameEn.Equals(rawEnName, StringComparison.OrdinalIgnoreCase))
                            return item.Name;
                    }
                }
            }
            return TranslateCommonReward(rawEnName ?? string.Empty);
        }

        private static string TranslateCommonReward(string en) => en switch
        {
            "Exilus Warframe Adapter" => "Адаптер Эксилус Варфрейма",
            "Exilus Weapon Adapter" => "Адаптер Эксилус Оружия",
            "Exilus Weapon Adapter Blueprint" => "Чертёж: Адаптер Эксилус Оружия",
            "Melee Arcane Adapter" => "Адаптер Мистификаторов (Ближний бой)",
            "Primary Arcane Adapter" => "Адаптер Мистификаторов (Основное)",
            "Secondary Arcane Adapter" => "Адаптер Мистификаторов (Вторичное)",
            "Aura Forma Blueprint" => "Чертёж: Форма Ауры",
            "Omni Forma Blueprint" => "Чертёж: Форма Омни",
            "Arcane Enhancements: Double Pack" => "Двойной набор Мистификаторов",
            "3 Day Resource Drop Chance Booster" => "Усилитель шанса выпадения ресурсов (3 дн.)",
            "3 Day Mod Drop Chance Booster" => "Усилитель шанса выпадения модов (3 дн.)",
            "3 Day Resource Booster" => "Усилитель ресурсов (3 дн.)",
            "3 Day Affinity Booster" => "Усилитель синтеза (3 дн.)",
            "3 Day Credit Booster" => "Усилитель кредитов (3 дн.)",
            "Mutalist Alad V Nav Coordinate" => "Навигационная координата: Муталист Алад V",
            "Orokin Catalyst" => "Катализатор Орокин",
            "Orokin Reactor" => "Реактор Орокин",
            "Kuva" => "Кува",
            _ => Instance.TranslateEnToRu(en)
        };
    }
}
