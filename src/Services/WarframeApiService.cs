using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WarfAssistant.Services
{
    public class WarframeApiService
    {
        private static readonly HttpClient _http;

        static WarframeApiService()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WarfAssistant/2.0");
        }

        private const string BaseApiUrl = "https://api.warframestat.us/pc";

        public static async Task<JsonDocument?> GetJsonAsync(string endpoint)
        {
            string url = string.IsNullOrEmpty(endpoint)
                ? $"{BaseApiUrl}?language=ru"
                : $"{BaseApiUrl}/{endpoint}";
            return await GetJsonFromUrlAsync(url).ConfigureAwait(false);
        }

        public static async Task<JsonDocument?> GetFullWorldstateAsync(string language = "ru")
        {
            string url = $"{BaseApiUrl}?language={Uri.EscapeDataString(language)}";
            return await GetJsonFromUrlAsync(url, timeoutSeconds: 7).ConfigureAwait(false);
        }

        public static async Task<JsonDocument?> GetJsonFromUrlAsync(string url, int timeoutSeconds = 15)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    return JsonDocument.Parse(content);
                }
            }
            catch
            {
                // Graceful fallback on network failure / offline
            }
            return null;
        }

        public static async Task<string?> GetStringAsync(string url, int timeoutSeconds = 15)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            return null;
        }

        public static DateTime ParseIsoOrFallback(string? iso, TimeSpan fallbackDelta)
        {
            if (!string.IsNullOrEmpty(iso) &&
                DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToUniversalTime();
            }
            return DateTime.UtcNow.Add(fallbackDelta);
        }

        public static bool TryParseIso(string? iso, out DateTime utcResult)
        {
            if (!string.IsNullOrEmpty(iso) &&
                DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                utcResult = dt.ToUniversalTime();
                return utcResult.Year > 2000 && utcResult.Year < 2100;
            }
            utcResult = DateTime.UtcNow;
            return false;
        }
    }
}
