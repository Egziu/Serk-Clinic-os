using Microsoft.EntityFrameworkCore;

namespace Serk.Clinic.Platform.Data;

public static class SnakeCaseNaming
{
    /// <summary>Maps every CLR property to a snake_case column (matches hand-written SQL migrations).</summary>
    public static void ApplySnakeCaseColumns(this ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    public static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
