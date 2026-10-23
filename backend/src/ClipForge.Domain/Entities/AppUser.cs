using Microsoft.AspNetCore.Identity;

namespace ClipForge.Domain.Entities;

/// <summary>Identity user (table users). Guid keys.</summary>
public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? DeletionRequestedAt { get; set; }
}
