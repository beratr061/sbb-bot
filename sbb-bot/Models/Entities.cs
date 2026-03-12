namespace SbbBot.Models;

public class BusLine
{
    public int ApiId { get; set; }
    public string LineNumber { get; set; } = "";
    public string LineName { get; set; } = "";
    public string BusType { get; set; } = "";
    public string RawJson { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class Fare
{
    public string LineNumber { get; set; } = "";
    public decimal FullFare { get; set; }
    public decimal StudentFare { get; set; }
    public decimal DiscountedFare { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class Announcement
{
    public string AnnouncementId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string ContentHash { get; set; } = "";
    public string RawJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
