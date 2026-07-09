using System.Runtime.InteropServices;

namespace ResHog.Native;

/// <summary>
/// P/Invoke declarations for the Windows PDH (Performance Data Helper) API.
/// Using PdhAddEnglishCounterW ensures counter names work regardless of system locale.
/// A single PDH query with all counters allows one PdhCollectQueryData call per cycle,
/// which is orders of magnitude faster than creating individual PerformanceCounter objects.
/// </summary>
internal static class PdhNative
{
    // --- Constants ---

    /// <summary>Format as a double-precision floating point.</summary>
    public const uint PDH_FMT_DOUBLE = 0x00000200;

    /// <summary>Counter data is valid.</summary>
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;

    /// <summary>Counter data is valid and new (different from previous sample).</summary>
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    /// <summary>No data available yet (counter just added, need second collection).</summary>
    public const uint PDH_NO_DATA = 0x800007D5;

    // --- Functions ---

    /// <summary>
    /// Creates a new query that is used to manage the collection of performance data.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PdhOpenQueryW(
        IntPtr dataSource,
        IntPtr userData,
        out IntPtr queryHandle);

    /// <summary>
    /// Adds an English-language counter path to the query. Available since Windows Vista.
    /// This avoids localization issues — the counter name is always in English.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PdhAddEnglishCounterW(
        IntPtr queryHandle,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counterHandle);

    /// <summary>
    /// Collects all counter data for the specified query in a single operation.
    /// This is the key performance advantage: one call collects data for ALL counters.
    /// </summary>
    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhCollectQueryData(
        IntPtr queryHandle);

    /// <summary>
    /// Returns a formatted counter value from a previously collected sample.
    /// For rate-based counters, PDH uses the two most recent samples internally.
    /// </summary>
    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhGetFormattedCounterValue(
        IntPtr counterHandle,
        uint format,
        out uint counterType,
        out PDH_FMT_COUNTERVALUE value);

    /// <summary>
    /// Removes a counter from a query and frees associated resources.
    /// </summary>
    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhRemoveCounter(
        IntPtr counterHandle);

    /// <summary>
    /// Closes a query, removes all counters, and frees resources.
    /// </summary>
    [DllImport("pdh.dll", SetLastError = true)]
    public static extern uint PdhCloseQuery(
        IntPtr queryHandle);

    // --- Structures ---

    /// <summary>
    /// Formatted counter value returned by PdhGetFormattedCounterValue.
    /// The union overlaps at offset 8 (after CStatus + 4-byte padding on x64).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;

        [FieldOffset(8)]
        public long LargeValue;
    }

    // --- Helper ---

    /// <summary>
    /// Returns true if the counter status indicates valid data.
    /// </summary>
    public static bool IsValid(uint cStatus)
    {
        return cStatus == PDH_CSTATUS_VALID_DATA || cStatus == PDH_CSTATUS_NEW_DATA;
    }
}
