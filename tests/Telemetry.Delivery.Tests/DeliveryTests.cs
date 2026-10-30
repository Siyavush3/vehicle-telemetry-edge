using Telemetry.Delivery;
using Xunit;

namespace Telemetry.Delivery.Tests;

public class DeliveryPolicyTests
{
    // Example receiver codes; the real ones come from configuration.
    private static readonly DeliveryPolicy Policy = new(new DeliveryPolicyOptions
    {
        AcceptedCodes = new HashSet<int> { 1, 2 },
        RejectedCodes = new HashSet<int> { 7, 8, 9 },
        MaxAttempts = 4,
        BaseDelay = TimeSpan.FromSeconds(5),
        MaxDelay = TimeSpan.FromSeconds(30),
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Accepted_codes_are_delivered(int code) =>
        Assert.Equal(DeliveryDecision.Delivered, Policy.Decide(1, 200, code).Decision);

    [Fact]
    public void Rejected_code_is_final_without_retries()
    {
        var outcome = Policy.Decide(1, 200, 8);
        Assert.Equal(DeliveryDecision.GiveUp, outcome.Decision);
        Assert.True(outcome.IsFinal);
    }

    [Theory]
    [InlineData(200, 99)]     // unknown application code: v1 looped on this forever
    [InlineData(200, null)]   // body could not be parsed
    [InlineData(500, null)]
    [InlineData(503, null)]
    [InlineData(429, null)]
    [InlineData(null, null)]  // no response at all
    public void Transient_failures_are_retried_with_backoff(int? http, int? code)
    {
        var outcome = Policy.Decide(1, http, code);
        Assert.Equal(DeliveryDecision.Retry, outcome.Decision);
        Assert.Equal(TimeSpan.FromSeconds(5), outcome.RetryAfter);
        Assert.False(outcome.IsFinal);
    }

    [Fact]
    public void Every_answer_becomes_final_once_attempts_run_out()
    {
        foreach (var (http, code) in new (int?, int?)[] { (200, 99), (500, null), (null, null), (401, null) })
            Assert.True(Policy.Decide(4, http, code).IsFinal, $"http={http}, code={code}");
    }

    [Fact]
    public void Unauthorized_triggers_token_refresh()
    {
        Assert.Equal(DeliveryDecision.RefreshTokenAndRetry, Policy.Decide(1, 401, null).Decision);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(422)]
    public void Client_errors_are_not_retried(int http)
    {
        Assert.Equal(DeliveryDecision.GiveUp, Policy.Decide(1, http, null).Decision);
    }

    [Fact]
    public void Backoff_doubles_and_is_capped()
    {
        Assert.Equal(
            new[] { 5, 10, 20, 30, 30 },
            Enumerable.Range(1, 5).Select(a => (int)Policy.Backoff(a).TotalSeconds));
    }
}

public class TokenProviderTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (TokenProvider Provider, ManualClock Clock, Func<int> Calls) Create(TimeSpan? expiresIn)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        int calls = 0;
        var provider = new TokenProvider(
            _ => Task.FromResult(new AccessToken($"token-{Interlocked.Increment(ref calls)}", expiresIn)),
            clock,
            refreshMargin: TimeSpan.FromSeconds(60),
            fallbackLifetime: TimeSpan.FromMinutes(14));
        return (provider, clock, () => calls);
    }

    [Fact]
    public async Task Reuses_token_until_refresh_margin()
    {
        var (provider, clock, calls) = Create(TimeSpan.FromMinutes(15));
        Assert.Equal("token-1", await provider.GetAsync());

        clock.Now += TimeSpan.FromMinutes(13);
        Assert.Equal("token-1", await provider.GetAsync());

        clock.Now += TimeSpan.FromMinutes(1); // 14 min = 15 min lifetime − 60 s margin
        Assert.Equal("token-2", await provider.GetAsync());
        Assert.Equal(2, calls());
    }

    [Fact]
    public async Task Falls_back_when_server_lifetime_is_missing_or_too_short()
    {
        var (provider, clock, _) = Create(TimeSpan.FromSeconds(30));
        await provider.GetAsync();
        clock.Now += TimeSpan.FromMinutes(13);
        Assert.Equal("token-1", await provider.GetAsync()); // fallback lifetime of 14 min applies
    }

    [Fact]
    public async Task Invalidate_forces_a_new_token()
    {
        var (provider, _, calls) = Create(TimeSpan.FromMinutes(15));
        await provider.GetAsync();
        provider.Invalidate();
        Assert.Equal("token-2", await provider.GetAsync());
        Assert.Equal(2, calls());
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var (provider, _, calls) = Create(TimeSpan.FromMinutes(15));
        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAsync()));
        Assert.All(tokens, t => Assert.Equal("token-1", t));
        Assert.Equal(1, calls());
    }
}
