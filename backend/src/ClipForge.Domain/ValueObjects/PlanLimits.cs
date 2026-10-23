using System.Text.Json.Serialization;

namespace ClipForge.Domain.ValueObjects;

/// <summary>Configurable limits of a plan (stored as jsonb in plans.limits).</summary>
public class PlanLimits
{
    public int MonthlyProcessingMinutes { get; set; } = 60;
    public int MaxSourceDurationMinutes { get; set; } = 120;
    public int MaxFileSizeMb { get; set; } = 2048;
    /// <summary>0 = unlimited.</summary>
    public int MaxActiveProjects { get; set; } = 3;
    public int MaxCandidatesPerJob { get; set; } = 15;
    public int MaxAutoRenderPerJob { get; set; } = 15;
    /// <summary>0 = unlimited.</summary>
    public int MaxRendersPerMonth { get; set; } = 10;
    public int MaxRenderHeight { get; set; } = 720;
    public int MaxConcurrentJobs { get; set; } = 1;
    public int Priority { get; set; }
    public int RetentionDays { get; set; } = 7;
    public int StorageQuotaGb { get; set; } = 5;
    public string TranscriptionModel { get; set; } = "base";
    public bool AllowWebcamLayout { get; set; }
    public bool AllowCustomSubtitleStyles { get; set; }
    /// <summary>Assistant chat messages per billing period (usage kind assistant_message). 0 = unlimited.</summary>
    public int AssistantMessagesPerMonth { get; set; } = 100;

    [JsonIgnore]
    public long MonthlyProcessingSeconds => (long)MonthlyProcessingMinutes * 60;
    [JsonIgnore]
    public long MaxSourceDurationSeconds => (long)MaxSourceDurationMinutes * 60;
    [JsonIgnore]
    public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;
    [JsonIgnore]
    public long StorageQuotaBytes => (long)StorageQuotaGb * 1024 * 1024 * 1024;

    public PlanLimits Clone() => (PlanLimits)MemberwiseClone();
}
