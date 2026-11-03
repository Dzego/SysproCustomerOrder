using System.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace Syspro.Api.ExceptionHandling;

public static class ProblemDetailsCustomization
{
    // Runs for every ProblemDetails response, whichever component produced it:
    // the exception handler, status code pages, or MVC validation results.
    public static void Apply(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var httpContext = context.HttpContext;

        // The framework only fills these in for common statuses. These are the
        // defaults RFC 7807 defines, so that statuses such as 413 match the rest.
        problem.Type ??= "about:blank";
        problem.Title ??= ReasonPhrases.GetReasonPhrase(
            problem.Status ?? httpContext.Response.StatusCode);

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = GetTraceId(httpContext);
    }

    public static string GetTraceId(HttpContext httpContext)
    {
        return Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }
}
