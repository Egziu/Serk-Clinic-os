using Npgsql;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Care.Application;

/// <summary>Small parameterised-SQL helper over the request's tenant-scoped transaction (row-level security applies).</summary>
internal static class Sql
{
    public static async Task<int> ExecAsync(this DbSession session, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<T>> QueryAsync<T>(this DbSession session, string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, ps);
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(map(r));
        }

        return list;
    }

    public static async Task<T?> ScalarAsync<T>(this DbSession session, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        Bind(cmd, ps);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? default : (T)v;
    }

    public static string? Str(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static DateTimeOffset? Time(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public static Guid? Id(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static void Bind(NpgsqlCommand cmd, (string Name, object? Value)[] ps)
    {
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
    }
}
