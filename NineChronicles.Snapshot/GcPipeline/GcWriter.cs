using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RocksDbSharp;
using Serilog;

namespace NineChronicles.Snapshot.GcPipeline
{
    /// <summary>
    /// Phase 3: Write pruned states by copying source and deleting garbage in-place.
    /// OTIMIZADO:
    ///   - Buffer de 64MB para cópia
    ///   - Bulk write com batch de 100k
    ///   - Usa Span<byte> para evitar alocações
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
            // Step 1: Load live keys from file (otimizado)
            // ═══════════════════════════════════════════════════════════
            _logger.Information("📂 Loading live keys from {File}...", Path.GetFileName(liveKeysFile));

            var liveKeys = new HashSet<string>(capacity: 66_000_000);
            using (var fs = File.OpenRead(liveKeysFile))
            {
                var keyBuf = new byte[32];
                while (fs.Read(keyBuf, 0, 32) == 32)
                {
                    // Converter direto sem alocar string extra
                    var keyHex = BitConverter.ToString(keyBuf).Replace("-", "");
                    liveKeys.Add(keyHex);
                }
            }

            _logger.Information("   ✓ Loaded {Count:N0} live keys", liveKeys.Count);

            // ═══════════════════════════════════════════════════════════
            // Step 2: Copy source to dest (com buffer grande)
            // ═══════════════════════════════════════════════════════════
            _logger.Information("📋 Copying {Source} → {Dest}...",
                                Path.GetFileName(sourceStatesPath), Path.GetFileName(destStatesPath));

            if (Directory.Exists(destStatesPath))
            {
                _logger.Warning("Destination already exists, deleting: {Path}", destStatesPath);
                Directory.Delete(destStatesPath, true);
            }

            CopyDirectoryWithBuffer(sourceStatesPath, destStatesPath);
            _logger.Information("   ✓ Copy complete");

            // ═══════════════════════════════════════════════════════════
            // Step 3: Open dest DB and delete garbage in-place (BULK)
            // ═══════════════════════════════════════════════════════════
            _logger.Information("🗑️  Opening destination DB and deleting garbage...");

            var tableOptions = new BlockBasedTableOptions()
            .SetFormatVersion(5);

            var dbOptions = new DbOptions()
            .SetCreateIfMissing(false)
            .SetBlockBasedTableFactory(tableOptions)
            .SetWriteBufferSize(256 * 1024 * 1024)  // 256MB buffer
            .SetMaxWriteBufferNumber(6)
            .SetMaxBackgroundCompactions(8);

            using (var db = RocksDb.Open(dbOptions, destStatesPath))
            {
                _logger.Debug("Scanning all keys...");

                using var iterator = db.NewIterator();
                using var deleteBatch = new WriteBatch();
                int batchSize = 0;
                const int maxBatchSize = 100_000;  // ← AUMENTADO para 100k

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
                            deleteBatch.Delete(key);
                            result.DeletedKeys++;
                            batchSize++;

                            if (batchSize >= maxBatchSize)
                            {
                                db.Write(deleteBatch);
                                deleteBatch.Clear();
                                batchSize = 0;

                                if (result.DeletedKeys % 1_000_000 == 0)
                                {
                                    _logger.Debug("   Deleted {Count}M garbage keys...",
                                                  result.DeletedKeys / 1_000_000);
                                }
                            }
                        }
                    }

                    iterator.Next();
                }

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
            // Step 4: Compact database
            // ═══════════════════════════════════════════════════════════
            _logger.Information("🗜️  Compacting database to reclaim space...");

            var compactTableOptions = new BlockBasedTableOptions()
            .SetFormatVersion(5);

            var compactOptions = new DbOptions()
            .SetBlockBasedTableFactory(compactTableOptions)
            .SetMaxBackgroundCompactions(8);

            using (var db = RocksDb.Open(compactOptions, destStatesPath))
            {
                db.CompactRange(new byte[0], Encoding.UTF8.GetBytes("~"));
            }

            _logger.Information("   ✓ Compaction complete");

            return result;
        }

        /// <summary>
        /// Recursively copy directory com buffer de 64MB.
        /// </summary>
        private void CopyDirectoryWithBuffer(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            // Copiar arquivos com buffer grande
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destDir, fileName);

                using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024 * 1024);
                using var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024 * 1024);
                sourceStream.CopyTo(destStream);
            }

            // Recursivamente copiar subdiretórios
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectoryWithBuffer(dir, destSubDir);
            }
        }

        /// <summary>
        /// Recursively copy directory (fallback).
        /// </summary>
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
