using System;
using System.IO;
using Libplanet.RocksDBStore;
using Serilog;

namespace NineChronicles.Snapshot.GcPipeline
{
    /// <summary>
    /// Phase 1: Export all KV pairs from states/ RocksDB to binary file.
    /// Format: [key:32b][val_len:4b little-endian][val:Nb] repeated
    /// </summary>
    public class GcExporter
    {
        private readonly ILogger _logger;

        public GcExporter(ILogger logger)
        {
            _logger = logger;
        }

        public ExportResult ExportStates(string statesPath, string outputFile)
        {
            var result = new ExportResult();
            
            _logger.Debug("Opening states/ KV store: {Path}", statesPath);
            using var stateKeyValueStore = new RocksDBKeyValueStore(statesPath);
            
            _logger.Debug("Creating export file: {File}", outputFile);
            using var outputStream = File.Create(outputFile);
            using var writer = new BinaryWriter(outputStream);
            
            long count = 0;
            
            _logger.Debug("Scanning all keys...");
            foreach (var key in stateKeyValueStore.ListKeys())
            {
                var keyBytes = key.ToByteArray();
                if (keyBytes.Length != 32)
                {
                    _logger.Warning("Skipping key with length {Length} (expected 32)", keyBytes.Length);
                    continue;
                }
                
                var value = stateKeyValueStore.Get(key);
                
                // Write: [key:32b][val_len:4b][val:Nb]
                writer.Write(keyBytes);
                writer.Write(value.Length);
                writer.Write(value);
                
                count++;
                if (count % 10_000_000 == 0)
                {
                    _logger.Debug("Exported {Count}M pairs...", count / 1_000_000);
                }
            }
            
            writer.Flush();
            
            result.TotalEntries = count;
            result.FileSizeGB = new FileInfo(outputFile).Length / 1_000_000_000.0;
            
            _logger.Debug("Export complete: {Count} entries, {Size:F1} GB", 
                result.TotalEntries, result.FileSizeGB);
            
            return result;
        }
    }
}
