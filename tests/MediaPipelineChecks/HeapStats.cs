using System.Runtime.InteropServices;

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
