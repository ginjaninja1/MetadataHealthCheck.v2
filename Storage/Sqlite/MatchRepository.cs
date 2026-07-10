using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using SQLitePCL.pretty;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// Phase 1 core tables only: resolution_candidates, evidence, match_results
    /// (§9.1). Remaining tables (identity_cache, negative_cache, api_cache,
    /// anchor_dependencies, review_decisions, etc.) are added as the phases
    /// that need them are built (§21). CRUD idiom mirrors
    /// SqliteFileOrganizationRepository.cs from the reference plugin: named
    /// parameters via TryBind, RunInTransaction for writes, PrepareStatement +
    /// ExecuteQuery for reads.
    /// </summary>
    public class MatchRepository : BaseSqliteRepository, IMatchRepository
    {
        public MatchRepository(string dbPath, StructuredLogger logger) : base(logger)
        {
            DbFilePath = dbPath;
            Initialize();
        }

        public void Initialize()
        {
            using var connection = CreateConnection();
            RunDefaultInitialization(connection);

            connection.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER)");
            bool hasVersion = false;
            using (var stmt = connection.PrepareStatement("SELECT version FROM schema_version LIMIT 1"))
            {
                foreach (var _ in stmt.ExecuteQuery()) { hasVersion = true; break; }
            }
            if (!hasVersion)
                connection.Execute("INSERT INTO schema_version (version) VALUES (1)");

            string[] queries =
            {
                @"CREATE TABLE IF NOT EXISTS resolution_candidates (
                    id TEXT PRIMARY KEY, source_entity_id TEXT, target_system TEXT, target_type TEXT,
                    target_id TEXT, generation_strategy TEXT, generation_query TEXT, created_at TEXT)",

                @"CREATE TABLE IF NOT EXISTS evidence (
                    candidate_id TEXT, evidence_type TEXT, raw_value TEXT, role TEXT,
                    source_track_id TEXT, album_id TEXT, relationship_type TEXT, rationale TEXT)",

                @"CREATE TABLE IF NOT EXISTS match_results (
                    source_system TEXT, source_id TEXT, target_system TEXT, target_type TEXT,
                    target_id TEXT, status TEXT, confidence REAL, margin REAL,
                    scoring_config_version TEXT, decided_at TEXT, decided_by TEXT)",

                "CREATE INDEX IF NOT EXISTS idx_match_results_source ON match_results (source_system, source_id, target_system)",
            };
            connection.RunQueries(queries);
        }

        public void SaveCandidate(Candidate candidate)
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                var sql = "insert into resolution_candidates (id, source_entity_id, target_system, target_type, target_id, generation_strategy, generation_query, created_at) " +
                          "values (@Id, @SourceEntityId, @TargetSystem, @TargetType, @TargetId, @GenerationStrategy, @GenerationQuery, @CreatedAt)";
                using var statement = db.PrepareStatement(sql);
                statement.TryBind("@Id", candidate.Id);
                statement.TryBind("@SourceEntityId", candidate.SourceEntityId);
                statement.TryBind("@TargetSystem", candidate.TargetSystem);
                statement.TryBind("@TargetType", candidate.TargetEntityType);
                statement.TryBind("@TargetId", candidate.TargetId);
                statement.TryBind("@GenerationStrategy", candidate.GenerationStrategy);
                statement.TryBind("@GenerationQuery", candidate.GenerationQuery);
                statement.TryBind("@CreatedAt", candidate.CreatedAt.ToString("O"));
                statement.MoveNext();
            }, TransactionMode);
        }

        public void SaveEvidence(EvidenceRecord evidence)
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                var sql = "insert into evidence (candidate_id, evidence_type, raw_value, role, source_track_id, album_id, relationship_type, rationale) " +
                          "values (@CandidateId, @EvidenceType, @RawValue, @Role, @SourceTrackId, @AlbumId, @RelationshipType, @Rationale)";
                using var statement = db.PrepareStatement(sql);
                statement.TryBind("@CandidateId", evidence.CandidateId);
                statement.TryBind("@EvidenceType", evidence.EvidenceType);
                statement.TryBind("@RawValue", evidence.RawValue);
                statement.TryBind("@Role", evidence.Role);
                statement.TryBind("@SourceTrackId", evidence.SourceTrackId);
                statement.TryBind("@AlbumId", evidence.AlbumId);
                statement.TryBind("@RelationshipType", evidence.RelationshipType);
                statement.TryBind("@Rationale", evidence.Rationale);
                statement.MoveNext();
            }, TransactionMode);
        }

        public void SaveMatchResult(MatchResult result)
        {
            using var connection = CreateConnection();
            connection.RunInTransaction(db =>
            {
                var sql = "insert into match_results (source_system, source_id, target_system, target_type, target_id, status, confidence, margin, scoring_config_version, decided_at, decided_by) " +
                          "values (@SourceSystem, @SourceId, @TargetSystem, @TargetType, @TargetId, @Status, @Confidence, @Margin, @ScoringConfigVersion, @DecidedAt, @DecidedBy)";
                using var statement = db.PrepareStatement(sql);
                statement.TryBind("@SourceSystem", result.SourceSystem);
                statement.TryBind("@SourceId", result.SourceId);
                statement.TryBind("@TargetSystem", result.TargetSystem);
                statement.TryBind("@TargetType", result.TargetEntityType);
                statement.TryBind("@TargetId", result.TargetId);
                statement.TryBind("@Status", result.Status);
                statement.TryBind("@Confidence", result.Confidence);
                statement.TryBind("@Margin", result.Margin);
                statement.TryBind("@ScoringConfigVersion", result.ScoringConfigVersion);
                statement.TryBind("@DecidedAt", result.DecidedAt.ToString("O"));
                statement.TryBind("@DecidedBy", result.DecidedBy);
                statement.MoveNext();
            }, TransactionMode);
        }

        public MatchResult? GetExisting(string sourceSystem, string sourceId, string targetSystem)
        {
            using var connection = CreateConnection(true);
            var sql = "select source_system, source_id, target_system, target_type, target_id, status, confidence, margin, scoring_config_version, decided_at, decided_by " +
                      "from match_results where source_system=@SourceSystem and source_id=@SourceId and target_system=@TargetSystem " +
                      "order by decided_at desc limit 1";
            using var statement = connection.PrepareStatement(sql);
            statement.TryBind("@SourceSystem", sourceSystem);
            statement.TryBind("@SourceId", sourceId);
            statement.TryBind("@TargetSystem", targetSystem);

            foreach (var row in statement.ExecuteQuery())
            {
                return new MatchResult
                {
                    SourceSystem = row.GetString(0),
                    SourceId = row.GetString(1),
                    TargetSystem = row.GetString(2),
                    TargetEntityType = row.GetString(3),
                    TargetId = row.GetString(4),
                    Status = row.GetString(5),
                    Confidence = row.GetDouble(6),
                    Margin = row.GetDouble(7),
                    ScoringConfigVersion = row.GetString(8),
                    DecidedAt = DateTime.TryParse(row.GetString(9), out var d) ? d : default,
                    DecidedBy = row.GetString(10),
                };
            }
            return null;
        }
    }
}