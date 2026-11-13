using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;
using Olga.Nlp.Application;
using Olga.Nlp.Contracts;
using Olga.Nlp.Domain;

namespace Olga.Nlp.Infrastructure;

public sealed class InMemoryVentureIntentSetRepository : IVentureIntentSetRepository
{
    private readonly ConcurrentDictionary<string, SetState> sets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> intentSets = new(StringComparer.Ordinal);

    public Task BeginAsync(string memberId, VentureIntentSetUpsertRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = new SetState(memberId, request.VentureId, request.OfferIntentId, request.WantIntentId);
        var stored = sets.GetOrAdd(request.IntentSetId, state);
        if (stored != state)
            throw new DomainConflictException("VENTURE_INTENT_SET_CONFLICT");
        return Task.CompletedTask;
    }

    public Task BindIntentAsync(string memberId, string intentSetId, string intentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!sets.TryGetValue(intentSetId, out var state)
            || !string.Equals(state.MemberId, memberId, StringComparison.Ordinal)
            || intentId != state.OfferIntentId && intentId != state.WantIntentId
            || !string.Equals(intentSets.GetOrAdd(intentId, intentSetId), intentSetId, StringComparison.Ordinal))
            throw new DomainConflictException("VENTURE_INTENT_BIND_FAILED");
        return Task.CompletedTask;
    }

    private sealed record SetState(string MemberId, string VentureId, string OfferIntentId, string WantIntentId);
}

public sealed class VentureIntentSetRepository(NpgsqlDataSource dataSource) : IVentureIntentSetRepository
{
    public async Task BeginAsync(string memberId, VentureIntentSetUpsertRequest request, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO nlp.venture_intent_set (intent_set_id, venture_pk, owner_member_id, intent_version, raw_text, structured_intent_json, valid_until, status)
            SELECT @intent_set_id, v.venture_pk, @member_id,
                   COALESCE((SELECT max(intent_version)+1 FROM nlp.venture_intent_set WHERE venture_pk=v.venture_pk),1),
                   @raw_text, @structured_intent_json, @valid_until, 'PROCESSING'
            FROM core.venture v WHERE v.venture_id=@venture_id AND v.owner_member_id=@member_id AND v.status='ACTIVE'
            ON CONFLICT (intent_set_id) DO NOTHING
            """);
        Add(command, "intent_set_id", request.IntentSetId);
        Add(command, "member_id", memberId);
        Add(command, "raw_text", $"OFFER: {request.OfferText}\nWANT: {request.WantText}");
        command.Parameters.Add("structured_intent_json", NpgsqlDbType.Jsonb).Value = request.StructuredIntent.GetRawText();
        command.Parameters.AddWithValue("valid_until", request.ExpiresAt);
        Add(command, "venture_id", request.VentureId);

        if (await command.ExecuteNonQueryAsync(ct) != 0) return;

        await using var replay = dataSource.CreateCommand("""
            SELECT count(*) FROM nlp.venture_intent_set s
            JOIN core.venture v ON v.venture_pk=s.venture_pk
            WHERE s.intent_set_id=@intent_set_id AND s.owner_member_id=@member_id AND v.venture_id=@venture_id
            """);
        Add(replay, "intent_set_id", request.IntentSetId);
        Add(replay, "member_id", memberId);
        Add(replay, "venture_id", request.VentureId);
        if (Convert.ToInt32(await replay.ExecuteScalarAsync(ct)) != 1)
            throw new DomainConflictException("VENTURE_INTENT_SET_CONFLICT");
    }

    public async Task BindIntentAsync(string memberId, string intentSetId, string intentId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE nlp.nlp_intent i SET venture_intent_set_pk=s.venture_intent_set_pk
            FROM nlp.venture_intent_set s
            WHERE s.intent_set_id=@intent_set_id AND s.owner_member_id=@member_id
              AND i.intent_id=@intent_id AND i.member_id=@member_id
              AND i.context_id=(SELECT venture_id FROM core.venture WHERE venture_pk=s.venture_pk)
              AND (i.venture_intent_set_pk IS NULL OR i.venture_intent_set_pk=s.venture_intent_set_pk)
            """);
        Add(command, "intent_set_id", intentSetId);
        Add(command, "member_id", memberId);
        Add(command, "intent_id", intentId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new DomainConflictException("VENTURE_INTENT_BIND_FAILED");

        await using var activate = dataSource.CreateCommand("""
            WITH activated AS (
                UPDATE nlp.venture_intent_set vis SET status='ACTIVE', updated_at=CURRENT_TIMESTAMP
                WHERE vis.intent_set_id=@intent_set_id AND vis.owner_member_id=@member_id AND vis.status='PROCESSING'
                  AND 2=(SELECT count(DISTINCT i.intent_type) FROM nlp.nlp_intent i
                         WHERE i.venture_intent_set_pk=vis.venture_intent_set_pk AND i.intent_type IN ('OFFER','WANT')
                           AND i.status='MATCH_READY' AND i.expires_at>CURRENT_TIMESTAMP)
                RETURNING vis.venture_intent_set_pk, vis.owner_member_id, vis.venture_pk, vis.intent_set_id
            ), superseded AS (
                UPDATE nlp.venture_intent_set previous
                   SET status='SUPERSEDED', updated_at=CURRENT_TIMESTAMP
                  FROM activated a
                 WHERE previous.venture_pk=a.venture_pk
                   AND previous.venture_intent_set_pk<>a.venture_intent_set_pk
                   AND previous.status='ACTIVE'
                RETURNING previous.venture_intent_set_pk
            )
            SELECT nlp.enqueue_sci_scoring_job(
                'sci_' || substr(md5(a.intent_set_id),1,32), a.owner_member_id, v.venture_id, a.owner_member_id,
                'OWNER_INTENT_READY', 'intent-ready:' || a.intent_set_id, CURRENT_TIMESTAMP)
            FROM activated a
            JOIN core.venture v ON v.venture_pk=a.venture_pk
            CROSS JOIN (SELECT count(*) FROM superseded) completed
            """);
        Add(activate, "intent_set_id", intentSetId);
        Add(activate, "member_id", memberId);
        await activate.ExecuteScalarAsync(ct);
    }

    private static void Add(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;
}
