using SyncAudio.Core.Services.Sync;
using Xunit;

namespace SyncAudio.Tests.Sync;

public class AdaptiveLeadCalculatorTests
{
    private const long Now = 1_000_000;

    [Fact]
    public void Empty_member_list_returns_floor_lead_with_no_pending_remaining()
    {
        var result = AdaptiveLeadCalculator.Compute([], Now, fallbackLeadSeconds: 5.0);

        Assert.Equal(AdaptiveLeadCalculator.MinLeadMs, result.LeadMs);
        Assert.Equal(0, result.SlowestRemainingMs);
        Assert.False(result.CapHit);
    }

    [Fact]
    public void Fallback_below_min_lead_is_floored_to_min_lead()
    {
        var result = AdaptiveLeadCalculator.Compute([], Now, fallbackLeadSeconds: 1.0);

        Assert.Equal(AdaptiveLeadCalculator.MinLeadMs, result.LeadMs);
    }

    [Fact]
    public void Fallback_above_min_lead_is_used_as_floor()
    {
        var result = AdaptiveLeadCalculator.Compute([], Now, fallbackLeadSeconds: 8.0);

        Assert.Equal(8_000, result.LeadMs);
    }

    [Fact]
    public void Member_at_full_progress_contributes_no_remaining_time()
    {
        var sample = new ProgressSample(0.0, Now - 1_000, 1.0, Now - 200);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 5.0);

        Assert.Equal(AdaptiveLeadCalculator.MinLeadMs, result.LeadMs);
        Assert.Equal(0, result.SlowestRemainingMs);
        Assert.False(result.CapHit);
    }

    [Fact]
    public void Member_at_half_progress_after_5s_projects_5s_remaining_plus_decode_buffer()
    {
        // 0 -> 0.5 over 5_000 ms -> remaining 0.5 should also take ~5_000 ms.
        var sample = new ProgressSample(0.0, Now - 5_000, 0.5, Now);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 3.0);

        Assert.InRange(result.SlowestRemainingMs, 4_999, 5_001);
        Assert.Equal(5_000 + AdaptiveLeadCalculator.DecodeBufferMs, result.LeadMs);
        Assert.False(result.CapHit);
    }

    [Fact]
    public void Slow_member_pushes_lead_above_min_but_below_cap()
    {
        // 0 -> 0.4 over 5_000 ms -> remaining 0.6 takes 7_500 ms.
        var sample = new ProgressSample(0.0, Now - 5_000, 0.4, Now);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 3.0);

        Assert.InRange(result.SlowestRemainingMs, 7_499, 7_501);
        Assert.Equal(7_500 + AdaptiveLeadCalculator.DecodeBufferMs, result.LeadMs);
        Assert.False(result.CapHit);
    }

    [Fact]
    public void Member_too_slow_to_finish_within_cap_returns_capped_lead_and_flags_caphit()
    {
        // 0 -> 0.1 over 5_000 ms -> remaining 0.9 takes 45_000 ms (above 30s cap).
        var sample = new ProgressSample(0.0, Now - 5_000, 0.1, Now);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 3.0);

        Assert.True(result.SlowestRemainingMs >= 45_000);
        Assert.Equal(AdaptiveLeadCalculator.MaxLeadMs, result.LeadMs);
        Assert.True(result.CapHit);
        Assert.Equal(0.1, result.SlowestFraction, precision: 5);
    }

    [Fact]
    public void Multiple_members_use_the_slowest_for_lead_estimation()
    {
        // Fast: 0 -> 0.9 over 5_000 ms -> ~556 ms remaining.
        var fast = new ProgressSample(0.0, Now - 5_000, 0.9, Now);
        // Slow: 0 -> 0.5 over 5_000 ms -> 5_000 ms remaining.
        var slow = new ProgressSample(0.0, Now - 5_000, 0.5, Now);

        var result = AdaptiveLeadCalculator.Compute([fast, slow], Now, fallbackLeadSeconds: 3.0);

        Assert.InRange(result.SlowestRemainingMs, 4_999, 5_001);
        Assert.Equal(0.5, result.SlowestFraction, precision: 5);
        Assert.Equal(5_000 + AdaptiveLeadCalculator.DecodeBufferMs, result.LeadMs);
    }

    [Fact]
    public void Member_with_no_useful_sample_pessimistically_assumes_max_remaining()
    {
        // Empty sample (just joined, no real data yet) — extrapolation is undefined.
        var sample = ProgressSample.Empty(Now);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 3.0);

        Assert.Equal(AdaptiveLeadCalculator.MaxLeadMs, result.SlowestRemainingMs);
        Assert.Equal(AdaptiveLeadCalculator.MaxLeadMs, result.LeadMs);
        Assert.True(result.CapHit);
    }

    [Fact]
    public void Stuck_member_with_no_progress_growth_pessimistically_assumes_max_remaining()
    {
        // Reported 0.3 once and never moved — no observable rate.
        var sample = new ProgressSample(0.3, Now - 10_000, 0.3, Now - 10_000);

        var result = AdaptiveLeadCalculator.Compute([sample], Now, fallbackLeadSeconds: 3.0);

        Assert.Equal(AdaptiveLeadCalculator.MaxLeadMs, result.SlowestRemainingMs);
        Assert.True(result.CapHit);
    }
}
