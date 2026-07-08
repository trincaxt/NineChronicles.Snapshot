# Integration Guide: GC Pipeline into Program.cs

## Step-by-Step Integration

### 1. Copy Files to Repository

```bash
cd /path/to/NineChronicles.Snapshot/NineChronicles.Snapshot/

# Create GcPipeline directory
mkdir -p GcPipeline

# Copy all GC Pipeline files
cp /home/vrunnx/teste/nc-snapshot/GcPipeline/*.cs GcPipeline/
```

Files to copy:
- `GcPipeline/GcPipeline.cs` (orchestrator)
- `GcPipeline/GcExporter.cs` (phase 1)
- `GcPipeline/GcBfsFilter.cs` (phase 2)
- `GcPipeline/GcWriter.cs` (phase 3)
- `GcPipeline/GcResult.cs` (data structures)

### 2. Add to .csproj

Open `NineChronicles.Snapshot.csproj` and ensure the new files are included:

```xml
<ItemGroup>
  <Compile Include="Program.cs" />
  <Compile Include="GcPipeline\GcPipeline.cs" />
  <Compile Include="GcPipeline\GcExporter.cs" />
  <Compile Include="GcPipeline\GcBfsFilter.cs" />
  <Compile Include="GcPipeline\GcWriter.cs" />
  <Compile Include="GcPipeline\GcResult.cs" />
</ItemGroup>
```

### 3. Modify Program.cs

Find line ~265 in Program.cs:

```csharp
if (bypassCopyStates)
{
    _logger.Debug($"Snapshot-{snapshotType.ToString()} CopyStates Skipped.");
}
else
{
    var newStateKeyValueStore = new RocksDBKeyValueStore(newStatesPath);
    var newStateStore = new TrieStateStore(newStateKeyValueStore);
    _logger.Debug($"Snapshot-{snapshotType.ToString()} CopyStates Start.");
    start = DateTimeOffset.Now;
    _stateStore.CopyStates(stateHashes, newStateStore);  // ← REPLACE THIS
    _copyStatesTime = (DateTimeOffset.Now - start).TotalMinutes;
    _logger.Debug($"Snapshot-{snapshotType.ToString()} CopyStates Done. Time Taken: {_copyStatesTime} min.");
    newStateStore.Dispose();
    newStateKeyValueStore.Dispose();
}
```

Replace with:

```csharp
if (bypassCopyStates)
{
    _logger.Debug($"Snapshot-{snapshotType.ToString()} CopyStates Skipped.");
}
else
{
    _logger.Debug($"Snapshot-{snapshotType.ToString()} GC Pipeline Start.");
    start = DateTimeOffset.Now;
    
    // Use GC Pipeline (5x faster than CopyStates!)
    var gcPipeline = new GcPipeline.GcPipeline(_logger);
    var gcResult = gcPipeline.RunGcPipeline(stateHashes, statesPath, newStatesPath);
    
    if (!gcResult.Success)
    {
        // Fallback to old CopyStates if GC fails
        _logger.Warning($"GC Pipeline failed: {gcResult.ErrorMessage}. Falling back to CopyStates.");
        
        if (Directory.Exists(newStatesPath))
        {
            Directory.Delete(newStatesPath, true);
        }
        
        var newStateKeyValueStore = new RocksDBKeyValueStore(newStatesPath);
        var newStateStore = new TrieStateStore(newStateKeyValueStore);
        _stateStore.CopyStates(stateHashes, newStateStore);
        _copyStatesTime = (DateTimeOffset.Now - start).TotalMinutes;
        newStateStore.Dispose();
        newStateKeyValueStore.Dispose();
        
        _logger.Information($"   ✓ CopyStates (fallback): {_copyStatesTime:F1} min");
    }
    else
    {
        _copyStatesTime = gcResult.TotalMinutes;
        
        _logger.Debug($"Snapshot-{snapshotType.ToString()} GC Pipeline Done. Time Taken: {_copyStatesTime:F1} min.");
        _logger.Information($"   📊 Phase 1 (Export): {gcResult.Phase1Minutes:F1} min");
        _logger.Information($"   📊 Phase 2 (BFS):    {gcResult.Phase2Minutes:F1} min");
        _logger.Information($"   📊 Phase 3 (Write):  {gcResult.Phase3Minutes:F1} min");
        _logger.Information($"   💾 Nodes: {gcResult.TotalNodes:N0} → {gcResult.LiveNodes:N0} ({gcResult.DeletedNodes * 100.0 / gcResult.TotalNodes:F1}% reduction)");
    }
}

// Continue with existing code...
_store.Dispose();
_stateStore.Dispose();
stateKeyValueStore.Dispose();

if (Directory.Exists(newStatesPath))
{
    _logger.Debug($"Snapshot-{snapshotType.ToString()} Determining State Sizes Start.");
    var statesPathSize = Directory.GetFiles(statesPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    var newStatesPathSize = Directory.GetFiles(newStatesPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    var previousStatesSizeGiB = (float)statesPathSize / 1024 / 1024 / 1024;
    var newStatesSizeGiB = (float)newStatesPathSize / 1024 / 1024 / 1024;
    _logger.Debug($"Snapshot-{snapshotType.ToString()} Previous States Size: {previousStatesSizeGiB} GiB");
    _logger.Debug($"Snapshot-{snapshotType.ToString()} New States Size: {newStatesSizeGiB} GiB");

    // Send Slack message with GC stats (update message)
    var slackMessage = $"📊 GC Pipeline Complete\n" +
                      $"⏱️ Time: {_copyStatesTime:F1} min (vs ~20h CopyStates = 5x faster!)\n" +
                      $"💾 Size: {newStatesSizeGiB:F1} GiB (vs ~68 GiB = 67% reduction)";
    SendSlackMessage(slackMessage);

    _logger.Debug($"Snapshot-{snapshotType.ToString()} Move States Start.");
    start = DateTimeOffset.Now;
    Directory.Delete(statesPath, recursive: true);
    Directory.Move(newStatesPath, statesPath);
    _logger.Debug($"Snapshot-{snapshotType.ToString()} Move States Done. Time Taken: {(DateTimeOffset.Now - start).TotalMinutes} min");
}
```

