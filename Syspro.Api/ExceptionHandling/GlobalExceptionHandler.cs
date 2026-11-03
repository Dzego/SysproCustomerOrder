using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Syspro.Api.Services;

namespace Syspro.Api.ExceptionHandling;

// Client errors are mapped from exception types declared for that purpose only.
// ArgumentException and KeyNotFoundException are deliberately not mapped: the
// framework and libraries also throw them for server-side faults, which must
// surface as 500 rather than be blamed on the client.
//
// ExceptionHandlerMiddleware handles a top-level OperationCanceledException itself
// when RequestAborted is signalled, but SqlClient can report the same disconnect
// as a SqlException, and EF Core can wrap either one, so those arrive here. A
// disconnect needs both the signal and a cancellation exception: any other
// exception is a genuine failure, even if the client also left, and a cancellation
// without the signal is a server-side timeout. Both are logged as errors.
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private const string UnexpectedErrorDetail =
        "An unexpected error occurred. Quote the traceId when reporting this problem.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        IHostEnvironment environment,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _environment = environment;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.RequestAborted.IsCancellationRequested &&
            CancellationExceptionClassifier.IsCancellation(exception))
        {
            LogClientDisconnect(httpContext, exception);

            // Matches what the middleware records for a disconnect. No client is
            // left to receive this status or a response body.
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;

            return true;
        }

        var problem = CreateProblem(exception);

        LogException(httpContext, exception, problem.Status!.Value);

        httpContext.Response.StatusCode = problem.Status.Value;

        // The result is ignored on purpose. It is false when the Accept header
        // rules out JSON, and returning false would make the middleware replace
        // this status code with a 500. The client still gets the right status.
        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });

        return true;
    }

    private ProblemDetails CreateProblem(Exception exception)
    {
        return exception switch
        {
            BusinessRuleException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Detail = exception.Message
            },
            ResourceNotFoundException => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Detail = exception.Message
            },
            // Raised by the server for a malformed or oversized request body,
            // carrying the status code that describes the failure.
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Detail = badRequest.Message
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Detail = _environment.IsDevelopment()
                    ? exception.ToString()
                    : UnexpectedErrorDetail
            }
        };
    }

    private void LogClientDisconnect(HttpContext httpContext, Exception exception)
    {
        _logger.LogInformation(
            "Request {Method} {Path} was cancelled because the client disconnected ({ExceptionType}). TraceId: {TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            exception.GetType().Name,
            ProblemDetailsCustomization.GetTraceId(httpContext));
    }

    private void LogException(HttpContext httpContext, Exception exception, int statusCode)
    {
        var traceId = ProblemDetailsCustomization.GetTraceId(httpContext);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Request {Method} {Path} failed with status {StatusCode}. TraceId: {TraceId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                statusCode,
                traceId);

            return;
        }

        _logger.LogInformation(
            "Request {Method} {Path} was rejected with status {StatusCode}: {Reason}. TraceId: {TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            statusCode,
            exception.Message,
            traceId);
    }
}
