using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Thread-safe asynchronous image loader with bundled asset fallback,
    /// persistent disk cache (data/cache/images), bounded RAM LRU cache, and CDN fallbacks.
    /// </summary>
    public sealed class ImageCacheService
    {
        private static ImageCacheService? _instance;
        public static ImageCacheService Instance => _instance ??= new ImageCacheService();

        private const int MaxMemoryEntries = 450;
        private const int DecodePixelSize = 148;

        private readonly string _diskCacheDir;
        private readonly string _localAssetsDir;
        private readonly Dictionary<string, string> _localAssetFiles = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _lruLock = new();
        private readonly Dictionary<string, (LinkedListNode<string> Node, BitmapImage Bitmap)> _memCache = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lruList = new();

        private readonly object _pendingLock = new();
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CollectionItemModel>> _pendingItemWaiters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Action<BitmapImage>>> _pendingUrlCallbacks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failedUntil = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _downloadSlots = new(6, 6);
        private readonly HttpClient _http;

        private ImageCacheService()
        {
            string dataDir = Path.GetDirectoryName(StorageService.Instance.SaveDirectoryPath)
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            _diskCacheDir = Path.Combine(dataDir, "cache", "images");
            _localAssetsDir = Path.Combine(dataDir, "assets", "items");

            try
            {
                Directory.CreateDirectory(_diskCacheDir);
                if (Directory.Exists(_localAssetsDir))
                {
                    foreach (string file in Directory.GetFiles(_localAssetsDir))
                    {
                        string name = Path.GetFileName(file);
                        _localAssetFiles.TryAdd(name, file);
                    }
                }
            }
            catch { }

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WarfAssistant/2.0");

            _ = Task.Run(() => PruneDiskCache(maxFiles: 2200, maxBytes: 180L * 1024 * 1024));
        }

        public string DiskCacheDirectory => _diskCacheDir;

        public string? ResolveBundledAssetPath(string? wsImg, string? iconPath)
        {
            if (!string.IsNullOrEmpty(wsImg))
            {
                if (_localAssetFiles.TryGetValue(wsImg, out string? p)) return p;
                string noHyphen = wsImg.Replace("-", "");
                if (_localAssetFiles.TryGetValue(noHyphen, out string? p2)) return p2;
            }
            if (!string.IsNullOrEmpty(iconPath))
            {
                string file = Path.GetFileName(iconPath);
                if (!string.IsNullOrEmpty(file) && _localAssetFiles.TryGetValue(file, out string? p3))
                    return p3;
            }
            return null;
        }

        public static string ComputeCacheKey(string? iconPath, string? imageHash, string? wsImg)
        {
            string raw = !string.IsNullOrEmpty(iconPath)
                ? $"{iconPath}|{imageHash}"
                : $"ws|{wsImg}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
        }

        public BitmapImage? TryGetFromMemory(string cacheKey)
        {
            lock (_lruLock)
            {
                if (_memCache.TryGetValue(cacheKey, out var entry))
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                    return entry.Bitmap;
                }
            }
            return null;
        }

        public void RequestItemThumbnail(CollectionItemModel item)
        {
            if (string.IsNullOrEmpty(item.IconPath) && string.IsNullOrEmpty(item.FallbackImageName) && string.IsNullOrEmpty(item.ThumbnailUrl))
                return;

            string key = item.ImageCacheKey;
            if (TryGetFromMemory(key) is BitmapImage cached)
            {
                item.SetLoadedBitmap(cached);
                return;
            }

            if (_failedUntil.TryGetValue(key, out DateTime retryAfter) && DateTime.UtcNow < retryAfter)
                return;

            bool shouldStart;
            lock (_pendingLock)
            {
                if (TryGetFromMemory(key) is BitmapImage cachedInside)
                {
                    item.SetLoadedBitmap(cachedInside);
                    return;
                }

                if (!_pendingItemWaiters.TryGetValue(key, out var list))
                {
                    list = new List<CollectionItemModel>(1);
                    _pendingItemWaiters[key] = list;
                }
                if (!list.Contains(item))
                    list.Add(item);

                shouldStart = _inFlight.Add(key);
            }

            if (!shouldStart) return;

            _ = Task.Run(async () =>
            {
                BitmapImage? bmp = null;
                try
                {
                    bmp = await LoadOrFetchAsync(item, key).ConfigureAwait(false);
                    if (bmp != null)
                        AddToMemoryCache(key, bmp);
                    else
                        _failedUntil[key] = DateTime.UtcNow.AddMinutes(3);
                }
                catch
                {
                    _failedUntil[key] = DateTime.UtcNow.AddMinutes(3);
                }
                finally
                {
                    List<CollectionItemModel>? waiters;
                    lock (_pendingLock)
                    {
                        _inFlight.Remove(key);
                        _pendingItemWaiters.Remove(key, out waiters);
                    }

                    if (bmp != null && waiters != null && waiters.Count > 0)
                    {
                        void DeliverAll()
                        {
                            foreach (var w in waiters) w.SetLoadedBitmap(bmp);
                        }
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)DeliverAll);
                        else
                            DeliverAll();
                    }
                }
            });
        }

        private async Task<BitmapImage?> LoadOrFetchAsync(CollectionItemModel item, string key)
        {
            // 1. Check bundled local asset override first (0ms offline)
            string? localOverride = ResolveBundledAssetPath(item.FallbackImageName, item.IconPath);
            if (!string.IsNullOrEmpty(localOverride) && File.Exists(localOverride))
            {
                byte[] bytes = await File.ReadAllBytesAsync(localOverride).ConfigureAwait(false);
                var decoded = TryDecodeBitmap(bytes);
                if (decoded != null) return decoded;
            }

            // 2. Check persistent disk cache
            string diskPath = Path.Combine(_diskCacheDir, $"{key}.png");
            if (File.Exists(diskPath))
            {
                byte[] bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                var decoded = TryDecodeBitmap(bytes);
                if (decoded != null) return decoded;
                try { File.Delete(diskPath); } catch { }
            }

            // 3. Throttled remote fetch with CDN priority fallback
            await _downloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check disk in case another identical icon just finished
                if (File.Exists(diskPath))
                {
                    byte[] cachedBytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                    var decoded = TryDecodeBitmap(cachedBytes);
                    if (decoded != null) return decoded;
                }

                foreach (string url in EnumerateCandidateUrls(item))
                {
                    byte[]? data = await TryDownloadBytesAsync(url).ConfigureAwait(false);
                    if (data == null || !IsValidImageHeader(data)) continue;

                    var decoded = TryDecodeBitmap(data);
                    if (decoded == null) continue;

                    try
                    {
                        string tmp = diskPath + ".tmp";
                        await File.WriteAllBytesAsync(tmp, data).ConfigureAwait(false);
                        File.Move(tmp, diskPath, overwrite: true);
                    }
                    catch { }

                    return decoded;
                }
            }
            finally
            {
                _downloadSlots.Release();
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCandidateUrls(CollectionItemModel item)
        {
            if (!string.IsNullOrEmpty(item.IconPath))
            {
                // Fast CDN mirror first, then authoritative DE PublicExport origin with content hash
                yield return $"https://browse.wf{item.IconPath}";
                if (!string.IsNullOrEmpty(item.ImageHash))
                {
                    yield return $"https://content.warframe.com/PublicExport{item.IconPath}!{item.ImageHash}";
                }
            }

            if (!string.IsNullOrEmpty(item.FallbackImageName))
            {
                yield return $"https://cdn.warframestat.us/img/{Uri.EscapeDataString(item.FallbackImageName)}";
            }

            if (!string.IsNullOrEmpty(item.ThumbnailUrl) && item.ThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                yield return item.ThumbnailUrl;
            }
        }

        private async Task<byte[]?> TryDownloadBytesAsync(string url)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return bytes.Length >= 64 ? bytes : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidImageHeader(byte[] data)
        {
            if (data.Length < 12) return false;
            // PNG
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
            // JPEG
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
            // WEBP (RIFF....WEBP)
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return true;
            return false;
        }

        private static BitmapImage? TryDecodeBitmap(byte[] bytes, int decodeWidth = DecodePixelSize)
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0)
                    bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public static string? NormalizeImageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            string trimmed = url.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
                return $"https://browse.wf{trimmed}";
            return trimmed;
        }

        public BitmapImage? TryGetUrlFromMemory(string? url, int decodeWidth = 148)
        {
            string? norm = NormalizeImageUrl(url);
            if (string.IsNullOrWhiteSpace(norm)) return null;
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes($"url|{decodeWidth}|{norm}"));
            string key = "u_" + Convert.ToHexString(h.AsSpan(0, 10)).ToLowerInvariant();
            return TryGetFromMemory(key);
        }

        public void RequestUrlBitmap(string? url, Action<BitmapImage> onLoaded, int decodeWidth = 148)
        {
            string? normUrl = NormalizeImageUrl(url);
            if (string.IsNullOrWhiteSpace(normUrl)) return;

            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes($"url|{decodeWidth}|{normUrl}"));
            string key = "u_" + Convert.ToHexString(h.AsSpan(0, 10)).ToLowerInvariant();

            if (TryGetFromMemory(key) is BitmapImage cached)
            {
                onLoaded(cached);
                return;
            }

            if (_failedUntil.TryGetValue(key, out DateTime retryAfter) && DateTime.UtcNow < retryAfter)
                return;

            bool shouldStart;
            lock (_pendingLock)
            {
                if (TryGetFromMemory(key) is BitmapImage cachedInside)
                {
                    onLoaded(cachedInside);
                    return;
                }

                if (!_pendingUrlCallbacks.TryGetValue(key, out var list))
                {
                    list = new List<Action<BitmapImage>>(2);
                    _pendingUrlCallbacks[key] = list;
                }
                list.Add(onLoaded);
                shouldStart = _inFlight.Add(key);
            }

            if (!shouldStart)
                return;

            _ = Task.Run(async () =>
            {
                BitmapImage? bmp = null;
                try
                {
                    string diskPath = Path.Combine(_diskCacheDir, $"{key}.png");

                    if (!normUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(normUrl))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(normUrl).ConfigureAwait(false);
                        bmp = TryDecodeBitmap(bytes, decodeWidth);
                    }

                    if (bmp == null && File.Exists(diskPath))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                        bmp = TryDecodeBitmap(bytes, decodeWidth);
                    }

                    if (bmp == null && normUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        await _downloadSlots.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (File.Exists(diskPath))
                            {
                                byte[] bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                                bmp = TryDecodeBitmap(bytes, decodeWidth);
                            }

                            if (bmp == null)
                            {
                                byte[]? data = await TryDownloadBytesAsync(normUrl).ConfigureAwait(false);
                                if (data != null && IsValidImageHeader(data))
                                {
                                    bmp = TryDecodeBitmap(data, decodeWidth);
                                    if (bmp != null)
                                    {
                                        try
                                        {
                                            string tmp = diskPath + ".tmp";
                                            await File.WriteAllBytesAsync(tmp, data).ConfigureAwait(false);
                                            File.Move(tmp, diskPath, overwrite: true);
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _downloadSlots.Release();
                        }
                    }

                    if (bmp != null)
                        AddToMemoryCache(key, bmp);
                    else
                        _failedUntil[key] = DateTime.UtcNow.AddMinutes(3);
                }
                catch
                {
                    _failedUntil[key] = DateTime.UtcNow.AddMinutes(3);
                }
                finally
                {
                    List<Action<BitmapImage>>? snapshot;
                    lock (_pendingLock)
                    {
                        _inFlight.Remove(key);
                        _pendingUrlCallbacks.Remove(key, out snapshot);
                    }

                    if (bmp != null && snapshot != null && snapshot.Count > 0)
                    {
                        void DeliverAll()
                        {
                            foreach (var cb in snapshot) cb(bmp);
                        }
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)DeliverAll);
                        else
                            DeliverAll();
                    }
                }
            });
        }

        public (int FileCount, double TotalMegabytes) GetDiskCacheStats()
        {
            try
            {
                if (!Directory.Exists(_diskCacheDir)) return (0, 0);
                var files = new DirectoryInfo(_diskCacheDir).GetFiles();
                long total = 0;
                foreach (var f in files) total += f.Length;
                return (files.Length, Math.Round(total / (1024.0 * 1024.0), 2));
            }
            catch
            {
                return (0, 0);
            }
        }

        private void AddToMemoryCache(string key, BitmapImage bmp)
        {
            lock (_lruLock)
            {
                if (_memCache.TryGetValue(key, out var existing))
                {
                    _lruList.Remove(existing.Node);
                    var node = _lruList.AddFirst(key);
                    _memCache[key] = (node, bmp);
                    return;
                }

                var newNode = _lruList.AddFirst(key);
                _memCache[key] = (newNode, bmp);

                while (_memCache.Count > MaxMemoryEntries && _lruList.Last != null)
                {
                    string oldest = _lruList.Last.Value;
                    _lruList.RemoveLast();
                    _memCache.Remove(oldest);
                }
            }
        }

        public int ClearDiskCache()
        {
            int removed = 0;
            lock (_lruLock)
            {
                _memCache.Clear();
                _lruList.Clear();
            }
            _failedUntil.Clear();

            if (Directory.Exists(_diskCacheDir))
            {
                foreach (string file in Directory.GetFiles(_diskCacheDir))
                {
                    try { File.Delete(file); removed++; } catch { }
                }
            }
            return removed;
        }

        private void PruneDiskCache(int maxFiles, long maxBytes)
        {
            try
            {
                if (!Directory.Exists(_diskCacheDir)) return;
                var files = new DirectoryInfo(_diskCacheDir).GetFiles();
                if (files.Length <= maxFiles)
                {
                    long quickSum = 0;
                    foreach (var f in files) quickSum += f.Length;
                    if (quickSum <= maxBytes) return;
                }

                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long totalBytes = 0;
                foreach (var f in files) totalBytes += f.Length;
                int remaining = files.Length;

                foreach (var f in files)
                {
                    if (remaining <= maxFiles && totalBytes <= maxBytes) break;
                    try
                    {
                        long len = f.Length;
                        f.Delete();
                        totalBytes -= len;
                        remaining--;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
