using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf atk-check [--dump &lt;文件&gt;] [--json &lt;文件&gt;]`:用 Dnf.Game 的 .atk 加载器读全库 .atk(证据 E-047)。
///   统计每种不规整情况(未知标签、关键字拼错、值缺失、游离标记、整节跳过的模块节……)出现在多少个文件里;
///   --dump 逐文件写出读到的字段(一行一个文件,按路径排序),与 `python3 tools/py/atk_ref.py --dump` 的输出逐行比较,
///   用来对拍两份独立实现。只输出数字与字段,不输出脚本内容;--dump 文件放 out/,不入库。
/// </summary>
internal static class AtkCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        var lines = new List<(string Path, string Text)>();
        int files = 0, notTokens = 0, clean = 0;
        int unknownTags = 0, unknownKeywords = 0, badValues = 0, discarded = 0, stopped = 0, sections = 0, unterminated = 0, overflow = 0;
        int withUnknownTag = 0, withUnknownKeyword = 0, withBadValue = 0, withDiscarded = 0;
        foreach (PvfEntry e in pvf.Entries)
        {
            if (!e.Path.EndsWith(".atk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            byte[] raw = pvf.Read(e);
            if (!TokenScript.IsTokenScript(raw))
            {
                notTokens++;
                continue;
            }
            AttackInfoData data = AttackInfoLoader.Load(new ScriptCursor(TokenScript.Parse(raw), strings), out AttackInfoLoadReport r);
            files++;
            if (r.Clean)
            {
                clean++;
            }
            unknownTags += r.UnknownTags;
            unknownKeywords += r.UnknownKeywords;
            badValues += r.BadValues;
            discarded += r.DiscardedTokens;
            sections += r.SkippedModuleSections;
            overflow += r.StatusOverflow;
            stopped += r.StoppedAtStrayToken ? 1 : 0;
            unterminated += r.UnterminatedModuleSection ? 1 : 0;
            withUnknownTag += r.UnknownTags > 0 ? 1 : 0;
            withUnknownKeyword += r.UnknownKeywords > 0 ? 1 : 0;
            withBadValue += r.BadValues > 0 ? 1 : 0;
            withDiscarded += r.DiscardedTokens > 0 ? 1 : 0;
            if (opts.DumpFile != null)
            {
                lines.Add((e.Path.ToLowerInvariant(), data.Describe()));
            }
        }

        Console.WriteLine("读取 " + files + " 个 .atk(非标记流 " + notTokens + ");整个文件没有任何不规整之处的 " + clean + " 个");
        Console.WriteLine("  未知标签 " + unknownTags + " 次 / " + withUnknownTag + " 个文件;关键字不认识 " + unknownKeywords + " 次 / " + withUnknownKeyword
            + " 个文件;值位置读到别的标记 " + badValues + " 次 / " + withBadValue + " 个文件");
        Console.WriteLine("  被丢弃的游离标记 " + discarded + " 个 / " + withDiscarded + " 个文件;游离标记后还有标签而停止加载 " + stopped + " 个文件;整节跳过的模块节 " + sections
            + " 个;模块节未结束 " + unterminated + " 个;[active status] 超过三个槽 " + overflow + " 次");

        if (opts.DumpFile != null)
        {
            lines.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.DumpFile))!);
            using var w = new StreamWriter(opts.DumpFile, false, new UTF8Encoding(false)) { NewLine = "\n" };
            foreach ((string path, string text) in lines)
            {
                w.WriteLine(path + "\t" + text);
            }
            Console.WriteLine("已写出 " + lines.Count + " 行 -> " + opts.DumpFile);
        }
        if (opts.JsonFile != null)
        {
            var summary = new
            {
                files, notTokens, clean, unknownTags, unknownKeywords, badValues, discarded, stopped, sections, unterminated, overflow,
                withUnknownTag, withUnknownKeyword, withBadValue, withDiscarded,
            };
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
