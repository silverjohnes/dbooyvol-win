using System.Text.Json.Nodes;
using DBooyvol.Core.Models;
using DBooyvol.Core.Protocol;
using DBooyvol.Core.Sql;

namespace DBooyvol.Core.Db;

/// <summary>
/// One JDBC connection owned by one editor tab. Wraps the NDJSON sidecar and
/// keeps the cursor contract: a queryId is valid until <c>done</c> or
/// <c>closeCursor</c>.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly SidecarClient _client;
    private readonly Profile _profile;
    private readonly Func<CancellationToken, Task<string?>>? _passwordProvider;
    private string? _connId;

    public Profile Profile => _profile;
    public string? ConnId => _connId;
    public string ServerVersion { get; private set; } = "";
    public string Driver { get; private set; } = "";
    public string ConnectedUser { get; private set; } = "";

    private Session(SidecarClient client, Profile profile,
        Func<CancellationToken, Task<string?>>? passwordProvider)
    {
        _client = client;
        _profile = profile;
        _passwordProvider = passwordProvider;
    }

    public static async Task<Session> ConnectAsync(SidecarClient client, Profile profile,
        string? password, Func<CancellationToken, Task<string?>>? passwordProvider = null,
        CancellationToken ct = default)
    {
        var session = new Session(client, profile, passwordProvider);
        await session.OpenAsync(password, ct).ConfigureAwait(false);
        return session;
    }

    private async Task OpenAsync(string? password, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["kind"] = _profile.Kind,
            ["host"] = _profile.Host,
            ["port"] = _profile.EffectivePort,
            ["database"] = _profile.Database,
            ["user"] = _profile.User,
            ["password"] = password,
            ["driverPath"] = _profile.DriverPath,
            ["url"] = _profile.JdbcUrl,
            ["oracleSid"] = _profile.OracleSid,
            ["oracleMode"] = _profile.OracleMode,
            ["oracleAlias"] = _profile.OracleAlias,
            ["tnsAdmin"] = _profile.TnsAdmin,
            ["ldapHost"] = _profile.LdapHost,
            ["ldapPort"] = _profile.LdapPort,
            ["ldapContext"] = _profile.LdapContext,
            ["ldapSsl"] = _profile.LdapSsl,
            ["oracleAuthServices"] = _profile.OracleAuthServices
        };

        var result = await _client.CallAsync("connect", parameters, ct).ConfigureAwait(false);
        _connId = result?["connId"]?.GetValue<string>()
                  ?? throw new SidecarException("Sidecar не вернул connId");
        ServerVersion = result?["serverVersion"]?.GetValue<string>() ?? "";
        Driver = result?["driver"]?.GetValue<string>() ?? "";
        ConnectedUser = result?["user"]?.GetValue<string>() ?? _profile.User;
    }

    private string RequireConnId() =>
        _connId ?? throw new SidecarException("Нет активного соединения", lost: true);

    public async Task<QueryResult> QueryAsync(string sql, int count,
        IReadOnlyDictionary<string, string?>? parameters = null,
        string queryName = "", CancellationToken ct = default)
    {
        try
        {
            return await QueryOnceAsync(sql, count, parameters, queryName, ct).ConfigureAwait(false);
        }
        catch (SidecarException e) when (e.IsConnectionLost)
        {
            await ReconnectAsync(ct).ConfigureAwait(false);
            return await QueryOnceAsync(sql, count, parameters, queryName, ct).ConfigureAwait(false);
        }
    }

    private async Task<QueryResult> QueryOnceAsync(string sql, int count,
        IReadOnlyDictionary<string, string?>? parameters, string queryName, CancellationToken ct)
    {
        var result = await _client.CallAsync("query", new
        {
            connId = RequireConnId(),
            sql,
            kind = _profile.Kind,
            count,
            @params = parameters ?? new Dictionary<string, string?>()
        }, ct).ConfigureAwait(false);

        return ParseQueryResult(result, queryName);
    }

    public static QueryResult ParseQueryResult(JsonNode? result, string queryName)
    {
        var qr = new QueryResult { QueryName = queryName };
        if (result is not JsonObject obj) return qr;

        qr.QueryId = obj["queryId"]?.GetValue<string>() ?? "";
        qr.Done = obj["done"]?.GetValue<bool>() ?? false;
        qr.Truncated = obj["truncated"]?.GetValue<bool>() ?? false;
        qr.DurationMs = obj["durationMs"]?.GetValue<long>() ?? 0;
        if (obj["updateCount"] is JsonNode uc && uc.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            qr.UpdateCount = uc.GetValue<int>();

        if (obj["columns"] is JsonArray columns)
        {
            foreach (var node in columns.OfType<JsonObject>())
            {
                qr.Columns.Add(new ColumnInfo
                {
                    Name = node["name"]?.GetValue<string>() ?? "",
                    Type = node["type"]?.GetValue<string>() ?? "",
                    ClassName = node["className"]?.GetValue<string>(),
                    JdbcType = node["jdbcType"]?.GetValue<int>() ?? 0,
                    Precision = node["precision"]?.GetValue<int>() ?? 0,
                    Scale = node["scale"]?.GetValue<int>() ?? 0,
                    Nullable = node["nullable"]?.GetValue<bool>() ?? true,
                    Table = node["table"]?.GetValue<string>(),
                    Schema = node["schema"]?.GetValue<string>(),
                    Catalog = node["catalog"]?.GetValue<string>()
                });
            }
        }

        AppendRows(qr, obj["rows"] as JsonArray);
        qr.EnsureRowIds();
        qr.TableNames = qr.Columns
            .Select(c => c.QualifiedTable)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return qr;
    }

    private static void AppendRows(QueryResult qr, JsonArray? rows)
    {
        if (rows is null) return;
        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonArray cells) continue;
            var row = new string?[cells.Count];
            for (var i = 0; i < cells.Count; i++)
                row[i] = cells[i] is null ? null : cells[i]!.GetValue<string>();
            qr.Rows.Add(row);
            qr.RowIds.Add(Guid.NewGuid());
        }
    }

    public async Task<bool> FetchAsync(QueryResult result, int count, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.QueryId)) return false;
        var response = await _client.CallAsync("fetch", new
        {
            queryId = result.QueryId,
            count
        }, ct).ConfigureAwait(false);

        var rows = response?["rows"] as JsonArray;
        var before = result.Rows.Count;
        AppendRows(result, rows);
        // AppendRows adds ids too; keep counts in sync.
        result.EnsureRowIds();
        result.Done = response?["done"]?.GetValue<bool>() ?? true;
        result.Truncated = response?["truncated"]?.GetValue<bool>() ?? result.Truncated;
        return result.Rows.Count > before;
    }

    public async Task<long> CountAsync(string sql, CancellationToken ct = default)
    {
        var first = SQLText.FirstRowReturningStatement(sql);
        if (first is null) return 0;
        // sast:allow DBY009 — оборачивает уже разрешённый пользовательский запрос,
        // чтобы посчитать строки; новых значений в SQL не добавляется.
        var countSql = $"SELECT count(*) FROM (\n{first.Trim().TrimEnd(';')}\n) dbooyvol_count";
        var response = await _client.CallAsync("count", new
        {
            connId = RequireConnId(),
            sql = countSql,
            kind = _profile.Kind,
            @params = new Dictionary<string, string?>()
        }, ct).ConfigureAwait(false);
        return response?["count"]?.GetValue<long>() ?? 0;
    }

    public async Task<List<string>> PrimaryKeysAsync(string catalog, string schema, string table,
        CancellationToken ct = default)
    {
        var response = await _client.CallAsync("primaryKeys", new
        {
            connId = RequireConnId(), catalog, schema, table
        }, ct).ConfigureAwait(false);
        return (response?["columns"] as JsonArray)?
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0).ToList() ?? new List<string>();
    }

    public async Task<TableMeta> TableMetaAsync(string catalog, string schema, string table,
        CancellationToken ct = default)
    {
        var response = await _client.CallAsync("tableMeta", new
        {
            connId = RequireConnId(), catalog, schema, table
        }, ct).ConfigureAwait(false);
        var meta = new TableMeta();
        if (response?["columns"] is JsonArray columns)
        {
            foreach (var node in columns.OfType<JsonObject>())
            {
                meta.Columns.Add(new TableColumnMeta
                {
                    Name = node["name"]?.GetValue<string>() ?? "",
                    Type = node["type"]?.GetValue<string>() ?? "",
                    Size = node["size"]?.GetValue<int>() ?? 0,
                    Nullable = node["nullable"]?.GetValue<bool>() ?? true,
                    Primary = node["primary"]?.GetValue<bool>() ?? false
                });
            }
        }
        meta.PrimaryKey = (response?["primaryKey"] as JsonArray)?
            .Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
        meta.PrimaryKeyName = response?["primaryKeyName"]?.GetValue<string>() ?? "";
        return meta;
    }

    public async Task<(int updated, int inserted)> UpdateRowsAsync(
        string catalog, string schema, string table,
        IEnumerable<RowUpdate> updates, IEnumerable<Dictionary<string, string?>> inserts,
        CancellationToken ct = default)
    {
        var response = await _client.CallAsync("updateRows", new
        {
            connId = RequireConnId(),
            catalog,
            schema,
            table,
            updates = updates.Select(u => new { keys = u.Keys, values = u.Values }).ToList(),
            inserts = inserts.ToList()
        }, ct).ConfigureAwait(false);
        return (response?["updated"]?.GetValue<int>() ?? 0,
                response?["inserted"]?.GetValue<int>() ?? 0);
    }

    public async Task CloseCursorAsync(string queryId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(queryId)) return;
        try { await _client.CallAsync("closeCursor", new { queryId }, ct).ConfigureAwait(false); }
        catch (SidecarException) { }
    }

    public async Task CancelAsync(string queryId, CancellationToken ct = default)
    {
        try { await _client.CallAsync("cancel", new { queryId }, ct).ConfigureAwait(false); }
        catch (SidecarException) { }
    }

    public async Task CancelConnectionAsync(CancellationToken ct = default)
    {
        if (_connId is null) return;
        try { await _client.CallAsync("cancelConn", new { connId = _connId }, ct).ConfigureAwait(false); }
        catch (SidecarException) { }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        _connId = null;
        string? password = null;
        if (_passwordProvider is not null)
            password = await _passwordProvider(ct).ConfigureAwait(false);
        await OpenAsync(password, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_connId is null) return;
        try { await _client.CallAsync("disconnect", new { connId = _connId }, ct).ConfigureAwait(false); }
        catch (SidecarException) { }
        _connId = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}

public sealed class RowUpdate
{
    public Dictionary<string, string?> Keys { get; set; } = new();
    public Dictionary<string, string?> Values { get; set; } = new();
}

public sealed class TableMeta
{
    public List<TableColumnMeta> Columns { get; set; } = new();
    public List<string> PrimaryKey { get; set; } = new();
    public string PrimaryKeyName { get; set; } = "";

    public string KeyDescription(string catalog, string schema, string table)
    {
        if (PrimaryKey.Count == 0) return "";
        var prefix = string.Join(".", new[] { catalog, schema, table }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return $"{prefix}({string.Join(", ", PrimaryKey)})";
    }
}

public sealed class TableColumnMeta
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Size { get; set; }
    public bool Nullable { get; set; }
    public bool Primary { get; set; }
}
