using System.Text.Json;

namespace SbbBot.Helpers;

/// <summary>
/// Minimal storage helper. Legacy static methods have been removed —
/// all persistence now flows through the typed repository classes.
/// Only the BusSchedule model (still referenced by BusLineWatcherService
/// and InteractionManager) remains here.
/// </summary>
public static class StorageHelper
{
    // Kept for backward compatibility with Program.cs Initialize call,
    // but no longer used for data access.
    private static IDbConnectionFactory? _dbFactory;
    public static void Initialize(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }
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
