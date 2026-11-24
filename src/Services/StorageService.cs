using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    public sealed class CollectionSavedState
    {
        public bool UsesUniqueNames { get; set; }
        public HashSet<string> Owned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Mastered { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Subsumed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Incarnon { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class StorageService
    {
        private static readonly Lazy<StorageService> _lazy = new(() => new StorageService());
        public static StorageService Instance => _lazy.Value;

        private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
        private readonly object _fileLock = new();

        public string SaveDirectoryPath { get; private set; }
        public string SaveFilePath { get; private set; }
        public string? LastError { get; private set; }

        public StorageService()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localDataDir = Path.Combine(baseDir, "data", "saves");

            if (!Directory.Exists(localDataDir))
            {
                string parentSearch = Path.Combine(baseDir, "..", "..", "..", "..", "data", "saves");
                if (Directory.Exists(Path.GetFullPath(parentSearch)))
                {
                    localDataDir = Path.GetFullPath(parentSearch);
                }
                else
                {
                    Directory.CreateDirectory(localDataDir);
                }
            }

            SaveDirectoryPath = localDataDir;
            SaveFilePath = Path.Combine(SaveDirectoryPath, "userdata.json");

            EnsureDefaultSaveFileExists();
        }

        private void EnsureDefaultSaveFileExists()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(SaveDirectoryPath))
                        Directory.CreateDirectory(SaveDirectoryPath);

                    if (!File.Exists(SaveFilePath))
                    {
                        var initial = new JsonObject
                        {
                            ["profile_version"] = "2.0",
                            ["player_name"] = "Тэнно",
                            ["last_backup"] = DateTime.UtcNow.ToString("o"),
                            ["collection"] = new JsonObject
                            {
                                ["owned_uids"] = new JsonArray(),
                                ["mastered_uids"] = new JsonArray(),
                                ["subsumed_uids"] = new JsonArray(),
                                ["incarnon_uids"] = new JsonArray(),
                                ["exclude_founders"] = false
                            }
                        };
                        WriteRootJsonAtomicLocked(initial);
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
        }

        private JsonObject? TryReadRootJsonLocked()
        {
            if (!File.Exists(SaveFilePath)) return null;
            string json = File.ReadAllText(SaveFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonNode.Parse(json) as JsonObject;
        }

        private void WriteRootJsonAtomicLocked(JsonObject obj)
        {
            if (!Directory.Exists(SaveDirectoryPath))
                Directory.CreateDirectory(SaveDirectoryPath);

            string tmpPath = SaveFilePath + ".tmp";
            File.WriteAllText(tmpPath, obj.ToJsonString(WriteOpts));
            File.Move(tmpPath, SaveFilePath, overwrite: true);
        }

        private T ReadFromRoot<T>(Func<JsonObject?, T> reader, T fallback)
        {
            lock (_fileLock)
            {
                try
                {
                    var root = TryReadRootJsonLocked();
                    return reader(root);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return fallback;
                }
            }
        }

        private void UpdateRootJson(Action<JsonObject> mutate)
        {
            lock (_fileLock)
            {
                try
                {
                    var obj = TryReadRootJsonLocked() ?? new JsonObject();
                    mutate(obj);
                    WriteRootJsonAtomicLocked(obj);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
        }

        public bool OpenSaveFolder()
        {
            try
            {
                if (!Directory.Exists(SaveDirectoryPath))
                    Directory.CreateDirectory(SaveDirectoryPath);

                if (File.Exists(SaveFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{SaveFilePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{SaveDirectoryPath}\"",
                        UseShellExecute = true
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public bool CreateBackup(out string? backupPath, out string? error)
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(SaveFilePath))
                    {
                        backupPath = null;
                        error = "Файл сохранения ещё не создан";
                        return false;
                    }

                    var root = TryReadRootJsonLocked() ?? new JsonObject();
                    root["last_backup"] = DateTime.UtcNow.ToString("o");
                    WriteRootJsonAtomicLocked(root);

                    string fileName = $"userdata_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                    backupPath = Path.Combine(SaveDirectoryPath, fileName);
                    File.Copy(SaveFilePath, backupPath, overwrite: true);
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    backupPath = null;
                    error = ex.Message;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public bool ExportProfileToFile(string destPath, out string? error)
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(SaveFilePath))
                    {
                        error = "Файл сохранения не найден";
                        return false;
                    }

                    string? dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    string tmp = destPath + ".tmp";
                    File.Copy(SaveFilePath, tmp, overwrite: true);
                    File.Move(tmp, destPath, overwrite: true);
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public bool ImportProfileFromFile(string sourcePath, out string? error)
        {
            lock (_fileLock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        error = "Указанный файл не найден";
                        return false;
                    }

                    string raw = File.ReadAllText(sourcePath);
                    var parsed = JsonNode.Parse(raw) as JsonObject;
                    if (parsed == null)
                    {
                        error = "Файл не является корректным JSON-объектом профиля";
                        return false;
                    }

                    string[] knownKeys =
                    {
                        "profile_version", "player_name", "collection", "craft_queue", "craft_queue_v2",
                        "resource_inventory", "saved_builds", "market_watchlist", "saved_rivens",
                        "tracked_duviri_moods", "bookmarked_incarnon_weeks", "notify_cycles", "weekly_checklist"
                    };
                    if (!knownKeys.Any(k => parsed.ContainsKey(k)))
                    {
                        error = "Файл не похож на профиль WarfAssistant (нет известных разделов)";
                        return false;
                    }

                    if (File.Exists(SaveFilePath))
                    {
                        string preBackup = Path.Combine(SaveDirectoryPath, $"userdata_pre_import_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        File.Copy(SaveFilePath, preBackup, overwrite: true);
                    }

                    WriteRootJsonAtomicLocked(parsed);
                    LastError = null;
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public string LoadPlayerName()
        {
            return ReadFromRoot(root =>
            {
                string? name = root?["player_name"]?.ToString()?.Trim();
                return string.IsNullOrEmpty(name) ? "Тэнно" : name;
            }, "Тэнно");
        }

        public void SavePlayerName(string playerName)
        {
            string clean = string.IsNullOrWhiteSpace(playerName) ? "Тэнно" : playerName.Trim();
            UpdateRootJson(root => root["player_name"] = clean);
        }

        public string LoadLastBackupDisplay()
        {
            return ReadFromRoot(root =>
            {
                string? iso = root?["last_backup"]?.ToString();
                if (!string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                return "ещё не создавался";
            }, "ещё не создавался");
        }

        public List<string> LoadTrackedMoods()
        {
            return ReadFromRoot(root =>
            {
                if (root?["tracked_duviri_moods"] is JsonArray arr)
                {
                    var list = new List<string>();
                    foreach (var item in arr)
                    {
                        string? str = item?.ToString();
                        if (!string.IsNullOrEmpty(str)) list.Add(str);
                    }
                    return list;
                }
                return new List<string> { "sorrow", "anger" };
            }, new List<string> { "sorrow", "anger" });
        }

        public void SaveTrackedMoods(IEnumerable<string> moodKeys)
        {
            var keys = moodKeys.ToList();
            UpdateRootJson(root =>
            {
                var arr = new JsonArray();
                foreach (var k in keys) arr.Add(k);
                root["tracked_duviri_moods"] = arr;
            });
        }

        public List<int> LoadBookmarkedIncarnonWeeks()
        {
            return ReadFromRoot(root =>
            {
                var list = new List<int>();
                if (root?["bookmarked_incarnon_weeks"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item != null && int.TryParse(item.ToString(), out int weekNum))
                            list.Add(weekNum);
                    }
                }
                return list;
            }, new List<int>());
        }

        public void SaveBookmarkedIncarnonWeeks(IEnumerable<int> weekNumbers)
        {
            var weeks = weekNumbers.ToList();
            UpdateRootJson(root =>
            {
                var arr = new JsonArray();
                foreach (var w in weeks) arr.Add(w);
                root["bookmarked_incarnon_weeks"] = arr;
            });
        }

        private static void ReadStringArrayInto(JsonObject obj, string key, HashSet<string> target)
        {
            if (obj[key] is not JsonArray arr) return;
            foreach (var item in arr)
            {
                string? s = item?.ToString();
                if (!string.IsNullOrEmpty(s)) target.Add(s);
            }
        }

        public CollectionSavedState LoadCollectionStateV2()
        {
            return ReadFromRoot(root =>
            {
                var state = new CollectionSavedState();
                if (root?["collection"] is not JsonObject colObj) return state;

                if (colObj["owned_uids"] is JsonArray)
                {
                    state.UsesUniqueNames = true;
                    ReadStringArrayInto(colObj, "owned_uids", state.Owned);
                    ReadStringArrayInto(colObj, "mastered_uids", state.Mastered);
                    ReadStringArrayInto(colObj, "subsumed_uids", state.Subsumed);
                    ReadStringArrayInto(colObj, "incarnon_uids", state.Incarnon);
                    return state;
                }

                // Legacy name-based format fallback (migrated on next SaveCollectionStateV2)
                if (colObj["owned_items"] is JsonArray)
                {
                    ReadStringArrayInto(colObj, "owned_items", state.Owned);
                }
                else
                {
                    ReadStringArrayInto(colObj, "owned_warframes", state.Owned);
                    ReadStringArrayInto(colObj, "owned_weapons", state.Owned);
                    ReadStringArrayInto(colObj, "owned_ephemeras", state.Owned);
                }
                ReadStringArrayInto(colObj, "mastered_items", state.Mastered);
                ReadStringArrayInto(colObj, "subsumed_warframes", state.Subsumed);
                ReadStringArrayInto(colObj, "incarnon_weapons", state.Incarnon);
                return state;
            }, new CollectionSavedState());
        }

        public (HashSet<string> Owned, HashSet<string> Mastered, HashSet<string> Subsumed, HashSet<string> Incarnon) LoadCollectionStateExtended()
        {
            var st = LoadCollectionStateV2();
            return (st.Owned, st.Mastered, st.Subsumed, st.Incarnon);
        }

        public (HashSet<string> Owned, HashSet<string> Mastered) LoadCollectionState()
        {
            var st = LoadCollectionStateV2();
            return (st.Owned, st.Mastered);
        }

        public void SaveCollectionStateV2(
            IEnumerable<string> ownedUids,
            IEnumerable<string> masteredUids,
            IEnumerable<string> subsumedUids,
            IEnumerable<string> incarnonUids)
        {
            var oList = ownedUids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mList = masteredUids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sList = subsumedUids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var iList = incarnonUids.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            UpdateRootJson(root =>
            {
                var colObj = root["collection"] as JsonObject ?? new JsonObject();
                colObj.Remove("owned_warframes");
                colObj.Remove("owned_weapons");
                colObj.Remove("owned_ephemeras");

                static JsonArray ToArr(List<string> src)
                {
                    var a = new JsonArray();
                    foreach (var x in src) a.Add(x);
                    return a;
                }

                colObj["owned_uids"] = ToArr(oList);
                colObj["mastered_uids"] = ToArr(mList);
                colObj["subsumed_uids"] = ToArr(sList);
                colObj["incarnon_uids"] = ToArr(iList);
                root["collection"] = colObj;
            });
        }

        public void SaveCollectionState(
            IEnumerable<string> ownedItems,
            IEnumerable<string> masteredItems,
            IEnumerable<string>? subsumedWarframes = null,
            IEnumerable<string>? incarnonWeapons = null)
        {
            SaveCollectionStateV2(
                ownedItems,
                masteredItems,
                subsumedWarframes ?? Enumerable.Empty<string>(),
                incarnonWeapons ?? Enumerable.Empty<string>());
        }

        public List<string> LoadCraftQueue()
        {
            return ReadFromRoot(root =>
            {
                var list = new List<string>();
                if (root?["craft_queue"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        string? s = item?.ToString();
                        if (!string.IsNullOrEmpty(s)) list.Add(s);
                    }
                }
                return list;
            }, new List<string>());
        }

        public void SaveCraftQueue(IEnumerable<string> itemNames)
        {
            var names = itemNames.ToList();
            UpdateRootJson(root =>
            {
                var arr = new JsonArray();
                foreach (var name in names) arr.Add(name);
                root["craft_queue"] = arr;
            });
        }

        public Dictionary<string, int> LoadResourceInventory()
        {
            return ReadFromRoot(root =>
            {
                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (root?["resource_inventory"] is JsonObject obj)
                {
                    foreach (var kvp in obj)
                    {
                        if (kvp.Value != null && int.TryParse(kvp.Value.ToString(), out int val))
                            dict[kvp.Key] = val;
                    }
                }
                return dict;
            }, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }

        public void SaveResourceInventory(Dictionary<string, int> resources)
        {
            var snapshot = new Dictionary<string, int>(resources, StringComparer.OrdinalIgnoreCase);
            UpdateRootJson(root =>
            {
                var obj = new JsonObject();
                foreach (var kvp in snapshot) obj[kvp.Key] = kvp.Value;
                root["resource_inventory"] = obj;
            });
        }

        public HashSet<string> LoadCycleNotifications()
        {
            return ReadFromRoot(root =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root?["notify_cycles"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        string? s = item?.ToString();
                        if (!string.IsNullOrEmpty(s)) set.Add(s);
                    }
                }
                return set;
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        public void SaveCycleNotifications(IEnumerable<string> cycleIds)
        {
            var ids = cycleIds.ToList();
            UpdateRootJson(root =>
            {
                var arr = new JsonArray();
                foreach (var id in ids) arr.Add(id);
                root["notify_cycles"] = arr;
            });
        }

        public HashSet<string> LoadWeeklyChecklist(string weekKey)
        {
            return ReadFromRoot(root =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root?["weekly_checklist"] is JsonObject obj &&
                    string.Equals(obj["week_key"]?.ToString(), weekKey, StringComparison.OrdinalIgnoreCase) &&
                    obj["done"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        string? s = item?.ToString();
                        if (!string.IsNullOrEmpty(s)) set.Add(s);
                    }
                }
                return set;
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        public void SaveWeeklyChecklist(string weekKey, IEnumerable<string> doneIds)
        {
            var ids = doneIds.ToList();
            UpdateRootJson(root =>
            {
                var obj = new JsonObject { ["week_key"] = weekKey };
                var arr = new JsonArray();
                foreach (var id in ids) arr.Add(id);
                obj["done"] = arr;
                root["weekly_checklist"] = obj;
            });
        }

        public List<CraftQueueEntry> LoadCraftQueueEntries()
        {
            return ReadFromRoot(root =>
            {
                var result = new List<CraftQueueEntry>();
                if (root == null) return result;

                if (root["craft_queue_v2"] is JsonArray v2Arr && v2Arr.Count > 0)
                {
                    foreach (var el in v2Arr)
                    {
                        if (el is JsonObject obj)
                        {
                            string nm = obj["name"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(nm)) continue;
                            int qty = obj["qty"]?.GetValue<int>() ?? 1;
                            string uid = obj["uid"]?.ToString() ?? string.Empty;
                            result.Add(new CraftQueueEntry { Name = nm, UniqueName = uid, Quantity = Math.Clamp(qty, 1, 99) });
                        }
                    }
                    if (result.Count > 0) return result;
                }

                if (root["craft_queue"] is JsonArray legacyArr)
                {
                    foreach (var el in legacyArr)
                    {
                        string? s = el?.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            result.Add(new CraftQueueEntry { Name = s, Quantity = 1 });
                    }
                }
                return result;
            }, new List<CraftQueueEntry>());
        }

        public void SaveCraftQueueEntries(IEnumerable<CraftQueueEntry> entries)
        {
            var list = entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
            UpdateRootJson(root =>
            {
                var legacyArr = new JsonArray();
                var v2Arr = new JsonArray();
                foreach (var e in list)
                {
                    legacyArr.Add(e.Name);
                    v2Arr.Add(new JsonObject
                    {
                        ["name"] = e.Name,
                        ["uid"] = e.UniqueName,
                        ["qty"] = Math.Clamp(e.Quantity, 1, 99)
                    });
                }
                root["craft_queue"] = legacyArr;
                root["craft_queue_v2"] = v2Arr;
            });
        }

        public List<SavedBuildEntry> LoadSavedBuilds()
        {
            return ReadFromRoot(root =>
            {
                var result = new List<SavedBuildEntry>();
                if (root?["saved_builds"] is JsonArray arr)
                {
                    var list = JsonSerializer.Deserialize<List<SavedBuildEntry>>(arr.ToJsonString());
                    if (list != null) result.AddRange(list);
                }
                return result;
            }, new List<SavedBuildEntry>());
        }

        public void SaveSavedBuilds(IEnumerable<SavedBuildEntry> builds)
        {
            var snapshot = builds.ToList();
            UpdateRootJson(root => root["saved_builds"] = JsonSerializer.SerializeToNode(snapshot));
        }

        public List<MarketWatchlistItem> LoadMarketWatchlist()
        {
            return ReadFromRoot(root =>
            {
                var result = new List<MarketWatchlistItem>();
                if (root?["market_watchlist"] is JsonArray arr)
                {
                    var list = JsonSerializer.Deserialize<List<MarketWatchlistItem>>(arr.ToJsonString());
                    if (list != null) result.AddRange(list);
                }
                return result;
            }, new List<MarketWatchlistItem>());
        }

        public void SaveMarketWatchlist(IEnumerable<MarketWatchlistItem> items)
        {
            var snapshot = items.ToList();
            UpdateRootJson(root => root["market_watchlist"] = JsonSerializer.SerializeToNode(snapshot));
        }

        public List<SavedRivenEntry> LoadSavedRivens()
        {
            return ReadFromRoot(root =>
            {
                var result = new List<SavedRivenEntry>();
                if (root?["saved_rivens"] is JsonArray arr)
                {
                    var list = JsonSerializer.Deserialize<List<SavedRivenEntry>>(arr.ToJsonString());
                    if (list != null) result.AddRange(list);
                }
                return result;
            }, new List<SavedRivenEntry>());
        }

        public void SaveSavedRivens(IEnumerable<SavedRivenEntry> items)
        {
            var snapshot = items.ToList();
            UpdateRootJson(root => root["saved_rivens"] = JsonSerializer.SerializeToNode(snapshot));
        }

        public WindowPlacementData? LoadWindowPlacement()
        {
            return ReadFromRoot<WindowPlacementData?>(root =>
            {
                var wp = root?["window_placement"];
                if (wp == null) return null;
                return new WindowPlacementData
                {
                    Left = wp["left"]?.GetValue<double>() ?? double.NaN,
                    Top = wp["top"]?.GetValue<double>() ?? double.NaN,
                    Width = wp["width"]?.GetValue<double>() ?? 1380,
                    Height = wp["height"]?.GetValue<double>() ?? 860,
                    IsMaximized = wp["maximized"]?.GetValue<bool>() ?? false
                };
            }, null);
        }

        public void SaveWindowPlacement(WindowPlacementData data)
        {
            UpdateRootJson(root =>
            {
                root["window_placement"] = new JsonObject
                {
                    ["left"] = Math.Round(data.Left, 1),
                    ["top"] = Math.Round(data.Top, 1),
                    ["width"] = Math.Round(data.Width, 1),
                    ["height"] = Math.Round(data.Height, 1),
                    ["maximized"] = data.IsMaximized
                };
            });
        }

        public bool LoadExcludeFounders()
        {
            return ReadFromRoot(root => root?["collection"]?["exclude_founders"]?.GetValue<bool>() ?? false, false);
        }

        public void SaveExcludeFounders(bool exclude)
        {
            UpdateRootJson(root =>
            {
                var col = root["collection"] as JsonObject ?? new JsonObject();
                col["exclude_founders"] = exclude;
                root["collection"] = col;
            });
        }

        public (bool DesktopEnabled, bool SoundEnabled) LoadNotificationSettings()
        {
            return ReadFromRoot(root =>
            {
                var prefs = root?["notification_settings"] as JsonObject;
                bool desktop = prefs?["desktop_enabled"]?.GetValue<bool>() ?? true;
                bool sound = prefs?["sound_enabled"]?.GetValue<bool>() ?? true;
                return (desktop, sound);
            }, (true, true));
        }

        public void SaveNotificationSettings(bool desktopEnabled, bool soundEnabled)
        {
            UpdateRootJson(root =>
            {
                root["notification_settings"] = new JsonObject
                {
                    ["desktop_enabled"] = desktopEnabled,
                    ["sound_enabled"] = soundEnabled
                };
            });
        }
    }

    public class WindowPlacementData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; } = 1380;
        public double Height { get; set; } = 860;
        public bool IsMaximized { get; set; }
    }

    public class CraftQueueEntry
    {
        public string Name { get; set; } = string.Empty;
        public string UniqueName { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
    }
}
