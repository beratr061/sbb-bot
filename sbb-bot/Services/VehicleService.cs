using System.Text.Json;
using System.Text.Json.Serialization;
using SbbBot.Helpers;
using SbbBot.Models;

namespace SbbBot.Services;

public class VehicleService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VehicleService> _logger;
    private Dictionary<int, int> _sbbToAsisMap = new();
    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/VehicleTracking?AsisId={0}";

    public VehicleService(IHttpClientFactory httpClientFactory, ILogger<VehicleService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        LoadMap();
    }

    private void LoadMap()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "asis_map.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<Dictionary<string, AsisMapEntry>>(json);
                if (root != null)
                {
                    foreach (var kvp in root)
                    {
                        if (int.TryParse(kvp.Key, out int asisId))
                        {
                            // Map SbbId -> AsisId
                            if (!_sbbToAsisMap.ContainsKey(kvp.Value.sbb_id))
                            {
                                _sbbToAsisMap[kvp.Value.sbb_id] = asisId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load asis_map.json");
            }
        }
    }

    public async Task<List<VehicleLocation>> GetVehicleLocationsAsync(int lineId)
    {
        if (!_sbbToAsisMap.TryGetValue(lineId, out int asisId))
        {
            return new List<VehicleLocation>();
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiUrl, asisId));
            request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<VehicleLocation>();

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<VehicleLocation>>(json, options) ?? new List<VehicleLocation>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching vehicles for line {lineId} (Asis: {asisId})");
            return new List<VehicleLocation>();
        }
    }

    private class AsisMapEntry
    {
        public string line_no { get; set; } = "";
        public string name { get; set; } = "";
        public int sbb_id { get; set; }
    }
}

public class VehicleLocation
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string BusNumber { get; set; } = "";
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LineNumber { get; set; } = "";
    public LocationDetail Location { get; set; } = new();
    public double Speed { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string CurrentStopName { get; set; } = "";
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string NextStopName { get; set; } = "";
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Status { get; set; } = "";
    public double? DistNextStopMeter { get; set; }
}

/// <summary>Reads JSON values as string regardless of whether they are string, number, or bool tokens.</summary>
public class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public class LocationDetail
{
    public double[] Coordinates { get; set; } = []; // [lon, lat]
}
