# Snapshot

로컬에 있는 스토어를 압축하여 제네시스와 스냅샷 팁 블록의 해시에 따라 이름을 정해줍니다. *{genesisHash}-snapshot-{tipHash}.zip*

리오그로 스냅샷이 동작하지 않는 경우를 피하기 위해 기본값으로 10블록 이전의 블록을 팁으로하는
스냅샷을 만들며 `--block-before` 옵션을 통해 몇블록 전의 스냅샷을 찍을지 조정할 수 있습니다.

팁 블록의 헤더 정보와 APV를 별도의 파일로 저장합니다. *{genesisHash}-snapshot-{tipHash}.json*

생성한 스냅샷을 론처 업데이터에서 사용하게 하고 싶다면 *https://9c-test.s3.ap-northeast-2.amazonaws.com/latest/{genesisHash}-snapshot.zip*,
*https://9c-test.s3.ap-northeast-2.amazonaws.com/latest/{genesisHash}-snapshot.json*
경로에 업로드 해야 합니다.

`--snapshot-type` 옵션을 사용하는 경우, default로 증분 스냅샷이 찍힙니다. 옵션 값으로 `full`을 사용하면, 통 스냅샷으로 찍히며 `all`을 사용하면 증분 & 통 스냅샷이 모두 찍힙니다.

---

## English Documentation & Advanced Features

This repository is a production-grade tool for creating and managing Nine Chronicles blockchain node database snapshots.

We have enhanced this tool with **high-performance parallel I/O, live hot-checkpoints, and direct streaming Zstandard compression** similar to the Rust `nc-snapshot` tool architecture.

### Key Advanced Features

#### 1. Live Snapshot Support (`--live`)
- Allows taking consistent snapshots **without stopping the running node**.
- **Staging Checkpoints**: When `--live` is enabled, the tool creates an instant point-in-time hard-link copy of the database (`.sst` files) in a temporary checkpoint directory. Small mutable metadata/manifest files are copied cleanly, and database `LOCK` files are excluded.
- **Zero Downtime**: The running node continues executing compactions and writes blocks while the snapshot tool works independently on the staging checkpoint.
- **Auto-Cleanup**: The temporary staging checkpoint directory is automatically cleaned up when archiving completes.

> [!NOTE]
> It is highly recommended to run `--live` with `--bypass-copystates` for daily runs, as state trie copying can be extremely resource-intensive during live chain updates.

#### 2. Direct Streaming Compression (`--zstd`)
- Replaces intermediate temporary file creation with **on-the-fly streaming compression**.
- Original behavior wrote a massive uncompressed `.tar` archive to `/tmp` before compressing it, which required up to ~130 GiB of extra free space in `/tmp` and frequently caused `No space left on device` crashes.
- Now, data is piped dynamically from the concurrent reader pool into `TarWriter`, which pushes directly into `ZstdSharp.CompressionStream`. 
- **Zero temp disk overhead**: The only space required is the size of the final compressed `.tar.zst` output file.

#### 3. Parallel Disk I/O & Pipeline (`--parallelism`)
- Uses a producer-consumer pipeline with `System.Threading.Channels` and `Parallel.ForEachAsync`.
- Concurrently reads RocksDB files from disk using up to $N$ threads, queues them in a bounded channel (with backpressure support to keep memory usage low), and serializes the writes into the single-threaded archive format.
- Uses `.AsParallel()` directory scanning in `ArchivePaths`, accelerating metadata scanning over 50,000+ RocksDB database files.

---

### Command Line Options

```bash
$ dotnet run --project NineChronicles.Snapshot -- --help
Usage: NineChronicles.Snapshot [--apv <String>] [--output-directory <String>] [--bypass-copystates] [--zstd] [--compression-level <Int32>] [--live] [--parallelism <Int32>] [--store-path <String>] [--block-before <Int32>] [--snapshot-type <SnapshotType>] [--slack-webhook-url <String>] [--help] [--version]

NineChronicles.Snapshot

Options:
  --apv <String>                      (Required) The APV string matching the chain version.
  -o, --output-directory <String>     (Required) Path to save the generated archives and metadata.
  --bypass-copystates                 Bypasses copying state trie databases (useful for raw, rapid snapshots).
  --zstd                              Uses ZStandard multi-threaded compression (.tar.zst format).
  --compression-level <Int32>         Zstandard compression level. (Default: 0)
  --live                              Takes a snapshot without stopping the node (uses staging hardlink checkpoints).
  --parallelism <Int32>               Parallel read threads. 0 = auto-resolve (All logical CPU cores - 1). (Default: 0)
  --store-path <String>               Path to the 9c blockchain store directory.
  --block-before <Int32>              Truncate snapshots by N blocks before the current tip. (Default: 1)
  --snapshot-type <SnapshotType>      Snapshot modes: Partition, Full, or All. (Default: Partition)
  --slack-webhook-url <String>        Optional Slack webhook URL to send status updates.
  -h, --help                          Show help message.
  --version                           Show version.
```

---

### Examples

#### Offline State + Partition Snapshot (Zstandard compressed)
```bash
dotnet run --project NineChronicles.Snapshot -- \
  --store-path ~/9c-blockchain \
  --output-directory ~/snapshots/ \
  --snapshot-type Partition \
  --zstd \
  --apv "<CURRENT_APV>"
```

#### Online Live Snapshot (No downtime, bypassing CopyStates, 8 parallel threads)
```bash
dotnet run --project NineChronicles.Snapshot -- \
  --store-path ~/9c-blockchain \
  --output-directory ~/snapshots/ \
  --snapshot-type Partition \
  --zstd \
  --live \
  --bypass-copystates \
  --parallelism 8 \
  --apv "<CURRENT_APV>"
```
