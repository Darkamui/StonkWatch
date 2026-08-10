namespace StonkWatch.Web.Data;

public static class EnumParsing
{
    /// <summary>
    /// Parses loosely-formatted text ("near trigger", "Near-Trigger", "NEAR_TRIGGER") into an enum member,
    /// so callers (MCP tools, API clients) don't need to know the exact C# casing/spelling.
    /// Returns <paramref name="current"/> unchanged when <paramref name="raw"/> is null.
    /// </summary>
    public static TEnum ParseOrDefault<TEnum>(string? raw, TEnum current) where TEnum : struct, Enum
    {
        if (raw is null)
        {
            return current;
        }

        var normalized = Normalize(raw);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (Normalize(value.ToString()) == normalized)
            {
                return value;
            }
        }

        throw new ValidationException(
            $"Invalid value '{raw}' for {typeof(TEnum).Name}. Valid values: {string.Join(", ", Enum.GetValues<TEnum>())}");
    }

    public static TEnum? ParseNullableOrDefault<TEnum>(string? raw, TEnum? current) where TEnum : struct, Enum
    {
        if (raw is null)
        {
            return current;
        }

        if (raw.Length == 0)
        {
            return null;
        }

        return ParseOrDefault<TEnum>(raw, default);
    }

    private static string Normalize(string value) =>
        value.Replace(" ", "").Replace("-", "").Replace("_", "").ToUpperInvariant();
}

public class ValidationException(string message) : Exception(message);

public class ConflictException(string message) : Exception(message);
