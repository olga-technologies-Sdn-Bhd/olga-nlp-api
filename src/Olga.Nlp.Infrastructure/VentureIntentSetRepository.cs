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

    public Task<PrimaryIntentHistoryPage> ListHistoryAsync(string memberId, string? cursor, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var items = sets.Where(x => x.Value.MemberId == memberId).Take(limit)
            .Select(x => new PrimaryIntentHistoryItem(x.Key, x.Value.VentureId, 1, "ACTIVE", "in-memory", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        return Task.FromResult(new PrimaryIntentHistoryPage(items, null));
    }

    private sealed record SetState(string MemberId, string VentureId, string OfferIntentId, string WantIntentId);
}

public sealed class VentureIntentSetRepository(NpgsqlDataSource dataSource) : IVentureIntentSetRepository
{
    public async Task BeginAsync(string memberId, VentureIntentSetUpsertRequest request, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO nlp.venture_intent_set (
                intent_set_id, venture_pk, owner_member_id, intent_version, raw_text,
                structured_intent_json, tag_fingerprint, previewed_at, valid_until, status)
            SELECT @intent_set_id, v.venture_pk, @member_id,
                   COALESCE((SELECT max(intent_version)+1 FROM nlp.venture_intent_set WHERE venture_pk=v.venture_pk),1),
                   @raw_text, @structured_intent_json, @tag_fingerprint, @previewed_at, @valid_until, 'PROCESSING'
            FROM core.venture v WHERE v.venture_id=@venture_id AND v.owner_member_id=@member_id AND v.status='ACTIVE'
            ON CONFLICT (intent_set_id) DO NOTHING
            """);
        Add(command, "intent_set_id", request.IntentSetId);
        Add(command, "member_id", memberId);
        Add(command, "raw_text", $"OFFER: {request.OfferText}\nWANT: {request.WantText}");
        command.Parameters.Add("structured_intent_json", NpgsqlDbType.Jsonb).Value = request.StructuredIntent.GetRawText();
        Add(command, "tag_fingerprint", request.TagFingerprint!);
        command.Parameters.AddWithValue("previewed_at", request.PreviewedAt!.Value);
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
            ), state_changed AS (
                INSERT INTO social.sci_member_state (
                    state_id, owner_member_id, primary_venture_pk, primary_intent_set_pk,
                    projection_status, current_projection_version, projection_started_at)
                SELECT 'state_' || substr(md5(a.owner_member_id),1,32), a.owner_member_id,
                       a.venture_pk, a.venture_intent_set_pk, 'RECALCULATING', 1, CURRENT_TIMESTAMP
                FROM activated a
                ON CONFLICT (owner_member_id) DO UPDATE SET
                    primary_venture_pk=EXCLUDED.primary_venture_pk,
                    primary_intent_set_pk=EXCLUDED.primary_intent_set_pk,
                    projection_status='RECALCULATING',
                    current_projection_version=social.sci_member_state.current_projection_version + 1,
                    projection_started_at=CURRENT_TIMESTAMP,
                    projection_completed_at=NULL,
                    projection_error_code=NULL,
                    updated_at=CURRENT_TIMESTAMP
                RETURNING owner_member_id, primary_venture_pk, primary_intent_set_pk, current_projection_version
            ), published AS (
                INSERT INTO ops.outbox_event (
                    outbox_event_id, aggregate_type, aggregate_id, event_type, payload_json, occurred_at)
                SELECT 'intent_' || substr(md5(a.intent_set_id),1,32), 'SCI_INTENT', a.intent_set_id,
                       'SciPrimaryIntentChanged.v1',
                       jsonb_build_object(
                           'owner_member_id', a.owner_member_id,
                           'intent_set_id', a.intent_set_id,
                           'projection_version', s.current_projection_version),
                       CURRENT_TIMESTAMP
                FROM activated a JOIN state_changed s ON s.owner_member_id=a.owner_member_id
                ON CONFLICT (outbox_event_id) DO NOTHING
                RETURNING aggregate_id
            )
            SELECT nlp.enqueue_sci_scoring_job(
                'sci_' || substr(md5(a.intent_set_id),1,32), a.owner_member_id, v.venture_id, a.owner_member_id,
                'OWNER_INTENT_READY', 'intent-ready:' || a.intent_set_id, CURRENT_TIMESTAMP)
            FROM activated a
            JOIN core.venture v ON v.venture_pk=a.venture_pk
            JOIN state_changed s ON s.owner_member_id=a.owner_member_id
            CROSS JOIN (SELECT count(*) FROM superseded) completed
            CROSS JOIN (SELECT count(*) FROM published) emitted
            """);
        Add(activate, "intent_set_id", intentSetId);
        Add(activate, "member_id", memberId);
        await activate.ExecuteScalarAsync(ct);
    }

    public async Task<PrimaryIntentHistoryPage> ListHistoryAsync(string memberId, string? cursor, int limit, CancellationToken ct)
    {
        var (afterUpdatedAt, afterId) = DecodeCursor(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT intent.intent_set_id, venture.venture_id, intent.intent_version, intent.status,
                   intent.tag_fingerprint, intent.previewed_at, intent.created_at, intent.updated_at
            FROM nlp.venture_intent_set intent
            JOIN core.venture venture ON venture.venture_pk=intent.venture_pk
            WHERE intent.owner_member_id=@member_id
              AND (@after_updated_at IS NULL OR (intent.updated_at,intent.intent_set_id)<(@after_updated_at,@after_id))
            ORDER BY intent.updated_at DESC, intent.intent_set_id DESC
            LIMIT @take
            """);
        Add(command, "member_id", memberId);
        command.Parameters.Add("after_updated_at", NpgsqlDbType.TimestampTz).Value = afterUpdatedAt is null ? DBNull.Value : afterUpdatedAt.Value;
        command.Parameters.Add("after_id", NpgsqlDbType.Varchar).Value = afterId is null ? DBNull.Value : afterId;
        command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<PrimaryIntentHistoryItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4)?null:reader.GetString(4), reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7)));
        var page = rows.Take(limit).ToArray();
        return new(page, rows.Count > limit ? EncodeCursor(page[^1].UpdatedAt, page[^1].IntentSetId) : null);
    }

    private static string EncodeCursor(DateTimeOffset updatedAt, string intentSetId) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{updatedAt.UtcTicks}\n{intentSetId}"));
    private static (DateTimeOffset? UpdatedAt, string? IntentSetId) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks <= 0 || string.IsNullOrWhiteSpace(parts[1]) || parts[1].Length > 64)
                throw new FormatException();
            return (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
        }
        catch (FormatException) { throw new ArgumentException("PRIMARY_INTENT_HISTORY_CURSOR_INVALID"); }
    }

    private static void Add(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;
}
