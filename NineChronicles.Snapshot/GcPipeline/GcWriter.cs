using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RocksDbSharp;
using Serilog;

namespace NineChronicles.Snapshot.GcPipeline
{
    /// <summary>
    /// Phase 3: Write pruned states by copying source and deleting garbage in-place.
    /// This preserves RocksDB format compatibility.
    /// </summary>
    public class GcWriter
    {
        private readonly ILogger _logger;

        public GcWriter(ILogger logger)
        {
            _logger = logger;
        }

        public WriteResult WritePrunedStates(
            string sourceStatesPath,
            string destStatesPath,
            string liveKeysFile)
        {
            var result = new WriteResult();
            
            // ═══════════════════════════════════════════════════════════
            // Step 1: Load live keys from file
            // ═══════════════════════════════════════════════════════════
            _logger.Information("📂 Loading live keys from {File}...", Path.GetFileName(liveKeysFile));
            
            var liveKeys = new HashSet<string>();
            using (var fs = File.OpenRead(liveKeysFile))
            {
                var keyBuf = new byte[32];
                while (fs.Read(keyBuf, 0, 32) == 32)
                {
                    var keyHex = BitConverter.ToString(keyBuf).Replace("-", "");
                    liveKeys.Add(keyHex);
                }
            }
            
            _logger.Information("   ✓ Loaded {Count:N0} live keys", liveKeys.Count);
            
            // ═══════════════════════════════════════════════════════════
            // Step 2: Copy source to dest (preserves format_version)
            // ═══════════════════════════════════════════════════════════
            _logger.Information("📋 Copying {Source} → {Dest}...", 
                Path.GetFileName(sourceStatesPath), Path.GetFileName(destStatesPath));
            
            if (Directory.Exists(destStatesPath))
            {
                _logger.Warning("Destination already exists, deleting: {Path}", destStatesPath);
                Directory.Delete(destStatesPath, true);
            }
            
            CopyDirectory(sourceStatesPath, destStatesPath);
            _logger.Information("   ✓ Copy complete");
            
            // ═══════════════════════════════════════════════════════════
            // Step 3: Open dest DB and delete garbage in-place
            // CRITICAL: Use format_version: 5 to match Libplanet compatibility!
            // ═══════════════════════════════════════════════════════════
            _logger.Information("🗑️  Opening destination DB and deleting garbage...");
            
            // Configure RocksDB to use format_version: 5 (compatible with Libplanet/Headless)
            var tableOptions = new BlockBasedTableOptions()
                .SetFormatVersion(5);  // ← CRITICAL: Force format_version 5!
            
            var dbOptions = new DbOptions()
                .SetCreateIfMissing(false)  // DB already exists from copy
                .SetBlockBasedTableFactory(tableOptions);
            
            using (var db = RocksDb.Open(dbOptions, destStatesPath))
            {
                _logger.Debug("Scanning all keys...");
                
                using var iterator = db.NewIterator();
                using var deleteBatch = new WriteBatch();
                int batchSize = 0;
                const int maxBatchSize = 50_000;
                
                iterator.SeekToFirst();
                
                while (iterator.Valid())
                {
                    var key = iterator.Key();
                    result.TotalKeysScanned++;
                    
                    if (key.Length == 32)
                    {
                        var keyHex = BitConverter.ToString(key).Replace("-", "");
                        
                        if (!liveKeys.Contains(keyHex))
                        {
                            // This is garbage - delete it
                            deleteBatch.Delete(key);
                            result.DeletedKeys++;
                            batchSize++;
                            
                            if (batchSize >= maxBatchSize)
                            {
                                db.Write(deleteBatch);
                                deleteBatch.Clear();
                                batchSize = 0;
                                
                                if (result.DeletedKeys % 500_000 == 0)
                                {
                                    _logger.Debug("   Deleted {Count}M garbage keys...", 
                                        result.DeletedKeys / 1_000_000);
                                }
                            }
                        }
                    }
                    
                    iterator.Next();
                }
                
                // Flush final batch
                if (batchSize > 0)
                {
                    db.Write(deleteBatch);
                }
            }
            
            result.KeptKeys = result.TotalKeysScanned - result.DeletedKeys;
            
            _logger.Information("   ✓ Deletion complete: {Total:N0} total, {Deleted:N0} deleted, {Kept:N0} kept ({Percent:F1}% removed)", 
                result.TotalKeysScanned, 
                result.DeletedKeys, 
                result.KeptKeys,
                result.DeletedKeys * 100.0 / result.TotalKeysScanned);
            
            // ═══════════════════════════════════════════════════════════
            // Step 4: Compact database to reclaim disk space
            // CRITICAL: Use format_version: 5 to ensure compatibility!
            // ═══════════════════════════════════════════════════════════
            _logger.Information("🗜️  Compacting database to reclaim space...");
            
            // Use same format_version: 5 for compaction
            var compactTableOptions = new BlockBasedTableOptions()
                .SetFormatVersion(5);  // ← CRITICAL: Force format_version 5!
            
            var compactOptions = new DbOptions()
                .SetBlockBasedTableFactory(compactTableOptions);
            
            using (var db = RocksDb.Open(compactOptions, destStatesPath))
            {
                db.CompactRange(new byte[0], Encoding.UTF8.GetBytes("~"));
            }
            
            _logger.Information("   ✓ Compaction complete");
            
            return result;
        }

        /// <summary>
        /// Recursively copy directory (files and subdirectories).
        /// </summary>
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            
            // Copy all files
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }
            
            // Recursively copy subdirectories
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
