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
        }

        public static string ResolveFissureRegion(string planet, string node, bool isStorm)
        {
            if (isStorm || planet.Contains("Проксима", StringComparison.OrdinalIgnoreCase))
                return "Рэйлджек (Проксима)";
            if (planet.Contains("Зариман", StringComparison.OrdinalIgnoreCase))
                return "Зариман";
            if (planet.Contains("Хольван", StringComparison.OrdinalIgnoreCase) || planet.Contains("1999", StringComparison.OrdinalIgnoreCase))
                return "1999 (Хольвания)";
            if (planet.Contains("Деймос", StringComparison.OrdinalIgnoreCase) &&
                (node.Contains("Cambire", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Munio", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Persto", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Armatus", StringComparison.OrdinalIgnoreCase) ||
                 node.Contains("Nex", StringComparison.OrdinalIgnoreCase)))
                return "Санктум (Деймос)";
            if (planet.Equals("Луа", StringComparison.OrdinalIgnoreCase))
                return "Луа";
            if (planet.Contains("Кув", StringComparison.OrdinalIgnoreCase))
                return "Крепость Кувы";
            if (planet.Equals("Бездна", StringComparison.OrdinalIgnoreCase))
                return "Бездна";

            string[] classicOrigin = { "Меркурий", "Венера", "Земля", "Марс", "Фобос", "Деймос", "Церера", "Юпитер", "Европа", "Сатурн", "Уран", "Нептун", "Плутон", "Седна", "Эрида" };
            if (!string.IsNullOrEmpty(planet) && !classicOrigin.Contains(planet, StringComparer.OrdinalIgnoreCase))
                return planet;

            return "Звёздная карта";
        }

        public async Task<DeOfficialWorldFallback?> FetchOfficialDeFissuresAndThreatsAsync(DateTime utcNow)
        {
            try
            {
                await EnsureArbitrationScheduleLoadedAsync().ConfigureAwait(false);
                string? raw = await WarframeApiService.GetStringAsync("https://api.warframe.com/cdn/worldState.php").ConfigureAwait(false);
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

                        string nodeName = nodeKey;
                        string planet = isStorm ? "Проксима" : "Система";
                        string mission = TranslateMission(GetStr(el, "MissionType"));
                        string faction = "Враги";

                        if (_regionsMap != null && _regionsMap.TryGetValue(nodeKey, out var reg))
                        {
                            nodeName = reg.NameEn;
                            planet = TranslatePlanet(reg.SystemEn);
                            mission = TranslateMission(reg.MissionEn);
                            faction = TranslateFaction(reg.FactionEn);
                        }

                        if (el.TryGetProperty("MissionType", out var mtEl) && mtEl.ValueKind == JsonValueKind.String)
                        {
                            string ov = TranslateDeMissionCode(mtEl.GetString() ?? string.Empty);
                            if (!string.IsNullOrEmpty(ov)) mission = ov;
                        }

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

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseDeUnixExpiry(JsonElement el)
        {
            if (el.TryGetProperty("Expiry", out var expEl) &&
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
            "MT_INTERCEPTION" => "Перехват",
            "MT_EXCAVATION" => "Раскопки",
            "MT_DISRUPTION" => "Сбой",
            "MT_EVACUATION" => "Перебежчики",
            "MT_INFESTED_SALVAGE" => "Сбор ресурсов",
            "MT_MOBILE_DEFENSE" => "Мобильная оборона",
            "MT_EXTERMINATION" => "Зачистка",
            "MT_RESCUE" => "Спасение",
            "MT_CAPTURE" or "MT_TERRITORY" => "Захват",
            "MT_SABOTAGE" => "Диверсия",
            "MT_INTEL" => "Шпионаж",
            "MT_HIVE" => "Улей",
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

                var enTask = WarframeApiService.GetStringAsync(DictEnUrl);
                var ruTask = WarframeApiService.GetStringAsync(DictRuUrl);
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
                        regJson = await WarframeApiService.GetStringAsync(RegionsUrl).ConfigureAwait(false);
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
                        arbText = await WarframeApiService.GetStringAsync(ArbysUrl).ConfigureAwait(false);
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

        public static string TranslatePlanet(string en) => en switch
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
            "Kuva Fortress" or "Fortress" or "KuvaFortress" => "Крепость Кувы",
            "Zariman" => "Зариман",
            "Höllvania" => "Хольвания",
            "Earth Proxima" => "Земля Проксима",
            "Venus Proxima" => "Венера Проксима",
            "Saturn Proxima" => "Сатурн Проксима",
            "Neptune Proxima" => "Нептун Проксима",
            "Pluto Proxima" => "Плутон Проксима",
            "Veil Proxima" => "Вуаль Проксима",
            _ => en
        };

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

        public static string GetArchonShardIconPath(string rawBoss)
        {
            if (rawBoss.Contains("Boreal", StringComparison.OrdinalIgnoreCase))
                return "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardBorealMythicGlow.png";
            if (rawBoss.Contains("Amar", StringComparison.OrdinalIgnoreCase))
                return "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmarMythicGlow.png";
            if (rawBoss.Contains("Nira", StringComparison.OrdinalIgnoreCase))
                return "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardNiraMythicGlow.png";
            return "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardMythicGreenGlow.png";
        }

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
