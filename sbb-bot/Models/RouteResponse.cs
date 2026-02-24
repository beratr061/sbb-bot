using System.Text.Json.Serialization;

namespace SbbBot.Models;

public class RouteResponse
{
    [JsonPropertyName("lineId")]
    public int LineId { get; set; }

    [JsonPropertyName("lineName")]
    public string LineName { get; set; } = "";

    [JsonPropertyName("lineDetail")]
    public object? LineDetail { get; set; }

    [JsonPropertyName("typeValueId")]
    public int TypeValueId { get; set; }

    [JsonPropertyName("lineNumber")]
    public string LineNumber { get; set; } = "";

    [JsonPropertyName("routes")]
    public List<RouteDetail> Routes { get; set; } = new();

    [JsonPropertyName("ekentLineIntegrationId")]
    public object? EkentLineIntegrationId { get; set; }

    /// <summary>Timestamp of last successful data fetch (UTC)</summary>
    [JsonPropertyName("lastChecked")]
    public DateTime LastChecked { get; set; }
}

public class RouteDetail
{
    [JsonPropertyName("routeId")]
    public int RouteId { get; set; }

    [JsonPropertyName("routeName")]
    public string RouteName { get; set; } = "";

    [JsonPropertyName("routeGeometry")]
    public RouteGeometry? RouteGeometry { get; set; }

    [JsonPropertyName("busStops")]
    public List<BusStop> BusStops { get; set; } = new();

    [JsonPropertyName("routeTypeId")]
    public int RouteTypeId { get; set; }

    [JsonPropertyName("startLocation")]
    public string StartLocation { get; set; } = "";

    [JsonPropertyName("endLocation")]
    public string EndLocation { get; set; } = "";
}

public class RouteGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("coordinates")]
    public List<List<List<double>>> Coordinates { get; set; } = new();
}

public class BusStop
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("busStopGeometry")]
    public BusStopGeometry? BusStopGeometry { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("busStopTypeName")]
    public string BusStopTypeName { get; set; } = "";

    [JsonPropertyName("busStopTypeId")]
    public int BusStopTypeId { get; set; }

    [JsonPropertyName("isSmartStop")]
    public bool IsSmartStop { get; set; }

    [JsonPropertyName("busStopNumber")]
    public int? BusStopNumber { get; set; }
}

public class BusStopGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("coordinates")]
    public List<double> Coordinates { get; set; } = new();
}