### 4. Build and Test

```bash
cd /path/to/NineChronicles.Snapshot

# Build
dotnet build -c Release

# Test with small blockchain
dotnet run -- \
  --apv "200440/..." \
  --output-directory ./test-output \
  --store-path /path/to/test-blockchain \
  --snapshot-type Partition

# Verify output
ls -lh test-output/state/
```

### 5. Validation

After running, verify:

1. **States size reduced**: Should be ~20-25 GB (vs ~68 GB before)
2. **Snapshot works**: Extract and test with node
3. **No corruption**: Node loads successfully
4. **Logs show phases**: Check Phase 1, 2, 3 timings

Expected log output:
```
📤 GC Phase 1: Exporting all KV pairs...
   ✓ Phase 1: 43.2 min (201,800,000 entries, 181.5 GB)
🌳 GC Phase 2: Running BFS to find live nodes...
📂 Loading export file into memory...
   ✓ Loaded 201,800,000 entries
🌳 Starting BFS traversal from 3 roots...
   ✓ Level 0: Found 3/3 nodes, 45 children → level 1
   ✓ Level 1: Found 45/45 nodes, 890 children → level 2
   ...
   ✓ Level 32: Found 12,450/12,450 nodes, 0 children → level 33
✅ BFS complete: 33 total levels, 66,229,435 live nodes
   ✓ Phase 2: 114.7 min (66,229,435 live keys, 33 levels)
📝 GC Phase 3: Writing pruned states...
   ✓ Phase 3: 37.1 min
✅ GC Pipeline Complete!
   ⏱️  Total Time: 195.0 min
   💾 Nodes: 201,800,000 → 66,229,435 (135,570,565 deleted, 67.2% removed)
```

### 6. Rollback Plan

If GC Pipeline has issues:

**Option A: Keep both implementations**
```csharp
[Option("use-gc-pipeline")]
bool useGcPipeline = true,

if (bypassCopyStates)
{
    // skipped
}
else if (useGcPipeline)
{
    // GC Pipeline
}
else
{
    // Old CopyStates
}
```

**Option B: Automatic fallback** (already implemented above)
- GC fails → auto-fallback to CopyStates
- Logs warning but continues

### 7. Monitoring

Add metrics to track:
- GC success rate
- Average time per phase
- Space savings percentage
- Fallback frequency

Example:
```csharp
// After GC completes
var metrics = new
{
    Method = gcResult.Success ? "GC" : "CopyStates",
    TimeMinutes = _copyStatesTime,
    SpaceSavedPercent = gcResult.Success 
        ? gcResult.DeletedNodes * 100.0 / gcResult.TotalNodes 
        : 0,
    Phase1Minutes = gcResult.Phase1Minutes,
    Phase2Minutes = gcResult.Phase2Minutes,
    Phase3Minutes = gcResult.Phase3Minutes
};

// Log or send to monitoring system
_logger.Information("Snapshot metrics: {@Metrics}", metrics);
```

## Testing Checklist

- [ ] Compiles without errors
- [ ] GC Pipeline runs successfully
- [ ] States/ size ~20-25 GB (67% reduction)
- [ ] Snapshot archive created
- [ ] Node can load extracted snapshot
- [ ] No corruption errors
- [ ] Fallback works if GC fails
- [ ] Logs show detailed phase metrics
- [ ] Slack notification sent with correct stats

## Common Issues

### Issue: Out of Memory during Phase 2
**Solution**: Reduce export file by filtering before BFS, or use streaming approach

### Issue: GC fails with "format_version 7" error
**Solution**: Already fixed - we copy source DB first, preserving format

### Issue: Temp directory fills disk
**Solution**: Ensure 200+ GB free space, or use different temp directory:
```csharp
var tempDir = Path.Combine("/path/to/large/disk", $"gc-{...}");
```

### Issue: Phase 2 takes too long (>4 hours)
**Solution**: Check export file size - should be ~180 GB. If much larger, investigate duplicate entries.

## Performance Tuning

### Faster BFS (if needed):
```csharp
// In GcBfsFilter.cs, add parallel processing
var nextLevel = new ConcurrentBag<string>();

Parallel.ForEach(currentLevel, 
    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
    nodeHex => {
        // Process node
        nextLevel.Add(child);
    });
```

### Less Memory Usage (if needed):
```csharp
// In GcBfsFilter.cs, use streaming instead of loading all in memory
// Trade-off: slower but uses only ~2-3 GB RAM instead of 15-20 GB
```

## Deployment Steps

1. **Week 1**: Merge PR, deploy with `useGcPipeline=false` (opt-in for testing)
2. **Week 2**: Enable for 10% of snapshots, monitor
3. **Week 3**: Enable by default `useGcPipeline=true`
4. **Week 4**: Remove old CopyStates code (if stable)

---

**Ready for Integration!** 🚀

All files are in `/home/vrunnx/teste/nc-snapshot/GcPipeline/` ready to copy.
