namespace Telemetry.Delivery;

public enum DeliveryDecision
{
    /// <summary>The receiver accepted the record; mark it delivered.</summary>
    Delivered,
    /// <summary>Transient failure; try again after <see cref="DeliveryOutcome.RetryAfter"/>.</summary>
    Retry,
    /// <summary>The access token was rejected; refresh it and retry immediately.</summary>
    RefreshTokenAndRetry,
    /// <summary>Permanent failure (rejected data or retries exhausted); stop and park the record.</summary>
    GiveUp,
}

public sealed record DeliveryOutcome(DeliveryDecision Decision, string Reason, TimeSpan RetryAfter = default)
{
    /// <summary>True when the record should never be selected for this endpoint again.</summary>
    public bool IsFinal => Decision is DeliveryDecision.Delivered or DeliveryDecision.GiveUp;
}

/// <summary>Receiver-specific result codes. Kept in configuration, not in code.</summary>
public sealed record DeliveryPolicyOptions
{
    /// <summary>Application codes meaning "accepted" (including "already accepted" duplicates).</summary>
    public IReadOnlySet<int> AcceptedCodes { get; init; } = new HashSet<int>();
    /// <summary>Application codes meaning the data itself was rejected; retrying cannot help.</summary>
    public IReadOnlySet<int> RejectedCodes { get; init; } = new HashSet<int>();
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Decides what to do after one delivery attempt to the external API.
/// </summary>
/// <remarks>
/// The first production version only distinguished "accepted", a fixed list of
/// "rejected" codes and "no response". Any other answer (an unknown code, HTTP 5xx,
/// an unparsable body) left the record unfinished, so it was retried in a tight loop
/// and re-selected on every polling cycle forever. Here every possible answer maps
/// to exactly one decision, transient failures back off exponentially, and the
/// attempt budget is enforced so a record always reaches a final state.
/// </remarks>
public sealed class DeliveryPolicy(DeliveryPolicyOptions options)
{
    /// <param name="attempt">1-based number of the attempt that just finished.</param>
    /// <param name="httpStatus">HTTP status, or null if no response arrived (network error, timeout).</param>
    /// <param name="apiCode">Application result code from the body, or null if it could not be parsed.</param>
    public DeliveryOutcome Decide(int attempt, int? httpStatus, int? apiCode)
    {
        if (httpStatus is >= 200 and < 300 && apiCode is int code)
        {
            if (options.AcceptedCodes.Contains(code))
                return new(DeliveryDecision.Delivered, $"accepted (code {code})");
            if (options.RejectedCodes.Contains(code))
                return new(DeliveryDecision.GiveUp, $"rejected by receiver (code {code})");
        }

        if (httpStatus == 401)
            return attempt < options.MaxAttempts
                ? new(DeliveryDecision.RefreshTokenAndRetry, "token rejected (401)")
                : new(DeliveryDecision.GiveUp, "token rejected repeatedly");

        if (httpStatus is >= 400 and < 500 and not 408 and not 429)
            return new(DeliveryDecision.GiveUp, $"client error {httpStatus}; request will not succeed as is");

        string reason = httpStatus switch
        {
            null => "no response",
            >= 200 and < 300 => apiCode is null ? "unparsable response body" : $"unknown result code {apiCode}",
            _ => $"HTTP {httpStatus}",
        };

        return attempt >= options.MaxAttempts
            ? new(DeliveryDecision.GiveUp, $"{reason}; gave up after {attempt} attempts")
            : new(DeliveryDecision.Retry, reason, Backoff(attempt));
    }

    /// <summary>Exponential backoff: base, 2×base, 4×base… capped at <see cref="DeliveryPolicyOptions.MaxDelay"/>.</summary>
    public TimeSpan Backoff(int attempt)
    {
        double factor = Math.Pow(2, Math.Max(0, attempt - 1));
        double ms = Math.Min(options.BaseDelay.TotalMilliseconds * factor, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}
