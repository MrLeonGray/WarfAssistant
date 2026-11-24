using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Synchronizes the full game catalog from bundled compressed seed (instant offline load)
    /// and updates incrementally from official Digital Extremes PublicExport (.lzma + manifests),
    /// browse.wf public-export-plus metadata, and api.warframestat.us/items.
    /// Zero dependency on game client files or EE.log.
    /// </summary>
    public sealed class CatalogSyncService
    {
        private static CatalogSyncService? _instance;
        public static CatalogSyncService Instance => _instance ??= new CatalogSyncService();

        private const string DeIndexRuUrl = "https://origin.warframe.com/PublicExport/index_ru.txt.lzma";
        private const string DeIndexEnUrl = "https://origin.warframe.com/PublicExport/index_en.txt.lzma";
        private const string DeManifestBaseUrl = "https://content.warframe.com/PublicExport/Manifest/";
        private const string BrowseWfPlusBase = "https://browse.wf/warframe-public-export-plus/";
        private const string WarframeStatItemsUrl = "https://api.warframestat.us/items";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly Regex CyrillicRegex = new("[А-Яа-яЁё]", RegexOptions.Compiled);
        private static readonly Regex StripTagRegex = new(@"^<[^>]+>\s*", RegexOptions.Compiled);
        private static readonly Regex ControlCharRegex = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

        private static readonly HashSet<string> IncarnonBases = new(StringComparer.OrdinalIgnoreCase)
        {
            "Braton","Lato","Skana","Paris","Kunai","Boar","Gammacor","Angstrum","Gorgon","Anku",
            "Bo","Latron","Furis","Furax","Strun","Lex","Magistar","Boltor","Bronco","Ceramic Dagger",
            "Torid","Dual Toxocyst","Dual Ichor","Miter","Atomos","Ack & Brunt","Soma","Vasto","Nami Solo",
            "Burston","Zylok","Sibear","Dread","Despair","Hate","Dera","Sybaris","Cestra","Sicarus",
            "Okina","Vectis","Stug","Ballistica","Destreza","Obex","Phenmor","Felarx","Laetum","Innodem",
            "Praedos","Onos","Ruvox"
        };

        private static readonly string[] IncarnonExcludedDuals =
        {
            "Dex Furis", "Aklex", "Akbolto", "Akvasto", "Akbronco", "Aklato", "Dual Skana", "Dual Bronco", "Dual Cestra"
        };

        private static readonly string[] IncarnonPrefixes =
        {
            "MK1-", "Prisma ", "Synoid ", "Telos ", "Vaykor ", "Sancti ", "Secura ", "Rakta "
        };

        private static readonly Dictionary<string, string> ExtraRuOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AX-52"] = "АК-52",
            ["This Is What You Are"] = "Это то, кто ты есть",
            ["Eskhatos Pauldrons"] = "Наплечники: Эсхатос",
            ["Epoch Operation Vessel Emblem"] = "Эмблема операции: Сосуд Эпохи",
            ["Eskhatos Sekhara"] = "Секара: Эсхатос",
            ["Conquera Cateura Signa"] = "Сигна: Конквера Катеура",
            ["Narin Agile Animation Set"] = "Набор анимаций Нарин: Проворный",
            ["Narin Noble Animation Set"] = "Набор анимаций Нарин: Статный",
            ["Eskhatos Infinitas Ephemera"] = "Эфемера: Эсхатос Инфинитас",
            ["Eskhatos Sugatra"] = "Сугатра: Эсхатос",
            ["Jack O'Naut Keychain Sugatra"] = "Сугатра: Брелок Джек-о-Навт",
            ["Eschatos Necramech Helmet"] = "Шлем Некрамеха: Эсхатос",
            ["Eskhatos Necramech Skin"] = "Скин Некрамеха: Эсхатос",
            ["Eskhatos Parazon Skin"] = "Скин Паразона: Эсхатос",
            ["Dhampyre Syandana"] = "Сандана: Дампир",
            ["Taubound Sigil"] = "Сигил: Связь с Тау",
            ["Primate Sigil"] = "Сигил: Примат",
            ["Arbiters of Hexis Sigil"] = "Сигил: Арбитры Гексиса",
            ["Niveus Nepheri Skin"] = "Скин Нефери: Нивеус",
            ["Niveus Orvius Skin"] = "Скин Орвиуса: Нивеус",
            ["Niveus Paracesis Skin"] = "Скин Парацесиса: Нивеус",
            ["Niveus Glaxion Skin"] = "Скин Глаксиона: Нивеус",
            ["Niveus Quellor Skin"] = "Скин Квеллора: Нивеус",
            ["Neote Rapier Skin"] = "Скин рапиры: Неота",
            ["Niveus Dark Dagger Skin"] = "Скин Тёмного кинжала: Нивеус",
            ["Niveus Dark Split-Sword Skin"] = "Скин Тёмного двуклинкового меча: Нивеус",
            ["Niveus Dark Sword Skin"] = "Скин Тёмного меча: Нивеус"
        };

        private static string ApplyRuFallback(string enName, string ruName)
        {
            if (ExtraRuOverrides.TryGetValue(enName, out string? ov))
                return ov;
            if (!string.IsNullOrWhiteSpace(ruName) && CyrillicRegex.IsMatch(ruName))
                return ruName;
            string dictRu = WorldStateService.Instance.TranslateEnToRu(enName);
            return string.IsNullOrWhiteSpace(dictRu) ? enName : dictRu;
        }

        private readonly string _dataDir;
        private readonly string _catalogCachePath;
        private readonly string _metaCachePath;
        private readonly string _missingReportPath;
        private readonly HttpClient _http;

        private CatalogSyncService()
        {
            _dataDir = Path.GetDirectoryName(StorageService.Instance.SaveDirectoryPath)
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            string cacheDir = Path.Combine(_dataDir, "cache");
            try { Directory.CreateDirectory(cacheDir); } catch { }

            string legacyV2 = Path.Combine(cacheDir, "collection_catalog_v2.json");
            if (File.Exists(legacyV2))
            {
                try { File.Delete(legacyV2); } catch { }
            }

            _catalogCachePath = Path.Combine(cacheDir, "collection_catalog_v3.json");
            _metaCachePath = Path.Combine(cacheDir, "manifest_meta.json");
            _missingReportPath = ResolveMissingReportPath();

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WarfAssistant/2.0");
        }

        private string ResolveMissingReportPath()
        {
            string? repoRoot = Path.GetDirectoryName(_dataDir);
            if (!string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot))
                return Path.Combine(repoRoot, "MISSING_ASSETS_AND_LOCALIZATION.txt");
            return Path.Combine(_dataDir, "MISSING_ASSETS_AND_LOCALIZATION.txt");
        }

        public string CatalogCacheFilePath => _catalogCachePath;
        public string MissingReportFilePath => _missingReportPath;
        public string LastSyncSummary { get; private set; } = "Офлайн-каталог";

        /// <summary>
        /// Loads the catalog instantaneously from local disk cache or compressed embedded seed (<50ms).
        /// Never makes blocking network requests on the UI startup path.
        /// </summary>
        public List<CollectionItemModel> LoadCatalogFast()
        {
            CatalogDocument? doc = null;

            if (File.Exists(_catalogCachePath))
            {
                try
                {
                    using var fs = File.OpenRead(_catalogCachePath);
                    doc = JsonSerializer.Deserialize<CatalogDocument>(fs, JsonOpts);
                    if (doc?.Items == null || doc.Items.Count < 1000)
                        doc = null;
                }
                catch
                {
                    doc = null;
                }
            }

            if (doc == null)
            {
                doc = LoadEmbeddedSeed();
            }

            if (doc?.Items == null)
                return new List<CollectionItemModel>();

            var result = new List<CollectionItemModel>(doc.Items.Count);
            foreach (var dto in doc.Items)
            {
                result.Add(MapDtoToModel(dto));
            }

            LastSyncSummary = File.Exists(_catalogCachePath)
                ? $"Локальный кэш ({result.Count:N0} предм.)"
                : $"Встроенная база v{doc.Version} ({result.Count:N0} предм.)";

            return result;
        }

        private static CatalogDocument? LoadEmbeddedSeed()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("catalog_seed.json.gz", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return null;

                using Stream? rawStream = asm.GetManifestResourceStream(resName);
                if (rawStream == null) return null;

                using var gz = new GZipStream(rawStream, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<CatalogDocument>(gz, JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks official DE PublicExport index_ru/en.txt.lzma via managed LZMA decoder.
        /// Downloads updated manifests only when hashes differ or when forced.
        /// </summary>
        public async Task<(bool Updated, List<CollectionItemModel>? Items, string Message)> SyncFromApiAsync(bool forceRefresh = false)
        {
            try
            {
                var ruIdxTask = _http.GetByteArrayAsync(DeIndexRuUrl);
                var enIdxTask = _http.GetByteArrayAsync(DeIndexEnUrl);
                await Task.WhenAll(ruIdxTask, enIdxTask).ConfigureAwait(false);

                string ruIndexText = LzmaMinimalDecoder.DecompressUtf8(ruIdxTask.Result).Trim();
                string enIndexText = LzmaMinimalDecoder.DecompressUtf8(enIdxTask.Result).Trim();

                var ruManifestMap = ParseManifestIndex(ruIndexText);
                var enManifestMap = ParseManifestIndex(enIndexText);

                if (!ruManifestMap.ContainsKey("ExportWarframes") || !enManifestMap.ContainsKey("ExportWarframes"))
                {
                    return (false, null, "DE PublicExport: неожиданный формат index.txt");
                }

                string combinedSig = ruManifestMap.GetValueOrDefault("ExportWarframes", "") + "|" +
                                     ruManifestMap.GetValueOrDefault("ExportWeapons", "") + "|" +
                                     ruManifestMap.GetValueOrDefault("ExportCustoms", "") + "|" +
                                     ruManifestMap.GetValueOrDefault("ExportResources", "") + "|" +
                                     ruManifestMap.GetValueOrDefault("ExportSentinels", "");

                if (!forceRefresh && File.Exists(_catalogCachePath) && File.Exists(_metaCachePath))
                {
                    string prevSig = File.ReadAllText(_metaCachePath).Trim();
                    if (prevSig == combinedSig)
                    {
                        LastSyncSummary = $"DE API актуален ({DateTime.Now:HH:mm})";
                        return (false, null, "✓ База уже актуальна — изменений в DE PublicExport нет");
                    }
                }

                // Fetch official DE manifests + browse.wf image/price metadata + warframestat vaulted flags in parallel
                var wfRuT = FetchDeManifestAsync(ruManifestMap["ExportWarframes"]);
                var wfEnT = FetchDeManifestAsync(enManifestMap["ExportWarframes"]);
                var wepRuT = FetchDeManifestAsync(ruManifestMap["ExportWeapons"]);
                var wepEnT = FetchDeManifestAsync(enManifestMap["ExportWeapons"]);
                var sentRuT = FetchDeManifestAsync(ruManifestMap["ExportSentinels"]);
                var sentEnT = FetchDeManifestAsync(enManifestMap["ExportSentinels"]);
                var custRuT = FetchDeManifestAsync(ruManifestMap["ExportCustoms"]);
                var custEnT = FetchDeManifestAsync(enManifestMap["ExportCustoms"]);
                var resRuT = FetchDeManifestAsync(ruManifestMap["ExportResources"]);
                var resEnT = FetchDeManifestAsync(enManifestMap["ExportResources"]);
                var recRuT = FetchDeManifestAsync(ruManifestMap["ExportRecipes"]);

                var pImgsT = TryFetchStringAsync(BrowseWfPlusBase + "ExportImages.json");
                var pWfT = TryFetchStringAsync(BrowseWfPlusBase + "ExportWarframes.json");
                var pWepT = TryFetchStringAsync(BrowseWfPlusBase + "ExportWeapons.json");
                var pSentT = TryFetchStringAsync(BrowseWfPlusBase + "ExportSentinels.json");
                var pCustT = TryFetchStringAsync(BrowseWfPlusBase + "ExportCustoms.json");
                var pResT = TryFetchStringAsync(BrowseWfPlusBase + "ExportResources.json");
                var pVendT = TryFetchStringAsync(BrowseWfPlusBase + "ExportVendors.json");
                var pBundT = TryFetchStringAsync(BrowseWfPlusBase + "ExportBundles.json");
                var pRecT = TryFetchStringAsync(BrowseWfPlusBase + "ExportRecipes.json");
                var wsItemsT = TryFetchStringAsync(WarframeStatItemsUrl);

                await Task.WhenAll(new Task[]
                {
                    wfRuT, wfEnT, wepRuT, wepEnT, sentRuT, sentEnT, custRuT, custEnT,
                    resRuT, resEnT, recRuT, pImgsT, pWfT, pWepT, pSentT, pCustT, pResT, pVendT, pBundT, pRecT, wsItemsT
                }).ConfigureAwait(false);

                var dtos = BuildCatalogDtos(
                    wfRuT.Result, wfEnT.Result,
                    wepRuT.Result, wepEnT.Result,
                    sentRuT.Result, sentEnT.Result,
                    custRuT.Result, custEnT.Result,
                    resRuT.Result, resEnT.Result,
                    recRuT.Result,
                    pImgsT.Result, pWfT.Result, pWepT.Result, pSentT.Result, pCustT.Result, pResT.Result,
                    pVendT.Result, pBundT.Result, pRecT.Result, wsItemsT.Result,
                    out var missingImgList, out var missingRuList);

                if (dtos.Count < 500)
                {
                    return (false, null, "Ошибка сборки каталога: недостаточно данных от DE API");
                }

                var doc = new CatalogDocument
                {
                    Version = "2.0",
                    Items = dtos
                };

                string outJson = JsonSerializer.Serialize(doc, JsonOpts);
                File.WriteAllText(_catalogCachePath + ".tmp", outJson, Encoding.UTF8);
                File.Move(_catalogCachePath + ".tmp", _catalogCachePath, overwrite: true);
                File.WriteAllText(_metaCachePath, combinedSig, Encoding.UTF8);

                WriteMissingAssetsReport(dtos, missingImgList, missingRuList, ruManifestMap);

                var models = dtos.Select(MapDtoToModel).ToList();
                LastSyncSummary = $"Синхр. с DE API ({models.Count:N0} предм., {DateTime.Now:HH:mm})";
                return (true, models, $"✓ Каталог обновлён из DE PublicExport: {models.Count:N0} предметов");
            }
            catch (Exception ex)
            {
                return (false, null, $"Ошибка синхронизации с API: {ex.Message}");
            }
        }

        private static Dictionary<string, string> ParseManifestIndex(string indexText)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in indexText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int idx = line.IndexOf(".json!", StringComparison.OrdinalIgnoreCase);
                if (idx <= 0) continue;

                string left = line[..idx]; // ExportWarframes_ru
                int us = left.LastIndexOf('_');
                string key = us > 0 ? left[..us] : left;
                dict[key] = line;
            }
            return dict;
        }

        private async Task<string> FetchDeManifestAsync(string fileEntry)
        {
            string raw = await _http.GetStringAsync(DeManifestBaseUrl + fileEntry).ConfigureAwait(false);
            return ControlCharRegex.Replace(raw, " ");
        }

        private async Task<string?> TryFetchStringAsync(string url)
        {
            try
            {
                string raw = await _http.GetStringAsync(url).ConfigureAwait(false);
                return ControlCharRegex.Replace(raw, " ");
            }
            catch
            {
                return null;
            }
        }

        private static bool CanHaveIncarnon(string enName)
        {
            foreach (string ex in IncarnonExcludedDuals)
            {
                if (enName.Equals(ex, StringComparison.OrdinalIgnoreCase)) return false;
            }

            string stripped = enName;
            foreach (string pref in IncarnonPrefixes)
            {
                if (stripped.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
                {
                    stripped = stripped[pref.Length..];
                    break;
                }
            }
            if (enName.StartsWith("Dex ", StringComparison.OrdinalIgnoreCase) &&
                !enName.Equals("Dex Furis", StringComparison.OrdinalIgnoreCase))
            {
                stripped = enName[4..];
            }

            foreach (string b in IncarnonBases)
            {
                if (stripped.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                    stripped.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CleanName(string? name) =>
            string.IsNullOrEmpty(name) ? string.Empty : StripTagRegex.Replace(name, "").Trim();

        private static string NormStore(string s) =>
            s.Replace("/Lotus/Types/StoreItems/", "/Lotus/", StringComparison.Ordinal)
             .Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal);

        private List<CatalogItemDto> BuildCatalogDtos(
            string wfRuJson, string wfEnJson,
            string wepRuJson, string wepEnJson,
            string sentRuJson, string sentEnJson,
            string custRuJson, string custEnJson,
            string resRuJson, string resEnJson,
            string recRuJson,
            string? pImgsJson, string? pWfJson, string? pWepJson, string? pSentJson, string? pCustJson, string? pResJson,
            string? pVendJson, string? pBundJson, string? pRecJson, string? wsItemsJson,
            out List<(string Cat, string Sub, string Ru, string En, string Id)> missingImgs,
            out List<(string Cat, string Sub, string En, string Ru, string Id)> missingRus)
        {
            var missingImgLocal = new List<(string Cat, string Sub, string Ru, string En, string Id)>();
            var missingRuLocal = new List<(string Cat, string Sub, string En, string Ru, string Id)>();

            var deWfRu = ParseDeArray(wfRuJson, "ExportWarframes");
            var deWfEn = ParseDeArray(wfEnJson, "ExportWarframes");
            var deWepRu = ParseDeArray(wepRuJson, "ExportWeapons");
            var deWepEn = ParseDeArray(wepEnJson, "ExportWeapons");
            var deSentRu = ParseDeArray(sentRuJson, "ExportSentinels");
            var deSentEn = ParseDeArray(sentEnJson, "ExportSentinels");
            var deCustRu = ParseDeArray(custRuJson, "ExportCustoms");
            var deCustEn = ParseDeArray(custEnJson, "ExportCustoms");
            var deResRu = ParseDeArray(resRuJson, "ExportResources");
            var deResEn = ParseDeArray(resEnJson, "ExportResources");
            var deRecArr = ParseDeArrayRaw(recRuJson, "ExportRecipes");

            var plusImgs = ParseObjMap(pImgsJson);
            var plusWf = ParseObjMap(pWfJson);
            var plusWep = ParseObjMap(pWepJson);
            var plusSent = ParseObjMap(pSentJson);
            var plusCust = ParseObjMap(pCustJson);
            var plusRes = ParseObjMap(pResJson);
            var plusVend = ParseObjMap(pVendJson);
            var plusBund = ParseObjMap(pBundJson);
            var plusRec = ParseObjMap(pRecJson);

            // WarframeStat items for vaulted + fallback imageName
            var wsMap = new Dictionary<string, (bool Vaulted, string? ImageName)>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(wsItemsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(wsItemsJson);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (!el.TryGetProperty("uniqueName", out var unEl)) continue;
                        string? un = unEl.GetString();
                        if (string.IsNullOrEmpty(un)) continue;
                        bool vaulted = el.TryGetProperty("vaulted", out var vEl) && vEl.ValueKind == JsonValueKind.True;
                        string? img = el.TryGetProperty("imageName", out var iEl) ? iEl.GetString() : null;
                        wsMap[un] = (vaulted, img);
                    }
                }
                catch { }
            }

            // Index Vendors & Bundles
            var vendorByStore = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (vkey, vEl) in plusVend)
            {
                if (!vEl.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in itemsEl.EnumerateArray())
                {
                    if (!it.TryGetProperty("storeItem", out var siEl)) continue;
                    string si = NormStore(siEl.GetString() ?? "");
                    if (!string.IsNullOrEmpty(si) && !vendorByStore.ContainsKey(si))
                        vendorByStore[si] = vkey;
                }
            }

            var regalItems = new HashSet<string>(StringComparer.Ordinal);
            var realMoneyItems = new HashSet<string>(StringComparer.Ordinal);
            var platBundleItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (bkey, bEl) in plusBund)
            {
                bool isRegal = bkey.Contains("PrimeVault", StringComparison.OrdinalIgnoreCase) ||
                               (bEl.TryGetProperty("regalAyaCost", out var rCost) && rCost.ValueKind == JsonValueKind.Number && rCost.GetInt32() > 0);
                bool isReal = bkey.Contains("PrimeAccess", StringComparison.OrdinalIgnoreCase) ||
                              bkey.Contains("Supporter", StringComparison.OrdinalIgnoreCase) ||
                              bkey.Contains("TennoCon", StringComparison.OrdinalIgnoreCase) ||
                              bkey.Contains("Heirloom", StringComparison.OrdinalIgnoreCase) ||
                              bkey.Contains("DigitalPack", StringComparison.OrdinalIgnoreCase);
                bool isPlat = bEl.TryGetProperty("platinumCost", out var pCost) && pCost.ValueKind == JsonValueKind.Number && pCost.GetInt32() > 0;

                if (!bEl.TryGetProperty("components", out var compsEl) || compsEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var comp in compsEl.EnumerateArray())
                {
                    string cn = NormStore(comp.TryGetProperty("typeName", out var tEl) ? tEl.GetString() ?? "" : "");
                    if (string.IsNullOrEmpty(cn)) continue;
                    if (isRegal) regalItems.Add(cn);
                    else if (isReal) realMoneyItems.Add(cn);
                    else if (isPlat) platBundleItems.Add(cn);
                }
            }

            // Index Recipes & Ingredient reverse lookup
            var recipeByResult = new Dictionary<string, List<(string ItemType, int Count)>>(StringComparer.Ordinal);
            void IndexRecipe(JsonElement rEl)
            {
                if (!rEl.TryGetProperty("resultType", out var rtEl)) return;
                string rt = NormStore(rtEl.GetString() ?? "");
                if (string.IsNullOrEmpty(rt) || recipeByResult.ContainsKey(rt)) return;
                if (!rEl.TryGetProperty("ingredients", out var ingArr) || ingArr.ValueKind != JsonValueKind.Array) return;
                var list = new List<(string, int)>();
                foreach (var ing in ingArr.EnumerateArray())
                {
                    string it = NormStore(ing.TryGetProperty("ItemType", out var itEl) ? itEl.GetString() ?? "" : "");
                    int cnt = ing.TryGetProperty("ItemCount", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetInt32() : 1;
                    if (!string.IsNullOrEmpty(it)) list.Add((it, cnt));
                }
                recipeByResult[rt] = list;
            }
            foreach (var (_, rEl) in plusRec) IndexRecipe(rEl);
            foreach (var rEl in deRecArr) IndexRecipe(rEl);

            string ResolveNameRu(string uid)
            {
                if (deWfRu.TryGetValue(uid, out var e1)) return CleanName(GetStr(e1, "name"));
                if (deWepRu.TryGetValue(uid, out var e2)) return CleanName(GetStr(e2, "name"));
                if (deResRu.TryGetValue(uid, out var e3)) return CleanName(GetStr(e3, "name"));
                int slash = uid.LastIndexOf('/');
                return slash >= 0 ? uid[(slash + 1)..] : uid;
            }

            var usedInCrafting = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (rt, ings) in recipeByResult)
            {
                string outRu = ResolveNameRu(rt);
                foreach (var (it, _) in ings)
                {
                    if (deWepEn.ContainsKey(it) || deWfEn.ContainsKey(it) || deSentEn.ContainsKey(it))
                    {
                        if (!usedInCrafting.TryGetValue(it, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            usedInCrafting[it] = set;
                        }
                        set.Add(outRu);
                    }
                }
            }

            var dtos = new List<CatalogItemDto>(6000);

            (string? icon, string? hash, string? wsImg) ResolveImg(string uid, JsonElement? plusEl)
            {
                string? ic = plusEl.HasValue ? GetStr(plusEl.Value, "icon") : null;
                string? h = null;
                if (!string.IsNullOrEmpty(ic) && plusImgs.TryGetValue(ic, out var imgMeta))
                    h = GetStr(imgMeta, "contentHash");
                string? wsImg = wsMap.TryGetValue(uid, out var ws) ? ws.ImageName : null;
                return (ic, h, wsImg);
            }

            List<CatalogCompDto> MakeComps(string uid, out bool hasRec)
            {
                if (!recipeByResult.TryGetValue(uid, out var ings))
                {
                    hasRec = false;
                    return new List<CatalogCompDto>();
                }
                hasRec = true;
                var list = new List<CatalogCompDto>(ings.Count);
                foreach (var (it, cnt) in ings)
                {
                    string rn = ResolveNameRu(it);
                    string? ic = null;
                    if (plusRes.TryGetValue(it, out var pr)) ic = GetStr(pr, "icon");
                    else if (plusWep.TryGetValue(it, out var pw)) ic = GetStr(pw, "icon");
                    list.Add(new CatalogCompDto { Name = rn, Count = cnt, Icon = ic });
                }
                return list;
            }

            void AddDto(string uid, string displayRu, string enName, string cat, string sub, int xp, int mr,
                bool vaulted, bool isPrime, bool isFounder, bool subsume, bool incarnon, bool craft, bool masterable,
                string srcKind, string srcInfo, string cost, string? icon, string? hash, string? wsImg,
                List<CatalogCompDto> comps, string usedFor = "", string tag = "")
            {
                if (string.IsNullOrEmpty(icon) && string.IsNullOrEmpty(wsImg))
                    missingImgLocal.Add((cat, sub, string.IsNullOrEmpty(displayRu) ? enName : displayRu, enName, uid));
                if (string.IsNullOrEmpty(displayRu) || !CyrillicRegex.IsMatch(displayRu))
                    missingRuLocal.Add((cat, sub, enName, displayRu, uid));

                dtos.Add(new CatalogItemDto
                {
                    Id = uid,
                    Name = string.IsNullOrEmpty(displayRu) ? enName : displayRu,
                    NameEn = enName,
                    Cat = cat,
                    Sub = sub,
                    Xp = xp,
                    Mr = mr,
                    Vaulted = vaulted,
                    Prime = isPrime,
                    Founder = isFounder,
                    Subsume = subsume,
                    Incarnon = incarnon,
                    Craft = craft,
                    Masterable = masterable,
                    SrcKind = srcKind,
                    Src = srcInfo,
                    Cost = cost,
                    Icon = icon,
                    Hash = hash,
                    WsImg = wsImg,
                    Comps = comps,
                    UsedFor = usedFor,
                    Tag = tag
                });
            }

            // 1. Warframes, Necramechs, Archwings
            foreach (var (uid, enEl) in deWfEn)
            {
                if (uid.EndsWith("/Helminth", StringComparison.Ordinal) || uid.EndsWith("/OrionSuit", StringComparison.Ordinal))
                    continue;
                JsonElement? pwf = plusWf.TryGetValue(uid, out var pwEl) ? pwEl : null;
                if (pwf.HasValue && GetBool(pwf.Value, "excludeFromCodex")) continue;

                string enName = CleanName(GetStr(enEl, "name"));
                string ruName = deWfRu.TryGetValue(uid, out var ruEl) ? CleanName(GetStr(ruEl, "name")) : enName;
                string pc = GetStr(enEl, "productCategory") ?? "Suits";

                string cat, sub, tag;
                int xp;
                bool isP = enName.Contains("Prime", StringComparison.OrdinalIgnoreCase) ||
                           (pwf.HasValue && GetStr(pwf.Value, "variantType") == "VT_PRIME");
                if (uid.Contains("Archwing", StringComparison.OrdinalIgnoreCase) || pc == "SpaceSuits")
                {
                    cat = "Арчвинг"; sub = isP ? "Prime" : "Арчвинг"; tag = "Арчвинг"; xp = 6000;
                }
                else if (pc == "MechSuits")
                {
                    cat = "Некромехи"; sub = "Некромех"; tag = "Некромех"; xp = 8000; isP = false;
                }
                else if (pc == "Suits")
                {
                    cat = "Варфреймы";
                    sub = isP ? "Prime" : (enName.Contains("Umbra", StringComparison.OrdinalIgnoreCase) ? "Квестовый" : "Стандартный");
                    tag = ""; xp = 6000;
                }
                else continue;

                bool vaulted = wsMap.TryGetValue(uid, out var ws) && ws.Vaulted;
                bool founder = uid.EndsWith("/ExcaliburPrime", StringComparison.Ordinal);
                bool subsume = cat == "Варфреймы" && !isP && !enName.Contains("Umbra", StringComparison.OrdinalIgnoreCase);
                var (ic, h, wsImg) = ResolveImg(uid, pwf);
                var comps = MakeComps(uid, out bool hasRec);
                string srcKind = founder ? "Founder" : ((hasRec || isP) ? (regalItems.Contains(uid) ? "RegalAya" : "Foundry") : "QuestOrDrop");
                string srcInfo = founder ? "Эксклюзив Основателей (Hunter+)" :
                    ((hasRec || isP) ? (vaulted ? "В Хранилище (Реликвии / Варзия)" : (isP ? "Реликвии Бездны / Варзия" : "Кузница (Чертежи компонентов)")) : "Квестовая награда");
                ruName = ApplyRuFallback(enName, ruName);
                string display = (ruName.Equals(enName, StringComparison.OrdinalIgnoreCase) || !CyrillicRegex.IsMatch(ruName)) ? enName : $"{ruName} ({enName})";

                AddDto(uid, display, enName, cat, sub, xp, GetInt(enEl, "masteryReq"), vaulted, isP, founder,
                    subsume, false, hasRec && !founder && !enName.Contains("Umbra", StringComparison.OrdinalIgnoreCase),
                    true, srcKind, srcInfo, founder ? "Founders" : "", ic, h, wsImg, comps, "", tag);
            }

            // 2. Weapons, Archguns, Archmelee, Sentinel weapons, Modular barrels/tips/prisms/decks + Companions (MOA/Hound)
            foreach (var (uid, enEl) in deWepEn)
            {
                JsonElement? pw = plusWep.TryGetValue(uid, out var pwEl) ? pwEl : null;
                if (GetBool(enEl, "excludeFromCodex") || (pw.HasValue && GetBool(pw.Value, "excludeFromCodex")) || uid.Contains("PvPVariant", StringComparison.Ordinal))
                    continue;

                string pc = GetStr(enEl, "productCategory") ?? "";
                if (pc == "SpecialItems") continue;

                string enName = CleanName(GetStr(enEl, "name"));
                string ruName = deWepRu.TryGetValue(uid, out var ruEl) ? CleanName(GetStr(ruEl, "name")) : enName;
                bool masterable = true;

                string cat = "", sub = "", tag = "";
                int xp = 3000;
                bool matched = true;

                if (uid.Contains("OperatorAmplifiers", StringComparison.Ordinal) && uid.Contains("Barrel", StringComparison.Ordinal))
                { cat = "Модульное"; sub = "Усилитель"; tag = "Усилитель"; }
                else if (uid.Contains("Hoverboard", StringComparison.Ordinal) && uid.Contains("Deck", StringComparison.Ordinal))
                { cat = "Модульное"; sub = "К-Драйв"; tag = "К-Драйв"; xp = 6000; }
                else if (uid.Contains("ModularMelee", StringComparison.Ordinal) && uid.Contains("/Tip", StringComparison.Ordinal))
                { cat = "Модульное"; sub = "Зо"; tag = "Зо"; }
                else if (uid.Contains("Modular", StringComparison.Ordinal) && uid.Contains("/Barrel", StringComparison.Ordinal))
                { cat = "Модульное"; sub = "Китган"; tag = "Китган"; }
                else if (uid.Contains("MoaPetParts/MoaPetHead", StringComparison.Ordinal))
                { cat = "Компаньоны"; sub = "МОА"; tag = "МОА"; xp = 6000; }
                else if (uid.Contains("ZanukaPetParts/ZanukaPetPartHead", StringComparison.Ordinal))
                { cat = "Компаньоны"; sub = "Гончая"; tag = "Гончая"; xp = 6000; }
                else if (uid.Contains("Modular", StringComparison.Ordinal) || uid.Contains("/Tip", StringComparison.Ordinal) ||
                         uid.Contains("Handle", StringComparison.Ordinal) || uid.Contains("Grip", StringComparison.Ordinal) ||
                         uid.Contains("Scaffold", StringComparison.Ordinal) || uid.Contains("MoaPet", StringComparison.Ordinal) ||
                         uid.Contains("ZanukaPet", StringComparison.Ordinal) || uid.Contains("Hoverboard", StringComparison.Ordinal))
                { matched = false; }
                else if (pc == "SpaceGuns")
                {
                    cat = "Арчвинг"; tag = "Тяжелое оружие";
                    sub = enName.Contains("Prime") ? "Prime" : (enName.Contains("Kuva ") ? "Кува" : (enName.Contains("Vandal") ? "Вандал" : "Тяжелое оружие"));
                    xp = enName.Contains("Kuva ") ? 4000 : 3000;
                }
                else if (pc == "SpaceMelee")
                {
                    cat = "Арчвинг"; tag = "Ближний бой";
                    sub = enName.Contains("Prisma ") ? "Призма" : (enName.Contains("Prime") ? "Prime" : "Ближний бой");
                }
                else if (pc == "SentinelWeapons")
                {
                    bool isSentPrime = enName.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase);
                    cat = "Компаньоны"; tag = "Оружие стража";
                    sub = isSentPrime ? "Prime" : (enName.Contains("Prisma", StringComparison.OrdinalIgnoreCase) ? "Призма" : "Оружие стража");
                }
                else if (pc is "LongGuns" or "Pistols" or "Melee" or "DrifterMelee")
                {
                    cat = pc switch { "LongGuns" => "Основное", "Pistols" => "Вторичное", _ => "Ближний бой" };
                    if (enName.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase)) sub = "Prime";
                    else if (enName.StartsWith("Kuva ", StringComparison.OrdinalIgnoreCase)) sub = "Кува";
                    else if (enName.StartsWith("Tenet ", StringComparison.OrdinalIgnoreCase)) sub = "Догмат";
                    else if (enName.Contains("Coda ", StringComparison.OrdinalIgnoreCase)) sub = "Кода 1999";
                    else if (enName.Contains("Vandal", StringComparison.OrdinalIgnoreCase)) sub = "Вандал";
                    else if (enName.Contains("Wraith", StringComparison.OrdinalIgnoreCase)) sub = "Врайт";
                    else if (enName.Contains("Prisma", StringComparison.OrdinalIgnoreCase)) sub = "Призма";
                    else if (pc == "DrifterMelee") sub = "Дувири";
                    else sub = "Стандартный";

                    xp = (sub is "Кува" or "Догмат" or "Кода 1999" || enName == "Paracesis") ? 4000 : 3000;
                }
                else matched = false;

                if (!matched) continue;

                bool isP = enName.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase);
                bool founder = enName is "Lato Prime" or "Skana Prime";
                bool vaulted = wsMap.TryGetValue(uid, out var ws) && ws.Vaulted;
                var (ic, h, wsImg) = ResolveImg(uid, pw);
                var comps = MakeComps(uid, out bool hasRec);
                string usedFor = usedInCrafting.TryGetValue(uid, out var ufSet) ? string.Join(", ", ufSet) : "";

                string srcKind, srcInfo, costTxt = "";
                if (founder) { srcKind = "Founder"; srcInfo = "Эксклюзив Основателей (Набор Hunter и выше)"; costTxt = "Founders"; }
                else if (sub == "Кува") { srcKind = "QuestOrDrop"; srcInfo = "Лич Кувы (Реквием-реликвии)"; costTxt = "Лич Кувы"; }
                else if (sub == "Догмат")
                {
                    if (vendorByStore.TryGetValue(uid, out var vk) && vk.Contains("PerrinSequence", StringComparison.OrdinalIgnoreCase))
                    { srcKind = "InGameCurrency"; srcInfo = "Эрго Гласт (40 Повреждённых Голоключей)"; costTxt = "40 Голоключей"; }
                    else { srcKind = "QuestOrDrop"; srcInfo = "Сестра Парвоса (Короны Грэнума)"; costTxt = "Сестра Парвоса"; }
                }
                else if (sub == "Кода 1999") { srcKind = "InGameCurrency"; srcInfo = "Элеонора (Живые сердечники, Хёлвания 1999)"; costTxt = "Элеонора (1999)"; }
                else if (sub == "Призма") { srcKind = "InGameCurrency"; srcInfo = "Баро Ки'Тиир (Торговец Бездны)"; costTxt = "Дукаты (Баро)"; }
                else if (hasRec) { srcKind = "Foundry"; srcInfo = vaulted ? "В Хранилище / Реликвии Бездны" : "Кузница / Додзё / Синдикаты"; }
                else if (vendorByStore.ContainsKey(uid)) { var vc = ClassifyCollectibleSource(uid, pw); srcKind = vc.srcKind; srcInfo = vc.srcInfo; costTxt = vc.cost; }
                else if (enName.StartsWith("Dex ", StringComparison.Ordinal)) { srcKind = "QuestOrDrop"; srcInfo = "Годовщина Warframe (Бесплатная награда)"; costTxt = "Годовщина"; }
                else if (sub is "Вандал" or "Врайт") { srcKind = "QuestOrDrop"; srcInfo = "Вторжения / Баро Ки'Тиир / Угрозы (Фоморианец и Секачи)"; costTxt = sub; }
                else { srcKind = "InGameCurrency"; srcInfo = "Магазин / Репутация / Синдикаты"; costTxt = "Кредиты / Репут."; }

                ruName = ApplyRuFallback(enName, ruName);
                string display = (ruName.Equals(enName, StringComparison.OrdinalIgnoreCase) || !CyrillicRegex.IsMatch(ruName)) ? enName : $"{ruName} ({enName})";

                AddDto(uid, display, enName, cat, sub, xp, GetInt(enEl, "masteryReq"), vaulted, isP, founder,
                    false, CanHaveIncarnon(enName), hasRec && !founder, masterable, srcKind, srcInfo,
                    costTxt, ic, h, wsImg, comps, usedFor, tag);
            }

            // 3. Companions (Sentinels, Kubrows, Kavats, Vulpaphylas, Predasites)
            foreach (var (uid, enEl) in deSentEn)
            {
                JsonElement? ps = plusSent.TryGetValue(uid, out var psEl) ? psEl : null;
                if (GetBool(enEl, "excludeFromCodex") || (ps.HasValue && GetBool(ps.Value, "excludeFromCodex")) ||
                    uid.EndsWith("MoaPetPowerSuit", StringComparison.Ordinal) || uid.Contains("ZanukaPet", StringComparison.Ordinal))
                    continue;

                string enName = CleanName(GetStr(enEl, "name"));
                string ruName = deSentRu.TryGetValue(uid, out var ruEl) ? CleanName(GetStr(ruEl, "name")) : enName;
                string pc = GetStr(enEl, "productCategory") ?? "";
                bool isP = enName.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase);

                string sub, tag;
                if (pc == "Sentinels") { sub = isP ? "Prime" : (enName.Contains("Prisma") ? "Призма" : "Страж"); tag = "Страж"; }
                else if (enName.Contains("Vulpaphyla", StringComparison.OrdinalIgnoreCase)) { sub = "Вульпафила"; tag = "Вульпафила"; }
                else if (enName.Contains("Predasite", StringComparison.OrdinalIgnoreCase)) { sub = "Предазит"; tag = "Предазит"; }
                else if (enName.Contains("Kavat", StringComparison.OrdinalIgnoreCase)) { sub = "Кават"; tag = "Кават"; }
                else { sub = "Кубрау"; tag = "Кубрау"; }

                bool vaulted = wsMap.TryGetValue(uid, out var ws) && ws.Vaulted;
                var (ic, h, wsImg) = ResolveImg(uid, ps);
                var comps = MakeComps(uid, out bool hasRec);
                ruName = ApplyRuFallback(enName, ruName);
                string display = (ruName.Equals(enName, StringComparison.OrdinalIgnoreCase) || !CyrillicRegex.IsMatch(ruName)) ? enName : $"{ruName} ({enName})";

                AddDto(uid, display, enName, "Компаньоны", sub, 6000, GetInt(enEl, "masteryReq"), vaulted, isP, false,
                    false, false, hasRec, true, hasRec ? "Foundry" : "QuestOrDrop", "Кузница / Инкубатор / Сын на Деймосе", "",
                    ic, h, wsImg, comps, "", tag);
            }

            (string Info, string Cost) MapVendorRu(string vkey) => vkey switch
            {
                _ when vkey.Contains("Hallow") => ("Дочь — Ночи Наберуса (Деймос)", "Жетоны Матери"),
                _ when vkey.Contains("Valentine") => ("Тикер — Дни Звёзд (Фортуна)", "Долговые обяз."),
                _ when vkey.Contains("Scarlet") || vkey.Contains("JadeShadows") || vkey.Contains("WaterFight") || vkey.Contains("EventVendor")
                    => ("Магазин события / Сезонный торговец", "Ивентовая валюта"),
                _ when vkey.Contains("RadioLegion") || vkey.Contains("Nightwave") => ("Нора Найт (Ночная Волна)", "Доверие Норы"),
                _ when vkey.Contains("PrimeVault") || vkey.Contains("Varzia") => ("Варзия (Возрождение Прайм)", "Айя / К.Айя"),
                _ when vkey.Contains("TeshinHardMode") => ("Тешин (Стальной Путь)", "Стальная эссенция"),
                _ when vkey.Contains("EliteAlert") => ("Арбитраж Гексис (Реле)", "Эссенция Витус"),
                _ when vkey.Contains("PerrinSequence") => ("Эрго Гласт (Последовательность Перрина)", "Голоключи"),
                _ when vkey.Contains("Chipper") => ("Чиппер (Гарнизон Кахла)", "Запас Гарнизона"),
                _ when vkey.Contains("Triad") => ("Мари Леру (La Cathédrale)", "Триада (Тау)"),
                _ when vkey.Contains("EntratiLab") => ("Лойд / Птичка-3 (Санктум Анатомика)", "Репут. Кавии"),
                _ when vkey.Contains("Zariman") || vkey.Contains("Archimedean") => ("Хомбаск / Кавальеро (Зариман)", "Перья Бездны"),
                _ when vkey.Contains("TheHex") || vkey.Contains("InfestedLich") => ("Синдикат Хекс (Хёлвания 1999)", "Репут. Хекс"),
                _ when vkey.Contains("Acrithis") || vkey.Contains("Duviri") => ("Акритис (Дувири)", "Ресурсы Дувири"),
                _ when vkey.Contains("Deimos") => ("Некралиск (Семья Энтрати)", "Жетоны Энтрати"),
                _ when vkey.Contains("Ostron") => ("Цетус (Острон)", "Репут. Острон"),
                _ when vkey.Contains("Solaris") => ("Фортуна (Солярис)", "Репут. Солярис"),
                _ when vkey.Contains("Baro") || vkey.Contains("VoidTrader") => ("Баро Ки'Тиир", "Дукаты (Баро)"),
                _ => ("Торговец репутации / Синдикат", "Репутация")
            };

            (string srcKind, string srcInfo, string cost) ClassifyCollectibleSource(string uid, JsonElement? plusEl)
            {
                if (uid.Contains("MasteryTroph", StringComparison.OrdinalIgnoreCase) || (uid.Contains("Trophy", StringComparison.Ordinal) && uid.Contains("Rank", StringComparison.Ordinal)))
                    return ("QuestOrDrop", "Награда за Ранг Мастерства", "Бесплатно (MR)");
                if (uid.Contains("TennoCon", StringComparison.OrdinalIgnoreCase))
                    return ("RealMoney", "Набор / Реле TennoCon (Билет)", "TennoCon");
                if (uid.Contains("FoundersBadge", StringComparison.OrdinalIgnoreCase) || uid.Contains("Founders", StringComparison.Ordinal))
                    return ("Founder", "Эксклюзив Основателей", "Founders");
                if (uid.Contains("TutorialQuest", StringComparison.Ordinal) || (uid.Contains("Relay", StringComparison.Ordinal) && uid.Contains("Trophy", StringComparison.Ordinal)))
                    return ("QuestOrDrop", "Квест / Игровая награда", "Награда");

                int regal = plusEl.HasValue ? GetInt(plusEl.Value, "regalAyaCost") : 0;
                if (regalItems.Contains(uid) || regal > 0)
                    return ("RegalAya", "Возрождение Прайм (Варзия, Королевская Айя)", regal > 0 ? $"{regal} К.Айя" : "К.Айя");

                string? ic = plusEl.HasValue ? GetStr(plusEl.Value, "icon") : null;
                string? pn = plusEl.HasValue ? GetStr(plusEl.Value, "name") : null;
                if (realMoneyItems.Contains(uid) ||
                    (ic?.Contains("Tennogen", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (pn?.Contains("SteamWorkshop", StringComparison.OrdinalIgnoreCase) ?? false))
                    return ("RealMoney", "Прямая покупка / Prime Access / Steam Tennogen", "За реал / Tennogen");

                if (vendorByStore.TryGetValue(uid, out string? vkey))
                {
                    var (siV, costV) = MapVendorRu(vkey);
                    string kindV = (vkey.Contains("PrimeVault") || vkey.Contains("Varzia")) ? "RegalAya" : "InGameCurrency";
                    return (kindV, siV, costV);
                }

                int plat = plusEl.HasValue ? GetInt(plusEl.Value, "platinumCost") : 0;
                int cred = plusEl.HasValue ? GetInt(plusEl.Value, "creditsCost") : 0;
                if (recipeByResult.ContainsKey(uid))
                    return ("Foundry", "Чертёж / Крафт в Кузнице", "Кузница");
                if (cred > 0 && plat == 0)
                    return ("InGameCurrency", "Внутриигровой магазин (Кредиты)", $"{cred:N0} Кред");
                if (plat > 0)
                    return ("Platinum", "Внутриигровой магазин (Платина)", $"{plat} Пл");
                if (platBundleItems.Contains(uid))
                    return ("Platinum", "Набор за Платину в магазине", "Набор (Платина)");

                return ("QuestOrDrop", "Задание / Квест / Выпадение с врагов / Ивент", "Дроп / Задание");
            }

            // 4. Cosmetics
            foreach (var (uid, enEl) in deCustEn)
            {
                JsonElement? pc = plusCust.TryGetValue(uid, out var pcEl) ? pcEl : null;
                if (uid.Contains("EmptyCustomization", StringComparison.Ordinal) ||
                    uid.Contains("DefaultWarframeVoiceItem", StringComparison.Ordinal) ||
                    uid.EndsWith("Customization", StringComparison.Ordinal) ||
                    uid.EndsWith("BaseOperatorAnims", StringComparison.Ordinal) ||
                    (pc.HasValue && GetBool(pc.Value, "excludeFromCodex")))
                    continue;

                string enName = CleanName(GetStr(enEl, "name"));
                if (string.IsNullOrEmpty(enName) || enName.StartsWith("/Lotus/") || enName.StartsWith("Lotus", StringComparison.Ordinal) ||
                    enName == "BaseOperatorAnims" || enName == "OperatorCustomization")
                    continue;

                string rawRu = deCustRu.TryGetValue(uid, out var ruEl) ? CleanName(GetStr(ruEl, "name")) : enName;
                string ruName = ApplyRuFallback(enName, rawRu);
                var (ic, h, wsImg) = ResolveImg(uid, pc);
                string hint = $"{ic} {uid} {enName}";
                string sub =
                    hint.Contains("Ephemera", StringComparison.OrdinalIgnoreCase) ? "Эфемера" :
                    (hint.Contains("/Halo/") || enName.Contains("Signa") || uid.Contains("Crown")) ? "Сигна" :
                    (hint.Contains("/Syandanas/") || enName.Contains("Syandana") || hint.Contains("/Scarves/")) ? "Сандана" :
                    (hint.Contains("/Armour/") || uid.Contains("Armor") || enName.Contains("Shoulder") || enName.Contains("Chest Plate") || enName.Contains("Leg Plate")) ? "Броня" :
                    (hint.Contains("/AnimationSets/") || enName.Contains("Animation Set")) ? "Анимации" :
                    (hint.Contains("/Helmets/") || enName.EndsWith("Helmet", StringComparison.Ordinal)) ? "Шлем" :
                    (hint.Contains("/Sigils/") || hint.Contains("/Emblems/") || enName.Contains("Sigil") || enName.Contains("Emblem")) ? "Сигилы" :
                    (enName.Contains("Skin") || hint.Contains("/WarframeSkins/") || hint.Contains("/Weapons/")) ? "Скин" : "Прочее";

                var (sk, si, cost) = ClassifyCollectibleSource(uid, pc);
                var comps = MakeComps(uid, out bool hasRec);
                bool isFounder = sk == "Founder";
                bool vaulted = (wsMap.TryGetValue(uid, out var ws) && ws.Vaulted) || sk == "RegalAya";

                AddDto(uid, string.IsNullOrEmpty(ruName) ? enName : ruName, enName, "Косметика", sub, 0, 0,
                    vaulted, enName.Contains("Prime", StringComparison.OrdinalIgnoreCase), isFounder, false, false,
                    hasRec, false, sk, si, cost, ic, h, wsImg, comps);
            }

            // 5. Orbiter Decorations (ShipDecorations)
            foreach (var (uid, enEl) in deResEn)
            {
                if (!plusRes.TryGetValue(uid, out var prEl) || GetStr(prEl, "productCategory") != "ShipDecorations")
                    continue;

                string enName = CleanName(GetStr(enEl, "name"));
                string rawRu = deResRu.TryGetValue(uid, out var ruEl) ? CleanName(GetStr(ruEl, "name")) : enName;
                string ruName = ApplyRuFallback(enName, rawRu);
                var (ic, h, wsImg) = ResolveImg(uid, prEl);

                string sub =
                    (enName.Contains("Noggle") || uid.Contains("Bobble")) ? "Фигурки" :
                    (enName.Contains("Floof") || uid.Contains("Plush")) ? "Флоффы" :
                    (enName.Contains("Articula") || enName.Contains("Trophy") || uid.Contains("Trophy")) ? "Артикулы & Трофеи" :
                    (enName.Contains("Display") || enName.Contains("Poster") || enName.Contains("Scene") || enName.Contains("Glyph")) ? "Дисплеи" :
                    "Декор Орбитера";

                var (sk, si, cost) = ClassifyCollectibleSource(uid, prEl);
                var comps = MakeComps(uid, out bool hasRec);

                AddDto(uid, string.IsNullOrEmpty(ruName) ? enName : ruName, enName, "Украшения Убежища", sub, 0, 0,
                    sk == "RegalAya", enName.Contains("Prime", StringComparison.OrdinalIgnoreCase), false, false, false,
                    hasRec, false, sk, si, cost, ic, h, wsImg, comps);
            }

            missingImgs = missingImgLocal;
            missingRus = missingRuLocal;
            return dtos;
        }

        private void WriteMissingAssetsReport(
            List<CatalogItemDto> items,
            List<(string Cat, string Sub, string Ru, string En, string Id)> missingImgs,
            List<(string Cat, string Sub, string En, string Ru, string Id)> missingRus,
            Dictionary<string, string> manifestMap)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("========================================================================");
                sb.AppendLine("ОТЧЁТ ПО КАРТИНКАМ И РУССКОЙ ЛОКАЛИЗАЦИИ В API WARFRAME");
                sb.AppendLine($"Обновлено автоматически: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Всего в каталоге: {items.Count:N0} предметов");
                sb.AppendLine("========================================================================");
                sb.AppendLine();
                sb.AppendLine("[1] КАРТИНКИ:");
                sb.AppendLine($"  • Предметы без пути к иконке во всех API (DE + browse.wf + WarframeStat): {missingImgs.Count}");
                sb.AppendLine("  • Каскад загрузки: 1) data/assets/items -> 2) data/cache/images (диск)");
                sb.AppendLine("    -> 3) browse.wf{icon} -> 4) content.warframe.com/PublicExport{icon}!{hash}");
                sb.AppendLine("    -> 5) cdn.warframestat.us/img/{wsImg}");
                foreach (var m in missingImgs.Take(100))
                    sb.AppendLine($"    - [{m.Cat} / {m.Sub}] {m.Ru} | {m.Id}");
                sb.AppendLine();
                sb.AppendLine($"[2] ОТСУТСТВУЕТ КИРИЛЛИЧЕСКИЙ ПЕРЕВОД (RU) В ОФИЦИАЛЬНОМ EXPORT DE ({missingRus.Count}):");
                foreach (var m in missingRus)
                    sb.AppendLine($"  • [{m.Cat} / {m.Sub}] {m.En} ({m.Id})");
                sb.AppendLine();
                sb.AppendLine("[3] ПРИМЕЧАНИЕ ПО ЭКСКЛЮЗИВАМ ОСНОВАТЕЛЕЙ (FOUNDERS):");
                sb.AppendLine("  • Скрываются переключателем в фильтрах: Excalibur Prime, Lato Prime, Skana Prime.");
                sb.AppendLine("  • В официальных манифестах DE и вики у Набора Основателей никогда не было 'Сандана Прайм'");
                sb.AppendLine("    (только глифы/сигилы Основателей; Misa Prime относится к Возрождению Прайм / Варзии).");
                sb.AppendLine();
                sb.AppendLine("[4] ВЕРСИИ МАНИФЕСТОВ DE PUBLIC EXPORT:");
                foreach (var kv in manifestMap.OrderBy(k => k.Key))
                    sb.AppendLine($"  • {kv.Value}");

                File.WriteAllText(_missingReportPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static Dictionary<string, JsonElement> ParseDeArray(string json, string rootArrayName)
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var el in ParseDeArrayRaw(json, rootArrayName))
            {
                if (el.TryGetProperty("uniqueName", out var unEl))
                {
                    string? un = unEl.GetString();
                    if (!string.IsNullOrEmpty(un))
                        dict[un] = el;
                }
            }
            return dict;
        }

        private static List<JsonElement> ParseDeArrayRaw(string json, string rootArrayName)
        {
            var list = new List<JsonElement>();
            if (string.IsNullOrEmpty(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(rootArrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    list.Add(el.Clone());
            }
            return list;
        }

        private static Dictionary<string, JsonElement> ParseObjMap(string? json)
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json)) return dict;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.Clone();
                }
            }
            catch { }
            return dict;
        }

        private static string? GetStr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int GetInt(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int num) ? num : 0;

        private static bool GetBool(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

        private static CollectionItemModel MapDtoToModel(CatalogItemDto d)
        {
            string uid = d.Id ?? string.Empty;
            string nameEn = d.NameEn ?? string.Empty;
            string sub = d.Sub ?? "Стандартный";
            int xp = d.Xp;
            bool isFounder = d.Founder || d.SrcKind == "Founder" ||
                             uid.Contains("FoundersBadge", StringComparison.OrdinalIgnoreCase) ||
                             uid.EndsWith("/ExcaliburPrime", StringComparison.Ordinal);
            bool isVaulted = d.SrcKind == "RealMoney" ? false : d.Vaulted;
            string slotTag = d.Tag ?? string.Empty;
            string name = d.Name ?? string.Empty;

            if (sub == "К-Драйв" && xp < 6000) xp = 6000;
            if (slotTag == "Оружие стража" && d.Prime && sub != "Prime") sub = "Prime";
            if (d.Cat == "Компаньоны" && nameEn.Contains("Predasite", StringComparison.OrdinalIgnoreCase))
            {
                sub = "Предазит";
                slotTag = "Предазит";
            }
            if (!string.IsNullOrEmpty(nameEn) && ExtraRuOverrides.TryGetValue(nameEn, out string? ovRu))
            {
                name = d.Cat is "Косметика" or "Украшения Убежища"
                    ? ovRu
                    : (ovRu.Equals(nameEn, StringComparison.OrdinalIgnoreCase) ? nameEn : $"{ovRu} ({nameEn})");
            }

            var item = new CollectionItemModel
            {
                UniqueName = uid,
                Name = name,
                NameEn = nameEn,
                Category = d.Cat ?? "Варфреймы",
                SubCategory = sub,
                MasteryXp = xp,
                RequiredMr = d.Mr,
                IsVaulted = isVaulted,
                IsPrimeFlag = d.Prime,
                IsFounder = isFounder,
                CanBeSubsumed = d.Subsume,
                CanHaveIncarnon = d.Incarnon,
                CanBeCrafted = d.Craft,
                IsMasterable = d.Masterable,
                SourceKind = d.SrcKind ?? "Foundry",
                SourceInfo = d.Src ?? string.Empty,
                CostText = d.Cost ?? string.Empty,
                IconPath = d.Icon,
                ImageHash = d.Hash,
                FallbackImageName = d.WsImg,
                UsedInCraftingFor = d.UsedFor ?? string.Empty,
                IsUsedAsIngredient = !string.IsNullOrEmpty(d.UsedFor),
                SlotTag = slotTag,
                IconGlyph = ResolveCategoryGlyph(d.Cat, sub)
            };

            item.ImageCacheKey = ImageCacheService.ComputeCacheKey(item.IconPath, item.ImageHash, item.FallbackImageName);

            // Provide resolved URL string so CraftQueue/other tabs can render thumbnails too
            string? local = ImageCacheService.Instance.ResolveBundledAssetPath(item.FallbackImageName, item.IconPath);
            if (!string.IsNullOrEmpty(local))
                item.ThumbnailUrl = local;
            else if (!string.IsNullOrEmpty(item.IconPath))
                item.ThumbnailUrl = $"https://browse.wf{item.IconPath}";
            else if (!string.IsNullOrEmpty(item.FallbackImageName))
                item.ThumbnailUrl = $"https://cdn.warframestat.us/img/{item.FallbackImageName}";

            if (d.Comps != null && d.Comps.Count > 0)
            {
                item.Components = d.Comps.Select(c => new ItemCraftComponentBrief
                {
                    Name = c.Name ?? string.Empty,
                    ShortLabel = ResolveShortCompLabel(c.Name),
                    Count = c.Count,
                    IconGlyph = ResolveComponentGlyph(c.Name),
                    ImageUrl = !string.IsNullOrEmpty(c.Icon) ? $"https://browse.wf{c.Icon}" : null
                }).ToList();
            }

            return item;
        }

        private static string ResolveCategoryGlyph(string? cat, string? sub) => cat switch
        {
            "Варфреймы" => "🛡️",
            "Некромехи" => "🤖",
            "Основное" => "🔫",
            "Вторичное" => "🔫",
            "Ближний бой" => "⚔️",
            "Модульное" => "🔩",
            "Арчвинг" => "🚀",
            "Компаньоны" => "🐾",
            "Украшения Убежища" => "🗿",
            "Косметика" => sub switch
            {
                "Эфемера" => "✨",
                "Сандана" => "🧣",
                "Сигна" => "👑",
                "Броня" => "🦺",
                "Шлем" => "⛑️",
                "Анимации" => "💃",
                _ => "💎"
            },
            _ => "📦"
        };

        private static string ResolveShortCompLabel(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (name.Contains("Нейропт", StringComparison.OrdinalIgnoreCase)) return "Нейр";
            if (name.Contains("Каркас", StringComparison.OrdinalIgnoreCase)) return "Карк";
            if (name.Contains("Систем", StringComparison.OrdinalIgnoreCase)) return "Сист";
            if (name.Contains("Орокин", StringComparison.OrdinalIgnoreCase)) return "Орок";
            if (name.Contains("Ствол", StringComparison.OrdinalIgnoreCase)) return "Ствол";
            if (name.Contains("Приёмник", StringComparison.OrdinalIgnoreCase) || name.Contains("Приемник", StringComparison.OrdinalIgnoreCase)) return "Приём";
            if (name.Contains("Приклад", StringComparison.OrdinalIgnoreCase)) return "Прикл";
            if (name.Contains("Рукоят", StringComparison.OrdinalIgnoreCase)) return "Рукоят";
            if (name.Contains("Клинок", StringComparison.OrdinalIgnoreCase)) return "Клин";
            if (name.Contains("Лезв", StringComparison.OrdinalIgnoreCase)) return "Лезв";
            if (name.Contains("Чертёж", StringComparison.OrdinalIgnoreCase) || name.Contains("Чертеж", StringComparison.OrdinalIgnoreCase)) return "Черт";
            if (name.Contains("Форма", StringComparison.OrdinalIgnoreCase)) return "Форма";
            if (name.Contains("Тетива", StringComparison.OrdinalIgnoreCase)) return "Тетив";
            if (name.Contains("Плечи", StringComparison.OrdinalIgnoreCase)) return "Плечи";
            string trimmed = name.Trim();
            return trimmed.Length <= 5 ? trimmed : trimmed[..5];
        }

        private static string ResolveComponentGlyph(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "⚙️";
            if (name.Contains("Нейропт", StringComparison.OrdinalIgnoreCase)) return "🧠";
            if (name.Contains("Каркас", StringComparison.OrdinalIgnoreCase)) return "🦺";
            if (name.Contains("Систем", StringComparison.OrdinalIgnoreCase)) return "⚡";
            if (name.Contains("Ствол", StringComparison.OrdinalIgnoreCase)) return "📏";
            if (name.Contains("Приёмник", StringComparison.OrdinalIgnoreCase) || name.Contains("Приемник", StringComparison.OrdinalIgnoreCase)) return "⚙️";
            if (name.Contains("Приклад", StringComparison.OrdinalIgnoreCase) || name.Contains("Рукоят", StringComparison.OrdinalIgnoreCase)) return "🪵";
            if (name.Contains("Клинок", StringComparison.OrdinalIgnoreCase) || name.Contains("Лезв", StringComparison.OrdinalIgnoreCase)) return "🗡️";
            if (name.Contains("Форма", StringComparison.OrdinalIgnoreCase)) return "🧩";
            if (name.Contains("Кредит", StringComparison.OrdinalIgnoreCase)) return "💰";
            return "🔩";
        }

        private sealed class CatalogDocument
        {
            [JsonPropertyName("version")] public string Version { get; set; } = "2.0";
            [JsonPropertyName("items")] public List<CatalogItemDto> Items { get; set; } = new();
        }

        private sealed class CatalogItemDto
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("nameEn")] public string? NameEn { get; set; }
            [JsonPropertyName("cat")] public string? Cat { get; set; }
            [JsonPropertyName("sub")] public string? Sub { get; set; }
            [JsonPropertyName("xp")] public int Xp { get; set; }
            [JsonPropertyName("mr")] public int Mr { get; set; }
            [JsonPropertyName("vaulted")] public bool Vaulted { get; set; }
            [JsonPropertyName("prime")] public bool Prime { get; set; }
            [JsonPropertyName("founder")] public bool Founder { get; set; }
            [JsonPropertyName("subsume")] public bool Subsume { get; set; }
            [JsonPropertyName("incarnon")] public bool Incarnon { get; set; }
            [JsonPropertyName("craft")] public bool Craft { get; set; }
            [JsonPropertyName("masterable")] public bool Masterable { get; set; } = true;
            [JsonPropertyName("srcKind")] public string? SrcKind { get; set; }
            [JsonPropertyName("src")] public string? Src { get; set; }
            [JsonPropertyName("cost")] public string? Cost { get; set; }
            [JsonPropertyName("icon")] public string? Icon { get; set; }
            [JsonPropertyName("hash")] public string? Hash { get; set; }
            [JsonPropertyName("wsImg")] public string? WsImg { get; set; }
            [JsonPropertyName("comps")] public List<CatalogCompDto>? Comps { get; set; }
            [JsonPropertyName("usedFor")] public string? UsedFor { get; set; }
            [JsonPropertyName("tag")] public string? Tag { get; set; }
        }

        private sealed class CatalogCompDto
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("count")] public int Count { get; set; } = 1;
            [JsonPropertyName("icon")] public string? Icon { get; set; }
        }
    }
}
