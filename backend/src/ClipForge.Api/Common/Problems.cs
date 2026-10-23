using System.Diagnostics;
using ClipForge.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Common;

/// <summary>RFC 9457 problem details with a machine-readable "code" extension.</summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static ProblemDetails Create(int status, string code, string? detail, IDictionary<string, string[]>? errors = null, string? instance = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"urn:clipforge:error:{code}",
            Title = ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : "Error",
            Status = status,
            Detail = detail,
            Instance = instance,
        };
        problem.Extensions["code"] = code;
        if (errors is { Count: > 0 }) problem.Extensions["errors"] = errors;
        return problem;
    }

    public static IResult Result(int status, string code, string? detail, IDictionary<string, string[]>? errors = null)
    {
        var problem = Create(status, code, detail, errors);
        return Results.Problem(problem);
    }

    /// <param name="headers">Headers of this answer (e.g. Retry-After), set after the response is cleared.</param>
    public static async Task WriteAsync(HttpContext context, int status, string code, string? detail, IDictionary<string, string[]>? errors = null, Action<IHeaderDictionary>? headers = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        headers?.Invoke(context.Response.Headers);
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        var problem = Create(status, code, detail, errors, context.Request.Path);
        if (Activity.Current?.Id is { } traceId) problem.Extensions["traceId"] = traceId;
        await context.Response.WriteAsJsonAsync(problem, ApiJson.Options, ContentType);
    }
}

/// <summary>Maps exceptions to problem details. AppException → its status/code; validation → 400; others → 500.</summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case AppException app:
                if (app.Status == StatusCodes.Status503ServiceUnavailable) _logger.LogWarning("Service unavailable {Code}: {Message}", app.Code, app.Message);
                else if (app.Status >= 500) _logger.LogError(app, "Application error {Code}", app.Code);
                await Problems.WriteAsync(context, app.Status, app.Code, app.Message, app.Errors);
                return true;

            case FluentValidation.ValidationException validation:
            {
                var errors = validation.Errors
                    .GroupBy(e => ToCamel(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                await Problems.WriteAsync(context, 400, ErrorCodes.ValidationFailed, "Dados inválidos.", errors);
                return true;
            }

            case BadHttpRequestException bad:
            {
                var status = bad.StatusCode;
                var code = status == StatusCodes.Status413PayloadTooLarge ? "payload_too_large" : ErrorCodes.ValidationFailed;
                var detail = status == StatusCodes.Status413PayloadTooLarge ? "Corpo da requisição muito grande." : "Requisição inválida: " + bad.Message;
                await Problems.WriteAsync(context, status, code, detail);
                return true;
            }

            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;

            case DbUpdateConcurrencyException:
                await Problems.WriteAsync(context, 409, ErrorCodes.Conflict, "O recurso foi modificado por outra operação. Tente novamente.");
                return true;

            default:
                _logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                var message = _environment.IsDevelopment() ? exception.Message : "Erro interno. Tente novamente mais tarde.";
                await Problems.WriteAsync(context, 500, ErrorCodes.InternalError, message);
                return true;
        }
    }

    private static string ToCamel(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return propertyName;
        var parts = propertyName.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join('.', parts);
    }
}
