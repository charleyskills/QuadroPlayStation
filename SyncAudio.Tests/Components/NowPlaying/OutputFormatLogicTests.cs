using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SyncAudio.Components.Pages.NowPlaying;
using SyncAudio.Components.Pages.NowPlaying.StateMachines;
using SyncAudio.Models;
using SyncAudio.Services;
using SyncAudio.Services.TrackSplit;
using Xunit;

namespace SyncAudio.Tests.Components.NowPlaying;

/// <summary>
/// Behavioural tests for <see cref="NowPlayingLogic.SetOutputFormatAsync"/> — the
/// FORMAT button's entry point. Verifies validation, idempotency, peer broadcast,
/// re-split, and the play-restart cycle.
/// </summary>
public class OutputFormatLogicTests
{
    private static (NowPlayingLogic logic, NowPlayingState state, FakeSplitter splitter,
        Recorder recorder) BuildLogic(Track? initialTrack = null, bool joined = false, bool playing = false)
    {
        var library = new FakeLibrary(initialTrack);
        var splitter = new FakeSplitter();
        var state = new NowPlayingState();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logic = new NowPlayingLogic(library, splitter, NullLogger<NowPlayingLogic>.Instance, state, cache);

        if (initialTrack is not null) state.CurrentTrack = initialTrack;
        if (joined) state.Machine.Fire(LocalDeviceTrigger.Joined);
        if (playing)
        {
            // Drive the machine into Playing so IsPlaying / IsJoined are true.
            if (state.Machine.State == LocalDeviceState.Disconnected)
                state.Machine.Fire(LocalDeviceTrigger.Joined);
            state.Machine.Fire(LocalDeviceTrigger.TrackSelected);
            state.Machine.Fire(LocalDeviceTrigger.SplitDone);
            state.Machine.Fire(LocalDeviceTrigger.BufferReady);
            state.Machine.Fire(LocalDeviceTrigger.PlayStarted);
        }

        var recorder = new Recorder();
        // EventCallback wiring: a single-shot test receiver per signal.
        logic.FormatSelectionBroadcastRequested =
            new EventCallback<string>(null, (Action<string>)(f => recorder.BroadcastFormats.Add(f)));
        logic.PlayRequested =
            new EventCallback(null, (Action)(() => recorder.PlayRequestedCount++));
        logic.CurrentTrackOnChanged =
            new EventCallback<Track>(null, (Action<Track>)(_ => recorder.TrackChangedCount++));
        logic.OnStateHasChanged =
            new EventCallback(null, (Action)(() => { /* ignore */ }));

        return (logic, state, splitter, recorder);
    }

    private static Track MakeTrack(string id = "t1") => new(
        Id: id, Title: "T", Artist: "A", Album: "Alb",
        AudioUrl: "/audio/t.wav", Duration: TimeSpan.FromSeconds(60),
        Palette: ["#000"], Lossless: false, Spatial: false,
        CoverUrl: null, Source: TrackSource.Local, RemoteSourceUrl: null);

    [Fact]
    public void Default_OutputFormat_is_mp3()
    {
        var s = new NowPlayingState();
        Assert.Equal("mp3", s.OutputFormat);
    }

    [Fact]
    public async Task SetOutputFormat_unknown_value_is_ignored()
    {
        var (logic, state, splitter, rec) = BuildLogic(MakeTrack());

        await logic.SetOutputFormatAsync("ogg");

        Assert.Equal("mp3", state.OutputFormat);
        Assert.Empty(splitter.Calls);
        Assert.Empty(rec.BroadcastFormats);
        Assert.Equal(0, rec.PlayRequestedCount);
    }

    [Fact]
    public async Task SetOutputFormat_same_value_is_noop()
    {
        var (logic, state, splitter, rec) = BuildLogic(MakeTrack(), joined: true);

        await logic.SetOutputFormatAsync("mp3");

        Assert.Equal("mp3", state.OutputFormat);
        Assert.Empty(splitter.Calls);
        Assert.Empty(rec.BroadcastFormats);
    }

    [Fact]
    public async Task SetOutputFormat_when_idle_updates_state_and_resplits_but_does_not_play()
    {
        var (logic, state, splitter, rec) = BuildLogic(MakeTrack(), joined: true);

        await logic.SetOutputFormatAsync("flac");

        Assert.Equal("flac", state.OutputFormat);
        // Joined → broadcast fires.
        Assert.Equal(["flac"], rec.BroadcastFormats);
        // Re-split runs against the new format.
        Assert.Single(splitter.Calls);
        Assert.Equal("flac", splitter.Calls[0].Format);
        // Was not playing → no restart.
        Assert.Equal(0, rec.PlayRequestedCount);
    }

    [Fact]
    public async Task SetOutputFormat_when_playing_resplits_AND_fires_PlayRequested()
    {
        var (logic, state, splitter, rec) = BuildLogic(MakeTrack(), playing: true);

        await logic.SetOutputFormatAsync("flac");

        Assert.Equal("flac", state.OutputFormat);
        Assert.Equal(["flac"], rec.BroadcastFormats);
        Assert.Single(splitter.Calls);
        Assert.Equal("flac", splitter.Calls[0].Format);
        Assert.Equal(1, rec.PlayRequestedCount);
    }

    [Fact]
    public async Task MirrorRemoteFormat_resplits_but_never_fires_PlayRequested()
    {
        // Peer-side: receives broadcast; should never re-trigger play (origin drives that).
        var (logic, state, splitter, rec) = BuildLogic(MakeTrack(), playing: true);

        await logic.MirrorRemoteFormatChangeAsync("flac");

        Assert.Equal("flac", state.OutputFormat);
        Assert.Single(splitter.Calls);
        Assert.Equal("flac", splitter.Calls[0].Format);
        Assert.Equal(0, rec.PlayRequestedCount);
        // Mirror path must NOT echo back another broadcast.
        Assert.Empty(rec.BroadcastFormats);
    }

    [Fact]
    public async Task MirrorRemoteFormat_unknown_value_is_ignored()
    {
        var (logic, state, splitter, _) = BuildLogic(MakeTrack(), playing: true);

        await logic.MirrorRemoteFormatChangeAsync("ogg");

        Assert.Equal("mp3", state.OutputFormat);
        Assert.Empty(splitter.Calls);
    }

    // ─── fakes ─────────────────────────────────────────────────────────

    private sealed class FakeLibrary(Track? track) : ITrackLibraryService
    {
        public IReadOnlyList<Track> GetAll() => track is null ? [] : [track];
    }

    private sealed class FakeSplitter : ITrackSplitService
    {
        public List<(string TrackId, string Format)> Calls { get; } = [];

        public Task<TrackSplitResult> EnsureSplitAsync(
            Track track,
            ChannelMapping? overrideMapping = null,
            string? authHeaderValue = null,
            string? outputFormatOverride = null,
            CancellationToken ct = default)
        {
            var fmt = outputFormatOverride ?? "mp3";
            Calls.Add((track.Id, fmt));
            return Task.FromResult(new TrackSplitResult(
                FrontUrl: $"/audio/.split/{track.Id}/FL_FR.{fmt}",
                BackUrl: $"/audio/.split/{track.Id}/BL_BR.{fmt}",
                SourceChannelCount: 2,
                SourceChannelLayout: "stereo",
                EffectiveMapping: new ChannelMapping(0, 1, 0, 1)));
        }
    }

    private sealed class Recorder
    {
        public List<string> BroadcastFormats { get; } = [];
        public int PlayRequestedCount { get; set; }
        public int TrackChangedCount { get; set; }
    }
}
