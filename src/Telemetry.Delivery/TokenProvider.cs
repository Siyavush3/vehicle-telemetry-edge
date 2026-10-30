namespace Telemetry.Delivery;

/// <summary>An access token and the lifetime the server reported for it.</summary>
public sealed record AccessToken(string Value, TimeSpan? ExpiresIn);

/// <summary>
/// Caches an access token and refreshes it shortly before it expires.
/// Thread-safe: concurrent callers share a single refresh request.
/// </summary>
/// <remarks>
/// Compared with the first version: the token can be invalidated when the receiver
/// answers 401 (instead of failing until the cached expiry time), the clock is
/// injectable for tests, and the token value is never written to logs.
/// </remarks>
public sealed class TokenProvider(
    Func<CancellationToken, Task<AccessToken>> fetchToken,
    TimeProvider clock,
    TimeSpan refreshMargin,
    TimeSpan fallbackLifetime)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (IsValid()) return _token!;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsValid()) return _token!; // another caller refreshed it while we waited

            var fresh = await fetchToken(ct);
            if (string.IsNullOrWhiteSpace(fresh.Value))
                throw new InvalidOperationException("Token endpoint returned an empty token.");

            // Trust the server's lifetime only if it leaves room for the safety margin.
            var lifetime = fresh.ExpiresIn is { } reported && reported > refreshMargin
                ? reported - refreshMargin
                : fallbackLifetime;

            _token = fresh.Value;
            _expiresAt = clock.GetUtcNow() + lifetime;
            return _token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Forget the cached token, e.g. after the receiver answered 401.</summary>
    public void Invalidate()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private bool IsValid() => _token is not null && clock.GetUtcNow() < _expiresAt;
}
