using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ScreenMaster.Core
{
    /// <summary>
    /// City-level location from the internet address (IP), only when you ask for it.
    /// Two public services, the second as fallback; nothing but the request itself is sent.
    /// </summary>
    public static class GeoIp
    {
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenMaster/1.0");
            return c;
        }

        public static async Task<Location> Lookup()
        {
            Exception? last = null;
            foreach (var source in new Func<Task<Location?>>[] { IpInfo, IpApi })
            {
                try
                {
                    var loc = await source().ConfigureAwait(false);
                    if (loc != null) return loc;
                }
                catch (Exception ex) { last = ex; Log.Write("geoip: " + ex.Message); }
            }
            throw new InvalidOperationException(last is HttpRequestException or TaskCanceledException
                ? "Sem resposta da internet. Confira a conexão e tente de novo."
                : "Não foi possível descobrir a localização agora. Tente de novo mais tarde.");
        }

        static async Task<Location?> IpInfo()
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipinfo.io/json").ConfigureAwait(false));
            var r = doc.RootElement;
            if (!r.TryGetProperty("loc", out var locEl) || locEl.GetString() is not { } loc) return null;
            var parts = loc.Split(',');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;
            return Make(Str(r, "city"), Str(r, "region"), lat, lon);
        }

        static async Task<Location?> IpApi()
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipapi.co/json/").ConfigureAwait(false));
            var r = doc.RootElement;
            if (!r.TryGetProperty("latitude", out var la) || la.ValueKind != JsonValueKind.Number
                || !r.TryGetProperty("longitude", out var lo) || lo.ValueKind != JsonValueKind.Number) return null;
            return Make(Str(r, "city"), Str(r, "region"), la.GetDouble(), lo.GetDouble());
        }

        static string? Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static Location? Make(string? city, string? region, double lat, double lon)
        {
            if (lat is < -90 or > 90 || lon is < -180 or > 180 || (lat == 0 && lon == 0)) return null;
            var name = string.Join(", ", new[] { city, region }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new Location { Name = name.Length > 0 ? name : "Minha localização", Lat = Math.Round(lat, 3), Lon = Math.Round(lon, 3) };
        }
    }
}
