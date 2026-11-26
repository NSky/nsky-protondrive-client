using Microsoft.Data.Sqlite;

namespace NSkyLabs.ProtonDrive.Sync.State;

/// <summary>A row of the remote tree table.</summary>
internal sealed record RemoteNodeRow(
    string Uid,
    string? ParentUid,
    string? Name,
    bool IsFolder,
    string? RevisionUid,
    long Size,
    string? Sha1,
    long? ModifiedUnixMs,
    string? MediaType,
    bool IsTrashed);

/// <summary>
/// The per-pair SQLite database: the base (last synced state), the remote tree kept current through Drive
/// events, and a few values such as the events cursor.
/// </summary>
/// <remarks>Thread-safe: the executor records results from parallel transfers.</remarks>
internal sealed class SyncStateStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    private SyncStateStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static SyncStateStore Open(string databasePath)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(databasePath)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();

        var store = new SyncStateStore(connection);
        store.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        store.Execute(
            """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS base (
                path TEXT PRIMARY KEY,
                is_folder INTEGER NOT NULL,
                node_uid TEXT NOT NULL,
                revision_uid TEXT,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS remote (
                uid TEXT PRIMARY KEY,
                parent_uid TEXT,
                name TEXT,
                is_folder INTEGER NOT NULL,
                revision_uid TEXT,
                size INTEGER NOT NULL,
                sha1 TEXT,
                mtime_ms INTEGER,
                media_type TEXT,
                trashed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS remote_parent ON remote (parent_uid);
            """);

        var version = store.GetMeta("schema_version");
        if (version is null)
        {
            store.SetMeta("schema_version", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (version != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            connection.Dispose();
            throw new InvalidOperationException($"The sync database {databasePath} has an unsupported schema version {version}.");
        }

        return store;
    }

    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var command = Command("SELECT value FROM meta WHERE key = $key", ("$key", key));
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_lock)
        {
            using var command = value is null
                ? Command("DELETE FROM meta WHERE key = $key", ("$key", key))
                : Command("INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", ("$key", key), ("$value", value));
            command.ExecuteNonQuery();
        }
    }

    // ---- Base ----

    public Dictionary<string, BaseItem> LoadBase()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            using var command = Command("SELECT path, is_folder, node_uid, revision_uid, size, mtime FROM base");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new BaseItem(
                    reader.GetString(0),
                    reader.GetInt64(1) != 0,
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5));
                result[item.Path] = item;
            }

            return result;
        }
    }

    public int CountBase()
    {
        lock (_lock)
        {
            using var command = Command("SELECT COUNT(*) FROM base");
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public void UpsertBase(BaseItem item)
    {
        lock (_lock)
        {
            using var command = Command(
                """
                INSERT INTO base (path, is_folder, node_uid, revision_uid, size, mtime)
                VALUES ($path, $folder, $uid, $rev, $size, $mtime)
                ON CONFLICT (path) DO UPDATE SET is_folder = excluded.is_folder, node_uid = excluded.node_uid,
                    revision_uid = excluded.revision_uid, size = excluded.size, mtime = excluded.mtime
                """,
                ("$path", item.Path),
                ("$folder", item.IsFolder ? 1 : 0),
                ("$uid", item.NodeUid),
                ("$rev", item.RevisionUid),
                ("$size", item.Size),
                ("$mtime", item.ModifiedTicks));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteBase(string path)
    {
        lock (_lock)
        {
            using var command = Command("DELETE FROM base WHERE path = $path", ("$path", path));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes the entry for <paramref name="path"/> and everything under it.</summary>
    public void DeleteBaseSubtree(string path)
    {
        lock (_lock)
        {
            using var command = Command(
                "DELETE FROM base WHERE path = $path OR substr(path, 1, length($path) + 1) = $path || '/'",
                ("$path", path));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The path a node is recorded at in the base, or null.</summary>
    public string? GetBasePath(string nodeUid)
    {
        lock (_lock)
        {
            using var command = Command("SELECT path FROM base WHERE node_uid = $uid LIMIT 1", ("$uid", nodeUid));
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>Renames the entry for <paramref name="from"/> and everything under it to <paramref name="to"/>.</summary>
    public void MoveBaseSubtree(string from, string to)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using (var delete = Command("DELETE FROM base WHERE path = $to OR substr(path, 1, length($to) + 1) = $to || '/'", ("$to", to)))
            {
                delete.Transaction = transaction;
                delete.ExecuteNonQuery();
            }

            using (var move = Command(
                "UPDATE base SET path = $to || substr(path, length($from) + 1) WHERE path = $from OR substr(path, 1, length($from) + 1) = $from || '/'",
                ("$from", from),
                ("$to", to)))
            {
                move.Transaction = transaction;
                move.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    // ---- Remote tree ----

    public List<RemoteNodeRow> LoadRemote()
    {
        lock (_lock)
        {
            var result = new List<RemoteNodeRow>();
            using var command = Command("SELECT uid, parent_uid, name, is_folder, revision_uid, size, sha1, mtime_ms, media_type, trashed FROM remote");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new RemoteNodeRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt64(9) != 0));
            }

            return result;
        }
    }

    public HashSet<string> LoadRemoteUids()
    {
        lock (_lock)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = Command("SELECT uid FROM remote");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
    }

    /// <summary>Replaces the whole remote tree (after a full scan) and sets the events cursor in one transaction.</summary>
    public void ReplaceRemote(IEnumerable<RemoteNodeRow> rows, string cursor)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using (var clear = Command("DELETE FROM remote"))
            {
                clear.Transaction = transaction;
                clear.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                UpsertRemoteCore(row, transaction);
            }

            SetMetaCore("remote_cursor", cursor, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Applies changes found through events and moves the cursor, in one transaction.</summary>
    public void ApplyRemoteChanges(IEnumerable<RemoteNodeRow> upserts, IEnumerable<string> deletedUids, string? cursor)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var row in upserts)
            {
                UpsertRemoteCore(row, transaction);
            }

            foreach (var uid in deletedUids)
            {
                using var delete = Command("DELETE FROM remote WHERE uid = $uid", ("$uid", uid));
                delete.Transaction = transaction;
                delete.ExecuteNonQuery();
            }

            if (cursor is not null)
            {
                SetMetaCore("remote_cursor", cursor, transaction);
            }

            transaction.Commit();
        }
    }

    public void UpsertRemote(RemoteNodeRow row) => ApplyRemoteChanges([row], [], cursor: null);

    public void SetRemoteTrashed(string uid)
    {
        lock (_lock)
        {
            using var command = Command("UPDATE remote SET trashed = 1 WHERE uid = $uid", ("$uid", uid));
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void UpsertRemoteCore(RemoteNodeRow row, SqliteTransaction transaction)
    {
        using var command = Command(
            """
            INSERT INTO remote (uid, parent_uid, name, is_folder, revision_uid, size, sha1, mtime_ms, media_type, trashed)
            VALUES ($uid, $parent, $name, $folder, $rev, $size, $sha1, $mtime, $media, $trashed)
            ON CONFLICT (uid) DO UPDATE SET parent_uid = excluded.parent_uid, name = excluded.name, is_folder = excluded.is_folder,
                revision_uid = excluded.revision_uid, size = excluded.size, sha1 = excluded.sha1, mtime_ms = excluded.mtime_ms,
                media_type = excluded.media_type, trashed = excluded.trashed
            """,
            ("$uid", row.Uid),
            ("$parent", row.ParentUid),
            ("$name", row.Name),
            ("$folder", row.IsFolder ? 1 : 0),
            ("$rev", row.RevisionUid),
            ("$size", row.Size),
            ("$sha1", row.Sha1),
            ("$mtime", row.ModifiedUnixMs),
            ("$media", row.MediaType),
            ("$trashed", row.IsTrashed ? 1 : 0));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private void SetMetaCore(string key, string value, SqliteTransaction transaction)
    {
        using var command = Command(
            "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key),
            ("$value", value));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
