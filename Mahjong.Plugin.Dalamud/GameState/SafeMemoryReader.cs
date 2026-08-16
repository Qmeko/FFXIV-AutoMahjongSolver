using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Mahjong.Plugin.Dalamud.GameState;

internal static unsafe class SafeMemoryReader
{
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        nint lpAddress,
        out MemoryBasicInformation lpBuffer,
        nuint dwLength);

    public static bool TryRead(nint address, int length, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (address == nint.Zero || length <= 0)
            return false;

        var result = new byte[length];
        int copied = 0;
        nint cursor = address;

        while (copied < length)
        {
            if (VirtualQuery(cursor, out var mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                return false;
            if (mbi.State != MemCommit || (mbi.Protect & (PageNoAccess | PageGuard)) != 0)
                return false;

            long regionStart = mbi.BaseAddress.ToInt64();
            long regionEnd = checked(regionStart + (long)mbi.RegionSize);
            long current = cursor.ToInt64();
            if (current < regionStart || current >= regionEnd)
                return false;

            int chunk = (int)Math.Min(length - copied, regionEnd - current);
            if (chunk <= 0)
                return false;

            Marshal.Copy(cursor, result, copied, chunk);
            copied += chunk;
            cursor += chunk;
        }

        bytes = result;
        return true;
    }

    /// <summary>
    /// Number of bytes starting at <paramref name="address"/> that can be read
    /// directly without faulting (committed, non-guard, non-noaccess pages),
    /// capped at <paramref name="desired"/>. Returns 0 when the first page is
    /// unreadable. Use this to clamp spans over raw game structures whose real
    /// allocation size is unknown; reading past the allocation crossed into an
    /// unmapped page and terminated FFXIV (crash capture 2026-08-01 20:33, a
    /// pon animation event during the meld-region hex dump).
    /// </summary>
    public static int ReadableLength(nint address, int desired)
    {
        if (address == nint.Zero || desired <= 0)
            return 0;

        int readable = 0;
        nint cursor = address;
        while (readable < desired)
        {
            if (VirtualQuery(cursor, out var mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                break;
            if (mbi.State != MemCommit || (mbi.Protect & (PageNoAccess | PageGuard)) != 0)
                break;

            long regionStart = mbi.BaseAddress.ToInt64();
            long regionEnd = regionStart + (long)mbi.RegionSize;
            long current = cursor.ToInt64();
            if (current < regionStart || current >= regionEnd)
                break;

            int chunk = (int)Math.Min(desired - readable, regionEnd - current);
            if (chunk <= 0)
                break;
            readable += chunk;
            cursor += chunk;
        }
        return readable;
    }

    public static IReadOnlyList<(int Offset, nint Pointer)> FindReadablePointers(
        nint baseAddress,
        int scanBytes,
        int targetProbeBytes = 0x100)
    {
        var result = new List<(int Offset, nint Pointer)>();
        if (!TryRead(baseAddress, scanBytes, out var bytes))
            return result;

        int pointerSize = IntPtr.Size;
        for (int off = 0; off + pointerSize <= bytes.Length; off += pointerSize)
        {
            long raw = pointerSize == 8
                ? BitConverter.ToInt64(bytes, off)
                : BitConverter.ToInt32(bytes, off);
            if (raw < 0x10000 || raw > 0x00007FFFFFFFFFFF)
                continue;
            var ptr = (nint)raw;
            if (TryRead(ptr, targetProbeBytes, out _))
                result.Add((off, ptr));
        }
        return result;
    }
}
