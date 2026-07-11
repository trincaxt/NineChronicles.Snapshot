using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Libplanet.Common;
using Serilog;

namespace NineChronicles.Snapshot.GcPipeline
{
    // ─────────────────────────────────────────────────────────────────────────
    // Hash32: struct de 32 bytes para representar SHA256 sem alocação de string.
    //
    // Vantagens vs string hex:
    //   - GetHashCode: combina 2 longs vs scannear 64 chars
    //   - Equals: 4 comparações de long vs string.Equals(64 chars)
    //   - Memória: 32 bytes vs ~180 bytes (string overhead + 64 chars)
    //   - Zero alocação: criada inline na stack
    // ─────────────────────────────────────────────────────────────────────────
    internal readonly struct Hash32 : IEquatable<Hash32>
    {
        private readonly long A, B, C, D; // 4 × 8 = 32 bytes

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Hash32(byte[] bytes, int offset = 0)
        {
            A = BitConverter.ToInt64(bytes, offset);
            B = BitConverter.ToInt64(bytes, offset + 8);
            C = BitConverter.ToInt64(bytes, offset + 16);
            D = BitConverter.ToInt64(bytes, offset + 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Hash32 o) => A == o.A && B == o.B && C == o.C && D == o.D;

        public override bool Equals(object obj) => obj is Hash32 h && Equals(h);

        // Usa só A e B (16 bytes) para o hash — suficiente para distribuição uniforme.
        // SHA256 tem entropia uniforme em todos os bytes.
        public override int GetHashCode() => HashCode.Combine(A, B);

        public byte[] ToBytes()
        {
            var b = new byte[32];
            Buffer.BlockCopy(BitConverter.GetBytes(A), 0, b, 0,  8);
            Buffer.BlockCopy(BitConverter.GetBytes(B), 0, b, 8,  8);
            Buffer.BlockCopy(BitConverter.GetBytes(C), 0, b, 16, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(D), 0, b, 24, 8);
            return b;
        }
    }

    /// <summary>
    /// GcBfsFilter — BFS por fixpoint (multi-passe, low-RAM).
    ///
    /// Estratégia (inspirada na versão Rust):
    ///   1. Working-set único mutável (pending). Filhos entram no MESMO passe.
    ///   2. Scan sequencial completo do arquivo; filhos à frente do pai são pegos de graça.
    ///   3. Repete passes até um passe não achar nada novo (fixpoint).
    ///
    /// RAM: só visited + pending (HashSet de Hash32), sem índices key→offset.
    /// I/O: 100% sequencial (NVMe feliz), zero random-access.
    ///
    /// Otimizações:
    ///   - Hash32 struct         — sem alocação de string por entry
    ///   - FileOptions.SequentialScan — hint ao OS para prefetch agressivo
    ///   - Buffer de 126 MB      — menos syscalls de I/O
    ///   - Sem paralelismo       — I/O sequencial single-thread é ideal para NVMe
    /// </summary>
    public class GcBfsFilter
    {
        private readonly ILogger _logger;
        private const int HASH_LENGTH    = 32;
        private const int FILE_BUFFER    = 126 * 1024 * 1024; // 126 MB

        // Padrão Bencodex para hash: b"32:" + 32 bytes
        private static readonly byte B3 = (byte)'3';
        private static readonly byte B2 = (byte)'2';
        private static readonly byte BC = (byte)':';

        public GcBfsFilter(ILogger logger) => _logger = logger;

        // ─────────────────────────────────────────────────────────────────
        // API pública — interface idêntica ao original
        // ─────────────────────────────────────────────────────────────────
        public BfsResult RunBfs(
            string exportFilePath,
            IEnumerable<HashDigest<SHA256>> roots,
            string outputFilePath)
        {
            _logger.Information("🌳 BFS fixpoint (multi-passe, HashSet, low-RAM)...");

            var fileInfo = new FileInfo(exportFilePath);
            _logger.Information("   File size: {Size:F2} GB",
                fileInfo.Length / 1024.0 / 1024.0 / 1024.0);

            // visited = já processado (filhos extraídos).
            // pending = descoberto, ainda não localizado no arquivo.
            var visited = new HashSet<Hash32>();
            var pending = new HashSet<Hash32>();

            foreach (var r in roots)
                pending.Add(new Hash32(r.ToByteArray()));

            int pass = 0;

            while (pending.Count > 0)
            {
                var start = DateTime.Now;
                int pendingAtStart = pending.Count;
                int foundThisPass = 0;

                using var fs = new FileStream(
                    exportFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FILE_BUFFER,
                    FileOptions.SequentialScan);
                using var reader = new BinaryReader(fs);

                var keyBuf = new byte[HASH_LENGTH];
                long scanned = 0;

                while (fs.Position < fs.Length)
                {
                    if (reader.Read(keyBuf, 0, HASH_LENGTH) != HASH_LENGTH)
                        break;

                    var valLen = reader.ReadInt32();
                    if (valLen < 0 || valLen > 100_000_000)
                        break;

                    scanned++;

                    // Se está pendente, processa AGORA. Filhos entram no pending
                    // e, se estiverem à frente neste arquivo, são pegos neste
                    // mesmo passe.
                    var key = new Hash32(keyBuf);
                    if (pending.Remove(key))
                    {
                        visited.Add(key);
                        foundThisPass++;

                        var value = reader.ReadBytes(valLen);
                        ExtractChildren(value, visited, pending);
                    }
                    else
                    {
                        fs.Seek(valLen, SeekOrigin.Current);
                    }

                    if (scanned % 20_000_000 == 0)
                    {
                        _logger.Debug("   ...scanned {N}M | pending {P} | visited {V}",
                            scanned / 1_000_000, pending.Count, visited.Count);
                    }
                }

                pass++;
                _logger.Information(
                    "✓ Pass {P}: found {F:N0} (pending era {T:N0}) | " +
                    "resta {R:N0} pending | {Elapsed:F1}s | {V:N0} visited",
                    pass, foundThisPass, pendingAtStart, pending.Count,
                    (DateTime.Now - start).TotalSeconds, visited.Count);

                // Fixpoint: se nada foi achado, o que sobrou em pending
                // não existe no arquivo (dangling references).
                if (foundThisPass == 0)
                {
                    if (pending.Count > 0)
                    {
                        _logger.Warning(
                            "   ℹ️ {N} pending não existem no export (dangling refs), ignorando.",
                            pending.Count);
                    }
                    break;
                }
            }

            _logger.Information(
                "✅ BFS fixpoint: {P} passes, {V:N0} live nodes", pass, visited.Count);

            // Escreve keys vivas (bytes binários, sem conversão hex)
            _logger.Information(
                "📤 Writing {N:N0} live keys to {F}...",
                visited.Count, Path.GetFileName(outputFilePath));

            using var fsOut = new FileStream(
                outputFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                FILE_BUFFER,
                FileOptions.WriteThrough);
            using var writer = new BinaryWriter(fsOut);

            foreach (var key in visited)
                writer.Write(key.ToBytes());

            writer.Flush();
            _logger.Information(
                "✅ GC filter complete: {N:N0} keys written", visited.Count);

            return new BfsResult
            {
                LiveNodes    = visited.Count,
                TotalLevels  = pass,
                TotalScanned = visited.Count,
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // Extrai child hashes do value usando padrão b"32:" + 32 bytes.
        // Versão otimizada: compara bytes individuais sem alocação.
        // Adiciona filhos não visitados diretamente ao pending set.
        // ─────────────────────────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExtractChildren(
            byte[] data,
            HashSet<Hash32> visited,
            HashSet<Hash32> pending)
        {
            const int STEP = 3 + HASH_LENGTH; // "32:" + 32 bytes = 35
            if (data.Length < STEP) return;

            int i = 0;
            while (i + STEP <= data.Length)
            {
                if (data[i] == B3 && data[i + 1] == B2 && data[i + 2] == BC)
                {
                    var child = new Hash32(data, i + 3);
                    if (!visited.Contains(child))
                        pending.Add(child);
                    i += STEP;
                }
                else
                {
                    i++;
                }
            }
        }
    }
}
