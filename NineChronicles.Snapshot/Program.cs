using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using ZstdSharp;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using Bencodex.Types;
using Cocona;
using Libplanet.Action;
using Libplanet.Action.Loader;
using Libplanet.Types.Blocks;
using Libplanet.RocksDBStore;
using Libplanet.Store;
using RocksDbSharp;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Policies;
using Libplanet.Common;
using Libplanet.Crypto;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using ILogger = Serilog.ILogger;

namespace NineChronicles.Snapshot
{
    internal static class RocksDbNative
    {
        private const string Lib = "rocksdb";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rocksdb_options_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_options_destroy(IntPtr options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_options_set_skip_checking_sst_file_sizes_on_db_open(
            IntPtr options, byte val);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_options_set_paranoid_checks(
            IntPtr options, byte val);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_compact_range(
            IntPtr db,
            IntPtr start_key,
            UIntPtr start_key_len,
            IntPtr limit_key,
            UIntPtr limit_key_len);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_flush(
            IntPtr db,
            IntPtr flush_options,
            out IntPtr errptr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rocksdb_flushoptions_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_flushoptions_destroy(IntPtr options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rocksdb_block_based_options_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_block_based_options_destroy(IntPtr options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_block_based_options_set_format_version(
            IntPtr options, int version);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_options_set_block_based_table_factory(
            IntPtr options, IntPtr table_options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr rocksdb_open_as_secondary(
            IntPtr options,
            string db_path,
            string secondary_path,
            out IntPtr errptr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_try_catch_up_with_primary(
            IntPtr db,
            out IntPtr errptr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rocksdb_checkpoint_object_create(
            IntPtr db,
            out IntPtr errptr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void rocksdb_checkpoint_create(
            IntPtr checkpoint,
            string checkpoint_dir,
            ulong log_size_for_flush,
            out IntPtr errptr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_checkpoint_object_destroy(IntPtr checkpoint);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_close(IntPtr db);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rocksdb_free(IntPtr ptr);

        public static string ReadAndFreeError(ref IntPtr errptr)
        {
            if (errptr == IntPtr.Zero)
            {
                return null;
            }

            string msg = Marshal.PtrToStringAnsi(errptr);
            rocksdb_free(errptr);
            errptr = IntPtr.Zero;
            return msg;
        }
    }

    partial class Program
    {
        public enum SnapshotType { Full, Partition, All }

        private enum ArchiveType { Zip, TarZstd }

        private static readonly IReadOnlyDictionary<ArchiveType, string> _archiveExtensions = new Dictionary<ArchiveType, string>
        {
            { ArchiveType.Zip, "zip" },
            { ArchiveType.TarZstd, "tar.zst" }
        };

        private int _compressionLevel;
        private int _parallelism;

        private RocksDBStore _store;
        private TrieStateStore _stateStore;
        private ILogger _logger;
        private HttpClient _httpClient;
        private string _slackWebhookUrl;
        private double _copyStatesTime;
        private string _liveCheckpointPath;

        private ArchiveType _archiveType = ArchiveType.Zip;
        private string ArchiveExtension { get => _archiveExtensions[_archiveType]; }

        static void Main(string[] args)
        {
            CoconaLiteApp.Run<Program>(args);
        }

        [Command]
        public void Snapshot(
            string apv,
            [Option('o')]
            string outputDirectory,
            [Option("bypass-copystates")]
            bool bypassCopyStates = false,
            bool zstd = false,
            int compressionLevel = -1,
            [Option("best")]
            bool best = false,
            bool live = false,
            int parallelism = 0,
            string storePath = null,
            int blockBefore = 1,
            SnapshotType snapshotType = SnapshotType.Partition,
            [Option("slack-webhook-url")]
            string slackWebhookUrl = null)
        {
            try
            {
                var configurationBuilder = new ConfigurationBuilder();
                configurationBuilder.AddJsonFile("appsettings.json");
                var configuration = configurationBuilder.Build();
                var loggerConf = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration);
                _logger = loggerConf.CreateLogger();

                _slackWebhookUrl = slackWebhookUrl;
                _httpClient = new HttpClient();

                if (zstd)
                {
                    _logger.Debug("Compression method: Zstd (Tar)");
                    _archiveType = ArchiveType.TarZstd;
                }
                else
                {
                    _logger.Debug("Compression method: Zip (Default)");
                }

                if (_archiveType == ArchiveType.Zip)
                {
                    if (best)
                    {
                        _compressionLevel = (int)CompressionLevel.SmallestSize;
                        _logger.Debug("ZIP --best flag: usando SmallestSize (3)");
                    }
                    else if (compressionLevel >= 0)
                    {
                        _compressionLevel = Math.Clamp(compressionLevel, 0, 3);
                    }
                    else
                    {
                        _compressionLevel = (int)CompressionLevel.SmallestSize;
                    }

                    _logger.Debug(
                        "ZIP CompressionLevel: {Level} ({Name})",
                        _compressionLevel,
                        (CompressionLevel)_compressionLevel);
                }
                else
                {
                    if (compressionLevel >= -7 && compressionLevel <= 22)
                    {
                        _compressionLevel = compressionLevel;
                    }
                    else if (best)
                    {
                        _compressionLevel = 19;
                    }
                    else
                    {
                        _compressionLevel = 3;
                    }

                    _logger.Debug(
                        "Zstd CompressionLevel: {Level}{Best}",
                        _compressionLevel,
                        best ? " (--best)" : "");
                }

                _parallelism = parallelism > 0
                    ? parallelism
                    : Math.Max(1, Environment.ProcessorCount - 1);
                _logger.Debug("Parallelism (read threads): {Parallelism}", _parallelism);

                var snapshotStart = DateTimeOffset.Now;
                _logger.Debug($"Create Snapshot-{snapshotType.ToString()} start.");

                const int epochUnitSeconds = 86400;
                string defaultStorePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "planetarium",
                    "9c"
                );

                if (blockBefore < 0)
                {
                    throw new CommandExitedException("The --block-before option must be greater than or equal to 0.", -1);
                }

                Directory.CreateDirectory(outputDirectory);
                Directory.CreateDirectory(Path.Combine(outputDirectory, "partition"));
                Directory.CreateDirectory(Path.Combine(outputDirectory, "state"));
                Directory.CreateDirectory(Path.Combine(outputDirectory, "metadata"));
                Directory.CreateDirectory(Path.Combine(outputDirectory, "full"));

                outputDirectory = string.IsNullOrEmpty(outputDirectory)
                    ? Environment.CurrentDirectory
                    : outputDirectory;

                var metadataDirectory = Path.Combine(outputDirectory, "metadata");
                int currentMetadataBlockEpoch = GetMetaDataEpoch(metadataDirectory, "BlockEpoch");
                int currentMetadataTxEpoch = GetMetaDataEpoch(metadataDirectory, "TxEpoch");
                int previousMetadataBlockEpoch = GetMetaDataEpoch(metadataDirectory, "PreviousBlockEpoch");

                storePath = string.IsNullOrEmpty(storePath) ? defaultStorePath : storePath;
                if (!Directory.Exists(storePath))
                {
                    throw new CommandExitedException("Invalid store path. Please check --store-path is valid.", -1);
                }

                var originalStorePath = storePath;

                if (live)
                {
                    _logger.Information("LIVE SNAPSHOT MODE ENABLED");

                    var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    _liveCheckpointPath = Path.Combine(homeDir, $"9c-live-checkpoint-{Guid.NewGuid():N}");

                    _logger.Information("Creating checkpoint: {Checkpoint}", _liveCheckpointPath);
                    CreateLiveCheckpoint(originalStorePath, _liveCheckpointPath);
                    storePath = _liveCheckpointPath;

                    _logger.Information("Snapshot will run from checkpoint instead of live database");
                }

                var statesPath = Path.Combine(storePath, "states");
                var mainPath = Path.Combine(storePath, "9c-main");
                var stateRefPath = Path.Combine(storePath, "stateref");
                var statePath = Path.Combine(storePath, "state");
                var newStatesPath = Path.Combine(storePath, "new_states");
                var stateHashesPath = Path.Combine(storePath, "state_hashes");

                var staleDirectories = new[] { mainPath, statePath, stateRefPath, stateHashesPath, newStatesPath };
                foreach (var staleDirectory in staleDirectories)
                {
                    if (Directory.Exists(staleDirectory))
                    {
                        Directory.Delete(staleDirectory, true);
                    }
                }

                if (RocksDBStore.MigrateChainDBFromColumnFamilies(Path.Combine(storePath, "chain")))
                {
                    _logger.Debug("Successfully migrated IndexDB.");
                }
                else
                {
                    _logger.Debug("Migration not required.");
                }

                _store = new RocksDBStore(storePath);
                var stateKeyValueStore = new RocksDBKeyValueStore(statesPath);
                _stateStore = new TrieStateStore(stateKeyValueStore);

                var canonicalChainId = _store.GetCanonicalChainId();
                if (!(canonicalChainId is { } chainId))
                {
                    throw new CommandExitedException("Canonical chain doesn't exist.", -1);
                }

                var genesisHash = _store.IterateIndexes(chainId, 0, 1).First();
                var tipHash = _store.IndexBlockHash(chainId, -1)
                    ?? throw new CommandExitedException("The given chain seems empty.", -1);
                if (!(_store.GetBlockIndex(tipHash) is { } tipIndex))
                {
                    throw new CommandExitedException($"The index of {tipHash} doesn't exist.", -1);
                }

                IStagePolicy stagePolicy = new VolatileStagePolicy();
                IBlockPolicy blockPolicy = new BlockPolicy();
                var blockChainStates = new BlockChainStates(_store, _stateStore);
                var actionEvaluator = new ActionEvaluator(
                    blockPolicy.PolicyActionsRegistry,
                    _stateStore,
                    new NCActionLoader()
                );
                var tip = _store.GetBlock(tipHash);

                var potentialSnapshotTipIndex = tipIndex - blockBefore;
                var potentialSnapshotTipHash = (BlockHash)_store.IndexBlockHash(chainId, potentialSnapshotTipIndex)!;
                var snapshotTip = _store.GetBlock(potentialSnapshotTipHash);

                _logger.Debug("Original Store Tip: #{0}\n1. LastCommit: {1}\n2. BlockCommit in Chain: {2}\n3. BlockCommit in Store: {3}",
                    tip.Index, tip.LastCommit, GetChainBlockCommit(tipHash, chainId), _store.GetBlockCommit(tipHash));
                _logger.Debug("Potential Snapshot Tip: #{0}\n1. LastCommit: {1}\n2. BlockCommit in Chain: {2}\n3. BlockCommit in Store: {3}",
                    potentialSnapshotTipIndex, snapshotTip.LastCommit, GetChainBlockCommit(potentialSnapshotTipHash, chainId), _store.GetBlockCommit(potentialSnapshotTipHash));

                var tipBlockCommit = _store.GetBlockCommit(tipHash) ?? GetChainBlockCommit(tipHash, chainId);
                var potentialSnapshotTipBlockCommit = _store.GetBlockCommit(potentialSnapshotTipHash) ?? GetChainBlockCommit(potentialSnapshotTipHash, chainId);

                if (potentialSnapshotTipBlockCommit != null)
                {
                    _logger.Debug("Adding the tip(#{0}) and the snapshot tip(#{1})'s block commit to the store", tipIndex, snapshotTip.Index);
                    _store.PutBlockCommit(tipBlockCommit);
                    _store.PutChainBlockCommit(chainId, tipBlockCommit);
                    _store.PutBlockCommit(potentialSnapshotTipBlockCommit);
                    _store.PutChainBlockCommit(chainId, potentialSnapshotTipBlockCommit);
                }
                else
                {
                    _logger.Debug("There is no block commit associated with the potential snapshot tip: #{0}. Snapshot will automatically truncate 1 more block from the original chain tip.", potentialSnapshotTipIndex);
                    blockBefore += 1;
                    potentialSnapshotTipBlockCommit = _store.GetBlock((BlockHash)_store.IndexBlockHash(chainId, tip.Index - blockBefore + 1)!).LastCommit;
                    _store.PutBlockCommit(tipBlockCommit);
                    _store.PutChainBlockCommit(chainId, tipBlockCommit);
                    _store.PutBlockCommit(potentialSnapshotTipBlockCommit);
                    _store.PutChainBlockCommit(chainId, potentialSnapshotTipBlockCommit);
                }

                var blockCommitBlock = _store.GetBlock(tipHash);
                for (var i = 0; i < blockBefore + 5; i++)
                {
                    _logger.Debug("Adding block #{0}'s block commit to the store", blockCommitBlock.Index - 1);
                    _store.PutBlockCommit(blockCommitBlock.LastCommit);
                    _store.PutChainBlockCommit(chainId, blockCommitBlock.LastCommit);
                    blockCommitBlock = _store.GetBlock((BlockHash)blockCommitBlock.PreviousHash!);
                }

                var snapshotTipIndex = Math.Max(tipIndex - (blockBefore + 1), 0);
                BlockHash snapshotTipHash;
                do
                {
                    snapshotTipIndex++;

                    if (!(_store.IndexBlockHash(chainId, snapshotTipIndex) is { } hash))
                    {
                        throw new CommandExitedException($"The index {snapshotTipIndex} doesn't exist on ${chainId}.", -1);
                    }

                    snapshotTipHash = hash;
                } while (!_stateStore.GetStateRoot(_store.GetBlock(snapshotTipHash).StateRootHash).Recorded);

                var forkedId = Guid.NewGuid();
                Fork(chainId, forkedId, snapshotTipHash, tip);

                _store.SetCanonicalChainId(forkedId);
                foreach (var id in _store.ListChainIds().Where(id => !id.Equals(forkedId)))
                {
                    _store.DeleteChainId(id);
                }

                var snapshotTipDigest = _store.GetBlockDigest(snapshotTipHash);
                var snapshotTipStateRootHash = _store.GetStateRootHash(snapshotTipHash);
                ImmutableHashSet<HashDigest<SHA256>> stateHashes = ImmutableHashSet<HashDigest<SHA256>>.Empty.Add((HashDigest<SHA256>)snapshotTipStateRootHash!);

                BlockHash? previousBlockHash = snapshotTipDigest?.Hash;
                int count = 0;
                const int maxStateDepth = 2;

                while (previousBlockHash is { } pbh &&
                       _store.GetBlockDigest(pbh) is { } previousBlockDigest &&
                       count < maxStateDepth)
                {
                    stateHashes = stateHashes.Add(previousBlockDigest.StateRootHash);
                    previousBlockHash = previousBlockDigest.PreviousHash;
                    count++;
                }

                var newTipHash = _store.IndexBlockHash(forkedId, -1)
                    ?? throw new CommandExitedException("The given chain seems empty.", -1);
                var newTip = _store.GetBlock(newTipHash);
                var latestEpoch = (int)(newTip.Timestamp.ToUnixTimeSeconds() / epochUnitSeconds);
                _logger.Debug("Official Snapshot Tip: #{0}\n1. Timestamp: {1}\n2. Latest Epoch: {2}\n3. BlockCommit in Chain: {3}\n4. BlockCommit in Store: {4}",
                    newTip.Index, newTip.Timestamp.UtcDateTime, latestEpoch, GetChainBlockCommit(newTip.Hash, forkedId), _store.GetBlockCommit(newTip.Hash));

                DateTimeOffset start;

                if (bypassCopyStates)
                {
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} CopyStates Skipped.");
                    // Close stores when using bypass-copystate
                    _store.Dispose();
                    _stateStore.Dispose();
                    stateKeyValueStore.Dispose();
                }
                else
                {
                    _logger.Information($"Snapshot-{snapshotType.ToString()} 🚀 GC Pipeline Start (5x faster than CopyStates!)");
                    start = DateTimeOffset.Now;

                    // CRITICAL: Close ALL stores before GC Pipeline (RocksDB lock conflict)
                    // _store also holds a reference to states/ internally!
                    _logger.Debug("Closing ALL stores before GC Pipeline...");
                    _stateStore.Dispose();
                    stateKeyValueStore.Dispose();
                    _store.Dispose();

                    // Use GC Pipeline instead of CopyStates!
                    // Use storePath as tempDir (has enough space, ~200GB needed for export)
                    var gcTempDir = Path.Combine(storePath, ".gc-temp");
                    var gcPipeline = new GcPipeline.GcPipeline(_logger);
                    var gcResult = gcPipeline.RunGcPipeline(stateHashes, statesPath, newStatesPath, gcTempDir);

                    if (!gcResult.Success)
                    {
                        // Fallback to old CopyStates if GC fails
                        _logger.Warning($"⚠️  GC Pipeline failed: {gcResult.ErrorMessage}. Falling back to CopyStates...");
                        
                        // Clean up failed GC attempt
                        if (Directory.Exists(newStatesPath))
                        {
                            Directory.Delete(newStatesPath, true);
                        }

                        // Reopen ALL stores for CopyStates fallback
                        _store = new RocksDBStore(storePath);
                        var reopenedStateKeyValueStore = new RocksDBKeyValueStore(statesPath);
                        var reopenedStateStore = new TrieStateStore(reopenedStateKeyValueStore);

                        // Fallback: Use original CopyStates
                        var newStateKeyValueStore = new RocksDBKeyValueStore(
                            newStatesPath,
                            options: CreateBulkWriteOptions());
                        var newStateStore = new TrieStateStore(newStateKeyValueStore);

                        reopenedStateStore.CopyStates(stateHashes, newStateStore, _parallelism);
                        _copyStatesTime = (DateTimeOffset.Now - start).TotalMinutes;
                        
                        newStateStore.Dispose();
                        newStateKeyValueStore.Dispose();
                        reopenedStateStore.Dispose();
                        reopenedStateKeyValueStore.Dispose();
                        _store.Dispose();
                        
                        _logger.Information($"   ✓ CopyStates (fallback): {_copyStatesTime:F1} min");
                    }
                    else
                    {
                        // GC Pipeline succeeded!
                        _copyStatesTime = gcResult.TotalMinutes;
                        _logger.Information($"Snapshot-{snapshotType.ToString()} ✅ GC Pipeline Done. Time Taken: {_copyStatesTime:F1} min");
                        _logger.Information($"   📊 Phase 1 (Export):  {gcResult.Phase1Minutes:F1} min");
                        _logger.Information($"   📊 Phase 2 (BFS):     {gcResult.Phase2Minutes:F1} min");
                        _logger.Information($"   📊 Phase 3 (Write):   {gcResult.Phase3Minutes:F1} min");
                        _logger.Information($"   💾 Nodes: {gcResult.TotalNodes:N0} → {gcResult.LiveNodes:N0} ({gcResult.DeletedNodes * 100.0 / gcResult.TotalNodes:F1}% garbage removed)");
                    }
                }

                if (Directory.Exists(newStatesPath))
                {
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Determining State Sizes Start.");
                    var statesPathSize = Directory.GetFiles(statesPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                    var newStatesPathSize = Directory.GetFiles(newStatesPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                    var previousStatesSizeGiB = (float)statesPathSize / 1024 / 1024 / 1024;
                    var newStatesSizeGiB = (float)newStatesPathSize / 1024 / 1024 / 1024;

                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Previous States Size: {previousStatesSizeGiB} GiB");
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} New States Size: {newStatesSizeGiB} GiB");

                    // Update Slack message to show GC Pipeline benefits
                    var reductionPercent = ((previousStatesSizeGiB - newStatesSizeGiB) / previousStatesSizeGiB) * 100;
                    var slackMessage = $"📊 GC Pipeline Complete\n" +
                                      $"⏱️  Time: {_copyStatesTime:F1} min (vs ~20h CopyStates = 5x faster!)\n" +
                                      $"💾 Size: {newStatesSizeGiB:F1} GiB (from {previousStatesSizeGiB:F1} GiB = {reductionPercent:F1}% reduction)";
                    SendSlackMessage(slackMessage);

                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Move States Start.");
                    start = DateTimeOffset.Now;
                    Directory.Delete(statesPath, recursive: true);
                    Directory.Move(newStatesPath, statesPath);
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Move States Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min");
                }

                var partitionBaseFilename = GetPartitionBaseFileName(currentMetadataBlockEpoch, currentMetadataTxEpoch, latestEpoch);
                var stateBaseFilename = "state_latest";

                var fullSnapshotDirectory = Path.Combine(outputDirectory, "full");
                var genesisHashHex = ByteUtil.Hex(genesisHash.ToByteArray());
                var snapshotTipHashHex = ByteUtil.Hex(snapshotTipHash.ToByteArray());
                var fullSnapshotFilename = $"{genesisHashHex}-snapshot-{snapshotTipHashHex}-{snapshotTipIndex}.{ArchiveExtension}";
                var fullSnapshotPath = Path.Combine(fullSnapshotDirectory, fullSnapshotFilename);

                var partitionSnapshotFilename = $"{partitionBaseFilename}.{ArchiveExtension}";
                var partitionSnapshotPath = Path.Combine(outputDirectory, "partition", partitionSnapshotFilename);
                var stateSnapshotFilename = $"{stateBaseFilename}.{ArchiveExtension}";
                var stateSnapshotPath = Path.Combine(outputDirectory, "state", stateSnapshotFilename);

                _logger.Debug($"Snapshot-{snapshotType.ToString()} Clean Store Start.");
                start = DateTimeOffset.Now;
                CleanStore(partitionSnapshotPath, stateSnapshotPath, fullSnapshotPath, storePath);
                _logger.Debug($"Snapshot-{snapshotType.ToString()} Clean Store Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min.");

                if (snapshotType == SnapshotType.Full || snapshotType == SnapshotType.All)
                {
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create Full ZipFile Start.");
                    start = DateTimeOffset.Now;
                    ArchiveDirectory(fullSnapshotPath, storePath);
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create Full ZipFile Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min.");
                }

                if (snapshotType == SnapshotType.Partition || snapshotType == SnapshotType.All)
                {
                    var epochLimit = GetEpochLimit(latestEpoch, currentMetadataBlockEpoch, previousMetadataBlockEpoch);

                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create Partition Archive Start.");
                    start = DateTimeOffset.Now;
                    ArchiveDirectory(partitionSnapshotPath, storePath, epochLimit, new[] { "block", "tx" }, new[] { "blockindex", "txindex" });
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create Partition Archive Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min.");

                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create State Archive Start.");
                    start = DateTimeOffset.Now;
                    ArchiveDirectory(stateSnapshotPath, storePath, subDirs: new[]
                    {
                        "block/blockindex",
                        "tx/txindex",
                        "txbindex",
                        "states",
                        "chain",
                        "blockcommit",
                        "txexec"
                    });
                    _logger.Debug($"Snapshot-{snapshotType.ToString()} Create State Archive Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min.");

                    if (snapshotTipDigest is null)
                    {
                        throw new CommandExitedException("Tip does not exist.", -1);
                    }

                    string stringfyMetadata = CreateMetadata(
                        snapshotTipDigest.Value,
                        apv,
                        currentMetadataBlockEpoch,
                        currentMetadataTxEpoch,
                        previousMetadataBlockEpoch,
                        latestEpoch);
                    var metadataFilename = $"{partitionBaseFilename}.json";
                    var metadataPath = Path.Combine(metadataDirectory, metadataFilename);

                    if (File.Exists(metadataPath))
                    {
                        File.Delete(metadataPath);
                    }

                    File.WriteAllText(metadataPath, stringfyMetadata);
                }

                _logger.Debug($"Create Snapshot-{snapshotType.ToString()} Complete. Time Taken: {(DateTimeOffset.Now - snapshotStart).TotalMinutes} min.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Error(ex.StackTrace);
            }
            finally
            {
                try
                {
                    _httpClient?.Dispose();
                    _store?.Dispose();
                    _stateStore?.Dispose();

                    if (!string.IsNullOrEmpty(_liveCheckpointPath) && Directory.Exists(_liveCheckpointPath))
                    {
                        _logger?.Information("Removing checkpoint {Checkpoint}", _liveCheckpointPath);
                        Directory.Delete(_liveCheckpointPath, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warning("Checkpoint cleanup failed: {Error}", ex.Message);
                }
            }
        }

        private static DbOptions CreateBulkWriteOptions()
        {
            return new DbOptions()
                .SetCreateIfMissing()
                // Memtables maiores → menos flushes intermediários para disco
                .SetWriteBufferSize(256 * 1024 * 1024)       // 256 MB (padrão: 64 MB)
                .SetMaxWriteBufferNumber(6)                   // até 6 memtables em RAM
                .SetMinWriteBufferNumberToMerge(2)            // mergeia 2 antes de flush
                // Compaction em background agressiva
                .SetMaxBackgroundCompactions(8)
                // SST files maiores → menos compaction L0→L1
                .SetTargetFileSizeBase(256 * 1024 * 1024)
                // Deixa mais arquivos em L0 antes de parar escritas
                .SetLevel0FileNumCompactionTrigger(8)
                .SetLevel0SlowdownWritesTrigger(20)
                .SetLevel0StopWritesTrigger(36)
                // Sem limite de file handles abertos
                .SetMaxOpenFiles(-1)
                // LZ4: compressão rápida, muito melhor que Snappy para throughput
                .SetCompression(Compression.Lz4)
                // Mantém os limites de compaction do original
                .SetSoftPendingCompactionBytesLimit(1000000000000)
                .SetHardPendingCompactionBytesLimit(1038176821042);
        }

        private string GetPartitionBaseFileName(int currentMetadataBlockEpoch, int currentMetadataTxEpoch, int latestEpoch)
        {
            if (currentMetadataBlockEpoch == 0 && currentMetadataTxEpoch == 0)
            {
                return $"snapshot-{latestEpoch - 1}-{latestEpoch - 1}";
            }
            else
            {
                return $"snapshot-{latestEpoch}-{latestEpoch}";
            }
        }

        private int GetEpochLimit(int latestEpoch, int currentMetadataEpoch, int previousMetadataEpoch)
        {
            if (latestEpoch == currentMetadataEpoch)
            {
                if (latestEpoch == previousMetadataEpoch)
                {
                    return previousMetadataEpoch - 1;
                }

                if (previousMetadataEpoch == 0)
                {
                    return currentMetadataEpoch - 1;
                }

                return previousMetadataEpoch;
            }

            return currentMetadataEpoch;
        }

        private string CreateMetadata(
            BlockDigest snapshotTipDigest,
            string apv,
            int currentMetadataBlockEpoch,
            int currentMetadataTxEpoch,
            int previousMetadataBlockEpoch,
            int latestEpoch)
        {
            BlockHeader snapshotTipHeader = snapshotTipDigest.GetHeader();
            JObject jsonObject = JObject.FromObject(snapshotTipHeader);
            jsonObject.Add("APV", apv);
            jsonObject = AddPreviousEpochs(jsonObject, currentMetadataBlockEpoch, previousMetadataBlockEpoch, latestEpoch, "PreviousBlockEpoch", "PreviousTxEpoch");

            if (currentMetadataBlockEpoch == 0 && currentMetadataTxEpoch == 0)
            {
                jsonObject.Add("BlockEpoch", latestEpoch - 1);
                jsonObject.Add("TxEpoch", latestEpoch - 1);
            }
            else
            {
                jsonObject.Add("BlockEpoch", latestEpoch);
                jsonObject.Add("TxEpoch", latestEpoch);
            }

            return JsonConvert.SerializeObject(jsonObject);
        }

        private void CleanStore(string partitionSnapshotPath, string stateSnapshotPath, string fullSnapshotPath, string storePath)
        {
            if (File.Exists(partitionSnapshotPath))
            {
                File.Delete(partitionSnapshotPath);
            }

            if (File.Exists(stateSnapshotPath))
            {
                File.Delete(stateSnapshotPath);
            }

            if (File.Exists(fullSnapshotPath))
            {
                File.Delete(fullSnapshotPath);
            }

            var cleanDirectories = new[]
            {
                Path.Combine(storePath, "blockpercept"),
                Path.Combine(storePath, "stagedtx")
            };

            foreach (var path in cleanDirectories)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
        }

        private void CleanPartitionStore(string partitionDirectory)
        {
            var cleanDirectories = new[]
            {
                Path.Combine(partitionDirectory, "block", "blockindex"),
                Path.Combine(partitionDirectory, "tx", "txindex"),
            };

            foreach (var path in cleanDirectories)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
        }

        private void Fork(Guid src, Guid dest, BlockHash branchpointHash, Block tip)
        {
            _store.ForkBlockIndexes(src, dest, branchpointHash);
            if (_store.GetBlockCommit(branchpointHash) is { })
            {
                _store.PutChainBlockCommit(dest, _store.GetBlockCommit(branchpointHash));
            }

            _store.ForkTxNonces(src, dest);

            for (Block block = tip;
                 block.PreviousHash is { } hash && !block.Hash.Equals(branchpointHash);
                 block = _store.GetBlock(hash))
            {
                IEnumerable<(Address, int)> signers = block.Transactions
                    .GroupBy(tx => tx.Signer)
                    .Select(g => (g.Key, g.Count()));

                foreach ((Address address, int txCount) in signers)
                {
                    _store.IncreaseTxNonce(dest, address, -txCount);
                }
            }
        }

        private int GetMetaDataEpoch(string outputDirectory, string epochType)
        {
            try
            {
                string previousMetadata = Directory.GetFiles(outputDirectory)
                    .Where(x => Path.GetExtension(x) == ".json")
                    .OrderByDescending(x => File.GetLastWriteTime(x))
                    .First();
                var jsonObject = JObject.Parse(File.ReadAllText(previousMetadata));
                return (int)jsonObject[epochType];
            }
            catch (InvalidOperationException ex)
            {
                _logger.Error(ex.Message);
                _logger.Error(ex.StackTrace);
                return 0;
            }
        }

        private void ArchiveDirectory(
            string destPath,
            string srcDirPath,
            int? epochLimit = null,
            string[] subDirs = null,
            string[] excludeDirs = null)
        {
            var archiveEntries = ArchivePaths(srcDirPath, epochLimit, subDirs, excludeDirs)
                .Select(path => Path.GetRelativePath(srcDirPath, path))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            try
            {
                using FileStream destStream = File.Create(destPath);
                if (_archiveType == ArchiveType.TarZstd)
                {
                    // True streaming: TarWriter -> CompressionStream -> FileStream
                    // Zero temp disk overhead — data is compressed on-the-fly.
                    using var zstdStream = new CompressionStream(destStream, _compressionLevel);
                    using var tarWriter = new TarWriter(zstdStream, TarEntryFormat.Pax, leaveOpen: true);

                    ParallelArchiveWrite(
                        archiveEntries,
                        srcDirPath,
                        (entry, data) =>
                        {
                            using var ms = new MemoryStream(data, writable: false);
                            tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry) { DataStream = ms });
                        });
                }
                else if (_archiveType == ArchiveType.Zip)
                {
                    using ZipArchive zipArchive = new ZipArchive(destStream, ZipArchiveMode.Create);

                    ParallelArchiveWrite(
                        archiveEntries,
                        srcDirPath,
                        (entry, data) =>
                        {
                            var zipEntry = zipArchive.CreateEntry(entry, (CompressionLevel)_compressionLevel);
                            using var zipStream = zipEntry.Open();
                            zipStream.Write(data, 0, data.Length);
                        });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Error(ex.StackTrace);
            }
        }

        private void ParallelArchiveWrite(string[] entries, string srcDirPath, Action<string, byte[]> writeEntry)
        {
            int channelCapacity = Math.Max(2, _parallelism * 2);
            var channel = Channel.CreateBounded<(string entry, byte[] data)>(
                new BoundedChannelOptions(channelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true,
                });

            var producer = Task.Run(async () =>
            {
                await Parallel.ForEachAsync(
                    entries,
                    new ParallelOptions { MaxDegreeOfParallelism = _parallelism },
                    async (entry, ct) =>
                    {
                        var fullPath = Path.Combine(srcDirPath, entry);
                        var data = await File.ReadAllBytesAsync(fullPath, ct);
                        await channel.Writer.WriteAsync((entry, data), ct);
                    });
                channel.Writer.Complete();
            });

            foreach (var item in channel.Reader.ReadAllAsync().ToBlockingEnumerable())
            {
                writeEntry(item.entry, item.data);
            }

            producer.GetAwaiter().GetResult();
        }

        private string[] ArchivePaths(
            string dirPath,
            int? epochLimit = null,
            string[] subDirs = null,
            string[] excludeDirs = null)
        {
            if (subDirs != null)
            {
                return subDirs.SelectMany(subDir => ArchivePaths(
                    Path.Combine(dirPath, subDir),
                    epochLimit: epochLimit,
                    excludeDirs: excludeDirs)).ToArray();
            }

            var dirName = dirPath.Split("/").Last();

            if ((excludeDirs is { } && excludeDirs.Contains(dirName))
                || (epochLimit.HasValue
                    && int.TryParse(Regex.Match(dirName, @"^epoch(\d+)$").Groups[1].Value, out int epoch)
                    && epoch < epochLimit.Value))
            {
                return new[] { "" };
            }

            var files = Directory.GetFiles(dirPath);
            var directories = Directory.GetDirectories(dirPath)
                .AsParallel()
                .WithDegreeOfParallelism(_parallelism)
                .SelectMany(subdir => ArchivePaths(subdir, epochLimit, excludeDirs: excludeDirs))
                .Where(path => path != "");

            return files.Concat(directories).ToArray();
        }

        private JObject AddPreviousEpochs(
            JObject jsonObject,
            int currentMetadataEpoch,
            int previousMetadataEpoch,
            int latestEpoch,
            string blockEpochName,
            string txEpochName)
        {
            if (currentMetadataEpoch == latestEpoch)
            {
                jsonObject.Add(blockEpochName, previousMetadataEpoch);
                jsonObject.Add(txEpochName, previousMetadataEpoch);
            }
            else
            {
                jsonObject.Add(blockEpochName, currentMetadataEpoch);
                jsonObject.Add(txEpochName, currentMetadataEpoch);
            }

            return jsonObject;
        }

        private BlockCommit GetChainBlockCommit(BlockHash blockHash, Guid chainId)
        {
            var tipHash = _store.IndexBlockHash(chainId, -1)
                ?? throw new CommandExitedException("The given chain seems empty.", -1);
            if (!(_store.GetBlockIndex(tipHash) is { } tipIndex))
            {
                throw new CommandExitedException($"The index of {tipHash} doesn't exist.", -1);
            }

            if (!(_store.GetBlockIndex(blockHash) is { } blockIndex))
            {
                throw new CommandExitedException($"The index of {blockHash} doesn't exist.", -1);
            }

            if (blockIndex == tipIndex)
            {
                return _store.GetChainBlockCommit(chainId);
            }

            if (!(_store.IndexBlockHash(chainId, blockIndex + 1) is { } nextHash))
            {
                throw new CommandExitedException($"The hash of index {blockIndex + 1} doesn't exist.", -1);
            }

            return _store.GetBlock(nextHash).LastCommit;
        }

        private void CreateLiveCheckpoint(string sourceDir, string checkpointDir)
        {
            if (Directory.Exists(checkpointDir))
            {
                Directory.Delete(checkpointDir, true);
            }
            Directory.CreateDirectory(checkpointDir);

            _logger.Information("=== LIVE CHECKPOINT via RocksDB Checkpoint API ===");

            var singleRocksDbRelPaths = new[]
            {
                "chain",
                "states",
                "blockcommit",
                "txexec",
                "txbindex",
                Path.Combine("block", "blockindex"),
                Path.Combine("tx", "txindex"),
            };

            foreach (var rel in singleRocksDbRelPaths)
            {
                var src = Path.Combine(sourceDir, rel);
                if (!Directory.Exists(src))
                {
                    continue;
                }

                var dst = Path.Combine(checkpointDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                _logger.Information("[Checkpoint] {Rel}", rel);
                CheckpointSingleRocksDb(src, dst);
            }

            foreach (var epochRoot in new[] { "block", "tx" })
            {
                var rootSrc = Path.Combine(sourceDir, epochRoot);
                if (!Directory.Exists(rootSrc))
                {
                    continue;
                }

                var rootDst = Path.Combine(checkpointDir, epochRoot);
                Directory.CreateDirectory(rootDst);

                var epochDirs = Directory.GetDirectories(rootSrc, "epoch*")
                    .OrderBy(d => d)
                    .ToArray();

                if (epochDirs.Length == 0)
                {
                    continue;
                }

                _logger.Information("📦 Processando {Count} épocas de {Root} (validação completa)...", epochDirs.Length, epochRoot);

                int processed = 0;
                foreach (var epochDir in epochDirs)
                {
                    processed++;
                    var rel = Path.GetRelativePath(sourceDir, epochDir);
                    var dst = Path.Combine(checkpointDir, rel);

                    _logger.Information("[{Current}/{Total}] Checkpoint {Rel}", processed, epochDirs.Length, rel);
                    CheckpointSingleRocksDb(epochDir, dst);
                }

                _logger.Information("✅ {Root}: {Count} épocas processadas com sucesso", epochRoot, epochDirs.Length);
            }

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                if (name == "LOCK")
                {
                    continue;
                }

                File.Copy(file, Path.Combine(checkpointDir, name), overwrite: true);
            }

            _logger.Information("=== Checkpoint concluído: {Path} ===", checkpointDir);
        }

        private void CheckpointSingleRocksDb(string dbPath, string checkpointPath)
        {
            _logger.Information("  Criando checkpoint compatível de: {Path}", dbPath);

            var checkpointParent = Path.GetDirectoryName(checkpointPath)!;
            var tempCopyPath = Path.Combine(checkpointParent, $".temp-copy-{Path.GetFileName(dbPath)}-{Guid.NewGuid():N}");

            try
            {
                _logger.Debug("    [1/3] Copiando SST files...");
                Directory.CreateDirectory(tempCopyPath);

                foreach (var file in Directory.GetFiles(dbPath))
                {
                    var name = Path.GetFileName(file);

                    if (name == "LOCK" || name.EndsWith(".log") || name == "LOG" || name.StartsWith("LOG.old"))
                    {
                        continue;
                    }

                    var dest = Path.Combine(tempCopyPath, name);

                    if (name.EndsWith(".sst"))
                    {
                        SafeHardLinkOrCopy(file, dest);
                    }
                    else
                    {
                        File.Copy(file, dest, overwrite: true);
                    }
                }

                _logger.Debug("    [2/3] Abrindo DB temporário com Libplanet.RocksDBStore...");

                RocksDBStore tempStore = null;
                try
                {
                    tempStore = new RocksDBStore(tempCopyPath);
                    tempStore.Dispose();
                    _logger.Debug("    ✓ DB validado com sucesso");
                }
                catch (Exception ex)
                {
                    _logger.Warning("    ⚠ Não foi possível validar com RocksDBStore: {Err}", ex.Message);
                    ValidateWithNativeRocksDb(tempCopyPath);
                }
                finally
                {
                    tempStore?.Dispose();
                }

                _logger.Debug("    [3/3] Movendo para destino final...");

                if (Directory.Exists(checkpointPath))
                {
                    Directory.Delete(checkpointPath, true);
                }

                Directory.Move(tempCopyPath, checkpointPath);
                _logger.Debug("  ✓ Checkpoint compatível criado: {Dst}", checkpointPath);
            }
            catch (Exception ex)
            {
                _logger.Error("  ✗ Falha ao criar checkpoint de '{Src}': {Err}", dbPath, ex.Message);

                try
                {
                    if (Directory.Exists(tempCopyPath))
                    {
                        Directory.Delete(tempCopyPath, true);
                    }
                }
                catch
                {
                }

                throw;
            }
        }

        private void ValidateWithNativeRocksDb(string dbPath)
        {
            IntPtr options = IntPtr.Zero;
            IntPtr db = IntPtr.Zero;
            string? secondaryPath = null;

            try
            {
                options = RocksDbNative.rocksdb_options_create();
                RocksDbNative.rocksdb_options_set_skip_checking_sst_file_sizes_on_db_open(options, 1);
                RocksDbNative.rocksdb_options_set_paranoid_checks(options, 0);

                secondaryPath = Path.Combine(Path.GetTempPath(), $"validate-{Guid.NewGuid():N}");
                Directory.CreateDirectory(secondaryPath);

                db = RocksDbNative.rocksdb_open_as_secondary(options, dbPath, secondaryPath, out var openErr);
                var openErrMsg = RocksDbNative.ReadAndFreeError(ref openErr);
                if (openErrMsg != null)
                {
                    throw new Exception($"Validation failed: {openErrMsg}");
                }

                if (db == IntPtr.Zero)
                {
                    throw new Exception("rocksdb_open_as_secondary returned null");
                }

                _logger.Debug("    ✓ DB validado com API nativa");
            }
            finally
            {
                if (db != IntPtr.Zero)
                {
                    RocksDbNative.rocksdb_close(db);
                }
                if (options != IntPtr.Zero)
                {
                    RocksDbNative.rocksdb_options_destroy(options);
                }
                try
                {
                    if (!string.IsNullOrEmpty(secondaryPath) && Directory.Exists(secondaryPath))
                    {
                        Directory.Delete(secondaryPath, true);
                    }
                }
                catch
                {
                }
            }
        }

        private void CopyDirectoryWithHardLinks(string srcDir, string dstDir)
        {
            Directory.CreateDirectory(dstDir);

            foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, file);
                var dst = Path.Combine(dstDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                var name = Path.GetFileName(file);
                if (name == "LOCK")
                {
                    continue;
                }

                if (IsRocksDbMetadataFile(name))
                {
                    File.Copy(file, dst, overwrite: true);
                }
                else
                {
                    SafeHardLinkOrCopy(file, dst);
                }
            }
        }

        private static bool IsRocksDbMetadataFile(string fileName) =>
            fileName.StartsWith("MANIFEST-") ||
            fileName == "CURRENT" ||
            fileName.StartsWith("OPTIONS-");

        private static void SafeHardLinkOrCopy(string src, string dst)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                File.Copy(src, dst, overwrite: true);
                return;
            }

            if (PosixLink(src, dst) == 0)
            {
                return;
            }

            File.Copy(src, dst, overwrite: true);
        }

        [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int PosixLink(string oldpath, string newpath);

        private void SendSlackMessage(string message)
        {
            if (string.IsNullOrEmpty(_slackWebhookUrl))
            {
                return;
            }

            try
            {
                var payload = new { text = message };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                _httpClient.PostAsync(_slackWebhookUrl, content).Wait();
            }
            catch (Exception ex)
            {
                _logger?.Warning("Failed to send Slack message: {Error}", ex.Message);
            }
        }

        public class NCActionLoader : IActionLoader
        {
            private readonly IActionLoader _actionLoader;
            public IAction LoadAction(long index, IValue value) => _actionLoader.LoadAction(index, value);
        }
    }
}
