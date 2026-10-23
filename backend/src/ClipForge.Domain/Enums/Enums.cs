namespace ClipForge.Domain.Enums;

// All enums are stored as snake_case lower strings in the database and serialized the same way in the API.

public enum SubscriptionStatus { Trialing, Active, PastDue, Cancelled, Expired }

public enum SubscriptionProvider { None, Stripe, Mercadopago }

public enum SubscriptionEventType { Created, PlanChanged, Renewed, PaymentFailed, Cancelled, Reactivated, Expired }

public enum EventSource { System, User, Webhook, Admin }

public enum UsageKind { ProcessingSeconds, Render, StorageBytes, AssistantMessage }

public enum PaymentEventStatus { Received, Processed, Ignored, Failed }

public enum MediaAssetKind { SourceUpload, ClipRender, Thumbnail, Analysis, LiveSegment }

public enum MediaAssetStatus { Pending, Uploaded, Verified, Failed, Deleted }

public enum SourceKind { Upload, Link, Live }

public enum SourcePlatform { Upload, Twitch, Youtube, Kick, Other }

public enum DetectedPlatform { Twitch, Youtube, Kick, Unknown }

public enum ProjectStatus { Created, Processing, Ready, Failed, Cancelled }

public enum JobKind { AnalyzeSource, RenderClip, LiveSegment }

public enum JobStatus
{
    Pending,
    Queued,
    PreparingMedia,
    Analyzing,
    DetectingMoments,
    GeneratingClips,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

public enum JobEventLevel { Info, Warning, Error }

public enum ClipCategory { Highlight, Reaction, Funny, Hype, Tension, Gameplay, Quote }

public enum CandidateStatus { Detected, Selected, Rendered, Discarded }

public enum ClipStatus { Pending, Rendering, Ready, Failed }

public enum RenderStatus { Pending, Rendering, Ready, Failed }

public enum IntegrationProvider { Twitch, Youtube, ObsCompanion }

public enum IntegrationStatus { Connected, Revoked, Error }

public enum NotificationType { JobCompleted, JobFailed, ClipReady, QuotaWarning, Billing, System }
