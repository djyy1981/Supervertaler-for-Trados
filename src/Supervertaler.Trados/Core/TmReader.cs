using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads translation memories from Supervertaler's SQLite database
    /// (<c>supervertaler.db</c>). Mirrors <see cref="TermbaseReader"/>: both
    /// products already share the same file, so this class just exposes the
    /// existing <c>translation_memories</c> + <c>translation_units</c> tables
    /// to the Trados plugin's <c>SupervertalerTmProvider</c>.
    ///
    /// Phase 2 of the Shared TM work (Trados issue #31). The Workbench-side
    /// opt-in flag (<c>translation_memories.bridged_to_trados</c>) is the
    /// gate: only TMs the user has explicitly ticked in Workbench's TMs tab
    /// are visible here. Defaults to false for every TM so freelance TM
    /// libraries don't leak across product boundaries on upgrade.
    ///
    /// Uses <c>Microsoft.Data.Sqlite</c> to avoid the native-DLL hash
    /// mismatches that bite <c>System.Data.SQLite</c> inside Trados Studio's
    /// plugin process. The connection is opened <see cref="SqliteOpenMode.ReadOnly"/>
    /// so it doesn't fight Workbench's writer process for the WAL.
    ///
    /// Read-only in v1: exact match and concordance search work; fuzzy
    /// matching and write-back land in Phase 3.
    /// </summary>
    public class TmReader : IDisposable
    {
        private SqliteConnection _connection;
        private readonly string _dbPath;
        private bool _disposed;

        public TmReader(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        /// <summary>Last exception message from <see cref="Open"/>, or null on success.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Opens the SQLite database in read-only mode. Returns true on success;
        /// the file may legitimately be absent on a clean install (no Workbench
        /// presence) and the caller should handle false by treating the
        /// translation provider as "no TMs available" rather than as an error.
        /// </summary>
        public bool Open()
        {
            LastError = null;

            if (!File.Exists(_dbPath))
            {
                LastError = "File not found: " + _dbPath;
                return false;
            }

            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();

                _connection = new SqliteConnection(connStr);
                _connection.Open();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _connection?.Dispose();
                _connection = null;
                return false;
            }
        }

        // ─── Bridged TM discovery ─────────────────────────────────────

        /// <summary>
        /// Returns the TMs the user has flagged
        /// <c>bridged_to_trados = 1</c> in Workbench. Empty list when the
        /// column is missing (older Workbench install pre-v1.10.212) or no
        /// TM has been opted in yet. Never throws – callers can treat an
        /// empty list as "no bridged TMs" without further checks.
        /// </summary>
        public List<TmInfo> GetBridgedTms()
        {
            var result = new List<TmInfo>();
            if (_connection == null) return result;

            // Column may not exist on a v1.10.211-and-earlier database that
            // hasn't been opened by v1.10.212+ Workbench yet (the migration
            // runs on Workbench startup). Detect first so the query doesn't
            // crash on those older schemas.
            if (!HasColumn(_connection, "translation_memories", "bridged_to_trados"))
                return result;

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT
                            tm.id,
                            tm.name,
                            tm.tm_id,
                            tm.source_lang,
                            tm.target_lang,
                            tm.description,
                            tm.read_only,
                            tm.is_project_tm,
                            COUNT(tu.id) AS entry_count
                        FROM translation_memories tm
                        LEFT JOIN translation_units tu ON tm.tm_id = tu.tm_id
                        WHERE COALESCE(tm.bridged_to_trados, 0) = 1
                        GROUP BY tm.id
                        ORDER BY tm.is_project_tm DESC, tm.name ASC";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new TmInfo
                            {
                                DbId = reader.GetInt64(0),
                                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                TmId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                SourceLang = reader.IsDBNull(3) ? null : reader.GetString(3),
                                TargetLang = reader.IsDBNull(4) ? null : reader.GetString(4),
                                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                                ReadOnly = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                                IsProjectTm = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                                EntryCount = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Looks up a single bridged TM by its string <c>tm_id</c> (the value
        /// Workbench uses to key <c>translation_units.tm_id</c>). Returns null
        /// if the TM doesn't exist or is no longer bridged.
        /// </summary>
        public TmInfo GetBridgedTmByTmId(string tmId)
        {
            if (_connection == null || string.IsNullOrEmpty(tmId)) return null;
            if (!HasColumn(_connection, "translation_memories", "bridged_to_trados")) return null;

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT
                            tm.id, tm.name, tm.tm_id,
                            tm.source_lang, tm.target_lang, tm.description,
                            tm.read_only, tm.is_project_tm,
                            COUNT(tu.id) AS entry_count
                        FROM translation_memories tm
                        LEFT JOIN translation_units tu ON tm.tm_id = tu.tm_id
                        WHERE tm.tm_id = $tm_id
                          AND COALESCE(tm.bridged_to_trados, 0) = 1
                        GROUP BY tm.id";
                    cmd.Parameters.AddWithValue("$tm_id", tmId);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        return new TmInfo
                        {
                            DbId = reader.GetInt64(0),
                            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            TmId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            SourceLang = reader.IsDBNull(3) ? null : reader.GetString(3),
                            TargetLang = reader.IsDBNull(4) ? null : reader.GetString(4),
                            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                            ReadOnly = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                            IsProjectTm = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                            EntryCount = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        // ─── Search ────────────────────────────────────────────────────

        /// <summary>
        /// Returns up to <paramref name="maxResults"/> exact-source matches
        /// in <paramref name="tmId"/>. "Exact" = byte-for-byte equality on the
        /// <c>source_text</c> column. Hash-first lookup keeps this O(1) at any
        /// TM size; the hash column is indexed in Workbench's schema.
        /// </summary>
        public List<TmMatch> SearchExact(string tmId, string sourceText, int maxResults = 5)
        {
            var result = new List<TmMatch>();
            if (_connection == null || string.IsNullOrEmpty(tmId) || sourceText == null)
                return result;

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT id, source_text, target_text,
                               source_lang, target_lang,
                               created_date, modified_date,
                               usage_count, created_by
                        FROM translation_units
                        WHERE tm_id = $tm_id
                          AND source_text = $src
                        ORDER BY modified_date DESC
                        LIMIT $limit";
                    cmd.Parameters.AddWithValue("$tm_id", tmId);
                    cmd.Parameters.AddWithValue("$src", sourceText);
                    cmd.Parameters.AddWithValue("$limit", maxResults);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(ReadTmMatch(reader, score: 100));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Concordance search via FTS5. Returns up to <paramref name="maxResults"/>
        /// translation units whose source OR target text contains
        /// <paramref name="query"/>. Match score is fixed at 100 for hits;
        /// concordance is a substring/keyword search and doesn't have a
        /// "fuzziness percentage" the way segment-level fuzzy matching does.
        ///
        /// Searches either column based on <paramref name="searchTarget"/>:
        /// false = source-side, true = target-side.
        /// </summary>
        public List<TmMatch> SearchConcordance(
            string tmId,
            string query,
            bool searchTarget,
            int maxResults = 25)
        {
            var result = new List<TmMatch>();
            if (_connection == null || string.IsNullOrEmpty(tmId) || string.IsNullOrEmpty(query))
                return result;

            // FTS5 syntax: wrap in double quotes to treat as a phrase; escape
            // any embedded quotes by doubling them per FTS5 string-literal rules.
            var ftsQuery = "\"" + query.Replace("\"", "\"\"") + "\"";
            var column = searchTarget ? "target_text" : "source_text";

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT tu.id, tu.source_text, tu.target_text,
                               tu.source_lang, tu.target_lang,
                               tu.created_date, tu.modified_date,
                               tu.usage_count, tu.created_by
                        FROM translation_units tu
                        WHERE tu.tm_id = $tm_id
                          AND tu.id IN (
                              SELECT rowid FROM translation_units_fts
                              WHERE " + column + @" MATCH $q
                              LIMIT $limit
                          )
                        ORDER BY tu.modified_date DESC";
                    cmd.Parameters.AddWithValue("$tm_id", tmId);
                    cmd.Parameters.AddWithValue("$q", ftsQuery);
                    cmd.Parameters.AddWithValue("$limit", maxResults);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(ReadTmMatch(reader, score: 100));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // FTS5 may reject some queries (single character, all stop-words,
                // etc.). Don't treat that as a hard failure – just return empty.
            }

            return result;
        }

        // ─── Lifecycle ────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            try { _connection?.Close(); } catch { }
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _disposed = true;
        }

        // ─── Helpers ──────────────────────────────────────────────────

        private static TmMatch ReadTmMatch(SqliteDataReader reader, int score)
        {
            return new TmMatch
            {
                Id = reader.GetInt64(0),
                SourceText = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                TargetText = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                SourceLang = reader.IsDBNull(3) ? null : reader.GetString(3),
                TargetLang = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedDate = reader.IsDBNull(5) ? null : reader.GetString(5),
                ModifiedDate = reader.IsDBNull(6) ? null : reader.GetString(6),
                UsageCount = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                CreatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                Score = score,
            };
        }

        private static bool HasColumn(SqliteConnection conn, string table, string column)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(" + table + ")";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // ─── Plain DTOs (kept in the same file – they exist only for TmReader callers) ───

    /// <summary>
    /// Minimal metadata for a TM exposed to the Trados plugin. Mirrors the
    /// shape of the dict <c>tm_metadata_manager.get_all_tms()</c> returns on
    /// the Workbench side, restricted to the fields the Trados-side provider
    /// actually needs.
    /// </summary>
    public class TmInfo
    {
        public long DbId;
        public string Name;
        public string TmId;
        public string SourceLang;
        public string TargetLang;
        public string Description;
        public bool ReadOnly;
        public bool IsProjectTm;
        public long EntryCount;
    }

    /// <summary>
    /// A single translation-unit hit. <see cref="Score"/> is the percentage
    /// match (100 for exact and concordance hits in v1; fuzzy matching in
    /// Phase 3 will populate this with proper Levenshtein-derived scores).
    /// </summary>
    public class TmMatch
    {
        public long Id;
        public string SourceText;
        public string TargetText;
        public string SourceLang;
        public string TargetLang;
        public string CreatedDate;
        public string ModifiedDate;
        public long UsageCount;
        public string CreatedBy;
        public int Score;
    }
}
