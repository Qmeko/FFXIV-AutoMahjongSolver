using System;
using System.IO;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Records the exact AtkValue arrays supplied by the game when Emj is set up
/// or refreshed. These values are captured before the addon consumes them.
/// </summary>
internal sealed unsafe class AddonEmjAtkValueProbe : IDisposable
{
    private const int MaxValues = 2048;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly string logPath;
    private readonly List<Tile>[] discards = [[], [], [], []];
    private readonly List<bool>[] discardIsTedashi = [[], [], [], []];
    private readonly bool[] riichi = new bool[4];
    private readonly int[] riichiDiscardIndex = [-1, -1, -1, -1];
    private readonly bool[] ippatsu = new bool[4];
    private int pendingRiichiSeat = -1;
    private readonly string?[] lastIncompleteRiverDiagnostics = new string?[4];
    private bool disposed;

    public AddonEmjAtkValueProbe(
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        string pluginConfigDir)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        Directory.CreateDirectory(pluginConfigDir);
        logPath = Path.Combine(pluginConfigDir, "addon-emj-atkvalues.log");

        var names = MahjongAddon.CandidateNames;
        addonLifecycle.RegisterListener(AddonEvent.PreSetup, names, OnPreSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreRefresh, names, OnPreRefresh);
        addonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, names, OnPreRequestedUpdate);
        log.Information("[AddonEmjAtkValue] PreSetup/PreRefresh/PreRequestedUpdate recorder active");
    }

    private void OnPreSetup(AddonEvent type, AddonArgs args)
    {
        ResetPublicState();
        if (args is AddonSetupArgs setup)
            RecordValues("PreSetup", args, setup.AtkValues, setup.AtkValueCount);
    }

    private void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is AddonRefreshArgs refresh)
        {
            ObservePublicState(refresh.AtkValues, refresh.AtkValueCount);
            RecordValues("PreRefresh", args, refresh.AtkValues, refresh.AtkValueCount);
        }
    }

    private void ObservePublicState(nint valuesAddress, uint valueCount)
    {
        var values = (AtkValue*)valuesAddress;
        if (values == null || valueCount == 0 || values[0].Type != AtkValueType.Int)
            return;

        int code = values[0].Int;
        if (code == 2)
        {
            // A new hand begins with the complete initial-table refresh.
            ResetPublicState();
            return;
        }

        if (code == 7 && valueCount >= 2 && values[1].Type == AtkValueType.Int)
        {
            int seat = values[1].Int;
            if ((uint)seat < 4)
                pendingRiichiSeat = seat;
            return;
        }

        if (code != 8 || valueCount < 6)
            return;

        int discardSeat = values[1].Int;
        int textureId = values[2].Int;
        if ((uint)discardSeat >= 4 || !TryDecodeDiscardTile(textureId, out var tile, out bool isRed))
            return;

        discards[discardSeat].Add(tile);
        if (isRed)
            log.Debug($"[AddonEmjAtkValue] code=8 seat={discardSeat} texture={textureId} decoded={tile.ShortName} red=true");

        // Verified against our own Mortal actions: field 5 is 0 for a hand
        // discard and 1 when the just-drawn tile is discarded.
        discardIsTedashi[discardSeat].Add(values[5].Int == 0);

        // A code-7 declaration immediately precedes its sideways discard.
        // Field 3 is also 1 on every observed declaration discard.
        bool declarationDiscard = pendingRiichiSeat == discardSeat || values[3].Int == 1;
        if (declarationDiscard)
        {
            riichi[discardSeat] = true;
            riichiDiscardIndex[discardSeat] = discards[discardSeat].Count - 1;
            ippatsu[discardSeat] = true;
            pendingRiichiSeat = -1;
        }

        // Any subsequent discard ends every live ippatsu window.
        for (int seat = 0; seat < 4; seat++)
            if (seat != discardSeat && ippatsu[seat])
                ippatsu[seat] = false;
    }

    public StateSnapshot ApplyPublicState(StateSnapshot snapshot)
    {
        var seats = snapshot.Seats.ToArray();

        // Result and transition screens report an empty authoritative river
        // before the next code-2 setup reaches this probe.  Retaining the old
        // event stream through that boundary creates stale public state and a
        // per-frame diagnostic loop, so clear only when all four seats agree
        // that no river is active.
        if (seats.Length == 4 && seats.All(seat => seat.DiscardCount == 0))
        {
            if (discards.Any(river => river.Count != 0))
                ResetPublicState();
            return snapshot;
        }

        for (int seat = 0; seat < Math.Min(4, seats.Length); seat++)
        {
            if (discards[seat].Count == 0 && seats[seat].Discards.Count == 0)
                continue;

            var old = seats[seat];

            // A probe started after the hand began only owns the tail of the
            // event stream. Merge that tail with any visual/existing prefix so
            // mid-hand loads still expose a usable public river to Mortal.
            if (discards[seat].Count != old.DiscardCount)
            {
                var merged = PublicRiverMerger.Merge(
                    old.DiscardCount,
                    old.Discards,
                    old.DiscardIsTedashi,
                    discards[seat],
                    discardIsTedashi[seat]);

                if (old.DiscardCount > discards[seat].Count)
                    EmitIncompleteRiverDiagnostic(seat, discards[seat].Count, old.DiscardCount);

                if (merged.Discards.Count == 0)
                    continue;

                if (merged.Complete)
                    lastIncompleteRiverDiagnostics[seat] = null;

                seats[seat] = old with
                {
                    Discards = merged.Discards,
                    DiscardIsTedashi = merged.DiscardIsTedashi,
                    Riichi = riichi[seat] || old.Riichi,
                    RiichiDiscardIndex = riichi[seat] ? riichiDiscardIndex[seat] : old.RiichiDiscardIndex,
                    Ippatsu = riichi[seat] ? ippatsu[seat] : old.Ippatsu,
                };
                continue;
            }

            lastIncompleteRiverDiagnostics[seat] = null;

            seats[seat] = old with
            {
                Discards = discards[seat].ToArray(),
                DiscardIsTedashi = discardIsTedashi[seat].ToArray(),
                Riichi = riichi[seat],
                RiichiDiscardIndex = riichiDiscardIndex[seat],
                Ippatsu = ippatsu[seat],
            };
        }
        // The player seat is fixed to seat zero for Doman Mahjong.  Unlike an
        // unverified addon offset, a completed code-7 -> code-8 event stream
        // is authoritative for our riichi state as well.  Propagating it to
        // StateSnapshot is essential: after riichi, the only legal discard is
        // the just-drawn tile (unless the player accepts Tsumo).
        var ours = seats[0];
        var legal = snapshot.Legal;
        if (ours.Riichi && legal.Can(ActionFlags.Discard) && snapshot.Hand.Count % 3 == 2)
            legal = legal with { DiscardableTiles = [snapshot.Hand[^1]] };

        return snapshot with
        {
            Seats = seats,
            OurRiichi = ours.Riichi,
            OurIppatsu = ours.Ippatsu,
            Legal = legal,
        };
    }

    /// <summary>
    /// Code-8 events carry the same texture IDs as the hand array.  Keep their
    /// decoding in one place so the red-five aliases (base+34..36) are not
    /// silently dropped from a public river.
    /// </summary>
    private static bool TryDecodeDiscardTile(int textureId, out Tile tile, out bool isRed)
    {
        foreach (int textureBase in new[] { 76041, 76001 })
        {
            int tileId = HandArrayDecoder.DecodeTileId(textureId, textureBase, out isRed);
            if (tileId >= 0)
            {
                tile = Tile.FromId(tileId);
                return true;
            }
        }

        tile = default;
        isRed = false;
        return false;
    }

    private void EmitIncompleteRiverDiagnostic(int seat, int captured, int expected)
    {
        int missing = Math.Max(0, expected - captured);
        string diagnostic = $"seat={seat} captured={captured} expected={expected} missing={missing}";
        if (diagnostic == lastIncompleteRiverDiagnostics[seat])
            return;
        lastIncompleteRiverDiagnostics[seat] = diagnostic;
        log.Warning($"[OpponentSnapshot] incomplete river; {diagnostic}; retaining captured tiles without treating them as complete");
    }

    private void ResetPublicState()
    {
        for (int seat = 0; seat < 4; seat++)
        {
            discards[seat].Clear();
            discardIsTedashi[seat].Clear();
            riichi[seat] = false;
            riichiDiscardIndex[seat] = -1;
            ippatsu[seat] = false;
        }
        pendingRiichiSeat = -1;
        Array.Clear(lastIncompleteRiverDiagnostics);
    }

    private void OnPreRequestedUpdate(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRequestedUpdateArgs update)
            return;

        string line =
            $"{DateTime.UtcNow:O} source=PreRequestedUpdate addon={args.AddonName} " +
            $"address=0x{args.Addon.Address:X} " +
            $"numberArrayData=0x{update.NumberArrayData:X} " +
            $"stringArrayData=0x{update.StringArrayData:X}";
        Write(line);
    }

    private void RecordValues(
        string source,
        AddonArgs args,
        nint valuesAddress,
        uint valueCount)
    {
        try
        {
            var values = (AtkValue*)valuesAddress;
            int count = (int)Math.Min(valueCount, MaxValues);
            var sb = new StringBuilder(Math.Min(65536, 256 + count * 24));
            sb.Append(DateTime.UtcNow.ToString("O"))
                .Append(" source=").Append(source)
                .Append(" addon=").Append(args.AddonName)
                .Append(" address=0x").Append(args.Addon.Address.ToString("X"))
                .Append(" valueCount=").Append(valueCount)
                .Append(" values=[");
            for (int i = 0; i < count; i++)
            {
                if (i != 0)
                    sb.Append(',');
                AppendValue(sb, i, values == null ? default : values[i]);
            }
            if (valueCount > MaxValues)
                sb.Append(",...");
            sb.Append(']');
            Write(sb.ToString());
        }
        catch (Exception ex)
        {
            log.Warning($"[AddonEmjAtkValue] {source} record failed: {ex.Message}");
        }
    }

    private void Write(string line)
    {
        File.AppendAllText(logPath, line + Environment.NewLine);
        log.Information($"[AddonEmjAtkValue] {line}");
    }

    private static void AppendValue(StringBuilder sb, int index, AtkValue value)
    {
        sb.Append(index).Append(':').Append(value.Type).Append('=');
        switch (value.Type)
        {
            case AtkValueType.Int:
                sb.Append(value.Int);
                break;
            case AtkValueType.UInt:
                sb.Append(value.UInt);
                break;
            case AtkValueType.Bool:
                sb.Append(value.Byte != 0 ? "true" : "false");
                break;
            case AtkValueType.Float:
                sb.Append(value.Float);
                break;
            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                sb.Append("ptr:0x").Append(((nint)value.String.Value).ToString("X"));
                break;
            default:
                sb.Append("raw:0x").Append(value.UInt.ToString("X"));
                break;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        addonLifecycle.UnregisterListener(OnPreSetup);
        addonLifecycle.UnregisterListener(OnPreRefresh);
        addonLifecycle.UnregisterListener(OnPreRequestedUpdate);
    }
}
