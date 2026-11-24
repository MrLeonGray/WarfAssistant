using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    /// <summary>
    /// Stage 6 ViewModel: Warframe.Market live orderbook + Prime Set Arbitrage + Ducats,
    /// Riven Mod Helper (bounds calculator, S/A/B/C grader, DE weekly trade stats, live auctions, Riven->Builder bridge).
    /// </summary>
    public sealed class MarketRivenViewModel : ViewModelBase
    {
        private readonly MarketRivenService _svc = MarketRivenService.Instance;
        private readonly Action<ActiveBuilderRiven> _injectRivenToBuilder;
        private readonly Action<string> _statusCallback;
        private System.Threading.CancellationTokenSource? _marketCts;
        private System.Threading.CancellationTokenSource? _auctionsCts;
        private System.Threading.CancellationTokenSource? _ducatsCts;
        private bool _liveRefreshedOnce;

        private string _subTab = "Market"; // "Market" or "Riven"
        public string SubTab
        {
            get => _subTab;
            set
            {
                if (SetProperty(ref _subTab, value))
                {
                    OnPropertyChanged(nameof(IsMarketTab));
                    OnPropertyChanged(nameof(IsRivenTab));
                }
            }
        }
        public bool IsMarketTab => SubTab == "Market";
        public bool IsRivenTab => SubTab == "Riven";

        // ==========================================
        // 1. WARFRAME MARKET TAB
        // ==========================================
        public ObservableCollection<WfmItemSummary> MarketSearchResults { get; } = new();
        public ObservableCollection<WfmOrderEntry> SellOrders { get; } = new();
        public ObservableCollection<WfmOrderEntry> BuyOrders { get; } = new();
        public ObservableCollection<MarketSetPartPrice> SetParts { get; } = new();
        public bool HasSetParts => SetParts.Count > 0;
        public ObservableCollection<DucatArbitrageEntry> DucatArbitrageItems { get; } = new();
        public bool HasDucatArbitrage => DucatArbitrageItems.Count > 0;
        public ObservableCollection<MarketWatchlistItem> Watchlist { get; } = new();
        public ObservableCollection<WfmItemSummary> QuickMarketPicks { get; } = new();

        private string _marketSearchText = string.Empty;
        public string MarketSearchText
        {
            get => _marketSearchText;
            set
            {
                if (SetProperty(ref _marketSearchText, value))
                    RefreshMarketSearch();
            }
        }

        private WfmItemSummary? _selectedMarketItem;
        public WfmItemSummary? SelectedMarketItem
        {
            get => _selectedMarketItem;
            set
            {
                if (SetProperty(ref _selectedMarketItem, value) && value != null)
                    _ = LoadMarketOrdersAsync(value);
            }
        }

        private MarketItemAnalysis _currentAnalysis = new();
        public MarketItemAnalysis CurrentAnalysis
        {
            get => _currentAnalysis;
            set => SetProperty(ref _currentAnalysis, value);
        }

        private bool _isLoadingMarket;
        public bool IsLoadingMarket
        {
            get => _isLoadingMarket;
            set => SetProperty(ref _isLoadingMarket, value);
        }

        private bool _isScanningDucats;
        public bool IsScanningDucats
        {
            get => _isScanningDucats;
            set => SetProperty(ref _isScanningDucats, value);
        }

        // ==========================================
        // 2. RIVEN HELPER TAB
        // ==========================================
        public ObservableCollection<RivenWeaponOption> FilteredRivenWeapons { get; } = new();
        public ObservableCollection<RivenVariantOption> RivenVariants { get; } = new();

        private RivenVariantOption? _selectedRivenVariant;
        public RivenVariantOption? SelectedRivenVariant
        {
            get => _selectedRivenVariant;
            set
            {
                if (SetProperty(ref _selectedRivenVariant, value))
                {
                    OnPropertyChanged(nameof(ActiveRivenDispositionText));
                    UpdateBoundsAndEvaluate();
                }
            }
        }
        public string ActiveRivenDispositionText => SelectedRivenVariant != null
            ? $"{SelectedRivenVariant.Disposition:F2}"
            : (SelectedRivenWeapon != null ? $"{SelectedRivenWeapon.Disposition:F2}" : "1.00");
        public ObservableCollection<RivenAttrOption> CompatiblePositiveAttrs { get; } = new();
        public ObservableCollection<RivenAttrOption> CompatibleNegativeAttrs { get; } = new();
        public ObservableCollection<RivenStatSlot> PositiveSlots { get; } = new();
        public RivenStatSlot NegativeSlot { get; } = new() { Index = 0, IsNegative = true };
        public ObservableCollection<RivenAuctionListing> LiveAuctions { get; } = new();
        public ObservableCollection<SavedRivenEntry> SavedRivens { get; } = new();
        public IReadOnlyList<VeiledChallengeEntry> VeiledChallenges => MarketRivenService.VeiledChallenges;

        public IReadOnlyList<string> RivenConfigs { get; } = new[] { "2+ / 0-", "2+ / 1-", "3+ / 0-", "3+ / 1-" };

        private string _rivenWeaponSearchText = string.Empty;
        public string RivenWeaponSearchText
        {
            get => _rivenWeaponSearchText;
            set
            {
                if (SetProperty(ref _rivenWeaponSearchText, value))
                    RefreshRivenWeaponList();
            }
        }

        private RivenWeaponOption? _selectedRivenWeapon;
        public RivenWeaponOption? SelectedRivenWeapon
        {
            get => _selectedRivenWeapon;
            set
            {
                if (SetProperty(ref _selectedRivenWeapon, value) && value != null)
                    OnRivenWeaponChanged();
            }
        }

        private string _selectedRivenConfig = "3+ / 1-";
        public string SelectedRivenConfig
        {
            get => _selectedRivenConfig;
            set
            {
                if (SetProperty(ref _selectedRivenConfig, value))
                {
                    OnPropertyChanged(nameof(HasThirdBuff));
                    OnPropertyChanged(nameof(HasNegativeCurse));
                    UpdateBoundsAndEvaluate();
                }
            }
        }
        public bool HasThirdBuff => SelectedRivenConfig.StartsWith("3+");
        public bool HasNegativeCurse => SelectedRivenConfig.Contains("1-");

        private int _rivenRerolls = 12;
        public int RivenRerolls
        {
            get => _rivenRerolls;
            set { if (SetProperty(ref _rivenRerolls, Math.Clamp(value, 0, 999))) UpdateBoundsAndEvaluate(); }
        }

        private RivenEvaluationResult _evaluation = new();
        public RivenEvaluationResult Evaluation
        {
            get => _evaluation;
            set => SetProperty(ref _evaluation, value);
        }

        private WeeklyRivenPriceStats? _weeklyUnrolled;
        public WeeklyRivenPriceStats? WeeklyUnrolled
        {
            get => _weeklyUnrolled;
            set { _weeklyUnrolled = value; OnPropertyChanged(); OnPropertyChanged(nameof(WeeklyUnrolledText)); }
        }

        private WeeklyRivenPriceStats? _weeklyRolled;
        public WeeklyRivenPriceStats? WeeklyRolled
        {
            get => _weeklyRolled;
            set { _weeklyRolled = value; OnPropertyChanged(); OnPropertyChanged(nameof(WeeklyRolledText)); }
        }

        public string WeeklyUnrolledText => WeeklyUnrolled != null
            ? $"Чистый (0 рол.): медиана {WeeklyUnrolled.Median:F0} Пл (ср. {WeeklyUnrolled.Avg:F0}, мин {WeeklyUnrolled.Min}..макс {WeeklyUnrolled.Max})"
            : "Чистый: нет недельных данных DE";
        public string WeeklyRolledText => WeeklyRolled != null
            ? $"Ролленый: медиана {WeeklyRolled.Median:F0} Пл (ср. {WeeklyRolled.Avg:F0}, популярность {WeeklyRolled.Popularity}%)"
            : "Ролленый: нет недельных данных DE";

        private bool _isLoadingAuctions;
        public bool IsLoadingAuctions
        {
            get => _isLoadingAuctions;
            set => SetProperty(ref _isLoadingAuctions, value);
        }

        // Commands
        public ICommand SwitchSubTabCommand { get; }
        public ICommand SelectMarketItemCommand { get; }
        public ICommand AddWatchlistCommand { get; }
        public ICommand RemoveWatchlistCommand { get; }
        public ICommand OpenWatchlistItemCommand { get; }
        public ICommand CopyWhisperCommand { get; }
        public ICommand FetchAuctionsCommand { get; }
        public ICommand ScanDucatArbitrageCommand { get; }
        public ICommand TestRivenInBuilderCommand { get; }
        public ICommand SaveRivenCommand { get; }
        public ICommand DeleteSavedRivenCommand { get; }

        public MarketRivenViewModel(Action<ActiveBuilderRiven> injectRivenToBuilder, Action<string> statusCallback)
        {
            _injectRivenToBuilder = injectRivenToBuilder;
            _statusCallback = statusCallback;

            for (int i = 1; i <= 3; i++)
            {
                var slot = new RivenStatSlot { Index = i, IsNegative = false, OnUpdated = UpdateBoundsAndEvaluate };
                PositiveSlots.Add(slot);
            }
            NegativeSlot.OnUpdated = UpdateBoundsAndEvaluate;

            SwitchSubTabCommand = new RelayCommand(p => { if (p is string t) SubTab = t; });
            SelectMarketItemCommand = new RelayCommand(p => { if (p is WfmItemSummary it) SelectedMarketItem = it; });
            AddWatchlistCommand = new RelayCommand(AddSelectedToWatchlist);
            RemoveWatchlistCommand = new RelayCommand(p =>
            {
                if (p is MarketWatchlistItem w)
                {
                    Watchlist.Remove(w);
                    StorageService.Instance.SaveMarketWatchlist(Watchlist);
                }
            });
            OpenWatchlistItemCommand = new RelayCommand(p =>
            {
                if (p is MarketWatchlistItem w)
                {
                    var match = _svc.MarketItems.FirstOrDefault(i => i.Slug.Equals(w.Slug, StringComparison.OrdinalIgnoreCase));
                    if (match != null) SelectedMarketItem = match;
                }
            });
            CopyWhisperCommand = new RelayCommand(p =>
            {
                string txt = p switch
                {
                    WfmOrderEntry o => o.WhisperCommand,
                    RivenAuctionListing a => a.WhisperText,
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(txt))
                {
                    try
                    {
                        Clipboard.SetDataObject(txt, true);
                        _statusCallback($"📋 Скопировано в буфер: {txt}");
                    }
                    catch { }
                }
            });
            FetchAuctionsCommand = new RelayCommand(() => { if (SelectedRivenWeapon != null) _ = LoadAuctionsAsync(SelectedRivenWeapon); });
            ScanDucatArbitrageCommand = new RelayCommand(() => _ = RunDucatArbitrageScanAsync());
            TestRivenInBuilderCommand = new RelayCommand(SendRivenToBuilder);
            SaveRivenCommand = new RelayCommand(SaveCurrentRiven);
            DeleteSavedRivenCommand = new RelayCommand(p =>
            {
                if (p is SavedRivenEntry r)
                {
                    SavedRivens.Remove(r);
                    StorageService.Instance.SaveSavedRivens(SavedRivens);
                }
            });
        }

        public void Initialize()
        {
            Watchlist.Clear();
            SavedRivens.Clear();
            QuickMarketPicks.Clear();
            foreach (var w in StorageService.Instance.LoadMarketWatchlist()) Watchlist.Add(w);
            foreach (var sr in StorageService.Instance.LoadSavedRivens()) SavedRivens.Add(sr);

            // Populate popular quick picks
            string[] quickSlugs = { "mesa_prime_set", "torid", "arcane_energize", "galvanized_chamber", "prime_junk", "glaive_prime_set", "nautilus_prime_set" };
            foreach (var slug in quickSlugs)
            {
                var found = _svc.MarketItems.FirstOrDefault(i => i.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
                if (found != null) QuickMarketPicks.Add(found);
            }
            if (QuickMarketPicks.Count == 0)
            {
                foreach (var it in _svc.MarketItems.Where(i => i.IsPrimeSet).Take(6))
                    QuickMarketPicks.Add(it);
            }

            RefreshMarketSearch();
            RefreshRivenWeaponList();

            var defaultWep = _svc.RivenWeapons.FirstOrDefault(w => w.Slug == "torid") ?? _svc.RivenWeapons.FirstOrDefault();
            if (defaultWep != null)
            {
                SelectedRivenWeapon = defaultWep;
                // Populate sample Godroll for Torid so page immediately shines on open
                SetDefaultSampleRoll();
            }

            if (QuickMarketPicks.Count > 0)
                _selectedMarketItem = QuickMarketPicks[0];

            if (!_liveRefreshedOnce)
            {
                _liveRefreshedOnce = true;
                _ = Task.Run(async () =>
                {
                    bool updated = await _svc.RefreshWeeklyRivensAndMarketAsync().ConfigureAwait(false);
                    if (!updated) return;
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted)
                    {
                        _ = disp.BeginInvoke(new Action(() =>
                        {
                            RefreshMarketSearch();
                            if (SelectedRivenWeapon != null)
                            {
                                var (unr, rol) = _svc.GetWeeklyStatsForWeapon(SelectedRivenWeapon);
                                WeeklyUnrolled = unr;
                                WeeklyRolled = rol;
                            }
                        }));
                    }
                });
            }
        }

        private void RefreshMarketSearch()
        {
            MarketSearchResults.Clear();
            string q = _marketSearchText.Trim();
            IEnumerable<WfmItemSummary> matches = string.IsNullOrEmpty(q)
                ? QuickMarketPicks.Concat(_svc.MarketItems.Where(i => i.IsPrimeSet).Take(25)).Distinct()
                : _svc.MarketItems.Where(i =>
                    i.NameRu.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.Slug.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.NameRu.StartsWith(q, StringComparison.OrdinalIgnoreCase) || i.NameEn.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                    .Take(35);

            foreach (var m in matches) MarketSearchResults.Add(m);
        }

        public async Task LoadMarketOrdersAsync(WfmItemSummary item)
        {
            _marketCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _marketCts = cts;
            var ct = cts.Token;

            IsLoadingMarket = true;
            try
            {
                var analysis = await _svc.FetchLiveMarketItemAsync(item).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                if (analysis != null)
                {
                    // Check Prime Set vs Parts arbitrage if Prime Set
                    if (item.IsPrimeSet && item.Slug.EndsWith("_set", StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix = item.Slug[..^4];
                        var partItems = _svc.MarketItems
                            .Where(p => p.Slug.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) && !p.Slug.EndsWith("_set"))
                            .GroupBy(p => p.Slug)
                            .Select(g => g.First())
                            .Take(12)
                            .ToList();

                        int partsSum = 0;
                        foreach (var part in partItems)
                        {
                            if (ct.IsCancellationRequested) return;
                            var pAn = await _svc.FetchLiveMarketItemAsync(part).ConfigureAwait(false);
                            if (pAn != null && pAn.MinSellPrice > 0)
                            {
                                partsSum += pAn.MinSellPrice;
                                analysis.SetParts.Add(new MarketSetPartPrice { Name = part.NameRu, Slug = part.Slug, MinSellPrice = pAn.MinSellPrice });
                            }
                        }

                        if (partsSum > 0 && analysis.MinSellPrice > 0)
                        {
                            int diff = analysis.MinSellPrice - partsSum;
                            analysis.ArbitrageSummary = diff > 0
                                ? $"💡 По частям дешевле на {diff} Пл ({partsSum} Пл vs {analysis.MinSellPrice} Пл за сет)"
                                : $"✓ Сет выгоднее покупки по частям (экономия {-diff} Пл)";
                        }
                    }

                    if (ct.IsCancellationRequested) return;

                    void Apply()
                    {
                        if (ct.IsCancellationRequested) return;
                        CurrentAnalysis = analysis;
                        SellOrders.Clear();
                        foreach (var o in analysis.SellOrders) SellOrders.Add(o);
                        BuyOrders.Clear();
                        foreach (var o in analysis.BuyOrders) BuyOrders.Add(o);
                        SetParts.Clear();
                        foreach (var p in analysis.SetParts) SetParts.Add(p);
                        OnPropertyChanged(nameof(HasSetParts));

                        // Update watchlist price if present
                        var wMatch = Watchlist.FirstOrDefault(w => w.Slug == item.Slug);
                        if (wMatch != null && analysis.MinSellPrice > 0)
                        {
                            wMatch.LastMinSell = analysis.MinSellPrice;
                            wMatch.UpdatedAt = DateTime.Now.ToString("HH:mm");
                            StorageService.Instance.SaveMarketWatchlist(Watchlist);
                        }
                    }
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted) await disp.InvokeAsync((Action)Apply);
                    else Apply();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_marketCts, cts))
                {
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted) _ = disp.BeginInvoke((Action)(() => IsLoadingMarket = false));
                    else IsLoadingMarket = false;
                }
            }
        }

        private async Task RunDucatArbitrageScanAsync()
        {
            _ducatsCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _ducatsCts = cts;
            var ct = cts.Token;

            IsScanningDucats = true;
            _statusCallback("🪙 Сканирование Дукат-арбитража (топ предметов с дукатами)...");
            try
            {
                var found = await _svc.ScanDucatArbitrageAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                void Apply()
                {
                    DucatArbitrageItems.Clear();
                    foreach (var d in found) DucatArbitrageItems.Add(d);
                    OnPropertyChanged(nameof(HasDucatArbitrage));
                }
                var disp = Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted) await disp.InvokeAsync((Action)Apply);
                else Apply();

                _statusCallback(found.Count > 0
                    ? $"🪙 Найдено {found.Count} выгодных Дукат-лотов (лучший: {found[0].DucatsPerPlat:F1} Дук/Пл)"
                    : "⚠️ Не удалось получить live-ставки Дукатов");
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_ducatsCts, cts))
                {
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted) _ = disp.BeginInvoke((Action)(() => IsScanningDucats = false));
                    else IsScanningDucats = false;
                }
            }
        }

        private void AddSelectedToWatchlist()
        {
            if (SelectedMarketItem == null) return;
            if (Watchlist.Any(w => w.Slug == SelectedMarketItem.Slug)) return;

            Watchlist.Insert(0, new MarketWatchlistItem
            {
                Slug = SelectedMarketItem.Slug,
                NameRu = SelectedMarketItem.NameRu,
                NameEn = SelectedMarketItem.NameEn,
                LastMinSell = CurrentAnalysis.MinSellPrice,
                UpdatedAt = DateTime.Now.ToString("HH:mm")
            });
            StorageService.Instance.SaveMarketWatchlist(Watchlist);
            _statusCallback($"★ Добавлено в избранное Маркета: {SelectedMarketItem.NameRu}");
        }

        private void RefreshRivenWeaponList()
        {
            FilteredRivenWeapons.Clear();
            string q = _rivenWeaponSearchText.Trim();
            var list = string.IsNullOrEmpty(q)
                ? _svc.RivenWeapons.Take(500)
                : _svc.RivenWeapons.Where(w =>
                    w.NameRu.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    w.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(120);
            foreach (var w in list) FilteredRivenWeapons.Add(w);
            if (_selectedRivenWeapon != null && !FilteredRivenWeapons.Contains(_selectedRivenWeapon))
                FilteredRivenWeapons.Insert(0, _selectedRivenWeapon);
        }

        private void OnRivenWeaponChanged()
        {
            if (SelectedRivenWeapon == null) return;

            RivenVariants.Clear();
            var matchedPlatforms = BuilderDatabaseService.Instance.Platforms
                .Where(p => p.Category == "Оружие" && !p.IsExalted &&
                            (p.Name.Contains(SelectedRivenWeapon.NameEn, StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrEmpty(SelectedRivenWeapon.NameRu) && p.Name.Contains(SelectedRivenWeapon.NameRu, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(p => p.RivenDisposition)
                .ToList();

            foreach (var mp in matchedPlatforms)
            {
                RivenVariants.Add(new RivenVariantOption
                {
                    Name = mp.Name,
                    Disposition = mp.RivenDisposition > 0 ? mp.RivenDisposition : SelectedRivenWeapon.Disposition
                });
            }
            if (RivenVariants.Count == 0)
            {
                RivenVariants.Add(new RivenVariantOption
                {
                    Name = SelectedRivenWeapon.DisplayName,
                    Disposition = SelectedRivenWeapon.Disposition
                });
            }
            _selectedRivenVariant = RivenVariants[0];
            OnPropertyChanged(nameof(SelectedRivenVariant));
            OnPropertyChanged(nameof(ActiveRivenDispositionText));

            CompatiblePositiveAttrs.Clear();
            CompatibleNegativeAttrs.Clear();
            foreach (var a in _svc.RivenAttrs)
            {
                if (a.ExclusiveTo.Count > 0 && !a.ExclusiveTo.Contains(SelectedRivenWeapon.RivenType, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!a.NegativeOnly) CompatiblePositiveAttrs.Add(a);
                if (!a.PositiveOnly) CompatibleNegativeAttrs.Add(a);
            }

            var (unr, rol) = _svc.GetWeeklyStatsForWeapon(SelectedRivenWeapon);
            WeeklyUnrolled = unr;
            WeeklyRolled = rol;

            UpdateBoundsAndEvaluate();
        }

        private void SetDefaultSampleRoll()
        {
            RivenAttrOption? FindAttr(string slug) => CompatiblePositiveAttrs.FirstOrDefault(a => a.Slug == slug);
            if (PositiveSlots.Count >= 3)
            {
                PositiveSlots[0].SelectedAttr = FindAttr("critical_chance") ?? CompatiblePositiveAttrs.FirstOrDefault();
                PositiveSlots[1].SelectedAttr = FindAttr("critical_damage") ?? CompatiblePositiveAttrs.Skip(1).FirstOrDefault();
                PositiveSlots[2].SelectedAttr = FindAttr("multishot") ?? CompatiblePositiveAttrs.Skip(2).FirstOrDefault();
            }
            NegativeSlot.SelectedAttr = CompatibleNegativeAttrs.FirstOrDefault(a => a.Slug == "zoom")
                                     ?? CompatibleNegativeAttrs.FirstOrDefault();

            foreach (var s in PositiveSlots)
            {
                if (s.MaxRange > 0) s.Value = Math.Round((s.MinRange + s.MaxRange) * 0.52, 1);
            }
            if (NegativeSlot.MaxRange != 0) NegativeSlot.Value = Math.Round((NegativeSlot.MinRange + NegativeSlot.MaxRange) * 0.50, 1);
            UpdateBoundsAndEvaluate();
        }

        private void UpdateBoundsAndEvaluate()
        {
            if (SelectedRivenWeapon == null) return;

            if (PositiveSlots.Count >= 3)
                PositiveSlots[2].IsSlotActive = HasThirdBuff;

            double disp = SelectedRivenVariant?.Disposition ?? SelectedRivenWeapon.Disposition;

            foreach (var s in PositiveSlots)
            {
                var (lo, hi) = MarketRivenService.CalculateRivenBounds(SelectedRivenWeapon, s.SelectedAttr, SelectedRivenConfig, false, disp);
                s.MinRange = lo;
                s.MaxRange = hi;
                if (s.SelectedAttr != null && (s.Value <= 0 || s.Value > hi * 1.3 || s.Value < lo * 0.7))
                    s.Value = Math.Round((lo + hi) * 0.5, 1);
            }

            var (nlo, nhi) = MarketRivenService.CalculateRivenBounds(SelectedRivenWeapon, NegativeSlot.SelectedAttr, SelectedRivenConfig, true, disp);
            NegativeSlot.MinRange = nlo;
            NegativeSlot.MaxRange = nhi;
            if (NegativeSlot.SelectedAttr != null && Math.Abs(NegativeSlot.Value) < 0.1)
                NegativeSlot.Value = Math.Round((nlo + nhi) * 0.5, 1);

            var activePos = PositiveSlots.Take(HasThirdBuff ? 3 : 2).ToList();
            Evaluation = MarketRivenService.EvaluateRoll(
                SelectedRivenWeapon,
                activePos,
                HasNegativeCurse ? NegativeSlot : null,
                SelectedRivenConfig,
                RivenRerolls);
        }

        public async Task LoadAuctionsAsync(RivenWeaponOption weapon)
        {
            _auctionsCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _auctionsCts = cts;
            var ct = cts.Token;

            IsLoadingAuctions = true;
            try
            {
                var list = await _svc.SearchLiveRivenAuctionsAsync(weapon, null).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                void Apply()
                {
                    if (ct.IsCancellationRequested) return;
                    LiveAuctions.Clear();
                    foreach (var a in list) LiveAuctions.Add(a);
                }
                var disp = Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted) await disp.InvokeAsync((Action)Apply);
                else Apply();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_auctionsCts, cts))
                {
                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted) _ = disp.BeginInvoke((Action)(() => IsLoadingAuctions = false));
                    else IsLoadingAuctions = false;
                }
            }
        }

        private void SendRivenToBuilder()
        {
            if (SelectedRivenWeapon == null) return;
            var activePos = PositiveSlots.Take(HasThirdBuff ? 3 : 2).Where(s => s.SelectedAttr != null).ToList();
            string sum = string.Join(" | ", activePos.Select(s => $"+{s.Value:F1}% {s.SelectedAttr!.EffectRu}"));
            if (HasNegativeCurse && NegativeSlot.SelectedAttr != null)
                sum += $" | -{Math.Abs(NegativeSlot.Value):F1}% {NegativeSlot.SelectedAttr.EffectRu}";

            _injectRivenToBuilder(new ActiveBuilderRiven
            {
                WeaponName = string.IsNullOrEmpty(SelectedRivenWeapon.NameRu) ? SelectedRivenWeapon.NameEn : SelectedRivenWeapon.NameRu,
                WeaponNameEn = SelectedRivenWeapon.NameEn,
                RivenType = SelectedRivenWeapon.RivenType,
                Summary = sum,
                Stats = Evaluation.BuilderStatMap
            });
        }

        private void SaveCurrentRiven()
        {
            if (SelectedRivenWeapon == null) return;
            var activePos = PositiveSlots.Take(HasThirdBuff ? 3 : 2).Where(s => s.SelectedAttr != null).ToList();
            string sum = string.Join(" | ", activePos.Select(s => $"+{s.Value:F0}% {s.SelectedAttr!.EffectRu}"));
            if (HasNegativeCurse && NegativeSlot.SelectedAttr != null)
                sum += $" (-{Math.Abs(NegativeSlot.Value):F0}% {NegativeSlot.SelectedAttr.EffectRu})";

            SavedRivens.Insert(0, new SavedRivenEntry
            {
                WeaponSlug = SelectedRivenWeapon.Slug,
                WeaponName = SelectedRivenWeapon.DisplayName,
                Config = SelectedRivenConfig,
                Grade = Evaluation.Grade,
                Rerolls = RivenRerolls,
                StatsSummary = sum,
                SavedAt = DateTime.Now.ToString("dd.MM HH:mm")
            });
            StorageService.Instance.SaveSavedRivens(SavedRivens);
            _statusCallback($"💾 Мод Разлома для {SelectedRivenWeapon.NameRu} ({Evaluation.Grade}-тир) сохранён!");
        }
    }
}
