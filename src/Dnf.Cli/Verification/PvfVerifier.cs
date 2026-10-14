using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dnf.Data;

namespace Dnf.Cli.Verification;

/// <summary>
/// 与 Python 参考实现生成的清单(out/pvf_manifest.tsv.gz)逐文件对拍:路径、解密后长度、明文 SHA-256,
/// 再比较"全部十六进制哈希按目录顺序拼起来再哈希一次"的聚合值。
/// </summary>
internal static class PvfVerifier
{
    private sealed record ManifestRow(string Path, long Length, string Sha256);

    public static VerificationResult Run(DnfLocation loc, Options opts)
    {
        var r = new VerificationResult("pvf");
        var sw = Stopwatch.StartNew();

        List<ManifestRow> manifest = LoadManifest(Path.Combine(loc.OutDir, "pvf_manifest.tsv.gz"));
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);

        r.Metrics["version"] = (long)pvf.Version;
        r.Metrics["directory_crc"] = "0x" + pvf.DirectoryCrc.ToString("X8");
        r.Metrics["directory_trailing_bytes"] = (long)pvf.DirectoryTrailingBytes;
        r.Metrics["files"] = (long)pvf.Count;
        r.Metrics["manifest_rows"] = (long)manifest.Count;
        r.ExpectEqual("文件数", manifest.Count, pvf.Count);

        int n = pvf.Count;
        var hashes = new string[n];
        long totalBytes = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = opts.Threads };
        Parallel.For(0, n, options, i =>
        {
            byte[] data = pvf.Read(pvf.Entries[i]);
            hashes[i] = Convert.ToHexStringLower(SHA256.HashData(data));
            Interlocked.Add(ref totalBytes, data.Length);
        });
        r.Metrics["plaintext_bytes"] = totalBytes;

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var manifestAggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long pathMismatch = 0, lengthMismatch = 0, hashMismatch = 0;
        int common = Math.Min(n, manifest.Count);
        for (int i = 0; i < n; i++)
        {
            aggregate.AppendData(Encoding.ASCII.GetBytes(hashes[i]));
        }
        for (int i = 0; i < common; i++)
        {
            ManifestRow row = manifest[i];
            PvfEntry e = pvf.Entries[i];
            if (!string.Equals(row.Path, e.Path, StringComparison.Ordinal))
            {
                pathMismatch++;
                r.Fail("路径不一致 #" + i + ": 清单 " + row.Path + " / C# " + e.Path);
                continue;
            }
            if (row.Length != e.Length)
            {
                lengthMismatch++;
                r.Fail("长度不一致 " + e.Path + ": 清单 " + row.Length + " / C# " + e.Length);
            }
            if (!string.Equals(row.Sha256, hashes[i], StringComparison.Ordinal))
            {
                hashMismatch++;
                r.Fail("SHA-256 不一致 " + e.Path);
            }
        }
        foreach (ManifestRow row in manifest)
        {
            manifestAggregate.AppendData(Encoding.ASCII.GetBytes(row.Sha256));
        }
        string aggHex = Convert.ToHexStringLower(aggregate.GetHashAndReset());
        string manifestAggHex = Convert.ToHexStringLower(manifestAggregate.GetHashAndReset());
        r.Metrics["aggregate_sha256"] = aggHex;
        r.Metrics["manifest_aggregate_sha256"] = manifestAggHex;
        r.Metrics["path_mismatches"] = pathMismatch;
        r.Metrics["length_mismatches"] = lengthMismatch;
        r.Metrics["hash_mismatches"] = hashMismatch;
        r.Expect(aggHex == manifestAggHex, "聚合哈希不一致:清单 " + manifestAggHex + " / C# " + aggHex);
        if (opts.ExpectAggregate != null)
        {
            r.Expect(string.Equals(aggHex, opts.ExpectAggregate, StringComparison.OrdinalIgnoreCase),
                "聚合哈希与 --expect-aggregate 不一致:期望 " + opts.ExpectAggregate + ",实际 " + aggHex);
        }

        // stringtable.bin 也要能按定义解析
        PvfStringTable table = new(pvf.Read("stringtable.bin"));
        r.Metrics["stringtable_count"] = (long)table.Count;

        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    private static List<ManifestRow> LoadManifest(string path)
    {
        if (!File.Exists(path))
        {
            throw new UserError("缺少 Python 参考输出: " + path + "(运行 python3 tools/py/manifest.py 生成)");
        }
        var rows = new List<ManifestRow>(340_000);
        using FileStream fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, new UTF8Encoding(false));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] parts = line.Split('\t');
            if (parts.Length != 3)
            {
                throw new UserError("清单行格式不对: " + line);
            }
            rows.Add(new ManifestRow(parts[0], long.Parse(parts[1]), parts[2]));
        }
        return rows;
    }
}
