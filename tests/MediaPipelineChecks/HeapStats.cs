using System.Runtime.InteropServices;

/// <summary>
/// LIBAVFRAME_01 soak diagnostics (test-only, Windows). Sums HeapSummary over every process heap:
/// <c>Allocated</c> = bytes currently IN USE by heap blocks (a native leak grows this), <c>Committed</c>
/// = bytes the heaps hold from the OS (fragmentation and caching grow this, and it can be given back).
/// libav's av_malloc is _aligned_malloc → the UCRT heap → the process heap, so it is counted here.
/// </summary>
internal static class HeapStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HEAP_SUMMARY
    {
        public uint cb;
        public nuint cbAllocated;
        public nuint cbCommitted;
        public nuint cbReserved;
        public nuint cbMaxReserve;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessHeaps(uint numberOfHeaps, [Out] IntPtr[] processHeaps);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSummary(IntPtr hHeap, uint dwFlags, ref HEAP_SUMMARY lpSummary);

    public static (long Allocated, long Committed, int Heaps) Sample()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0, 0);
        uint n = GetProcessHeaps(0, Array.Empty<IntPtr>());
        var heaps = new IntPtr[n + 16];
        n = GetProcessHeaps((uint)heaps.Length, heaps);
        long alloc = 0, commit = 0;
        for (int i = 0; i < Math.Min(n, (uint)heaps.Length); i++)
        {
            var s = new HEAP_SUMMARY { cb = (uint)Marshal.SizeOf<HEAP_SUMMARY>() };
            if (HeapSummary(heaps[i], 0, ref s)) { alloc += (long)s.cbAllocated; commit += (long)s.cbCommitted; }
        }
        return (alloc, commit, (int)n);
    }
}
