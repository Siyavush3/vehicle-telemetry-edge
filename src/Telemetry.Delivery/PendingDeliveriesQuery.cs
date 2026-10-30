namespace Telemetry.Delivery;

/// <summary>
/// SQL (PostgreSQL) that picks the next batch of vehicle passes to deliver to one endpoint.
/// Table and column names are simplified; the real schema is not part of this repository.
/// </summary>
/// <remarks>
/// Lessons from the first version, where this query caused two production issues:
/// <list type="number">
/// <item>The endpoint was hard-coded inside the SQL, so changing it in the configuration silently
/// broke the "already delivered" check and caused duplicates. It is a parameter here.</item>
/// <item>The "has violations" filter ran in application code <i>after</i> <c>LIMIT</c>. Records without
/// violations were skipped but never marked, so the same oldest batch was selected on every cycle
/// and newer records starved. The filter now runs in SQL, before the limit.</item>
/// </list>
/// It also respects the retry schedule chosen by <see cref="DeliveryPolicy"/> and uses
/// <c>FOR UPDATE SKIP LOCKED</c> so several sender instances can run without sending a record twice.
/// Run it inside a transaction and record the attempt in <c>delivery_log</c> before committing.
/// </remarks>
public static class PendingDeliveriesQuery
{
    public const string Sql = """
        SELECT p.pass_id, p.direction, p.observed_at, p.payload
        FROM vehicle_passes AS p
        WHERE p.observed_at >= @window_start
          AND (NOT @violations_only
               OR jsonb_array_length(COALESCE(p.payload -> 'violations', '[]'::jsonb)) > 0)
          AND NOT EXISTS (
                SELECT 1
                FROM delivery_log AS d
                WHERE d.pass_id   = p.pass_id
                  AND d.direction = p.direction
                  AND d.endpoint  = @endpoint
                  AND (d.is_final OR d.next_attempt_at > now()))
        ORDER BY p.observed_at
        LIMIT @batch_size
        FOR UPDATE OF p SKIP LOCKED;
        """;

    /// <summary>Supporting index so the anti-join stays cheap as the log grows.</summary>
    public const string Index = """
        CREATE INDEX IF NOT EXISTS ix_delivery_log_lookup
            ON delivery_log (pass_id, direction, endpoint)
            INCLUDE (is_final, next_attempt_at);
        """;
}
