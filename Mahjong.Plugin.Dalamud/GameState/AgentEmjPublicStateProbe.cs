using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Temporary reverse-engineering probe for AgentEmj public-state storage.
/// Event-driven reverse-engineering recorder for AgentEmj public state.
/// A capture is written whenever meaningful public state changes. Live data
/// Captures the real AgentEmj object, its observed changing pointer slots,
/// and the separate Client::Game::UI::UIState::Emj public-display state.
/// AgentId must come from FFXIVClientStructs; numeric id 5 is ChatLog, not Emj.
/// </summary>
internal static unsafe class AgentEmjPublicStateProbe
{
    private const int CaptureBytes = 0x4000;
    private const int PointerCaptureBytes = 0x1000;
    private const int UiStateEmjBytes = 0x38;
    private const int MaxLoggedChanges = 192;
    private static readonly int[] AgentPointerOffsets =
    [
        0x12B0, 0x12C8,
        0x1790, 0x17F0, 0x1808,
        0x1E50, 0x1E68,
        0x1F10, 0x1F28,
        0x20F0, 0x2108, 0x21B0,
        0x2750, 0x2768,
    ];

    private static readonly object Sync = new();
    private static byte[]? previous;
    private static byte[]? previousUiStateEmj;
    private static string? previousSignature;
    private static string? sessionDir;
    private static long sequence;

