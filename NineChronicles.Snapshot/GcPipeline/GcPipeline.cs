using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using Libplanet.Common;
using Serilog;

namespace NineChronicles.Snapshot.GcPipeline
{
    /// <summary>
    /// GC Pipeline orchestrator: Coordinates the 3-phase garbage collection process.
    /// 
    /// Phase 1: Export all KV pairs from states/ to binary file (~43 min, 180 GB temp)
    /// Phase 2: BFS to find live nodes (~2h, 2 GB live keys file)
    /// Phase 3: Write only live nodes to new states/ (~37 min, 20-25 GB final)
    /// 
    /// Total: ~4-4.5 hours vs 20 hours CopyStates = 5x speedup!
    /// Bonus: 67% garbage removed (201M → 66M nodes)
    /// </summary>
    public class GcPipeline
    {
        private readonly ILogger _logger;

        public GcPipeline(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Run the complete GC pipeline to prune states.
        /// </summary>
        /// <param name="stateRootHashes">State root hashes to preserve (usually 3: tip, tip-1, tip-2)</param>
        /// <param name="sourceStatesPath">Source states/ directory</param>
        /// <param name="destStatesPath">Destination states/ directory (will be created)</param>
        /// <param name="tempDir">Temporary directory for intermediate files (must have ~200 GB free)</param>
        /// <returns>Result with success status and detailed metrics</returns>
        public GcResult RunGcPipeline(
            ImmutableHashSet<HashDigest<SHA256>> stateRootHashes,
            string sourceStatesPath,
            string destStatesPath,
            string tempDir = null)
        {
            var result = new GcResult { Success = false };
            var overallStart = DateTimeOffset.Now;
            
            try
            {
                _logger.Information("🧹 Starting GC Pipeline (C#→C#→C#)");
                _logger.Information("   Source:  {Source}", sourceStatesPath);
                _logger.Information("   Dest:    {Dest}", destStatesPath);
                _logger.Information("   Roots:   {Count}", stateRootHashes.Count);
                
                // Create temporary directory for intermediate files
                // Use provided tempDir (e.g., ~/snapshots/.gc-temp) which has enough space
                if (string.IsNullOrEmpty(tempDir))
                {
                    // Fallback: use /tmp
                    tempDir = Path.Combine(
                        Path.GetTempPath(), 
                        $"gc-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                }
                else
                {
                    // Use provided tempDir with timestamp
                    tempDir = Path.Combine(tempDir, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                }
                
                Directory.CreateDirectory(tempDir);
                _logger.Information("   TempDir: {TempDir}", tempDir);
                
                var exportFile = Path.Combine(tempDir, "states_export.bin");
                var liveKeysFile = Path.Combine(tempDir, "live_keys.bin");
                
                try
                {
                    // ═══════════════════════════════════════════════════
                    // PHASE 1: C# Export (GcExporter)
                    // ═══════════════════════════════════════════════════
                    _logger.Information("📤 GC Phase 1: Exporting all KV pairs...");
                    var phase1Start = DateTimeOffset.Now;
                    
                    var exporter = new GcExporter(_logger);
                    var exportResult = exporter.ExportStates(sourceStatesPath, exportFile);
                    
                    result.Phase1Minutes = (DateTimeOffset.Now - phase1Start).TotalMinutes;
                    result.TotalNodes = exportResult.TotalEntries;
                    
                    _logger.Information("   ✓ Phase 1: {Time:F1} min ({Entries:N0} entries, {Size:F1} GB)", 
                        result.Phase1Minutes, exportResult.TotalEntries, exportResult.FileSizeGB);
                    
                    // ═══════════════════════════════════════════════════
                    // PHASE 2: C# BFS (GcBfsFilter)
                    // ═══════════════════════════════════════════════════
                    _logger.Information("🌳 GC Phase 2: Running BFS to find live nodes...");
                    var phase2Start = DateTimeOffset.Now;
                    
                    var bfsFilter = new GcBfsFilter(_logger);
                    var bfsResult = bfsFilter.RunBfs(
                        exportFile,
                        stateRootHashes,
                        liveKeysFile);
                    
                    result.Phase2Minutes = (DateTimeOffset.Now - phase2Start).TotalMinutes;
                    result.LiveNodes = bfsResult.LiveNodes;
                    
                    _logger.Information("   ✓ Phase 2: {Time:F1} min ({Live:N0} live keys, {Levels} levels)", 
                        result.Phase2Minutes, result.LiveNodes, bfsResult.TotalLevels);
                    
                    // ═══════════════════════════════════════════════════
                    // PHASE 3: C# Write (GcWriter)
                    // ═══════════════════════════════════════════════════
                    _logger.Information("📝 GC Phase 3: Writing pruned states...");
                    var phase3Start = DateTimeOffset.Now;
                    
                    var writer = new GcWriter(_logger);
                    var writeResult = writer.WritePrunedStates(
                        sourceStatesPath,
                        destStatesPath,
                        liveKeysFile);
                    
                    result.Phase3Minutes = (DateTimeOffset.Now - phase3Start).TotalMinutes;
                    result.DeletedNodes = result.TotalNodes - result.LiveNodes;
                    
                    _logger.Information("   ✓ Phase 3: {Time:F1} min", result.Phase3Minutes);
                    
                    // ═══════════════════════════════════════════════════
                    // Summary
                    // ═══════════════════════════════════════════════════
                    result.TotalMinutes = (DateTimeOffset.Now - overallStart).TotalMinutes;
                    result.Success = true;
                    
                    _logger.Information("✅ GC Pipeline Complete!");
                    _logger.Information("   ⏱️  Total Time: {Total:F1} min", result.TotalMinutes);
                    _logger.Information("   📊 Breakdown: Phase1={P1:F1}m, Phase2={P2:F1}m, Phase3={P3:F1}m", 
                        result.Phase1Minutes, result.Phase2Minutes, result.Phase3Minutes);
                    _logger.Information("   💾 Nodes: {Total:N0} → {Live:N0} ({Deleted:N0} deleted, {Percent:F1}% removed)", 
                        result.TotalNodes, result.LiveNodes, result.DeletedNodes,
                        result.DeletedNodes * 100.0 / result.TotalNodes);
                }
                finally
                {
                    // Cleanup temporary files
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            _logger.Debug("Cleaning up temporary directory: {Dir}", tempDir);
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("Failed to clean temporary directory: {Error}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.TotalMinutes = (DateTimeOffset.Now - overallStart).TotalMinutes;
                
                _logger.Error(ex, "GC Pipeline failed after {Time:F1} min", result.TotalMinutes);
            }
            
            return result;
        }
    }
}
