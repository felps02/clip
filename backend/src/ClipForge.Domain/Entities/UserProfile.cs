using ClipForge.Domain.ValueObjects;

namespace ClipForge.Domain.Entities;

public class UserProfile
{
    public Guid UserId { get; set; }
    public string Locale { get; set; } = "pt-BR";
    public string Timezone { get; set; } = "America/Sao_Paulo";
    public EditingPreferences EditingPreferences { get; set; } = new();
    public NotificationPreferences NotificationPreferences { get; set; } = new();
    public DateTimeOffset? AcceptedTermsAt { get; set; }
    public bool MarketingConsent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedByIp { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
