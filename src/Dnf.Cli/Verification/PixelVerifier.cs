using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dnf.Data;

namespace Dnf.Cli.Verification;

/// <summary>
/// 逐像素对拍:Python 参考实现(tools/py/pixel_oracle.py)随机抽了 1000 个帧(含稀有格式、链接帧、空帧与占位帧),
/// 记下解码出的 RGBA 的 SHA-256;这里用 C# 解码器重新解码,哈希必须逐帧相同。
/// </summary>
internal static class PixelVerifier
{
    private sealed record Item(string Npk, string Entry, int Frame, string Class, int Type, int Width, int Height, string Sha256);

    public static VerificationResult Run(DnfLocation loc, Options opts)
    {
        var r = new VerificationResult("pixels");
        var sw = Stopwatch.StartNew();
        using JsonDocument doc = JsonHelpers.Load(Path.Combine(loc.OutDir, "oracle_pixels.json"));
        var items = new List<Item>();
        foreach (JsonElement e in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            items.Add(new Item(
                e.GetProperty("npk").GetString() ?? "",
                e.GetProperty("entry").GetString() ?? "",
                (int)e.GetProperty("frame").GetInt64(),
                e.GetProperty("class").GetString() ?? "",
                (int)e.GetProperty("type").GetInt64(),
                (int)e.GetProperty("w").GetInt64(),
                (int)e.GetProperty("h").GetInt64(),
                e.GetProperty("sha256").GetString() ?? ""));
        }
        r.Metrics["frames_in_oracle"] = (long)items.Count;
        r.ExpectEqual("参考帧数", doc.RootElement.GetProperty("count").GetInt64(), (long)items.Count);

        var byNpk = items.GroupBy(i => i.Npk).ToArray();
        long compared = 0, mismatched = 0;
        var perClass = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = opts.Threads };
        Parallel.ForEach(byNpk, options, group =>
        {
            using NpkArchive npk = NpkArchive.Open(Path.Combine(loc.NpkDir, group.Key));
            var byName = new Dictionary<string, NpkEntry>(StringComparer.Ordinal);
            foreach (NpkEntry entry in npk.Entries)
            {
                byName[entry.Name] = entry;
            }
            foreach (IGrouping<string, Item> entryGroup in group.GroupBy(i => i.Entry))
            {
                if (!byName.TryGetValue(entryGroup.Key, out NpkEntry ne))
                {
                    r.Fail("NPK " + group.Key + " 里找不到条目 " + entryGroup.Key);
                    Interlocked.Add(ref mismatched, entryGroup.Count());
                    continue;
                }
                ImgFile img = ImgFile.Parse(npk.Read(ne));
                foreach (Item it in entryGroup)
                {
                    Interlocked.Increment(ref compared);
                    perClass.AddOrUpdate(it.Class, 1, (_, v) => v + 1);
                    string? problem = null;
                    try
                    {
                        byte[] rgba = img.DecodeRgba(it.Frame, out ImgFrameInfo info);
                        string hex = Convert.ToHexStringLower(SHA256.HashData(rgba));
                        if (info.Type != it.Type || info.Width != it.Width || info.Height != it.Height)
                        {
                            problem = "类型/尺寸不同: 参考 0x" + it.Type.ToString("X") + " " + it.Width + "x" + it.Height
                                + ",C# 0x" + info.Type.ToString("X") + " " + info.Width + "x" + info.Height;
                        }
                        else if (!string.Equals(hex, it.Sha256, StringComparison.Ordinal))
                        {
                            problem = "像素哈希不同";
                        }
                    }
                    catch (DnfFormatException ex)
                    {
                        problem = "解码失败: " + ex.Message;
                    }
                    if (problem != null)
                    {
                        Interlocked.Increment(ref mismatched);
                        lock (r)
                        {
                            r.Fail(it.Npk + " " + it.Entry + " 帧 " + it.Frame + " (" + it.Class + "): " + problem);
                        }
                    }
                }
            }
        });

        r.Metrics["frames_compared"] = compared;
        r.Metrics["frames_mismatched"] = mismatched;
        r.Metrics["by_class"] = new SortedDictionary<string, long>(perClass, StringComparer.Ordinal);
        r.Expect(compared == items.Count, "只比较了 " + compared + " / " + items.Count + " 帧");
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }
}
