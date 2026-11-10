using System.Globalization;
using System.Text.Json;
using Satchel.Data;

namespace Satchel.Services;

public sealed record WeatherLocation(string Name, string Region, string Country, double Latitude, double Longitude)
{
    public string Display => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public sealed record WeatherDay(DateTime Date, int Code, double Max, double Min, int RainChance)
{
    public string DayName => Date.Date == DateTime.Today ? "Today" : Date.ToString("ddd", CultureInfo.CurrentCulture);
    public string Kind => WeatherService.KindOf(Code, true);
}

public sealed record WeatherReport(WeatherLocation Location, double Temperature, double FeelsLike, int Code, bool IsDay, double Wind, int Humidity,
    IReadOnlyList<WeatherDay> Days, DateTime FetchedAt)
{
    public string Kind => WeatherService.KindOf(Code, IsDay);
    public string Description => WeatherService.Describe(Code);
}

/// <summary>Weather from Open-Meteo (free, no account or key). The location is kept encrypted.</summary>
public sealed class WeatherService
{
    private const string LocationKey = "weather.location";
    private readonly DataStore _store;
    private readonly WebService _web;
    private readonly SettingsService _settings;
    private WeatherReport? _cache;
    private Task<WeatherReport?>? _inflight;

    public WeatherService(DataStore store, WebService web, SettingsService settings)
    {
        _store = store;
        _web = web;
        _settings = settings;
        _settings.Changed += name => { if (name == nameof(AppSettings.WeatherFahrenheit)) _cache = null; };
    }

    public string Unit => _settings.Current.WeatherFahrenheit ? "°F" : "°C";
    public bool IsOnline => _web.IsAvailable;

    public event Action? LocationChanged;

    public async Task<WeatherLocation?> GetLocationAsync()
    {
        try
        {
            var json = await _store.GetSecureAsync(LocationKey);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WeatherLocation>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetLocationAsync(WeatherLocation? location)
    {
        await _store.SetSecureAsync(LocationKey, location == null ? null : JsonSerializer.Serialize(location));
        _cache = null;
        LocationChanged?.Invoke();
    }

    public async Task<List<WeatherLocation>> SearchAsync(string query)
    {
        var results = new List<WeatherLocation>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        var json = await _web.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=8&language=en&format=json");
        if (json == null) return results;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var list)) return results;
            foreach (var r in list.EnumerateArray())
            {
                string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (!r.TryGetProperty("latitude", out var lat) || !r.TryGetProperty("longitude", out var lon)) continue;
                results.Add(new WeatherLocation(S("name"), S("admin1"), S("country"), lat.GetDouble(), lon.GetDouble()));
            }
        }
        catch (JsonException) { }
        return results;
    }

    /// <summary>Current weather and a 5-day forecast (cached for 20 minutes).</summary>
    public async Task<WeatherReport?> GetAsync(bool force = false)
    {
        if (!force && _cache != null && (DateTime.Now - _cache.FetchedAt).TotalMinutes < 20) return _cache;
        _inflight ??= FetchAsync();
        try
        {
            return await _inflight;
        }
        catch
        {
            return _cache;
        }
        finally
        {
            _inflight = null;
        }
    }

    /// <summary>The last report, if any (no network).</summary>
    public WeatherReport? Cached => _cache;

    private async Task<WeatherReport?> FetchAsync()
    {
        var location = await GetLocationAsync();
        if (location == null || !_web.IsAvailable) return _cache;
        var unit = _settings.Current.WeatherFahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
        var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}") +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=6" + unit;
        var json = await _web.GetStringAsync(url);
        if (json == null) return _cache;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var days = new List<WeatherDay>();
            if (root.TryGetProperty("daily", out var daily))
            {
                var dates = daily.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, CultureInfo.InvariantCulture)).ToList();
                var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList();
                var max = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList();
                var min = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList();
                var rain = daily.TryGetProperty("precipitation_probability_max", out var pr)
                    ? pr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList() : [];
                for (int i = 0; i < dates.Count; i++)
                    days.Add(new WeatherDay(dates[i], codes.ElementAtOrDefault(i), max.ElementAtOrDefault(i), min.ElementAtOrDefault(i), rain.ElementAtOrDefault(i)));
            }
            _cache = new WeatherReport(location, D(cur, "temperature_2m"), D(cur, "apparent_temperature"), (int)D(cur, "weather_code"), D(cur, "is_day") > 0,
                D(cur, "wind_speed_10m"), (int)D(cur, "relative_humidity_2m"), days, DateTime.Now);
            return _cache;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return _cache;
        }
    }

    /// <summary>Icon family for a WMO weather code: clear, night, partly, cloudy, fog, drizzle, rain, snow, storm.</summary>
    public static string KindOf(int code, bool isDay) => code switch
    {
        0 => isDay ? "clear" : "night",
        1 or 2 => isDay ? "partly" : "partly-night",
        3 => "cloudy",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "rain",
        71 or 73 or 75 or 77 or 85 or 86 => "snow",
        95 or 96 or 99 => "storm",
        _ => "cloudy"
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Rain showers",
        82 => "Violent showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Cloudy"
    };
}
