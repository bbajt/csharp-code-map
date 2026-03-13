namespace CodeMap.Core.Errors;

/// <summary>
/// Discriminated union representing either a success value or an error.
/// All fallible operations in CodeMap return this type instead of throwing exceptions.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
/// <typeparam name="TError">The error type.</typeparam>
public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the success value. Throws if result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value on a failed Result. Check IsSuccess first.");

    /// <summary>Gets the error. Throws if result is a success.</summary>
    public TError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error on a successful Result. Check IsFailure first.");

    private Result(T value)
    {
        IsSuccess = true;
        _value = value;
        _error = default;
    }

    private Result(TError error)
    {
        IsSuccess = false;
        _value = default;
        _error = error;
    }

    /// <summary>Creates a successful result.</summary>
    public static Result<T, TError> Success(T value) => new(value);

    /// <summary>Creates a failed result.</summary>
    public static Result<T, TError> Failure(TError error) => new(error);

    /// <summary>Implicit conversion from T to Result (success).</summary>
    public static implicit operator Result<T, TError>(T value) => Success(value);

    /// <summary>
    /// Pattern match on the result. Exactly one of the functions will be called.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<TError, TResult> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(_error!);

    /// <summary>
    /// Pattern match with side effects.
    /// </summary>
    public void Switch(Action<T> onSuccess, Action<TError> onFailure)
    {
        if (IsSuccess) onSuccess(_value!);
        else onFailure(_error!);
    }

    /// <summary>
    /// Maps the success value. Error passes through unchanged.
    /// </summary>
    public Result<TNew, TError> Map<TNew>(Func<T, TNew> transform) =>
        IsSuccess
            ? Result<TNew, TError>.Success(transform(_value!))
            : Result<TNew, TError>.Failure(_error!);

    /// <summary>
    /// Flat-maps the success value. Error passes through unchanged.
    /// </summary>
    public Result<TNew, TError> Bind<TNew>(Func<T, Result<TNew, TError>> transform) =>
        IsSuccess
            ? transform(_value!)
            : Result<TNew, TError>.Failure(_error!);
}
