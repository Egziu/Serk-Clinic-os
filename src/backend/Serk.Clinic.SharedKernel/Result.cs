namespace Serk.Clinic.SharedKernel;

/// <summary>Classified failure kinds. The API layer maps these to RFC 9457 problem responses.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,
    PreconditionFailed,
    BusinessRule,
}

/// <summary>A failure description. <see cref="Message"/> must never contain patient data (PHI).</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fields = null) => new(ErrorKind.Validation, code, message, fields);
    public static Error NotFound(string code = "not_found", string message = "The resource was not found.") => new(ErrorKind.NotFound, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Precondition(string code, string message) => new(ErrorKind.PreconditionFailed, code, message);
    public static Error Rule(string code, string message) => new(ErrorKind.BusinessRule, code, message);
}

public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public Error? Error { get; }
    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is a failure: " + Error!.Code);

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Fail(error);
}