    public static void Observe(StateSnapshot snapshot, string pluginConfigDir, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        ArgumentNullException.ThrowIfNull(log);

        if (snapshot.Seats.Count < 4)
            return;

        string signature = BuildSignature(snapshot);

        lock (Sync)
        {
            if (string.Equals(signature, previousSignature, StringComparison.Ordinal))
                return;

            if (!TryGetAgent(out byte* agent))
            {
                log.Debug("[AgentEmjProbe] AgentEmj unavailable");
                previousSignature = signature;
                return;
            }

            byte[]? current = TryRead(agent, CaptureBytes);
            if (current is null)
            {
                log.Debug("[AgentEmjProbe] AgentEmj base range is not readable");
                previousSignature = signature;
                return;
            }

            byte[]? uiStateEmj = TryReadUiStateEmj();
            long seq = ++sequence;
            string dir = EnsureSessionDirectory(pluginConfigDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            try
            {
                string prefix = $"capture-{seq:D6}-{stamp}";
                File.WriteAllBytes(Path.Combine(dir, $"{prefix}-agent.bin"), current);
                WriteSnapshotMetadata(Path.Combine(dir, $"{prefix}-state.json"), snapshot, signature, (nint)agent);
            }
            catch (Exception ex)
            {
                log.Warning($"[AgentEmjProbe] capture write failed: {ex.Message}");
            }

            if (previous is null)
            {
                log.Information(
                    $"[AgentEmjProbe] baseline captured seq={seq} bytes=0x{current.Length:X} " +
                    $"signature={signature}");
            }
            else
            {
                var changed = FindChangedOffsets(previous, current);
                var groups = GroupOffsets(changed);
                string groupText = groups.Count == 0
                    ? "none"
                    : string.Join(",", groups.Take(32).Select(g => $"+0x{g.Start:X4}-+0x{g.End:X4}"));

                log.Information(
                    $"[AgentEmjProbe] seq={seq} changedBytes={changed.Count} ranges={groupText} " +
                    $"uiStateChangedBytes={CountChanges(previousUiStateEmj, uiStateEmj)} " +
                    $"signature={signature}");

                if (changed.Count > 0)
                {
                    var sb = new StringBuilder();
                    int shown = 0;
                    foreach (int off in changed)
                    {
                        if (shown++ >= MaxLoggedChanges)
                            break;
                        sb.Append($"+0x{off:X4}:{previous[off]:X2}>{current[off]:X2} ");
                    }
                    if (changed.Count > MaxLoggedChanges)
                        sb.Append($"...(+{changed.Count - MaxLoggedChanges})");
                    log.Debug($"[AgentEmjProbe:delta] {sb}");
                }

                WriteDeltaReport(
                    dir, seq, previousSignature ?? "(none)", signature,
                    previous, current, changed, groups, log);
            }

            previous = current;
            previousUiStateEmj = uiStateEmj;
            previousSignature = signature;
        }
    }

    private static string BuildSignature(StateSnapshot snapshot)
    {
        var sb = new StringBuilder(512);
        sb.Append("turn=").Append(snapshot.TurnIndex)
            .Append(";wall=").Append(snapshot.WallRemaining)
            .Append(";hand=").AppendJoin(',', snapshot.Hand.Select(t => t.Id))
            .Append(";dora=").AppendJoin(',', snapshot.DoraIndicators.Select(t => t.Id))
            .Append(";honba=").Append(snapshot.Honba)
            .Append(";sticks=").Append(snapshot.RiichiSticks)
            .Append(";scores=").AppendJoin(',', snapshot.Scores);

        for (int i = 0; i < Math.Min(4, snapshot.Seats.Count); i++)
        {
            var seat = snapshot.Seats[i];
            sb.Append(";s").Append(i)
                .Append("d=").Append(Math.Max(seat.DiscardCount, seat.Discards.Count))
                .Append('[').AppendJoin(',', seat.Discards.Select(t => t.Id)).Append(']')
                .Append("t[").AppendJoin(',', seat.DiscardIsTedashi.Select(v => v ? 1 : 0)).Append(']')
                .Append("m=").Append(seat.Melds.Count)
                .Append("r=").Append(seat.Riichi ? 1 : 0)
                .Append("ri=").Append(seat.RiichiDiscardIndex)
                .Append("i=").Append(seat.Ippatsu ? 1 : 0);
            foreach (var meld in seat.Melds)
            {
                sb.Append("{mk=").Append((int)meld.Kind)
                    .Append(",mt=").AppendJoin(',', meld.Tiles.Select(t => t.Id))
                    .Append(",mc=").Append(meld.ClaimedTile is { } claimed ? claimed.Id : -1)
                    .Append(",mf=").Append(meld.ClaimedFromSeat)
                    .Append('}');
            }
        }
        return sb.ToString();
    }

    private static bool TryGetAgent(out byte* agent)
    {
        agent = null;
        try
        {
            var module = AgentModule.Instance();
            if (module == null)
                return false;
            agent = (byte*)module->GetAgentByInternalId(AgentId.Emj);
            return agent != null;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? TryReadUiStateEmj()
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState == null
                ? null
                : TryRead((byte*)&uiState->Emj, UiStateEmjBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryRead(byte* address, int requested)
    {
        if (address == null || requested <= 0)
            return null;
        try
        {
            if (!TryGetReadableLength((nint)address, requested, out int readable) || readable <= 0)
                return null;
            var bytes = new byte[readable];
            Marshal.Copy((nint)address, bytes, 0, readable);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static string EnsureSessionDirectory(string pluginConfigDir)
    {
        if (sessionDir is not null)
            return sessionDir;
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        sessionDir = Path.Combine(pluginConfigDir, "agent-emj-recordings", $"session-{stamp}");
        Directory.CreateDirectory(sessionDir);
        return sessionDir;
    }

    private static void WriteSnapshotMetadata(
        string path,
        StateSnapshot snapshot,
        string signature,
        nint agentAddress)
    {
        var seats = snapshot.Seats.Take(4).Select((seat, index) => new
        {
            index,
            discardCount = Math.Max(seat.DiscardCount, seat.Discards.Count),
            decodedDiscards = seat.Discards.Select(t => t.Id).ToArray(),
            tedashi = seat.DiscardIsTedashi.ToArray(),
            melds = seat.Melds.Select(m => new
            {
                kind = m.Kind.ToString(),
                tiles = m.Tiles.Select(t => t.Id).ToArray(),
                claimedTile = m.ClaimedTile?.Id,
                fromSeat = m.ClaimedFromSeat,
            }).ToArray(),
            seat.Riichi,
            seat.RiichiDiscardIndex,
            seat.Ippatsu,
        }).ToArray();

        var metadata = new
        {
            utc = DateTime.UtcNow,
            signature,
            agentAddress = $"0x{agentAddress:X}",
            snapshot.AddonStateCode,
            snapshot.TurnIndex,
            snapshot.WallRemaining,
            legalFlags = snapshot.Legal.Flags.ToString(),
            legalRaw = (int)snapshot.Legal.Flags,
            hand = snapshot.Hand.Select(t => t.Id).ToArray(),
            snapshot.OurSeat,
            snapshot.OurRiichi,
            snapshot.OurIppatsu,
            snapshot.Honba,
            snapshot.RiichiSticks,
            scores = snapshot.Scores.ToArray(),
            doraIndicators = snapshot.DoraIndicators.Select(t => t.Id).ToArray(),
            seats,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static void CaptureFocusedPointerTargets(
        string dir,
        string prefix,
        byte[] agentBytes,
        IPluginLog log)
    {
        var seen = new HashSet<nint>();
        foreach (int agentOffset in AgentPointerOffsets)
        {
            if (agentOffset < 0 || agentOffset + IntPtr.Size > agentBytes.Length)
                continue;
            long raw = BitConverter.ToInt64(agentBytes, agentOffset);
            var target = (nint)raw;
            if (!IsPointerCandidate(raw) || !seen.Add(target))
                continue;
            if (!TryGetReadableLength(target, PointerCaptureBytes, out int readable) || readable < 64)
                continue;

            byte[]? bytes = TryRead((byte*)target, Math.Min(readable, PointerCaptureBytes));
            if (bytes is null)
                continue;

            try
            {
                string name = $"{prefix}-focus-off{agentOffset:X4}-addr{target:X}.bin";
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
            }
            catch (Exception ex)
            {
                log.Warning($"[AgentEmjProbe] focused pointer capture write failed: {ex.Message}");
            }
        }
    }

    private static void CaptureUiStatePointerTargets(
        string dir,
        string prefix,
        byte[] uiStateEmj,
        IPluginLog log)
    {
        var seen = new HashSet<nint>();
        for (int offset = 0; offset + IntPtr.Size <= uiStateEmj.Length; offset += IntPtr.Size)
        {
            long raw = BitConverter.ToInt64(uiStateEmj, offset);
            var target = (nint)raw;
            if (!IsPointerCandidate(raw) || !seen.Add(target))
                continue;
            if (!TryGetReadableLength(target, PointerCaptureBytes, out int readable) || readable < 32)
                continue;

            byte[]? bytes = TryRead((byte*)target, Math.Min(readable, PointerCaptureBytes));
            if (bytes is null)
                continue;
            try
            {
                File.WriteAllBytes(
                    Path.Combine(
                        dir,
                        $"{prefix}-uistate-emj-off{offset:X2}-addr{target:X}.bin"),
                    bytes);
            }
            catch (Exception ex)
            {
                log.Warning($"[AgentEmjProbe] UIState.Emj pointer capture failed: {ex.Message}");
            }
        }
    }

    private static int CountChanges(byte[]? before, byte[]? after)
    {
        if (before is null || after is null)
            return -1;
        int count = Math.Abs(before.Length - after.Length);
        int length = Math.Min(before.Length, after.Length);
        for (int i = 0; i < length; i++)
            if (before[i] != after[i])
                count++;
        return count;
    }

    private static bool IsPointerCandidate(long raw) =>
        raw >= 0x10000
        && raw <= 0x00007FFFFFFFFFFF
        && (raw & 0x7) == 0;

    private static List<int> FindChangedOffsets(byte[] before, byte[] after)
    {
        int len = Math.Min(before.Length, after.Length);
        var result = new List<int>();
        for (int i = 0; i < len; i++)
            if (before[i] != after[i])
                result.Add(i);
        return result;
    }

    private static List<(int Start, int End)> GroupOffsets(IReadOnlyList<int> offsets)
    {
        var groups = new List<(int Start, int End)>();
        if (offsets.Count == 0)
            return groups;

        int start = offsets[0];
        int end = start;
        for (int i = 1; i < offsets.Count; i++)
        {
            int off = offsets[i];
            // Merge nearby changes into one structural range.
            if (off <= end + 8)
            {
                end = off;
                continue;
            }
            groups.Add((start, end));
            start = end = off;
        }
        groups.Add((start, end));
        return groups;
    }

    private static void WriteDeltaReport(
        string dir,
        long seq,
        string oldSignature,
        string newSignature,
        byte[] before,
        byte[] after,
        IReadOnlyList<int> changed,
        IReadOnlyList<(int Start, int End)> groups,
        IPluginLog log)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"seq={seq}");
            sb.AppendLine($"before={oldSignature}");
            sb.AppendLine($"after={newSignature}");
            sb.AppendLine($"changedBytes={changed.Count}");
            sb.AppendLine("ranges:");
            foreach (var g in groups)
                sb.AppendLine($"  +0x{g.Start:X4}..+0x{g.End:X4}");

            sb.AppendLine("changed:");
            foreach (int off in changed)
                sb.AppendLine($"  +0x{off:X4}: {before[off]:X2} -> {after[off]:X2}");

            File.WriteAllText(Path.Combine(dir, $"delta-{seq:D5}.txt"), sb.ToString());
        }
        catch (Exception ex)
        {
            log.Warning($"[AgentEmjProbe] delta report write failed: {ex.Message}");
        }
    }

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    private static bool TryGetReadableLength(nint address, int requested, out int readable)
    {
        readable = 0;
        try
        {
            if (VirtualQuery(
                    address,
                    out MemoryBasicInformation mbi,
                    (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                return false;
            if (mbi.State != MemCommit || (mbi.Protect & (PageNoAccess | PageGuard)) != 0)
                return false;

            ulong start = unchecked((ulong)address.ToInt64());
            ulong regionStart = unchecked((ulong)mbi.BaseAddress.ToInt64());
            ulong regionEnd = regionStart + (ulong)mbi.RegionSize;
            if (start < regionStart || start >= regionEnd)
                return false;
            ulong available = regionEnd - start;
            readable = (int)Math.Min((ulong)requested, available);
            return readable > 0;
        }
        catch
        {
            return false;
        }
    }
}
