using SyncAudio.Core.Services.Sync;
using Xunit;

namespace SyncAudio.Tests.Sync;

public class ClockSyncFilterTests
{
    [Fact]
    public void Picks_upper_middle_offset_among_six_lowest_rtt_samples()
    {
        var samples = new[]
        {
            new ClockSample(10, 100),
            new ClockSample(15, 110),
            new ClockSample(20, 120),
            new ClockSample(25, 130),
            new ClockSample(30, 140),
            new ClockSample(35, 150),
            new ClockSample(200, 9999),
            new ClockSample(210, 9999),
            new ClockSample(220, 9999),
            new ClockSample(230, 9999),
            new ClockSample(240, 9999),
            new ClockSample(250, 9999),
        };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.Equal(130, offset);
    }

    [Fact]
    public void Excludes_high_rtt_samples_even_when_their_offsets_are_extreme()
    {
        var samples = new[]
        {
            new ClockSample(5, 50),
            new ClockSample(6, 51),
            new ClockSample(7, 52),
            new ClockSample(8, 53),
            new ClockSample(9, 54),
            new ClockSample(10, 55),
            new ClockSample(500, -100000),
            new ClockSample(600, 100000),
            new ClockSample(700, -50000),
            new ClockSample(800, 50000),
            new ClockSample(900, -25000),
            new ClockSample(1000, 25000),
        };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.InRange(offset, 50, 55);
    }

    [Fact]
    public void Returns_offset_corresponding_to_unsorted_input_order_when_tied_rtts()
    {
        var samples = new[]
        {
            new ClockSample(10, 1),
            new ClockSample(10, 2),
            new ClockSample(10, 3),
            new ClockSample(10, 4),
            new ClockSample(10, 5),
            new ClockSample(10, 6),
        };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.Equal(4, offset);
    }

    [Fact]
    public void Falls_back_to_available_count_when_fewer_than_six_samples_supplied()
    {
        var samples = new[]
        {
            new ClockSample(5, 10),
            new ClockSample(6, 20),
            new ClockSample(7, 30),
        };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.Equal(20, offset);
    }

    [Fact]
    public void Single_sample_returns_its_own_offset()
    {
        var samples = new[] { new ClockSample(100, 42.5) };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.Equal(42.5, offset);
    }

    [Fact]
    public void Empty_sample_set_throws()
    {
        Assert.Throws<ArgumentException>(() => ClockSyncFilter.Filter(Array.Empty<ClockSample>()));
    }

    [Fact]
    public void Negative_offsets_are_handled_correctly()
    {
        var samples = new[]
        {
            new ClockSample(1, -300),
            new ClockSample(2, -200),
            new ClockSample(3, -100),
            new ClockSample(4, 0),
            new ClockSample(5, 100),
            new ClockSample(6, 200),
        };

        var offset = ClockSyncFilter.Filter(samples);

        Assert.Equal(0, offset);
    }
}
