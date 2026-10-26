namespace Pulse.Application;

/// <summary>Either a value or the field errors that prevented it.</summary>
public sealed class Result<T>
{
    internal Result(T? value, IReadOnlyDictionary<string, string[]>? errors)
    {
        Value = value;
        Errors = errors;
    }

    public T? Value { get; }

    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    public bool IsSuccess => Errors is null;

    /// <summary>True when the error is about who is asking, not about what was asked (bad credentials).</summary>
    public bool IsUnauthenticated => Errors?.ContainsKey(Result.CredentialsKey) == true;
}

/// <summary>Creates results; kept apart from the generic type so callers need not repeat the type argument.</summary>
public static class Result
{
    /// <summary>The error key that means "you are not who you say" rather than "this field is wrong".</summary>
    public const string CredentialsKey = "credentials";

    public static Result<T> Ok<T>(T value) => new(value, null);

    /// <summary>Success of an action that has nothing to give back.</summary>
    public static Result<bool> Done() => new(true, null);

    public static Result<T> Invalid<T>(IReadOnlyDictionary<string, string[]> errors) => new(default, errors);

    public static Result<T> Invalid<T>(string field, string message) =>
        new(default, new Dictionary<string, string[]> { [field] = [message] });
}
