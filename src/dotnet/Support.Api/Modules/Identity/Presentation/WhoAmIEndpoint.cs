using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Identity.Presentation;

internal static class WhoAmIEndpoint
{
    /// <summary>
    /// Development-only diagnostic: shows which principal the server uses. It ignores every header,
    /// query string, and body value, which is exactly what the identity tests check.
    /// </summary>
    public static IEndpointRouteBuilder MapWhoAmI(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/whoami", (IPrincipalSource principals) => new { customer_id = principals.Current.CustomerId });
        return app;
    }
}
