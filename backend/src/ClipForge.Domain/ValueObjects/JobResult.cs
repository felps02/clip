using System.Text.Json;

namespace ClipForge.Domain.ValueObjects;

/// <summary>Summary stored in processing_jobs.result (jsonb).</summary>
public class JobResult
{
    public int? CandidatesFound { get; set; }
    public int? ClipsRendered { get; set; }
    public decimal? ProcessedSeconds { get; set; }
    /// <summary>Signal availability as reported by the worker, e.g. { "audio": { "available": true }, ... }.</summary>
    public JsonElement? Signals { get; set; }
    public WebcamRegion? WebcamRegion { get; set; }
    public string? Language { get; set; }
}

/// <summary>Input of a render_clip job (processing_jobs.input).</summary>
public class RenderJobInput
{
    public Guid ClipId { get; set; }
    public Guid RenderId { get; set; }
}
