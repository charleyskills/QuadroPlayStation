using SyncAudio.Services.Sync;
using Xunit;

namespace SyncAudio.Tests.Sync;

public class PlayLatencySmootherTests
{
    [Fact]
    public void First_accepted_sample_becomes_the_smoothed_value()
    {
        var s = new PlayLatencySmoother();

        Assert.True(s.TrySubmit(120));

        Assert.Equal(120, s.SmoothedMs);
    }

    [Fact]
    public void Second_sample_blends_evenly_with_first()
    {
        var s = new PlayLatencySmoother();

        s.TrySubmit(100);
        s.TrySubmit(200);

        Assert.Equal(150, s.SmoothedMs);
    }

    [Fact]
    public void Repeated_sampling_converges_toward_steady_input()
    {
        var s = new PlayLatencySmoother();

        s.TrySubmit(1000);
        for (var i = 0; i < 20; i++) s.TrySubmit(50);

        Assert.InRange(s.SmoothedMs, 50, 51);
    }

    [Fact]
    public void Negative_measurement_is_rejected_without_changing_state()
    {
        var s = new PlayLatencySmoother();
        s.TrySubmit(80);

        var accepted = s.TrySubmit(-1);

        Assert.False(accepted);
        Assert.Equal(80, s.SmoothedMs);
    }

    [Fact]
    public void Measurement_above_max_threshold_is_rejected()
    {
        var s = new PlayLatencySmoother();
        s.TrySubmit(80);

        var accepted = s.TrySubmit(5001);

        Assert.False(accepted);
        Assert.Equal(80, s.SmoothedMs);
    }

    [Fact]
    public void Boundary_measurements_are_accepted()
    {
        var s = new PlayLatencySmoother();

        Assert.True(s.TrySubmit(0));
        Assert.True(s.TrySubmit(5000));
    }

    [Fact]
    public void Rejection_before_first_accepted_sample_keeps_smoother_uninitialized()
    {
        var s = new PlayLatencySmoother();

        s.TrySubmit(-50);
        s.TrySubmit(8000);
        s.TrySubmit(75);

        Assert.Equal(75, s.SmoothedMs);
    }

    [Fact]
    public void Reset_clears_state_so_next_sample_seeds_again()
    {
        var s = new PlayLatencySmoother();
        s.TrySubmit(100);
        s.TrySubmit(200);

        s.Reset();
        s.TrySubmit(42);

        Assert.Equal(42, s.SmoothedMs);
    }

    [Fact]
    public void Rounding_uses_away_from_zero_to_match_javascript_math_round()
    {
        var s = new PlayLatencySmoother();
        s.TrySubmit(100);
        s.TrySubmit(101);

        Assert.Equal(101, s.SmoothedMs);
    }

    [Fact]
    public void Custom_weight_biases_toward_history_when_low()
    {
        var s = new PlayLatencySmoother(newSampleWeight: 0.1);
        s.TrySubmit(1000);
        s.TrySubmit(0);

        Assert.Equal(900, s.SmoothedMs);
    }

    [Fact]
    public void Constructor_rejects_invalid_weight()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayLatencySmoother(newSampleWeight: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayLatencySmoother(newSampleWeight: -0.1));
    }
}
