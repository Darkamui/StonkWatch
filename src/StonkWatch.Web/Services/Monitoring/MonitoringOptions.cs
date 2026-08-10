using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.Monitoring;

public class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// Off by default so a developer running the app locally never emails anyone. Production
    /// opts in explicitly.
    /// </summary>
    public bool Enabled { get; set; }

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Ignore market hours. For local testing only.</summary>
    public bool IgnoreMarketHours { get; set; }

    /// <summary>
    /// How far price must move back past a level before a fired alert can fire again,
    /// as a percentage of the level. Stops a ticker oscillating on its trigger from
    /// emailing every tick.
    /// </summary>
    [Range(0, 100)]
    public decimal ReArmPercent { get; set; } = 0.5m;

    /// <summary>Minimum gap between two notifications for the same alert.</summary>
    [Range(0, 168)]
    public int MinNotifyHours { get; set; } = 6;
}

public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Absolute base URL used to build links in emails, e.g. https://stonks.example.com.</summary>
    public string? PublicBaseUrl { get; set; }
}
