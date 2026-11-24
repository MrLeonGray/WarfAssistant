using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Stage 6 service for Warframe.Market v1/v2 APIs, DE official weekly Riven pricing,
    /// authentic Riven stat range calculations, roll grading, and Riven->Builder integration.
    /// Respects WFM API rate limits (3 req/s throttle) with offline-first compressed seed.
    /// </summary>
    public sealed class MarketRivenService
    {
        private static MarketRivenService? _instance;
        public static MarketRivenService Instance => _instance ??= new MarketRivenService();

        private sealed class SeedDoc
        {
            [JsonPropertyName("items")] public List<WfmItemSummary>? Items { get; set; }
            [JsonPropertyName("rivenWeapons")] public List<RivenWeaponOption>? RivenWeapons { get; set; }
            [JsonPropertyName("rivenAttrs")] public List<RivenAttrOption>? RivenAttrs { get; set; }
            [JsonPropertyName("weekly")] public List<WeeklyRivenPriceStats>? Weekly { get; set; }
        }

        private readonly HttpClient _http;
        private readonly SemaphoreSlim _rateGate = new(1, 1);
        private DateTime _lastApiCallUtc = DateTime.MinValue;
        private bool _loaded;

        public List<WfmItemSummary> MarketItems { get; } = new();
        public List<RivenWeaponOption> RivenWeapons { get; } = new();
        public List<RivenAttrOption> RivenAttrs { get; } = new();
        public List<WeeklyRivenPriceStats> WeeklyStats { get; } = new();

        private readonly Dictionary<string, WfmItemSummary> _itemsBySlug = new(StringComparer.OrdinalIgnoreCase);

        private MarketRivenService()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WarfAssistant/2.0");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Language", "ru");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Platform", "pc");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Crossplay", "true");

            LoadSeed();
        }

        private void LoadSeed()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("market_riven_seed.json.gz", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return;

                using Stream? raw = asm.GetManifestResourceStream(resName);
                if (raw == null) return;
                using var gz = new GZipStream(raw, CompressionMode.Decompress);
                var doc = JsonSerializer.Deserialize<SeedDoc>(gz);
                if (doc == null) return;

                if (doc.Items != null)
                {
                    MarketItems.AddRange(doc.Items.OrderBy(i => i.NameRu));
                    foreach (var it in MarketItems)
                    {
                        if (!string.IsNullOrEmpty(it.Slug)) _itemsBySlug[it.Slug] = it;
                    }
                }
                if (doc.RivenWeapons != null)
                {
                    var platforms = BuilderDatabaseService.Instance.Platforms;
                    foreach (var w in doc.RivenWeapons)
                    {
                        if (_itemsBySlug.TryGetValue(w.Slug, out var mItem) && !string.IsNullOrEmpty(mItem.IconUrl))
                        {
                            w.IconUrl = mItem.IconUrl;
                        }
                        else
                        {
                            var wfmFallback = MarketItems.FirstOrDefault(m =>
                                !string.IsNullOrEmpty(m.IconUrl) &&
                                (m.NameEn.Equals(w.NameEn, StringComparison.OrdinalIgnoreCase) ||
                                 m.Slug.StartsWith(w.Slug + "_", StringComparison.OrdinalIgnoreCase)));
                            if (wfmFallback != null)
                            {
                                w.IconUrl = wfmFallback.IconUrl;
                            }
                            else
                            {
                                var plat = platforms.FirstOrDefault(p =>
                                    !string.IsNullOrEmpty(p.IconPath) &&
                                    (p.Name.Contains(w.NameEn, StringComparison.OrdinalIgnoreCase) ||
                                     p.Name.Contains(w.NameRu, StringComparison.OrdinalIgnoreCase)));
                                if (plat != null)
                                    w.IconUrl = ImageCacheService.NormalizeImageUrl(plat.IconPath);
                            }
                        }
                    }
                    RivenWeapons.AddRange(doc.RivenWeapons.OrderBy(w => w.NameRu));
                }
                if (doc.RivenAttrs != null)
                {
                    foreach (var a in doc.RivenAttrs)
                        a.EffectRu = TranslateRivenAttrRu(a.Slug, a.EffectRu);
                    RivenAttrs.AddRange(doc.RivenAttrs.OrderBy(a => a.EffectRu));
                }
                if (doc.Weekly != null) WeeklyStats.AddRange(doc.Weekly);
            }
            catch { }
        }

        private static string TranslateRivenAttrRu(string slug, string fallback)
        {
            if (RivenAttrRuNames.TryGetValue(slug, out var ru)) return ru;
            return !string.IsNullOrWhiteSpace(fallback) && fallback != slug ? fallback : slug;
        }

        private static readonly Dictionary<string, string> RivenAttrRuNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ammo_maximum"] = "Максимум патронов",
            ["ammo_efficiency"] = "Экономия боеприпасов",
            ["base_damage_/_melee_damage"] = "Базовый урон",
            ["blast"] = "Урон Взрывом",
            ["channeling_damage"] = "Начальное комбо / Тяж. атака",
            ["channeling_efficiency"] = "Эффективность тяжёлой атаки",
            ["chance_to_gain_combo_count"] = "Шанс доп. счётчика комбо",
            ["chance_to_gain_extra_combo_count"] = "Шанс доп. счётчика комбо",
            ["cold_damage"] = "Урон Холодом",
            ["combo_duration"] = "Длительность комбо",
            ["corrosive"] = "Урон Коррозией",
            ["critical_chance"] = "Шанс крит. удара",
            ["critical_chance_on_slide_attack"] = "Шанс крита в подкате",
            ["critical_damage"] = "Критич. урон",
            ["damage_to_orokin"] = "Урон по Орокин",
            ["damage_to_scaldra"] = "Урон по Скальдре",
            ["damage_to_techrot"] = "Урон по Техроту",
            ["damage_vs_corpus"] = "Урон по Корпусу",
            ["damage_vs_grineer"] = "Урон по Гринир",
            ["damage_vs_infested"] = "Урон по Заражённым",
            ["electric_damage"] = "Урон Электричеством",
            ["finisher_damage"] = "Урон добивания",
            ["fire_rate_/_attack_speed"] = "Скорострельность / Скорость атаки",
            ["gas"] = "Урон Газом",
            ["heat_damage"] = "Урон Огнём",
            ["heavy_attack_efficiency"] = "Эффективность тяжёлой атаки",
            ["heavy_attack_wind_up_speed"] = "Скорость подготовки тяж. атаки",
            ["impact_damage"] = "Ударный урон",
            ["initial_combo"] = "Начальное комбо",
            ["magazine_capacity"] = "Ёмкость магазина",
            ["magazine_reloaded_s_when_holstered"] = "Перезарядка в кобуре (%/с)",
            ["magnetic"] = "Урон Магнитом",
            ["melee_damage_on_heavy_attack"] = "Урон тяжёлой атакой",
            ["multishot"] = "Мультивыстрел",
            ["parry_angle"] = "Угол парирования",
            ["projectile_speed"] = "Скорость полёта снаряда",
            ["punch_through"] = "Пронзание навылет",
            ["puncture_damage"] = "Пронзающий урон",
            ["radiation"] = "Урон Радиацией",
            ["range"] = "Радиус атаки (ближний бой)",
            ["recoil"] = "Отдача оружия",
            ["reload_speed"] = "Скорость перезарядки",
            ["slam_attack_damage"] = "Урон ударом по земле",
            ["slash_damage"] = "Режущий урон",
            ["status_chance"] = "Шанс статуса",
            ["status_damage"] = "Урон статусных эффектов",
            ["status_duration"] = "Длительность статуса",
            ["toxin_damage"] = "Урон Токсином",
            ["viral"] = "Урон Вирусом",
            ["weak_point_critical_chance"] = "Шанс крита по уязв. местам",
            ["weak_point_damage"] = "Урон по уязвимым местам",
            ["weapon_recoil"] = "Отдача оружия",
            ["zoom"] = "Приближение (Зум)"
        };

        private async Task<string?> ThrottledGetAsync(string url)
        {
            await _rateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                double msSince = (DateTime.UtcNow - _lastApiCallUtc).TotalMilliseconds;
                if (msSince < 360)
                    await Task.Delay((int)(360 - msSince)).ConfigureAwait(false);
                _lastApiCallUtc = DateTime.UtcNow;

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                _rateGate.Release();
            }
        }

        public async Task<MarketItemAnalysis?> FetchLiveMarketItemAsync(WfmItemSummary item)
        {
            if (item == null || string.IsNullOrEmpty(item.Slug)) return null;

            string? ordJson = await ThrottledGetAsync($"https://api.warframe.market/v2/orders/item/{item.Slug}").ConfigureAwait(false);
            if (string.IsNullOrEmpty(ordJson)) return null;

            var analysis = new MarketItemAnalysis { Item = item };
            try
            {
                using var doc = JsonDocument.Parse(ordJson);
                if (doc.RootElement.TryGetProperty("data", out var orders) &&
                    orders.ValueKind == JsonValueKind.Array)
                {
                    var sellsIngame = new List<WfmOrderEntry>();
                    var buysIngame = new List<WfmOrderEntry>();
                    var sellsOnline = new List<WfmOrderEntry>();
                    var buysOnline = new List<WfmOrderEntry>();

                    foreach (var o in orders.EnumerateArray())
                    {
                        if (!o.TryGetProperty("user", out var u)) continue;
                        string status = u.TryGetProperty("status", out var st) ? (st.GetString() ?? "offline") : "offline";
                        if (status != "ingame" && status != "online") continue;

                        string type = o.TryGetProperty("type", out var ot) ? (ot.GetString() ?? "sell")
                            : (o.TryGetProperty("order_type", out var ot1) ? (ot1.GetString() ?? "sell") : "sell");
                        int plat = o.TryGetProperty("platinum", out var pl) && pl.ValueKind == JsonValueKind.Number ? (int)Math.Round(pl.GetDouble()) : 0;
                        int qty = o.TryGetProperty("quantity", out var qt) && qt.ValueKind == JsonValueKind.Number ? qt.GetInt32() : 1;
                        bool hasRank = (o.TryGetProperty("rank", out var mr) && mr.ValueKind == JsonValueKind.Number)
                                    || (o.TryGetProperty("mod_rank", out mr) && mr.ValueKind == JsonValueKind.Number);
                        int rank = hasRank ? mr.GetInt32() : 0;
                        string uname = u.TryGetProperty("ingameName", out var un) ? (un.GetString() ?? "")
                            : (u.TryGetProperty("ingame_name", out var un1) ? (un1.GetString() ?? "") : "");
                        int rep = u.TryGetProperty("reputation", out var rp) && rp.ValueKind == JsonValueKind.Number ? rp.GetInt32() : 0;

                        var entry = new WfmOrderEntry
                        {
                            OrderType = type,
                            Platinum = plat,
                            Quantity = qty,
                            HasRank = hasRank,
                            ModRank = rank,
                            UserIngameName = uname,
                            UserStatus = status,
                            Reputation = rep,
                            ItemNameForWhisper = item.NameEn
                        };

                        if (type == "sell")
                        {
                            if (status == "ingame") sellsIngame.Add(entry);
                            else sellsOnline.Add(entry);
                        }
                        else
                        {
                            if (status == "ingame") buysIngame.Add(entry);
                            else buysOnline.Add(entry);
                        }
                    }

                    var sellsPool = sellsIngame.Count >= 3 ? sellsIngame : sellsIngame.Concat(sellsOnline).ToList();
                    var buysPool = buysIngame.Count >= 3 ? buysIngame : buysIngame.Concat(buysOnline).ToList();

                    analysis.SellOrders = sellsPool.OrderBy(s => s.Platinum).Take(12).ToList();
                    analysis.BuyOrders = buysPool.OrderByDescending(b => b.Platinum).Take(8).ToList();

                    if (analysis.SellOrders.Count > 0)
                    {
                        analysis.MinSellPrice = analysis.SellOrders[0].Platinum;
                        var topPrices = analysis.SellOrders.Select(s => s.Platinum).OrderBy(p => p).ToList();
                        analysis.MedianSellPrice = topPrices[topPrices.Count / 2];
                        if (item.Ducats > 0 && analysis.MinSellPrice > 0)
                            analysis.PlatPerDucat = Math.Round((double)item.Ducats / analysis.MinSellPrice, 1);
                    }
                    if (analysis.BuyOrders.Count > 0)
                        analysis.MaxBuyPrice = analysis.BuyOrders[0].Platinum;
                }

                // Fetch 48h + 90d statistics summary
                string? statsJson = await ThrottledGetAsync($"https://api.warframe.market/v1/items/{item.Slug}/statistics").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(statsJson))
                {
                    using var sdoc = JsonDocument.Parse(statsJson);
                    if (sdoc.RootElement.TryGetProperty("payload", out var sp) &&
                        sp.TryGetProperty("statistics_closed", out var sc))
                    {
                        if (sc.TryGetProperty("48hours", out var h48) && h48.ValueKind == JsonValueKind.Array && h48.GetArrayLength() > 0)
                        {
                            int totalVol = 0;
                            double weightedSum = 0;
                            double fallbackLastAvg = 0;
                            foreach (var b in h48.EnumerateArray())
                            {
                                int v = b.TryGetProperty("volume", out var volEl) && volEl.ValueKind == JsonValueKind.Number ? volEl.GetInt32() : 0;
                                double ap = b.TryGetProperty("moving_avg", out var ma) && ma.ValueKind == JsonValueKind.Number
                                    ? ma.GetDouble()
                                    : (b.TryGetProperty("avg_price", out var apEl) && apEl.ValueKind == JsonValueKind.Number ? apEl.GetDouble() : 0);
                                totalVol += v;
                                if (ap > 0 && v > 0) weightedSum += ap * v;
                                if (ap > 0) fallbackLastAvg = ap;
                            }
                            analysis.Volume48h = totalVol;
                            analysis.Avg48hPrice = Math.Round(totalVol > 0 && weightedSum > 0 ? weightedSum / totalVol : fallbackLastAvg, 1);
                        }

                        if (sc.TryGetProperty("90days", out var d90) && d90.ValueKind == JsonValueKind.Array && d90.GetArrayLength() > 0)
                        {
                            int totalVol90 = 0;
                            double weightedSum90 = 0;
                            double fallbackAvg90 = 0;
                            foreach (var b in d90.EnumerateArray())
                            {
                                int v = b.TryGetProperty("volume", out var volEl) && volEl.ValueKind == JsonValueKind.Number ? volEl.GetInt32() : 0;
                                double ap = b.TryGetProperty("moving_avg", out var ma) && ma.ValueKind == JsonValueKind.Number
                                    ? ma.GetDouble()
                                    : (b.TryGetProperty("avg_price", out var apEl) && apEl.ValueKind == JsonValueKind.Number ? apEl.GetDouble() : 0);
                                totalVol90 += v;
                                if (ap > 0 && v > 0) weightedSum90 += ap * v;
                                if (ap > 0) fallbackAvg90 = ap;
                            }
                            analysis.Volume90d = totalVol90;
                            analysis.Avg90dPrice = Math.Round(totalVol90 > 0 && weightedSum90 > 0 ? weightedSum90 / totalVol90 : fallbackAvg90, 1);
                        }
                    }
                }
            }
            catch { }

            return analysis;
        }

        public async Task<List<RivenAuctionListing>> SearchLiveRivenAuctionsAsync(RivenWeaponOption weapon, string? statFilterSlug = null)
        {
            var results = new List<RivenAuctionListing>();
            if (weapon == null || string.IsNullOrEmpty(weapon.Slug)) return results;

            string url = $"https://api.warframe.market/v1/auctions/search?type=riven&weapon_url_name={Uri.EscapeDataString(weapon.Slug)}&sort_by=price_asc";
            if (!string.IsNullOrEmpty(statFilterSlug))
                url += $"&positive_stats={Uri.EscapeDataString(statFilterSlug)}";

            string? json = await ThrottledGetAsync(url).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return results;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("payload", out var pl) &&
                    pl.TryGetProperty("auctions", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        bool closed = a.TryGetProperty("closed", out var cl) && cl.ValueKind == JsonValueKind.True;
                        if (closed) continue;

                        int buyout = a.TryGetProperty("buyout_price", out var bp) && bp.ValueKind == JsonValueKind.Number ? bp.GetInt32() : 0;
                        int start = a.TryGetProperty("starting_price", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0;
                        int price = buyout > 0 ? buyout : start;
                        if (price <= 0) continue;

                        string ownerName = "";
                        string ownerStatus = "offline";
                        if (a.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object)
                        {
                            ownerName = ow.TryGetProperty("ingame_name", out var iname) ? (iname.GetString() ?? "") : "";
                            ownerStatus = ow.TryGetProperty("status", out var ost) ? (ost.GetString() ?? "offline") : "offline";
                        }

                        int rerolls = 0, modRank = 8, mr = weapon.ReqMr;
                        string pol = "madurai";
                        var attrs = new List<string>();
                        if (a.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object)
                        {
                            rerolls = it.TryGetProperty("re_rolls", out var rr) && rr.ValueKind == JsonValueKind.Number ? rr.GetInt32() : 0;
                            modRank = it.TryGetProperty("mod_rank", out var mrk) && mrk.ValueKind == JsonValueKind.Number ? mrk.GetInt32() : 8;
                            mr = it.TryGetProperty("mastery_level", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : weapon.ReqMr;
                            pol = it.TryGetProperty("polarity", out var plEl) ? (plEl.GetString() ?? "madurai") : "madurai";
                            if (it.TryGetProperty("attributes", out var atArr) && atArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var at in atArr.EnumerateArray())
                                {
                                    string nm = at.TryGetProperty("url_name", out var un) ? (un.GetString() ?? "") : "";
                                    double val = at.TryGetProperty("value", out var vl) && vl.ValueKind == JsonValueKind.Number ? vl.GetDouble() : 0;
                                    string ruLabel = RivenAttrs.FirstOrDefault(x => x.Slug == nm)?.EffectRu ?? nm;
                                    attrs.Add($"{(val > 0 ? "+" : "")}{val:F1}% {ruLabel}");
                                }
                            }
                        }

                        results.Add(new RivenAuctionListing
                        {
                            Price = price,
                            IsDirectBuy = buyout > 0,
                            Rerolls = rerolls,
                            ModRank = modRank,
                            MasteryLevel = mr,
                            OwnerName = ownerName,
                            OwnerStatus = ownerStatus,
                            Polarity = pol,
                            AttributesText = string.Join(" | ", attrs),
                            WhisperText = $"/w {ownerName} Hi! I want to buy your {weapon.NameEn} Riven ({string.Join(", ", attrs)}) for {price}p"
                        });

                        if (results.Count >= 18) break;
                    }
                }
            }
            catch { }

            return results.OrderByDescending(r => r.OwnerStatus == "ingame").ThenBy(r => r.Price).ToList();
        }

        public (WeeklyRivenPriceStats? Unrolled, WeeklyRivenPriceStats? Rolled) GetWeeklyStatsForWeapon(RivenWeaponOption? weapon)
        {
            if (weapon == null) return (null, null);
            var matches = WeeklyStats.Where(w =>
                w.Compatibility.Equals(weapon.NameEn, StringComparison.OrdinalIgnoreCase) ||
                weapon.NameEn.StartsWith(w.Compatibility + " ", StringComparison.OrdinalIgnoreCase)).ToList();

            return (matches.FirstOrDefault(m => !m.Rerolled), matches.FirstOrDefault(m => m.Rerolled));
        }

        // Base Riven Attribute Values at 1.0 Disposition (Rifle baseline, scaled per RivenType)
        private static readonly Dictionary<string, (double BaseVal, string BuilderKey, int Weight)> RivenBaseStats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base_damage_/_melee_damage"] = (165.0, "dmg", 8),
            ["multishot"] = (90.0, "ms", 10),
            ["critical_chance"] = (150.0, "cc", 10),
            ["critical_damage"] = (120.0, "cd", 10),
            ["status_chance"] = (90.0, "sc", 7),
            ["status_duration"] = (99.9, "", 4),
            ["status_damage"] = (90.0, "", 6),
            ["fire_rate_/_attack_speed"] = (60.0, "fr", 8),
            ["toxin_damage"] = (90.0, "tox", 9),
            ["cold_damage"] = (90.0, "cold", 8),
            ["heat_damage"] = (90.0, "heat", 7),
            ["electric_damage"] = (90.0, "elec", 7),
            ["blast"] = (90.0, "", 5),
            ["corrosive"] = (90.0, "", 7),
            ["gas"] = (90.0, "", 5),
            ["magnetic"] = (90.0, "", 6),
            ["radiation"] = (90.0, "", 6),
            ["viral"] = (90.0, "", 8),
            ["damage_vs_grineer"] = (45.0, "fac", 6),
            ["damage_vs_corpus"] = (45.0, "fac", 4),
            ["damage_vs_infested"] = (45.0, "fac", 2),
            ["damage_to_orokin"] = (45.0, "fac", 4),
            ["damage_to_scaldra"] = (45.0, "fac", 5),
            ["damage_to_techrot"] = (45.0, "fac", 5),
            ["magazine_capacity"] = (50.0, "mag", 4),
            ["magazine_reloaded_s_when_holstered"] = (30.0, "", 2),
            ["reload_speed"] = (50.0, "rel", 4),
            ["punch_through"] = (2.7, "", 5),
            ["combo_duration"] = (8.1, "", 5),
            ["initial_combo"] = (24.5, "", 6),
            ["channeling_damage"] = (30.0, "", 6),
            ["channeling_efficiency"] = (80.0, "", 6),
            ["heavy_attack_efficiency"] = (80.0, "", 6),
            ["heavy_attack_wind_up_speed"] = (60.0, "", 6),
            ["melee_damage_on_heavy_attack"] = (165.0, "", 7),
            ["slam_attack_damage"] = (120.0, "", 3),
            ["parry_angle"] = (45.0, "", 1),
            ["chance_to_gain_combo_count"] = (120.0, "", 4),
            ["chance_to_gain_extra_combo_count"] = (120.0, "", 4),
            ["range"] = (1.9, "", 8),
            ["zoom"] = (60.0, "", -8), // harmless curse!
            ["recoil"] = (-90.0, "", -6),
            ["weapon_recoil"] = (-90.0, "", -6),
            ["impact_damage"] = (120.0, "imp", 1),
            ["puncture_damage"] = (120.0, "pun", 1),
            ["slash_damage"] = (120.0, "sla", 6),
            ["finisher_damage"] = (120.0, "", 3),
            ["ammo_maximum"] = (49.9, "", 1),
            ["ammo_efficiency"] = (45.0, "", 3),
            ["projectile_speed"] = (90.0, "", 3),
            ["critical_chance_on_slide_attack"] = (90.0, "", 1),
            ["weak_point_damage"] = (90.0, "", 7),
            ["weak_point_critical_chance"] = (120.0, "", 7)
        };

        private static double GetRivenTypeFactor(string rivenType, string attrSlug)
        {
            if (rivenType is "pistol" or "kitgun")
            {
                return attrSlug switch
                {
                    "base_damage_/_melee_damage" => 219.6 / 165.0,
                    "multishot" => 119.7 / 90.0,
                    "fire_rate_/_attack_speed" => 74.7 / 60.0,
                    "punch_through" => 2.7 / 2.7,
                    _ => 1.0
                };
            }
            if (rivenType == "shotgun")
            {
                return attrSlug switch
                {
                    "multishot" => 119.7 / 90.0,
                    "critical_chance" => 90.0 / 150.0,
                    "critical_damage" => 90.0 / 120.0,
                    "fire_rate_/_attack_speed" => 90.0 / 60.0,
                    _ => 1.0
                };
            }
            if (rivenType is "melee" or "zaw")
            {
                return attrSlug switch
                {
                    "critical_chance" => 180.0 / 150.0,
                    "critical_damage" => 90.0 / 120.0,
                    "fire_rate_/_attack_speed" => 54.9 / 60.0,
                    _ => 1.0
                };
            }
            if (rivenType == "archgun")
            {
                return attrSlug switch
                {
                    "base_damage_/_melee_damage" => 99.9 / 165.0,
                    "multishot" => 60.0 / 90.0,
                    "critical_chance" => 99.9 / 150.0,
                    "critical_damage" => 90.0 / 120.0,
                    "status_chance" => 90.0 / 90.0,
                    "heat_damage" or "cold_damage" or "electric_damage" or "toxin_damage" => 120.0 / 90.0,
                    _ => 1.0
                };
            }
            return 1.0;
        }

        public static (double MinVal, double MaxVal) CalculateRivenBounds(
            RivenWeaponOption? weapon,
            RivenAttrOption? attr,
            string configMode,
            bool isNegative,
            double? dispositionOverride = null)
        {
            if (weapon == null || attr == null) return (0, 0);

            double disp = dispositionOverride ?? weapon.Disposition;
            double baseVal = RivenBaseStats.TryGetValue(attr.Slug, out var meta) ? meta.BaseVal : 75.0;
            double typeFactor = GetRivenTypeFactor(weapon.RivenType, attr.Slug);

            double configMult = (configMode, isNegative) switch
            {
                ("2+ / 0-", false) => 0.99,
                ("2+ / 1-", false) => 1.2375,
                ("2+ / 1-", true) => -0.495,
                ("3+ / 0-", false) => 0.75,
                ("3+ / 1-", false) => 0.9375,
                ("3+ / 1-", true) => -0.75,
                _ => 0.75
            };

            double mid = Math.Abs(baseVal) * typeFactor * disp * configMult;
            double lo = mid * 0.90;
            double hi = mid * 1.10;
            if (mid < 0) (lo, hi) = (hi, lo);
            return (Math.Round(lo, 1), Math.Round(hi, 1));
        }

        public async Task<bool> RefreshWeeklyRivensAndMarketAsync(CancellationToken ct = default)
        {
            bool updatedAny = false;
            try
            {
                string? weeklyJson = await ThrottledGetAsync("https://www-static.warframe.com/repos/weeklyRivensPC.json").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(weeklyJson) && !ct.IsCancellationRequested)
                {
                    using var doc = JsonDocument.Parse(weeklyJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var fresh = new List<WeeklyRivenPriceStats>();
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string comp = el.TryGetProperty("compatibility", out var cm) ? (cm.GetString() ?? "") : "";
                            if (string.IsNullOrEmpty(comp)) continue;
                            fresh.Add(new WeeklyRivenPriceStats
                            {
                                Compatibility = comp,
                                ItemType = el.TryGetProperty("itemType", out var it) ? (it.GetString() ?? "") : "",
                                Rerolled = el.TryGetProperty("rerolled", out var rr) && rr.ValueKind == JsonValueKind.True,
                                Avg = el.TryGetProperty("avg", out var av) && av.ValueKind == JsonValueKind.Number ? av.GetDouble() : 0,
                                Median = el.TryGetProperty("median", out var md) && md.ValueKind == JsonValueKind.Number ? md.GetDouble() : 0,
                                Min = el.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number ? (int)Math.Round(mn.GetDouble()) : 0,
                                Max = el.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? (int)Math.Round(mx.GetDouble()) : 0,
                                Popularity = el.TryGetProperty("pop", out var pp) && pp.ValueKind == JsonValueKind.Number ? (int)Math.Round(pp.GetDouble()) : 0
                            });
                        }
                        if (fresh.Count > 20)
                        {
                            WeeklyStats.Clear();
                            WeeklyStats.AddRange(fresh);
                            updatedAny = true;
                        }
                    }
                }

                string? itemsJson = await ThrottledGetAsync("https://api.warframe.market/v2/items").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(itemsJson) && !ct.IsCancellationRequested)
                {
                    using var doc = JsonDocument.Parse(itemsJson);
                    if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        int added = 0;
                        foreach (var el in arr.EnumerateArray())
                        {
                            string slug = el.TryGetProperty("slug", out var sEl) ? (sEl.GetString() ?? "") : "";
                            if (string.IsNullOrEmpty(slug) || _itemsBySlug.ContainsKey(slug)) continue;
                            string gameRef = el.TryGetProperty("gameRef", out var gr) ? (gr.GetString() ?? "") : "";
                            int ducats = el.TryGetProperty("ducats", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : 0;
                            string ru = "", en = "", icon = "";
                            if (el.TryGetProperty("i18n", out var i18n) && i18n.ValueKind == JsonValueKind.Object)
                            {
                                if (i18n.TryGetProperty("ru", out var ruEl))
                                {
                                    ru = ruEl.TryGetProperty("name", out var n1) ? (n1.GetString() ?? "") : "";
                                    icon = ruEl.TryGetProperty("icon", out var ic1) ? (ic1.GetString() ?? "") : "";
                                }
                                if (i18n.TryGetProperty("en", out var enEl))
                                {
                                    en = enEl.TryGetProperty("name", out var n2) ? (n2.GetString() ?? "") : "";
                                    if (string.IsNullOrEmpty(icon)) icon = enEl.TryGetProperty("icon", out var ic2) ? (ic2.GetString() ?? "") : "";
                                }
                            }
                            if (string.IsNullOrEmpty(en)) en = slug.Replace('_', ' ');
                            if (string.IsNullOrEmpty(ru)) ru = en;
                            var tags = new List<string>();
                            if (el.TryGetProperty("tags", out var tgArr) && tgArr.ValueKind == JsonValueKind.Array)
                                foreach (var t in tgArr.EnumerateArray()) if (t.GetString() is string ts) tags.Add(ts);

                            var item = new WfmItemSummary { Slug = slug, GameRef = gameRef, NameRu = ru, NameEn = en, IconPath = icon, Tags = tags, Ducats = ducats };
                            MarketItems.Add(item);
                            _itemsBySlug[slug] = item;
                            added++;
                        }
                        if (added > 0) updatedAny = true;
                    }
                }
            }
            catch { }
            return updatedAny;
        }

        public async Task<List<DucatArbitrageEntry>> ScanDucatArbitrageAsync(CancellationToken ct = default)
        {
            var results = new List<DucatArbitrageEntry>();
            var candidates = MarketItems
                .Where(m => m.Ducats >= 45 && !m.IsPrimeSet && !string.IsNullOrEmpty(m.Slug))
                .OrderByDescending(m => m.Ducats)
                .Take(12)
                .ToList();

            foreach (var item in candidates)
            {
                if (ct.IsCancellationRequested) break;
                var an = await FetchLiveMarketItemAsync(item).ConfigureAwait(false);
                if (an != null && an.MinSellPrice > 0)
                {
                    results.Add(new DucatArbitrageEntry
                    {
                        Name = item.NameRu,
                        Slug = item.Slug,
                        Ducats = item.Ducats,
                        MinSellPrice = an.MinSellPrice
                    });
                }
            }
            return results.OrderByDescending(r => r.DucatsPerPlat).Take(8).ToList();
        }

        public static RivenEvaluationResult EvaluateRoll(
            RivenWeaponOption? weapon,
            IEnumerable<RivenStatSlot> positiveSlots,
            RivenStatSlot? negativeSlot,
            string configMode,
            int rerolls)
        {
            int score = 0;
            var statMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in positiveSlots)
            {
                if (s.SelectedAttr == null) continue;
                int w = RivenBaseStats.TryGetValue(s.SelectedAttr.Slug, out var meta) ? meta.Weight : 4;
                score += Math.Max(1, w) * 3;
                if (s.QualityPercent >= 70) score += 4;
                if (!string.IsNullOrEmpty(meta.BuilderKey))
                    statMap[meta.BuilderKey] = statMap.GetValueOrDefault(meta.BuilderKey) + s.Value;
            }

            string curseVerdict = "Нет минуса — числа статов базовые";
            string curseColor = "#94A3B8";

            if (configMode.Contains("1-"))
            {
                if (negativeSlot?.SelectedAttr != null)
                {
                    string slug = negativeSlot.SelectedAttr.Slug;
                    if (slug is "zoom" or "impact_damage" or "puncture_damage" or "damage_vs_infested" or "critical_chance_on_slide_attack")
                    {
                        score += 18;
                        curseVerdict = $"✓ Безопасный минус ({negativeSlot.SelectedAttr.EffectRu}): усиливает статы на +25% без потери урона!";
                        curseColor = "#10B981";
                    }
                    else if (slug is "multishot" or "base_damage_/_melee_damage" or "critical_chance" or "critical_damage" or "fire_rate_/_attack_speed")
                    {
                        score -= 30;
                        curseVerdict = $"⚠ ОПАСНЫЙ МИНУС ({negativeSlot.SelectedAttr.EffectRu}): ломает урон — нужен переролл!";
                        curseColor = "#EF4444";
                    }
                    else
                    {
                        score += 4;
                        curseVerdict = $"Умеренный минус ({negativeSlot.SelectedAttr.EffectRu}): приемлем для большинства билдов.";
                        curseColor = "#FBBF24";
                    }

                    if (RivenBaseStats.TryGetValue(slug, out var nmeta) && !string.IsNullOrEmpty(nmeta.BuilderKey))
                        statMap[nmeta.BuilderKey] = statMap.GetValueOrDefault(nmeta.BuilderKey) - Math.Abs(negativeSlot.Value);
                }
                else
                {
                    curseVerdict = "Выберите отрицательный стат для усиления остальных бафов на +25%";
                }
            }

            var (grade, gColor, verdict) = score switch
            {
                >= 85 => ("S", "#FACC15", "GODROLL — топовая комбинация! Оставлять для эндгейма или дорогой продажи."),
                >= 65 => ("A", "#10B981", "Отличный ролл (A-тир) — уверенно заменяет слот мода в любом билде."),
                >= 45 => ("B", "#38BDF8", "Рабочий ролл — можно использовать сейчас, но есть потенциал улучшить."),
                _ => ("C", "#F87171", "Слабая синергия или вредный минус — рекомендуется перероллить Кувой.")
            };

            long totalKuva = 0;
            int[] costs = { 900, 1000, 1200, 1400, 1700, 2000, 2350, 2750, 3150 };
            for (int r = 0; r < Math.Max(0, rerolls); r++)
                totalKuva += r < costs.Length ? costs[r] : 3500;

            return new RivenEvaluationResult
            {
                Grade = grade,
                GradeColor = gColor,
                Score = Math.Clamp(score, 0, 100),
                Verdict = verdict,
                KuvaSummary = rerolls > 0
                    ? $"Потрачено ~{totalKuva:N0} Кувы ({rerolls} рол.) • След. ролл: {(rerolls < costs.Length ? costs[rerolls] : 3500):N0} Кувы"
                    : "Чистый мод (0 перероллов • Первый ролл: 900 Кувы)",
                CurseVerdict = curseVerdict,
                CurseColor = curseColor,
                BuilderStatMap = statMap
            };
        }

        public static readonly List<VeiledChallengeEntry> VeiledChallenges = new()
        {
            new VeiledChallengeEntry { Title = "Хедшоты в управляемом полёте (3-5 за один полёт)", OptimalFrame = "Зефир (Парение) + Китган / Арка Плазмор", OptimalNode = "Юпитер / Нептун (Газовые потоки)", Tip = "Зефир 1-й способностью зависает в воздухе, а Арка Плазмор / Катчмун легко засчитывает мульти-хедшоты." },
            new VeiledChallengeEntry { Title = "Одиночная Зачистка 30+ не будучи обнаруженным", OptimalFrame = "Ивара (Невидимость) + Бесшумное оружие", OptimalNode = "Седна — Adaro", Tip = "Выстрелы бесшумным оружием из невидимости не сбивают условие; при появлении тревоги лучше перезапустить." },
            new VeiledChallengeEntry { Title = "Оборона 30+ волн без урона объекту", OptimalFrame = "Гара / Лимбо / Кора / Вольт", OptimalNode = "Эрида — Akkad (Заражённые)", Tip = "Заражённые не стреляют с дистанции; достаточно поставить Лимбо Катаклизм + Стазис или стеклянную стену." },
            new VeiledChallengeEntry { Title = "Убийства на Дарджине в воздухе (Полёте)", OptimalFrame = "Титания / Нова / Эш", OptimalNode = "Равнины Эйдолона (Около лагерей)", Tip = "Даже если подбросить врага 4-й ультой Титании/Висп или взорвать пустой Дарджин с мобами рядом." },
            new VeiledChallengeEntry { Title = "Поймать рыбу, добыть руду и убить врага за 30с", OptimalFrame = "Вольт / Ивара (Усыпить)", OptimalNode = "Равнины Эйдолона (Озеро у входа)", Tip = "Подойдите к озеру справа от ворот Цетуса: там рядом и жила руды, и рыба, и патруль Гринир." },
            new VeiledChallengeEntry { Title = "Найти тайники (Ящики) без тревог / с ключом Дракона", OptimalFrame = "Ксаку (4-й взрыв ящиков) / Лимбо", OptimalNode = "Земля — Cervantes / Луа — Plato", Tip = "Высокий радиус Ксаку разбивает все ресурсы и подсвечивает только редкие тайники/скульптуры на радаре." }
        };
    }
}
