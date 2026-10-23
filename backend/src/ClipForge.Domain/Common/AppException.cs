namespace ClipForge.Domain.Common;

/// <summary>
/// Application error carrying a machine-readable code and an HTTP status. The API's global exception
/// handler turns it into an RFC 9457 problem details document.
/// </summary>
public class AppException : Exception
{
    public AppException(string code, int status, string message, IDictionary<string, string[]>? errors = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Errors = errors;
    }

    public string Code { get; }
    public int Status { get; }
    public IDictionary<string, string[]>? Errors { get; }

    public static AppException NotFound(string message = "Recurso não encontrado.") => new(ErrorCodes.NotFound, 404, message);
    public static AppException Forbidden(string message = "Acesso negado.") => new(ErrorCodes.Forbidden, 403, message);
    public static AppException Unauthorized(string message = "Não autenticado.") => new(ErrorCodes.Unauthorized, 401, message);
    public static AppException Conflict(string message) => new(ErrorCodes.Conflict, 409, message);
    public static AppException Validation(string message, IDictionary<string, string[]>? errors = null) => new(ErrorCodes.ValidationFailed, 400, message, errors);
    public static AppException PlanLimit(string message, int status = 409) => new(ErrorCodes.PlanLimit, status, message);
    public static AppException QuotaExceeded(string message) => new(ErrorCodes.QuotaExceeded, 409, message);
    public static AppException InvalidMedia(string message) => new(ErrorCodes.InvalidMedia, 422, message);
}

public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string QuotaExceeded = "quota_exceeded";
    public const string PlanLimit = "plan_limit";
    public const string InvalidMedia = "invalid_media";
    public const string SourceNotSupported = "source_not_supported";
    public const string PaymentsNotConfigured = "payments_not_configured";
    public const string RateLimited = "rate_limited";
    public const string JobNotCancellable = "job_not_cancellable";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string EmailNotConfirmed = "email_not_confirmed";
    public const string EmailTaken = "email_taken";
    public const string InvalidToken = "invalid_token";
    public const string AccountDeletionPending = "account_deletion_pending";
    public const string PaymentProviderError = "payment_provider_error";
    public const string InternalError = "internal_error";
    public const string AssistantUnavailable = "assistant_unavailable";
}
