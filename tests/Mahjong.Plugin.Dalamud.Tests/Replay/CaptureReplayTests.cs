using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.ExternalAi;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>
/// Field-capture replay regression suite. The bundled fixture is the exact
/// CALL_HOOK_CAPTURE stream from the 2026-08-01 16:45 session in which the
/// EMJ call prompt exposed a fabricated "Pon 6s from seat 1" candidate while
/// the public river showed seat 3 discarding 8m; the retained Mortal answer
/// (key "3|8m") could not be matched and the visible instruction was lost.
/// </summary>
public class CaptureReplayTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Replay", "capture-sessions",
        "capture-20260801-hand01-false-6s-claim.jsonl");

    private static IReadOnlyList<StateSnapshot> LoadFixture() =>
        CallHookCaptureLoader.LoadSnapshots(FixturePath);

    private static StateSnapshot FirstLiveCallPrompt(IReadOnlyList<StateSnapshot> snapshots) =>
        snapshots.First(ExternalMjaiProcess.IsLiveExternalCallPrompt);

    [Fact]
    public void Fixture_contains_the_false_claimed_tile_conflict()
    {
        var snapshots = LoadFixture();
        StateSnapshot prompt = FirstLiveCallPrompt(snapshots);

        // Documents the captured field data: candidate rows claim 6s from seat 1
        // while the river proves seat 3 discarded 8m. If a future capture format
        // change breaks the loader, this canary fails first.
        MeldCandidate pon = Assert.Single(prompt.Legal.PonCandidates);
        Assert.Equal(23, pon.ClaimedTile.Id); // 6s
        Assert.Equal(1, pon.FromSeat);
        Assert.Equal(7, prompt.Seats[3].Discards[^1].Id); // 8m
        Assert.True(prompt.Legal.Can(ActionFlags.Pon));
        Assert.True(prompt.Legal.Can(ActionFlags.Chi));
        Assert.True(prompt.Legal.Can(ActionFlags.Pass));

        Assert.True(ExternalMjaiProcess.TryGetRiverAuthoritativeCallOffer(
            prompt, out Tile tile, out int actor));
        Assert.Equal(7, tile.Id);
        Assert.Equal(3, actor);
    }

    [Fact]
    public void Deferred_pon_answer_survives_false_candidate_rows()
    {
        StateSnapshot prompt = FirstLiveCallPrompt(LoadFixture());
        JsonObject response = JsonNode.Parse(
            """{"type":"pon","actor":0,"target":3,"pai":"8m","consumed":["8m","8m"],"_post_call_pai":"1p"}""")!
            .AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", "3|8m");
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", "3|8m");

        Assert.True(process.TryGetDeferredCallChoice(prompt, out ActionChoice choice));
        Assert.Equal(ActionKind.Pon, choice.Kind);
        Assert.NotNull(choice.Call);
        Assert.Equal(7, choice.Call!.Value.ClaimedTile.Id); // 8m from the river, not 6s
        Assert.Equal(3, choice.Call!.Value.FromSeat);       // relative: seat 3 = kamicha
    }

    [Fact]
    public void Deferred_pass_answer_survives_false_candidate_rows()
    {
        StateSnapshot prompt = FirstLiveCallPrompt(LoadFixture());
        JsonObject response = JsonNode.Parse("""{"type":"none"}""")!.AsObject();

        using var process = new ExternalMjaiProcess(new StubPluginLog(), string.Empty);
        SetPrivateField(process, "deferredCallOfferKey", "3|8m");
        SetPrivateField(process, "deferredCallResponse", response);
        SetPrivateField(process, "latestOpponentDiscardKey", "3|8m");

        Assert.True(process.TryGetDeferredCallChoice(prompt, out ActionChoice choice));
        Assert.Equal(ActionKind.Pass, choice.Kind);
    }

    [Fact]
    public void Replay_of_field_capture_has_no_instruction_loss()
    {
        var snapshots = LoadFixture();
        var findings = CaptureReplayHarness.FindInstructionLoss(snapshots);

        Assert.True(
            findings.Count == 0,
            "instruction loss reproduced:\n"
            + string.Join('\n', findings.Select(f => $"  [{f.SnapshotIndex}] {f.Message}")));
    }

    /// <summary>
    /// Opt-in bulk replay: set MJ_REPLAY_CAPTURE to a CALL_HOOK_CAPTURE_*.jsonl
    /// file or a directory tree containing them, then run this test to verify
    /// entire recorded sessions against the instruction-loss invariant.
    /// </summary>
    [Fact]
    public void Replay_of_external_capture_sessions_has_no_instruction_loss()
    {
        string? root = Environment.GetEnvironmentVariable("MJ_REPLAY_CAPTURE");
        if (string.IsNullOrWhiteSpace(root))
            return;

        string[] files = File.Exists(root)
            ? [root]
            : Directory.GetFiles(root, "CALL_HOOK_CAPTURE_*.jsonl", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (string file in files)
        {
            var findings = CaptureReplayHarness.FindInstructionLoss(
                CallHookCaptureLoader.LoadSnapshots(file));
            failures.AddRange(findings.Select(f => $"{file} [{f.SnapshotIndex}] {f.Message}"));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        FieldInfo? field = null;
        for (Type? type = target.GetType(); type is not null && field is null; type = type.BaseType)
            field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}
