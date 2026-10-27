// DEVELOPMENT VERIFICATION TOOL ONLY. See NpgsqlPsqlStub.csproj.
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Npgsql;

public sealed class NpgsqlException(string message) : DbException(message);

public sealed class NpgsqlDataSource : IDisposable, IAsyncDisposable
{
    internal string Uri { get; }
    private NpgsqlDataSource(string uri) => Uri = uri;

    public static NpgsqlDataSource Create(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
        string Get(params string[] keys) => keys.Select(k => parts.GetValueOrDefault(k)).FirstOrDefault(v => v is not null) ?? "";
        var uri = $"postgresql://{System.Uri.EscapeDataString(Get("username", "user id", "user"))}:{System.Uri.EscapeDataString(Get("password"))}@{Get("host", "server")}:{(Get("port") is "" ? "5432" : Get("port"))}/{Get("database")}";
        return new NpgsqlDataSource(uri);
    }

    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) => Task.FromResult(new NpgsqlConnection(this));
    public NpgsqlCommand CreateCommand(string sql) => new(sql, new NpgsqlConnection(this));
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NpgsqlConnection(NpgsqlDataSource source) : IAsyncDisposable, IDisposable
{
    internal NpgsqlDataSource Source { get; } = source;
    public Task<NpgsqlTransaction> BeginTransactionAsync(CancellationToken ct = default) => Task.FromResult(new NpgsqlTransaction());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}

/// <summary>No-op: each psql invocation is its own session.</summary>
public sealed class NpgsqlTransaction : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NpgsqlParameter
{
    public object? Value { get; set; }
}

public sealed class NpgsqlParameterCollection : List<NpgsqlParameter>;

public sealed partial class NpgsqlCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction = null) : IAsyncDisposable, IDisposable
{
    public NpgsqlParameterCollection Parameters { get; } = new();

    public async Task<int> ExecuteNonQueryAsync(CancellationToken ct = default)
    {
        var text = Bind();
        if (IsQuery(text)) return (await RunQueryAsync(text, ct)).Rows.Count;
        var output = await Psql(text + ";\n", ct, quiet: false);
        var tag = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var m = Regex.Match(tag, @"^(INSERT \d+|UPDATE|DELETE|SELECT) ?(\d+)$");
        return m.Success ? int.Parse(m.Groups[2].Value) : -1;
    }

    public async Task<object?> ExecuteScalarAsync(CancellationToken ct = default)
    {
        var result = await RunQueryAsync(Bind(), ct);
        return result.Rows.Count == 0 ? null : result.Rows[0][0];
    }

    public async Task<NpgsqlDataReader> ExecuteReaderAsync(CancellationToken ct = default) => new(await RunQueryAsync(Bind(), ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    private static bool IsQuery(string s)
    {
        var t = s.TrimStart().ToUpperInvariant();
        return t.StartsWith("SELECT") || t.Contains(" RETURNING ") || t.Contains("\nRETURNING ");
    }

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex Placeholder();

    private string Bind() => Placeholder().Replace(sql, m => Literal(Parameters[int.Parse(m.Groups[1].Value) - 1].Value));

    private static string Literal(object? v) => v switch
    {
        null or DBNull => "NULL",
        string s => "'" + s.Replace("'", "''") + "'",
        Guid g => $"'{g}'",
        bool b => b ? "true" : "false",
        short or int or long => System.Convert.ToString(v, CultureInfo.InvariantCulture)!,
        DateTimeOffset d => $"'{d.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.ffffff}+00:00'",
        byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'",
        Guid[] arr => "'{" + string.Join(",", arr) + "}'",
        _ => throw new NotSupportedException($"Stub cannot bind {v.GetType()}"),
    };

    internal sealed record QueryResult(List<(string Name, string Type)> Columns, List<object?[]> Rows);

    private async Task<QueryResult> RunQueryAsync(string text, CancellationToken ct)
    {
        var desc = await Psql(text + "\n\\gdesc\n", ct);
        var columns = desc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Select(p => (Name: p[0], Type: p[1])).ToList();
        var json = (await Psql($"WITH __q AS ({text}) SELECT coalesce(json_agg(__q), '[]'::json) FROM __q;\n", ct)).Trim();
        var rows = new List<object?[]>();
        using var doc = JsonDocument.Parse(json);
        foreach (var obj in doc.RootElement.EnumerateArray())
        {
            var values = obj.EnumerateObject().Select(p => p.Value).ToList();
            rows.Add(columns.Select((c, i) => FromJson(values[i], c.Type)).ToArray());
        }
        return new QueryResult(columns, rows);
    }

    private static object? FromJson(JsonElement v, string type)
    {
        if (v.ValueKind == JsonValueKind.Null) return DBNull.Value;
        return type switch
        {
            "uuid" => Guid.Parse(v.GetString()!),
            "boolean" => v.GetBoolean(),
            "smallint" => v.GetInt16(),
            "integer" => v.GetInt32(),
            "bigint" => v.GetInt64(),
            "timestamp with time zone" => DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "bytea" => System.Convert.FromHexString(v.GetString()![2..]),
            "text[]" => v.EnumerateArray().Select(e => e.GetString()!).ToArray(),
            "jsonb" or "json" => v.GetRawText(),
            "void" => DBNull.Value,
            _ when type.StartsWith("character") || type == "text" => v.GetString(),
            _ => throw new NotSupportedException($"Stub cannot read type {type}"),
        };
    }

    private async Task<string> Psql(string script, CancellationToken ct, bool quiet = true)
    {
        var psi = new ProcessStartInfo("psql")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { connection.Source.Uri, "-X", "-v", "ON_ERROR_STOP=1", "-A", "-t", "-F", "\x1f" }) psi.ArgumentList.Add(a);
        if (quiet) psi.ArgumentList.Add("-q");
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new NpgsqlException($"psql failed: {stderr.Trim()}\nSQL: {script}");
        return stdout;
    }
}

public sealed class NpgsqlDataReader : IAsyncDisposable
{
    private readonly NpgsqlCommand.QueryResult _result;
    private int _row = -1;
    internal NpgsqlDataReader(NpgsqlCommand.QueryResult result) => _result = result;

    public Task<bool> ReadAsync(CancellationToken ct = default) => Task.FromResult(++_row < _result.Rows.Count);
    private object? V(int i) => _result.Rows[_row][i];
    public bool IsDBNull(int i) => V(i) is DBNull;
    public Guid GetGuid(int i) => (Guid)V(i)!;
    public bool GetBoolean(int i) => (bool)V(i)!;
    public string GetString(int i) => (string)V(i)!;
    public short GetInt16(int i) => Convert.ToInt16(V(i));
    public int GetInt32(int i) => Convert.ToInt32(V(i));
    public long GetInt64(int i) => Convert.ToInt64(V(i));
    public T GetFieldValue<T>(int i) => (T)V(i)!;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
