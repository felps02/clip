using System.Security.Claims;
using ClipForge.Domain.Common;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ClipForge.Api.Common;

public static class ClaimsExtensions
{
    /// <summary>User id from the "sub" (or NameIdentifier) claim. Throws 401 when missing.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(value, out var id)) return id;
        throw AppException.Unauthorized();
    }

    public static Guid? TryGetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
