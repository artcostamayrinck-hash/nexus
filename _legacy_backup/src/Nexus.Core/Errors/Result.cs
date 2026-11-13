using System.Diagnostics.CodeAnalysis;

namespace Nexus.Core.Errors;

/// <summary>
/// Represents the outcome of an operation without relying on exceptions for control flow.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, NexusError? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error is null)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public NexusError? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(NexusError error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);
    public static Result<T> Failure<T>(NexusError error) => Result<T>.Failure(error);

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<NexusError, TResult> onFailure)
        => IsSuccess ? onSuccess() : onFailure(Error!);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, NexusError? error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    [MemberNotNullWhen(true, nameof(_value))]
    public new bool IsSuccess => base.IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value on a failed Result ({Error?.Code}: {Error?.Message}).");

    public static Result<T> Success(T value) => new(true, value, null);
    public new static Result<T> Failure(NexusError error) => new(false, default, error);

    public Result<TOut> Map<TOut>(Func<T, TOut> mapper)
        => IsSuccess ? Result<TOut>.Success(mapper(Value)) : Result<TOut>.Failure(Error!);

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder)
        => IsSuccess ? binder(Value) : Result<TOut>.Failure(Error!);

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<NexusError, TResult> onFailure)
        => IsSuccess ? onSuccess(Value) : onFailure(Error!);
}

