using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TransactionalOutbox.Web;

public sealed class RequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Minimal APIs throw BadHttpRequestException for bad JSON/binding in Development (ThrowOnBadRequest);
        // handling it here keeps the response a 400 problem document in every environment.
        var (status, title, detail) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request", bad.Message),
            ArgumentException argument => (StatusCodes.Status400BadRequest, "Invalid request", argument.Message),
            _ => (0, null, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}
