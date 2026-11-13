using Nexus.Contracts.Common;
using Nexus.Contracts.Messaging;

namespace Nexus.Core.Errors;

public enum ErrorCategory
{
    Unexpected = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
    Storage = 6,
    Network = 7,
    Timeout = 8,
    Cancelled = 9
}

public sealed record NexusError(
    string Code,
    string Message,
    ErrorCategory Category = ErrorCategory.Unexpected,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    Exception? Exception = null)
{
    public static NexusError Validation(string message, IReadOnlyDictionary<string, string[]>? errors = null, string code = "VALIDATION_ERROR")
        => new(code, message, ErrorCategory.Validation, ValidationErrors: errors);

    public static NexusError Validation(ContractValidationResult result, string code = "CONTRACT_VALIDATION_FAILED")
        => new(
            code,
            result.Errors.FirstOrDefault()?.Message ?? "Contract validation failed.",
            ErrorCategory.Validation,
            ValidationErrors: result.ToDictionary());

    public static NexusError NotFound(string message, string code = "NOT_FOUND", string? detail = null)
        => new(code, message, ErrorCategory.NotFound, detail);

    public static NexusError Conflict(string message, string code = "CONFLICT", string? detail = null)
        => new(code, message, ErrorCategory.Conflict, detail);

    public static NexusError Unauthorized(string message, string code = "UNAUTHORIZED", string? detail = null)
        => new(code, message, ErrorCategory.Unauthorized, detail);

    public static NexusError Forbidden(string message, string code = "FORBIDDEN", string? detail = null)
        => new(code, message, ErrorCategory.Forbidden, detail);

    public static NexusError Storage(string message, string code = "STORAGE_ERROR", string? detail = null, Exception? exception = null)
        => new(code, message, ErrorCategory.Storage, detail, Exception: exception);

    public static NexusError Network(string message, string code = "NETWORK_ERROR", string? detail = null, Exception? exception = null)
        => new(code, message, ErrorCategory.Network, detail, Exception: exception);

    public static NexusError Cancelled(string message = "Operation was cancelled.", string code = "OPERATION_CANCELLED")
        => new(code, message, ErrorCategory.Cancelled);

    public static NexusError Unexpected(string message, string code = "INTERNAL_ERROR", string? detail = null, Exception? exception = null)
        => new(code, message, ErrorCategory.Unexpected, detail, Exception: exception);

    public ContractError ToContractError()
        => new(Code, Message, Detail, ValidationErrors);
}

public class NexusException : Exception
{
    public NexusError Error { get; }

    public NexusException(NexusError error)
        : base(error.Message, error.Exception)
    {
        Error = error;
    }
}

public sealed class NexusValidationException : NexusException
{
    public NexusValidationException(NexusError error) : base(error) { }
}

public sealed class NexusStorageException : NexusException
{
    public NexusStorageException(NexusError error) : base(error) { }
}

