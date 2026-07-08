namespace NineChronicles.Snapshot.GcPipeline
{
    /// <summary>
    /// Result of GC Pipeline execution with detailed metrics.
    /// </summary>
    public class GcResult
    {
        public bool Success { get; set; }
        public long TotalNodes { get; set; }
        public long LiveNodes { get; set; }
        public long DeletedNodes { get; set; }
        public double Phase1Minutes { get; set; }
        public double Phase2Minutes { get; set; }
        public double Phase3Minutes { get; set; }
        public double TotalMinutes { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of Phase 1 (Export).
    /// </summary>
    public class ExportResult
    {
        public long TotalEntries { get; set; }
        public double FileSizeGB { get; set; }
    }

    /// <summary>
    /// Result of Phase 2 (BFS).
    /// </summary>
    public class BfsResult
    {
        public long TotalScanned { get; set; }
        public long LiveNodes { get; set; }
        public int TotalLevels { get; set; }
    }

    /// <summary>
    /// Result of Phase 3 (Write).
    /// </summary>
    public class WriteResult
    {
        public long TotalKeysScanned { get; set; }
        public long DeletedKeys { get; set; }
        public long KeptKeys { get; set; }
    }
}
