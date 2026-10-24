using System.Data;
using System.Globalization;
using Iris.Core.Rsi;
using Iris.Core.Social;
using Microsoft.Data.Sqlite;

namespace Iris.Core.Cache;

/// <summary>
/// The lookup cache: one SQLite file holding what IRIS has learned from RSI.
/// This class only stores and loads rows; it knows nothing about TTLs or refreshing
/// (that is <see cref="CacheRefresher"/>) and it never touches the network.
///
/// TABLES (schema version 2). Every row has last_refreshed, an ISO 8601 UTC time such as 2026-10-21T08:30:00.0000000Z.
/// Handles and SIDs are compared case-insensitively (COLLATE NOCASE), stored as RSI shows them.
///
///   players        handle PK | source | last_refreshed
///                  One row per player whose primary page we fetched.
///   player_orgs    handle + position PK | sid (NULL = hidden org) | is_main | archetype | activities | last_refreshed
///                  The player's orgs in RSI page order (position 0 first). archetype/activities are only filled for the main org.
///                  activities is a '|' separated list.
///   orgs           sid PK | long_name | member_count | archetype | commitment | is_exclusive | activities
///                  | attrs_refreshed | members_refreshed | source | last_refreshed
///                  One row per org. The attribute columns are filled by org-page lookups (attrs_refreshed NULL = never);
///                  long_name and the member list come from the org list refresh (members_refreshed NULL = never).
///   org_members    sid + handle PK | last_refreshed
///                  Visible members of an org that is on the user's org list.
///   not_found      kind ('player' or 'org') + name PK | source | last_refreshed
///                  The negative cache: RSI answered 404 for this name.
///   duplicate_name_counts   name PK | frames | last_seen        (added in schema version 2)
///                  For the exclusion list: in how many frames this name was seen on two separate nameplates
///                  (see DuplicateNameTracker). The row is deleted once the name has been excluded.
///
/// source is one of: 'player_list', 'org_list', 'org_list_member', 'hotkey' (see <see cref="CacheSource"/>).
///
/// VERSIONING: the file's PRAGMA user_version holds the schema version. A fresh file is created at
/// <see cref="CurrentSchemaVersion"/>. A file with a newer version is refused. Later increments migrate
/// older files in <see cref="EnsureSchema"/> (version 1 gets the 1-to-2 changes, and so on).
///
/// THREADING: one SQLite connection is not thread-safe, so every public method takes a lock.
/// </summary>
public sealed class CacheDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private const string ActivitySeparator = "|";

    private readonly SqliteConnection _connection;
    private readonly bool _ownsConnection;
    private readonly object _gate = new();
    private SqliteTransaction? _transaction;

    /// <summary>
    /// Uses a connection that is already open (tests pass "Data Source=:memory:"). The caller keeps ownership.
    /// </summary>
    public CacheDatabase(SqliteConnection openConnection)
        : this(openConnection, ownsConnection: false)
    {
    }

    private CacheDatabase(SqliteConnection connection, bool ownsConnection)
    {
        if (connection.State != ConnectionState.Open)
            throw new ArgumentException("The SQLite connection must be open.", nameof(connection));
        _connection = connection;
        _ownsConnection = ownsConnection;
        EnsureSchema();
    }

    /// <summary>Opens (creating if needed) the cache file at <paramref name="path"/>. Dispose the result to close it.</summary>
    public static CacheDatabase OpenFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        try
        {
            return new CacheDatabase(connection, ownsConnection: true);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownsConnection)
                _connection.Dispose();
        }
    }

    /// <summary>The schema version stored in the file.</summary>
    public int SchemaVersion
    {
        get
        {
            lock (_gate)
                return (int)Convert.ToInt64(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Players
    // ---------------------------------------------------------------------------------------

    /// <summary>The cached player, or null if we have none.</summary>
    public CachedPlayer? GetPlayer(string handle)
    {
        lock (_gate)
        {
            string shownHandle;
            string sourceText;
            string refreshedText;
            using (var command = Command("SELECT handle, source, last_refreshed FROM players WHERE handle = $handle", ("$handle", handle)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    return null;
                shownHandle = reader.GetString(0);
                sourceText = reader.GetString(1);
                refreshedText = reader.GetString(2);
            }

            var orgs = new List<OrgMembership>();
            using (var command = Command(
                       "SELECT sid, is_main, archetype, activities FROM player_orgs WHERE handle = $handle ORDER BY position",
                       ("$handle", handle)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string? sid = reader.IsDBNull(0) ? null : reader.GetString(0);
                    bool isMain = reader.GetInt32(1) != 0;
                    string? archetype = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var activities = SplitActivities(reader.GetString(3));
                    orgs.Add(new OrgMembership(sid, isMain, archetype, activities));
                }
            }

            return new CachedPlayer(shownHandle, orgs, CacheSources.FromDb(sourceText), ParseTime(refreshedText));
        }
    }

    /// <summary>
    /// Stores a player's primary page (replacing any older copy) and forgets any "not found" note for the name.
    /// If the player is already stored with a stronger source, that source is kept.
    /// </summary>
    public void SavePlayer(PlayerOrgs player, CacheSource source, DateTimeOffset now)
    {
        InTransaction(() =>
        {
            var existing = ScalarText("SELECT source FROM players WHERE handle = $handle", ("$handle", player.Handle));
            var effective = existing is null ? source : CacheSources.Stronger(CacheSources.FromDb(existing), source);
            var time = FormatTime(now);

            Execute("DELETE FROM player_orgs WHERE handle = $handle", ("$handle", player.Handle));
            Execute("DELETE FROM players WHERE handle = $handle", ("$handle", player.Handle));
            Execute("DELETE FROM not_found WHERE kind = 'player' AND name = $handle", ("$handle", player.Handle));
            Execute(
                "INSERT INTO players (handle, source, last_refreshed) VALUES ($handle, $source, $time)",
                ("$handle", player.Handle), ("$source", CacheSources.ToDb(effective)), ("$time", time));

            int position = 0;
            foreach (var org in player.Orgs)
            {
                Execute(
                    "INSERT INTO player_orgs (handle, position, sid, is_main, archetype, activities, last_refreshed) "
                    + "VALUES ($handle, $position, $sid, $main, $archetype, $activities, $time)",
                    ("$handle", player.Handle), ("$position", position), ("$sid", org.Sid),
                    ("$main", org.IsMain ? 1 : 0), ("$archetype", org.Archetype),
                    ("$activities", JoinActivities(org.ActivityList)), ("$time", time));
                position++;
            }
        });
    }

    // ---------------------------------------------------------------------------------------
    // Orgs
    // ---------------------------------------------------------------------------------------

    /// <summary>The cached org row, or null.</summary>
    public CachedOrg? GetOrg(string sid)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
                + "attrs_refreshed, members_refreshed, source, last_refreshed FROM orgs WHERE sid = $sid",
                ("$sid", sid));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new CachedOrg(
                Sid: reader.GetString(0),
                LongName: reader.IsDBNull(1) ? null : reader.GetString(1),
                MemberCount: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Archetype: reader.IsDBNull(3) ? null : reader.GetString(3),
                Commitment: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsExclusive: reader.GetInt32(5) != 0,
                Activities: SplitActivities(reader.GetString(6)),
                AttributesRefreshed: reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                MembersRefreshed: reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
                Source: CacheSources.FromDb(reader.GetString(9)),
                LastRefreshed: ParseTime(reader.GetString(10)));
        }
    }

    /// <summary>The cached member handles of an org (empty if none).</summary>
    public IReadOnlyList<string> GetOrgMembers(string sid)
    {
        lock (_gate)
        {
            var handles = new List<string>();
            using var command = Command("SELECT handle FROM org_members WHERE sid = $sid ORDER BY handle", ("$sid", sid));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                handles.Add(reader.GetString(0));
            return handles;
        }
    }

    /// <summary>
    /// True when <paramref name="handle"/> is in the cached member list of the org <paramref name="sid"/> (case-insensitive, an indexed lookup of
    /// the primary key). False when the org has no cached member list.
    /// </summary>
    public bool IsOrgMember(string sid, string handle)
    {
        lock (_gate)
            return ScalarText("SELECT 1 FROM org_members WHERE sid = $sid AND handle = $handle LIMIT 1", ("$sid", sid), ("$handle", handle)) is not null;
    }

    /// <summary>
    /// Org-list caching: stores the org's long name and replaces its member list. Nothing else.
    /// Attribute columns of an existing row are left alone. The source becomes 'org_list'.
    /// </summary>
    public void SaveOrgListEntry(string sid, string longName, IEnumerable<string> memberHandles, DateTimeOffset now)
    {
        InTransaction(() =>
        {
            var time = FormatTime(now);
            var exists = ScalarText("SELECT sid FROM orgs WHERE sid = $sid", ("$sid", sid)) is not null;
            var source = CacheSources.ToDb(CacheSource.OrgList);

            if (exists)
            {
                Execute(
                    "UPDATE orgs SET long_name = $name, members_refreshed = $time, source = $source, last_refreshed = $time WHERE sid = $sid",
                    ("$name", longName), ("$time", time), ("$source", source), ("$sid", sid));
            }
            else
            {
                Execute(
                    "INSERT INTO orgs (sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
                    + "attrs_refreshed, members_refreshed, source, last_refreshed) "
                    + "VALUES ($sid, $name, NULL, NULL, NULL, 0, '', NULL, $time, $source, $time)",
                    ("$sid", sid), ("$name", longName), ("$time", time), ("$source", source));
            }

            Execute("DELETE FROM org_members WHERE sid = $sid", ("$sid", sid));
            foreach (var handle in memberHandles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Execute(
                    "INSERT INTO org_members (sid, handle, last_refreshed) VALUES ($sid, $handle, $time)",
                    ("$sid", sid), ("$handle", handle), ("$time", time));
            }

            Execute("DELETE FROM not_found WHERE kind = 'org' AND name = $sid", ("$sid", sid));
        });
    }

    /// <summary>
    /// The org is now on the user's org list: raise the source of its cached row to 'org_list' (never lowers it).
    /// Nothing else changes, in particular not the refresh times. Does nothing if there is no such row.
    /// </summary>
    public void PromoteOrgSource(string sid, CacheSource source)
    {
        InTransaction(() =>
        {
            var existing = ScalarText("SELECT source FROM orgs WHERE sid = $sid", ("$sid", sid));
            if (existing is null)
                return;
            var current = CacheSources.FromDb(existing);
            if (CacheSources.Stronger(current, source) == current)
                return;
            Execute("UPDATE orgs SET source = $source WHERE sid = $sid", ("$source", CacheSources.ToDb(source)), ("$sid", sid));
        });
    }

    /// <summary>
    /// Hotkey-path caching: stores an org page's attributes (and long name / member count).
    /// The member list is left alone. If the row already has a stronger source, that source is kept.
    /// </summary>
    public void SaveOrgAttributes(OrgInfo info, CacheSource source, DateTimeOffset now)
    {
        InTransaction(() =>
        {
            var time = FormatTime(now);
            var existing = ScalarText("SELECT source FROM orgs WHERE sid = $sid", ("$sid", info.Sid));
            var effective = CacheSources.ToDb(existing is null ? source : CacheSources.Stronger(CacheSources.FromDb(existing), source));
            var activities = JoinActivities(info.Activities);

            if (existing is null)
            {
                Execute(
                    "INSERT INTO orgs (sid, long_name, member_count, archetype, commitment, is_exclusive, activities, "
                    + "attrs_refreshed, members_refreshed, source, last_refreshed) "
                    + "VALUES ($sid, $name, $count, $archetype, $commitment, $exclusive, $activities, $time, NULL, $source, $time)",
                    ("$sid", info.Sid), ("$name", info.LongName), ("$count", info.MemberCount), ("$archetype", info.Archetype),
                    ("$commitment", info.Commitment), ("$exclusive", info.IsExclusive ? 1 : 0), ("$activities", activities),
                    ("$time", time), ("$source", effective));
            }
            else
            {
                Execute(
                    "UPDATE orgs SET long_name = $name, member_count = $count, archetype = $archetype, commitment = $commitment, "
                    + "is_exclusive = $exclusive, activities = $activities, attrs_refreshed = $time, source = $source, last_refreshed = $time "
                    + "WHERE sid = $sid",
                    ("$name", info.LongName), ("$count", info.MemberCount), ("$archetype", info.Archetype),
                    ("$commitment", info.Commitment), ("$exclusive", info.IsExclusive ? 1 : 0), ("$activities", activities),
                    ("$time", time), ("$source", effective), ("$sid", info.Sid));
            }

            Execute("DELETE FROM not_found WHERE kind = 'org' AND name = $sid", ("$sid", info.Sid));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Negative cache
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Remembers that RSI answered 404 for this name, and drops any cached data for it.
    /// Only call this for a definite 404, never for timeouts or other errors.
    /// </summary>
    public void SaveNotFound(NotFoundKind kind, string name, CacheSource source, DateTimeOffset now)
    {
        InTransaction(() =>
        {
            if (kind == NotFoundKind.Player)
            {
                Execute("DELETE FROM player_orgs WHERE handle = $name", ("$name", name));
                Execute("DELETE FROM players WHERE handle = $name", ("$name", name));
            }
            else
            {
                Execute("DELETE FROM org_members WHERE sid = $name", ("$name", name));
                Execute("DELETE FROM orgs WHERE sid = $name", ("$name", name));
            }

            Execute("DELETE FROM not_found WHERE kind = $kind AND name = $name", ("$kind", KindText(kind)), ("$name", name));
            Execute(
                "INSERT INTO not_found (kind, name, source, last_refreshed) VALUES ($kind, $name, $source, $time)",
                ("$kind", KindText(kind)), ("$name", name), ("$source", CacheSources.ToDb(source)), ("$time", FormatTime(now)));
        });
    }

    /// <summary>When the name was recorded as not found, or null if there is no note at all (expired or not).</summary>
    public DateTimeOffset? GetNotFoundTime(NotFoundKind kind, string name)
    {
        lock (_gate)
        {
            var text = ScalarText(
                "SELECT last_refreshed FROM not_found WHERE kind = $kind AND name = $name",
                ("$kind", KindText(kind)), ("$name", name));
            return text is null ? null : ParseTime(text);
        }
    }

    /// <summary>
    /// True while the "no such citizen" note is younger than 30 days.
    /// After that the answer is "unknown": the caller decides whether to ask RSI again
    /// (the rule is: only when that nameplate is encountered again).
    /// </summary>
    public bool IsKnownNotFound(string handle, DateTimeOffset now) => IsKnownNotFound(NotFoundKind.Player, handle, now);

    /// <summary>Same as <see cref="IsKnownNotFound(string, DateTimeOffset)"/> but for an org SID.</summary>
    public bool IsKnownOrgNotFound(string sid, DateTimeOffset now) => IsKnownNotFound(NotFoundKind.Org, sid, now);

    private bool IsKnownNotFound(NotFoundKind kind, string name, DateTimeOffset now)
    {
        var recordedAt = GetNotFoundTime(kind, name);
        return recordedAt is { } recorded && now - recorded < CacheSettings.NotFoundRetention;
    }

    // ---------------------------------------------------------------------------------------
    // Duplicate-name counts (exclusion list)
    // ---------------------------------------------------------------------------------------

    /// <summary>In how many frames this name has been seen on two separate nameplates so far (0 if never).</summary>
    public int GetDuplicateFrames(string name)
    {
        lock (_gate)
        {
            var text = ScalarText("SELECT frames FROM duplicate_name_counts WHERE name = $name", ("$name", name));
            return text is null ? 0 : int.Parse(text, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Adds one frame to the name's count (starting at 1) and returns the new count.</summary>
    public int IncrementDuplicateFrames(string name, DateTimeOffset now)
    {
        int frames = 0;
        InTransaction(() =>
        {
            Execute(
                "INSERT INTO duplicate_name_counts (name, frames, last_seen) VALUES ($name, 1, $time) "
                + "ON CONFLICT(name) DO UPDATE SET frames = frames + 1, last_seen = $time",
                ("$name", name), ("$time", FormatTime(now)));
            var text = ScalarText("SELECT frames FROM duplicate_name_counts WHERE name = $name", ("$name", name));
            frames = text is null ? 0 : int.Parse(text, CultureInfo.InvariantCulture);
        });
        return frames;
    }

    /// <summary>Forgets the name's count (used once the name has been put on the exclusion list).</summary>
    public void DeleteDuplicateFrames(string name)
    {
        InTransaction(() => Execute("DELETE FROM duplicate_name_counts WHERE name = $name", ("$name", name)));
    }

    // ---------------------------------------------------------------------------------------
    // Schema
    // ---------------------------------------------------------------------------------------

    private static readonly string[] CreateStatements =
    {
        "CREATE TABLE players ("
        + "handle TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL)",

        "CREATE TABLE player_orgs ("
        + "handle TEXT NOT NULL COLLATE NOCASE, "
        + "position INTEGER NOT NULL, "
        + "sid TEXT COLLATE NOCASE, "
        + "is_main INTEGER NOT NULL, "
        + "archetype TEXT, "
        + "activities TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (handle, position))",

        "CREATE TABLE orgs ("
        + "sid TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "long_name TEXT, "
        + "member_count INTEGER, "
        + "archetype TEXT, "
        + "commitment TEXT, "
        + "is_exclusive INTEGER NOT NULL, "
        + "activities TEXT NOT NULL, "
        + "attrs_refreshed TEXT, "
        + "members_refreshed TEXT, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL)",

        "CREATE TABLE org_members ("
        + "sid TEXT NOT NULL COLLATE NOCASE, "
        + "handle TEXT NOT NULL COLLATE NOCASE, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (sid, handle))",

        "CREATE TABLE not_found ("
        + "kind TEXT NOT NULL, "
        + "name TEXT NOT NULL COLLATE NOCASE, "
        + "source TEXT NOT NULL, "
        + "last_refreshed TEXT NOT NULL, "
        + "PRIMARY KEY (kind, name))",
    };

    // Added by schema version 2. IF NOT EXISTS keeps the migration safe to run twice.
    private static readonly string[] Version2Statements =
    {
        "CREATE TABLE IF NOT EXISTS duplicate_name_counts ("
        + "name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, "
        + "frames INTEGER NOT NULL, "
        + "last_seen TEXT NOT NULL)",
    };

    private void EnsureSchema()
    {
        lock (_gate)
        {
            var version = Convert.ToInt64(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"The cache file has schema version {version}, but this IRIS only understands up to {CurrentSchemaVersion}. "
                    + "It was probably written by a newer IRIS. Update IRIS or delete the cache file.");
            if (version == CurrentSchemaVersion)
                return;

            // version 0 = brand-new file: create everything. version 1 = an older file: only the 1-to-2 changes.
            // (Later increments: add the next step after the version 2 one.)
            InTransaction(() =>
            {
                if (version == 0)
                    foreach (var statement in CreateStatements)
                        Execute(statement);
                foreach (var statement in Version2Statements)
                    Execute(statement);
                Execute("PRAGMA user_version = " + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            });
        }
    }

    // ---------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------

    private void InTransaction(Action body)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            _transaction = transaction;
            try
            {
                body();
                transaction.Commit();
            }
            finally
            {
                _transaction = null;
            }
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private string? ScalarText(string sql, params (string Name, object? Value)[] parameters)
    {
        var value = Scalar(sql, parameters);
        if (value is null || value is DBNull)
            return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string KindText(NotFoundKind kind) => kind == NotFoundKind.Player ? "player" : "org";

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string JoinActivities(IEnumerable<string> activities) =>
        string.Join(ActivitySeparator, activities.Select(a => a.Replace(ActivitySeparator, "")));

    private static IReadOnlyList<string> SplitActivities(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Split(ActivitySeparator);
}
