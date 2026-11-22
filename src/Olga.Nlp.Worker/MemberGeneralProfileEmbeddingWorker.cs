using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Olga.Nlp.Application;
using Pgvector;

namespace Olga.Nlp.Worker;

public sealed class MemberGeneralProfileEmbeddingWorker(
    NpgsqlDataSource dataSource,
    IEmbeddingProvider embeddings,
    ILogger<MemberGeneralProfileEmbeddingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await FindCandidatesAsync(stoppingToken);
                if (candidates.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    if (!vectors.TryGetValue(candidate.InputHash, out var vector))
                    {
                        vector = await embeddings.EmbedAsync(candidate.ProfileText, stoppingToken);
                        if (vector.Length != 1536) throw new InvalidOperationException("SCI general profile embedding must contain 1536 dimensions.");
                        vectors[candidate.InputHash] = vector;
                    }
                    await SaveAsync(candidate, vector, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "SCI member general-profile embedding cycle failed; it will retry.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<Candidate>> FindCandidatesAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT link.contact_link_pk,link.owner_member_id,link.contact_member_id,state.primary_venture_pk,
                   concat_ws(E'\n',NULLIF(profile.headline,''),NULLIF(profile.role_category,''),NULLIF(profile.professional_summary,'')),
                   profile.headline,profile.role_category,profile.professional_summary,model.preprocessing_version,profile.updated_at
            FROM social.contact_link link
            JOIN iam.member member ON member.member_id=link.contact_member_id AND member.status='ACTIVE' AND member.deleted_at IS NULL
            JOIN core.member_profile profile ON profile.member_id=member.member_id AND profile.status='ACTIVE'
            JOIN social.sci_member_state state ON state.owner_member_id=link.owner_member_id
              AND state.primary_venture_pk IS NOT NULL AND state.primary_intent_set_pk IS NOT NULL
            JOIN nlp.nlp_model_version model ON model.model_version=@model_version AND model.status='ACTIVE'
            LEFT JOIN social.sci_member_state contact_state ON contact_state.owner_member_id=link.contact_member_id
            LEFT JOIN nlp.sci_estimated_contact_embedding current_embedding
              ON current_embedding.contact_link_pk=link.contact_link_pk AND current_embedding.model_version=model.model_version
             AND current_embedding.status='ACTIVE'
            WHERE link.lifecycle_state IN ('INVITED','JOINED','REQUEST_PENDING','CONNECTED')
              AND contact_state.primary_intent_set_pk IS NULL
              AND concat_ws('',profile.headline,profile.role_category,profile.professional_summary)<>''
              AND (current_embedding.sci_estimated_contact_embedding_pk IS NULL OR current_embedding.updated_at<profile.updated_at)
            ORDER BY link.contact_link_pk
            LIMIT 50
            """);
        Add(command, "model_version", embeddings.ModelVersion);
        var result = new List<Candidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var text = reader.GetString(4).Trim();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var profile = JsonSerializer.Serialize(new
            {
                headline = reader.IsDBNull(5) ? null : reader.GetString(5),
                role_category = reader.IsDBNull(6) ? null : reader.GetString(6),
                professional_summary = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
            result.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),text,hash,profile,reader.GetString(8),reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return result;
    }

    private async Task SaveAsync(Candidate candidate, float[] values, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE nlp.sci_estimated_contact_embedding SET status='SUPERSEDED',updated_at=CURRENT_TIMESTAMP
            WHERE contact_link_pk=@contact_link_pk AND model_version=@model_version AND status='ACTIVE';

            INSERT INTO nlp.sci_estimated_contact_embedding(
              estimated_embedding_id,contact_link_pk,input_hash,model_version,preprocessing_version,dimensions,
              embedding,structured_profile_json,status)
            VALUES(@embedding_id,@contact_link_pk,@input_hash,@model_version,@preprocessing_version,1536,
              @embedding,@profile,'ACTIVE')
            ON CONFLICT(contact_link_pk,model_version,input_hash) DO UPDATE SET
              preprocessing_version=EXCLUDED.preprocessing_version,embedding=EXCLUDED.embedding,
              structured_profile_json=EXCLUDED.structured_profile_json,status='ACTIVE',updated_at=CURRENT_TIMESTAMP;

            INSERT INTO nlp.sci_scoring_job(job_id,owner_member_id,venture_pk,trigger_member_id,trigger_type,dedupe_key)
            VALUES(@job_id,@owner_member_id,@venture_pk,@contact_member_id,'CONTACT_PROFILE_CHANGED',@dedupe_key)
            ON CONFLICT(dedupe_key) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("contact_link_pk", candidate.ContactLinkPk);
        Add(command,"model_version",embeddings.ModelVersion); Add(command,"input_hash",candidate.InputHash);
        Add(command,"preprocessing_version",candidate.PreprocessingVersion); Add(command,"owner_member_id",candidate.OwnerMemberId);
        Add(command,"contact_member_id",candidate.ContactMemberId); command.Parameters.AddWithValue("venture_pk",candidate.VenturePk);
        Add(command,"embedding_id",StableId("estimated",$"{candidate.ContactLinkPk}:{embeddings.ModelVersion}:{candidate.InputHash}"));
        var changeKey=$"{candidate.InputHash}:{candidate.ProfileUpdatedAt.ToUnixTimeMilliseconds()}";
        Add(command,"job_id",StableId("job",$"general:{candidate.OwnerMemberId}:{candidate.ContactLinkPk}:{changeKey}"));
        Add(command,"dedupe_key",$"general-profile:{candidate.OwnerMemberId}:{candidate.ContactLinkPk}:{changeKey}");
        command.Parameters.AddWithValue("embedding",new Vector(values));
        command.Parameters.Add("profile",NpgsqlDbType.Jsonb).Value=candidate.ProfileJson;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string StableId(string prefix,string value) =>
        $"{prefix}_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32]}";
    private static void Add(NpgsqlCommand command,string name,string value) => command.Parameters.Add(name,NpgsqlDbType.Varchar).Value=value;

    private sealed record Candidate(long ContactLinkPk,string OwnerMemberId,string ContactMemberId,long VenturePk,
        string ProfileText,string InputHash,string ProfileJson,string PreprocessingVersion,DateTimeOffset ProfileUpdatedAt);
}
