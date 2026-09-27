using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace TaskHive.Web.Services;

/// <summary>
/// Resolves the signed-in user for the current Blazor circuit.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<string> GetIdAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("No authenticated user in the current circuit.");
    }
}
