namespace SampleApp.Shared.SharedModels;

/// <summary>A generic result wrapper that carries either a value or an error message.</summary>
public record Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private Result(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string error) => new(default, error);
}
