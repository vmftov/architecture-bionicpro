using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace ReportsApi.ClaimsTransformations;

public class KeycloakClaimsTransformation : IClaimsTransformation {
#if DEBUG_AUTH
    private readonly ILogger<KeycloakClaimsTransformation> _logger;

    public KeycloakClaimsTransformation(ILogger<KeycloakClaimsTransformation> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
#endif

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) {
        // Keycloak claims:
        // {
        //    "name": "realm_access",
        //    "value": "{\"roles\":[\"user\"]}"
        // }
        AddRoles(principal);
        return Task.FromResult(principal);
    }

    private void AddRoles(ClaimsPrincipal principal) {
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (realmAccess is null || principal.Identity is not ClaimsIdentity identity) {
            return;
        }
        using var doc = JsonDocument.Parse(realmAccess);
        if (!doc.RootElement.TryGetProperty("roles", out var roles)) {
            return;
        }

#if DEBUG_AUTH
        _logger.LogInformation($"Search roles for {string.Join("    \r\n", principal.Claims.Select(v => $"{v.Type}={v.Value}"))}");
#endif
        foreach (var role in roles.EnumerateArray()) {
            if (role.ValueKind == JsonValueKind.String && role.GetString() is { } roleName && !identity.HasClaim(ClaimTypes.Role, roleName)) {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
#if DEBUG_AUTH
                _logger.LogInformation($"Role {roleName} added");
#endif
            }
        }
    }
}