namespace PADS.MoneyFlow.Api.Services;

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, string? error)
    {
        Value = value!;
        Error = error;
    }

    public T Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Success(T value) => new(value, null);
    public static ServiceResult<T> Failure(string error) => new(default, error);
}
