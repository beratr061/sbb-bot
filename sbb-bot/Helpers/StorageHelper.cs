using System.Text.Json;

namespace SbbBot.Helpers;

public static class StorageHelper
{
    private static readonly string DataPath = Environment.GetEnvironmentVariable("STORAGE_PATH") 
                                              ?? Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string SchedulePath = Path.Combine(DataPath, "schedules");

    public static string GetDataPath() => DataPath;

    /// <summary>Updates the modification time of a file in DataPath without changing content. Creates it if missing.</summary>
    public static void TouchDataFile(string fileName)
    {
        var path = Path.Combine(DataPath, fileName);
        if (File.Exists(path))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        else
            File.WriteAllText(path, "");
    }

    static StorageHelper()
    {
        if (!Directory.Exists(DataPath))
        {
            Directory.CreateDirectory(DataPath);
        }
        if (!Directory.Exists(SchedulePath))
        {
            Directory.CreateDirectory(SchedulePath);
        }
    }
    
    /// <summary>Returns how long ago (in hours) a data file was last written. Returns double.MaxValue if file doesn't exist.</summary>
    public static double GetDataFileAgeHours(string fileName)
    {
        var path = Path.Combine(DataPath, fileName);
        if (!File.Exists(path)) return double.MaxValue;
        return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalHours;
    }

    /// <summary>Returns how long ago (in minutes) a data file was last written. Returns double.MaxValue if file doesn't exist.</summary>
    public static double GetDataFileAgeMinutes(string fileName)
    {
        var path = Path.Combine(DataPath, fileName);
        if (!File.Exists(path)) return double.MaxValue;
        return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMinutes;
    }

    public static async Task<BusSchedule?> ReadScheduleAsync(string lineName, string subfolder)
    {
        // Sanitize filename
        var safeName = string.Join("_", lineName.Split(Path.GetInvalidFileNameChars()));
        var folderPath = Path.Combine(SchedulePath, subfolder);
        var path = Path.Combine(folderPath, $"{safeName}.json");
        
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path);
        var result = JsonSerializer.Deserialize<BusSchedule>(json);
        
        // Fallback for files where LastChecked might be default
        if (result != null && result.LastChecked == default)
        {
            result.LastChecked = File.GetLastWriteTimeUtc(path);
        }
        
        return result;
    }

    public static async Task SaveScheduleAsync(BusSchedule schedule, string subfolder)
    {
        var safeName = string.Join("_", schedule.LineName.Split(Path.GetInvalidFileNameChars()));
        var folderPath = Path.Combine(SchedulePath, subfolder);
        
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var path = Path.Combine(folderPath, $"{safeName}.json");
        
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(schedule, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<string?> ReadLastNewsAsync()
    {
        var path = Path.Combine(DataPath, "last_news.txt");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    public static async Task SaveLastNewsAsync(string title)
    {
        var path = Path.Combine(DataPath, "last_news.txt");
        await File.WriteAllTextAsync(path, title);
    }

    public static async Task<HashSet<string>> ReadDocumentsAsync()
    {
        var path = Path.Combine(DataPath, "documents.json");
        if (!File.Exists(path)) return new HashSet<string>();
        
        var json = await File.ReadAllTextAsync(path);
        var list = JsonSerializer.Deserialize<List<string>>(json);
        return list != null ? new HashSet<string>(list) : new HashSet<string>();
    }

    public static async Task SaveDocumentsAsync(HashSet<string> documents)
    {
        var path = Path.Combine(DataPath, "documents.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(documents, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<BusLinesData> ReadBusLinesAsync()
    {
        var path = Path.Combine(DataPath, "bus_lines.json");
        if (!File.Exists(path)) return new BusLinesData();

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<BusLinesData>(json) ?? new BusLinesData();
    }

    public static async Task SaveBusLinesAsync(BusLinesData data)
    {
        var path = Path.Combine(DataPath, "bus_lines.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(data, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<HashSet<string>> ReadMeetingsAsync()
    {
        var path = Path.Combine(DataPath, "meetings.json");
        if (!File.Exists(path)) return new HashSet<string>();

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
    }

    public static async Task SaveMeetingsAsync(HashSet<string> meetings)
    {
        var path = Path.Combine(DataPath, "meetings.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(meetings, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<HashSet<string>> ReadUkomeDecisionsAsync()
    {
        var path = Path.Combine(DataPath, "ukome.json");
        if (!File.Exists(path)) return new HashSet<string>();

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
    }

    public static async Task SaveUkomeDecisionsAsync(HashSet<string> decisions)
    {
        var path = Path.Combine(DataPath, "ukome.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(decisions, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<HashSet<string>> ReadUkomeYearsAsync()
    {
        var path = Path.Combine(DataPath, "ukome_years.json");
        if (!File.Exists(path)) return new HashSet<string>();

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
    }

    public static async Task SaveUkomeYearsAsync(HashSet<string> years)
    {
        var path = Path.Combine(DataPath, "ukome_years.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(years, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<HashSet<string>> ReadNewsHistoryAsync()
    {
        var path = Path.Combine(DataPath, "news.json");
        if (!File.Exists(path)) return new HashSet<string>();

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
    }

    public static async Task SaveNewsHistoryAsync(HashSet<string> news)
    {
        var path = Path.Combine(DataPath, "news.json");
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(news, options);
        await File.WriteAllTextAsync(path, json);
    }

    public static async Task<string?> ReadStateAsync(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    public static async Task SaveStateAsync(string relativePath, string content)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content);
    }

    public static async Task<Models.RouteResponse?> ReadRouteDataAsync(string lineId)
    {
        var path = Path.Combine(DataPath, "routes", $"{lineId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        var result = JsonSerializer.Deserialize<Models.RouteResponse>(json);
        
        // Fallback for files saved before LastChecked was added
        if (result != null && result.LastChecked == default)
        {
            result.LastChecked = File.GetLastWriteTimeUtc(path);
        }
        
        return result;
    }

    public static async Task SaveRouteDataAsync(Models.RouteResponse data)
    {
        var path = Path.Combine(DataPath, "routes", $"{data.LineId}.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        var json = JsonSerializer.Serialize(data, options);
        await File.WriteAllTextAsync(path, json);
    }


}

public class BusLinesData
{
    public List<string> ozel_halk { get; set; } = new();
    public List<string> belediye { get; set; } = new();
    public List<string> taksi_dolmus { get; set; } = new();
    public List<string> minibus { get; set; } = new();
}

public class BusSchedule
{
    public int LineId { get; set; }
    public string LineName { get; set; } = "";
    public string Url { get; set; } = "";
    public string LastScheduleHash { get; set; } = "";
    public string LastAlertHash { get; set; } = "";
    public Dictionary<string, List<string>> DayTimes { get; set; } = new(); 
    public DateTime LastChecked { get; set; }
}
