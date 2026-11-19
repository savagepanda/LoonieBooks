using MapleLedger.Data;

namespace MapleLedger.ApiService.Users;

/// <summary>
/// Guards every ledger endpoint: only premium users may keep data on the server, because server
/// storage has an operating cost. Everyone else keeps their ledger in their own browser. Also turns
/// the ledger's validation failures into 400 responses.
/// </summary>
public sealed class PremiumLedgerFilter(UserService users, CurrentUser user) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await users.IsPremiumAsync(user, context.HttpContext.RequestAborted))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "This account can't store a ledger on this server",
                detail: "Your ledger is stored in your browser. Ask whoever runs this server to allow your account to store it here.");
        }

        try
        {
            return await next(context);
        }
        catch (LedgerValidationException ex)
        {
            return Results.ValidationProblem(ex.Errors.ToDictionary(e => e.Key, e => e.Value));
        }
    }
}
