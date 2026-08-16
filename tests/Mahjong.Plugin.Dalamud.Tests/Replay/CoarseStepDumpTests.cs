using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>
/// Opt-in diagnostic: reproduces the production TryChoose cadence where several
/// game turns pass between two BuildBatch calls. Set MJ_REPLAY_DEBUG_FILE and
/// MJ_COARSE_WALLS (e.g. "62,59") to feed the tracker only the last snapshot of
/// each wall value, printing the produced batches. Not a regression test.
/// </summary>
public class CoarseStepDumpTests
{
    private readonly ITestOutputHelper output;

    public CoarseStepDumpTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Dump_coarse_stepped_batches()
    {
        string? file = Environment.GetEnvironmentVariable("MJ_REPLAY_DEBUG_FILE");
        string? walls = Environment.GetEnvironmentVariable("MJ_COARSE_WALLS");
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(walls))
            return;

        IReadOnlyList<StateSnapshot> snapshots = CallHookCaptureLoader.LoadSnapshots(file);
        output.WriteLine($"snapshots={snapshots.Count}");

        // Locate the first Pon+Pass prompt whose river shows seat3 tip C (id 33).
        int promptIndex = -1;
        for (int i = 0; i < snapshots.Count; i++)
        {
            StateSnapshot s = snapshots[i];
            if (s.Legal.Can(ActionFlags.Pon)
                && s.Legal.Can(ActionFlags.Pass)
                && !s.Legal.Can(ActionFlags.Discard)
                && s.WallRemaining == 59
                && s.Seats.Count == 4
                && s.Seats[3].Discards.Count > 0
                && s.Seats[3].Discards[^1].Id == 33)
            {
                promptIndex = i;
                break;
            }
        }
        output.WriteLine($"promptIndex={promptIndex}");
        if (promptIndex < 0)
            return;

        // The production tracker last ran while wall was 62 (own dahai echo).
        int preIndex = -1;
        for (int i = promptIndex; i >= 0; i--)
        {
            if (snapshots[i].WallRemaining >= 62)
            {
                preIndex = i;
                break;
            }
        }
        output.WriteLine($"preIndex={preIndex} wall={snapshots[preIndex].WallRemaining}");

        var tracker = new MjaiSessionTracker();
        // Establish the session up to preIndex at fine granularity, mirroring
        // that production was healthy until the poll gap.
        for (int i = 0; i <= preIndex; i++)
            tracker.BuildBatch(snapshots[i]);

        for (int i = preIndex + 1; i < Math.Min(promptIndex + 3, snapshots.Count); i++)
        {
            StateSnapshot s = snapshots[i];
            MjaiEventBatch batch = tracker.BuildBatch(s);
            string rivers = string.Join(
                " / ",
                s.Seats.Select((v, idx) =>
                    $"{idx}:{string.Join("", v.Discards.Select(t => t.ToString()))}(n={Math.Max(v.DiscardCount, v.Discards.Count)})"));
            output.WriteLine(
                $"[{i}] w={s.WallRemaining} hand={s.Hand.Count} melds={s.OurMelds.Count} fl={s.Legal.Flags}");
            output.WriteLine($"     rivers=({rivers})");
            output.WriteLine(
                $"     events={batch.EventCount} status={batch.Status}");
            if (batch.EventCount > 0)
                output.WriteLine($"     json={batch.Json}");
            output.WriteLine(
                $"     AlreadyHasCallOffer(3,C)={tracker.AlreadyHasCallOffer(3, Tile.FromId(33))}");
        }
    }
}
