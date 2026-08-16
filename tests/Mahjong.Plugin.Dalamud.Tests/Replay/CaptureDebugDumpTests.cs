using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Xunit.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>
/// Opt-in diagnostic dump: set MJ_REPLAY_DEBUG_FILE and MJ_REPLAY_DEBUG_RANGE
/// (e.g. "2280-2300") to print every tracker batch and prompt surface for the
/// requested snapshot range of one capture file. Not a regression test.
/// </summary>
public class CaptureDebugDumpTests
{
    private readonly ITestOutputHelper output;

    public CaptureDebugDumpTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Dump_batches_for_requested_range()
    {
        string? file = Environment.GetEnvironmentVariable("MJ_REPLAY_DEBUG_FILE");
        string? range = Environment.GetEnvironmentVariable("MJ_REPLAY_DEBUG_RANGE");
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(range))
            return;

        string[] parts = range.Split('-');
        int from = int.Parse(parts[0]);
        int to = int.Parse(parts[1]);

        IReadOnlyList<StateSnapshot> snapshots = CallHookCaptureLoader.LoadSnapshots(file);
        var tracker = new MjaiSessionTracker();
        for (int i = 0; i < snapshots.Count && i <= to; i++)
        {
            StateSnapshot state = snapshots[i];
            MjaiEventBatch batch = tracker.BuildBatch(state);
            if (i < from)
                continue;

            string rivers = string.Join(
                " / ",
                state.Seats.Select((s, idx) =>
                    $"{idx}:{string.Join("", s.Discards.Select(t => t.ToString()))}"));
            output.WriteLine(
                $"[{i}] w={state.WallRemaining} hand={state.Hand.Count} fl={state.Legal.Flags} "
                + $"melds={state.OurMelds.Count} rivers=({rivers})");
            if (batch.EventCount > 0)
            {
                output.WriteLine(
                    $"     batch({batch.EventCount}, expects={ExternalMjaiProcess.BatchExpectsDecision(batch.Json, state.OurSeat)}): {batch.Json}");
            }
            else
            {
                output.WriteLine($"     status: {batch.Status}");
            }
        }
    }
}
