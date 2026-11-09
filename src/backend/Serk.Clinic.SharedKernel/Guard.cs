namespace Serk.Clinic.SharedKernel;

/// <summary>Small helpers for collecting field validation errors without exceptions.</summary>
public sealed class ValidationCollector
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public ValidationCollector Require(bool condition, string field, string message)
    {
        if (!condition)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                list = [];
                _errors[field] = list;
            }

            list.Add(message);
        }

        return this;
    }

    public ValidationCollector MaxLength(string? value, int max, string field) =>
        Require(value is null || value.Length <= max, field, $"Must be at most {max} characters.");

    public ValidationCollector Required(string? value, string field) =>
        Require(!string.IsNullOrWhiteSpace(value), field, "Required.");

    public Error ToError(string code = "validation_failed") =>
        Error.Validation(code, "One or more fields are invalid.", _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
}
