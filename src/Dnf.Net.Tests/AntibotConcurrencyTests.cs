using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Dnf.Net.Crypto;
using Dnf.Net.Session;
using static Dnf.Net.Tests.AntibotTestSupport;

namespace Dnf.Net.Tests;

/// <summary>
/// 反机器人信封解包的并发一致性(算法 9 的静态轮密钥竞态,以及 19 个算法在并行下与串行逐字节一致)。
/// <para>
/// 背景:算法 9(MMB)曾把轮密钥放在一个静态可变字段里,每次调用先写它、再逐块读它;两个线程用不同的序号(不同密钥)同时拆信封时,
/// 一个线程的轮密钥被另一个线程覆盖,解出乱码,表现为拆包失败:CrcMismatch。xunit 默认让各测试类并行,所以全量并行跑时偶发。
/// 修法是让轮密钥随调用走(局部数组、显式传递),类里不再有可变静态状态。
/// </para>
/// <para>
/// 测试编号(trait case):900 并行压力、901 并行黄金向量、902 逐算法并行一致(19 条)、903 静态字段结构审查、904 用过之后静态内容不变、905 全部算法混合并行。
/// 全部合成:序号取 900 起、种子取 900 起,黄金向量取自 Golden/antibot_cipher_excerpt.txt(外部提供,不是被测实现算的)。
/// 压力用专用线程加栅栏同时起跑(不借线程池,免得被池子限流),并记录同时在途的最大调用数,保证"确实并发过"才算数。
/// </para>
/// </summary>
public class AntibotConcurrencyTests
{
    /// <summary>900:压力线程数。16 个线程,每个线程序号不同(密钥不同)。</summary>
    private const int StressThreads = 16;

    /// <summary>900:每个线程的轮次。每轮拆一个算法 9 的信封,16 线程共 16000 次。</summary>
    private const int StressRounds = 1000;

    /// <summary>901:黄金向量的并行线程数与每线程轮次(每轮把算法 9 的 6 条黄金向量加解密各过一遍)。</summary>
    private const int GoldenThreads = 12;

    private const int GoldenRounds = 600;

    /// <summary>902:逐算法并行用的线程数与每线程轮次(每轮把该算法的全部工作项过一遍)。</summary>
    private const int PerAlgorithmThreads = 8;

    private const int PerAlgorithmRounds = 12;

    /// <summary>905:全部算法混合并行的线程数与每线程轮次。</summary>
    private const int MixedThreads = 12;

    private const int MixedRounds = 8;

    /// <summary>算法 9 压力信封的内层包体长度,轮流取;长短混合,让轮密钥被覆盖的窗口时长不一。</summary>
    private static readonly int[] StressBodyLengths = { 23, 64, 255, 1000, 3000, 9000 };

    /// <summary>逐算法工作项的明文长度:覆盖不足一块、恰好整块、整块加尾巴、多块。</summary>
    private static readonly int[] SyntheticLengths = { 1, 7, 8, 15, 16, 17, 31, 33, 100, 301 };

    private sealed record GoldenRow(int Algo, uint Seq, string Name, byte[] Plain, byte[] Cipher);

    private static readonly List<GoldenRow> Golden = LoadGolden();

    private static List<GoldenRow> LoadGolden()
    {
        List<GoldenRow> rows = new();
        foreach (string[] r in FrameVectorFile.Rows("antibot_cipher_excerpt.txt"))
        {
            rows.Add(new GoldenRow(int.Parse(r[0]), uint.Parse(r[1]), r[2], FrameVectorFile.Hex(r[3]), FrameVectorFile.Hex(r[4])));
        }

        return rows;
    }

    public static IEnumerable<object[]> AllAlgorithms()
    {
        for (int algo = 1; algo <= 19; algo++)
        {
            yield return new object[] { algo };
        }
    }

    // ---- 共用小工具 ----

    /// <summary>各线程共用的计数:检查次数、不一致次数(按种类)、前几个不一致的现场、同时在途的最大调用数。</summary>
    private sealed class Tally
    {
        private long _checks;
        private long _failures;
        private int _inFlight;
        private int _maxInFlight;
        private readonly ConcurrentDictionary<string, long> _kinds = new();
        private readonly ConcurrentQueue<string> _samples = new();

        public long Checks => Interlocked.Read(ref _checks);

        public long Failures => Interlocked.Read(ref _failures);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public void Enter()
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen = Volatile.Read(ref _maxInFlight);
            while (now > seen)
            {
                int witnessed = Interlocked.CompareExchange(ref _maxInFlight, now, seen);
                if (witnessed == seen)
                {
                    break;
                }

                seen = witnessed;
            }
        }

        public void Leave()
        {
            Interlocked.Decrement(ref _inFlight);
        }

        public void Pass()
        {
            Interlocked.Increment(ref _checks);
        }

        public void Fail(string kind, string where)
        {
            Interlocked.Increment(ref _checks);
            Interlocked.Increment(ref _failures);
            _kinds.AddOrUpdate(kind, 1, (_, n) => n + 1);
            if (_samples.Count < 4)
            {
                _samples.Enqueue(where);
            }
        }

        public string Describe()
        {
            return "种类 " + string.Join(",", _kinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "×" + k.Value))
                + ";样例 " + string.Join(" / ", _samples)
                + ";同时在途的最大调用数 " + MaxInFlight;
        }

        /// <summary>确实有过重叠调用才算数,否则"通过"说明不了什么。单核机器没有真并行、只能靠抢占,不强求。</summary>
        public void AssertOverlapped(string what)
        {
            if (Environment.ProcessorCount >= 2)
            {
                Assert.True(MaxInFlight >= 2, what + "的压力没有真正并发(同时在途的最大调用数 " + MaxInFlight + "),这次通过说明不了什么");
            }
        }
    }

    /// <summary>起 <paramref name="threads"/> 个专用线程,栅栏放行后同时跑 <paramref name="worker"/>(参数是线程序号),全部结束才返回。</summary>
    private static void RunConcurrently(int threads, Action<int> worker)
    {
        using Barrier start = new(threads);
        Exception[] errors = new Exception[threads];
        Thread[] pool = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int index = t;
            pool[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    worker(index);
                }
                catch (Exception ex)
                {
                    errors[index] = ex;
                }
            })
            {
                IsBackground = true,
                Name = "antibot-concurrency-" + t,
            };
            pool[t].Start();
        }

        foreach (Thread thread in pool)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "并发压力线程三分钟没有结束");
        }

        foreach (Exception error in errors)
        {
            if (error != null)
            {
                Assert.Fail("并发压力线程抛出异常:" + error);
            }
        }
    }

    private static bool Same(byte[] expected, byte[] actual)
    {
        return expected != null && actual != null && expected.AsSpan().SequenceEqual(actual);
    }

    private static string Describe(int algo, Work work)
    {
        return "算法" + algo + " " + work.Label + " 序号" + work.Seq;
    }

    // ---- 逐算法工作项 ----

    /// <summary>一个工作项:一把密钥(序号)下的一组期望结果。合成项的期望来自串行基线,黄金项的期望来自外部向量文件。</summary>
    private sealed class Work
    {
        public string Label;
        public uint Seq;
        public byte[] Plain;
        public byte[] Cipher;                  // 对 Plain 做加密方向变换的期望
        public byte[] DecryptedPlain;          // 对 Plain 做解密方向变换的期望(串行基线)
        public byte[] Envelope;                // 合法信封(黄金项为空)
        public byte[] Inner;                   // 信封里的内层包
    }

    private static List<Work> BuildWork(int algo, int syntheticCount)
    {
        List<Work> items = new();
        for (int i = 0; i < syntheticCount; i++)
        {
            uint seq = 900u + (uint)i;
            byte[] plain = Synth(900 + algo * 16 + i, SyntheticLengths[i]);
            byte[] inner = InnerPacket(1, (ushort)(0x0900 + i), 5 + i * 13, 900 + i);
            items.Add(new Work
            {
                Label = "合成#" + i + "(长度" + plain.Length + ")",
                Seq = seq,
                Plain = plain,
                Cipher = AntibotCipher.Encrypt(algo, seq, plain),
                DecryptedPlain = AntibotCipher.Decrypt(algo, seq, plain),
                Envelope = ValidEnvelope(algo, seq, Concat(AlgorithmPacket(i % 4), inner)),
                Inner = inner,
            });
        }

        foreach (GoldenRow row in Golden.Where(r => r.Algo == algo))
        {
            items.Add(new Work
            {
                Label = "黄金向量 " + row.Name,
                Seq = row.Seq,
                Plain = row.Plain,
                Cipher = row.Cipher,
                DecryptedPlain = AntibotCipher.Decrypt(algo, row.Seq, row.Plain),
            });
        }

        return items;
    }

    /// <summary>对一个工作项做全部检查:加密、解密、对明文做解密方向变换、拆信封;每项都与期望逐字节比。</summary>
    private static void CheckWork(int algo, Work work, Tally tally)
    {
        tally.Enter();
        try
        {
            Verdict(tally, Same(work.Cipher, AntibotCipher.Encrypt(algo, work.Seq, work.Plain)), "加密", algo, work);
            Verdict(tally, Same(work.Plain, AntibotCipher.Decrypt(algo, work.Seq, work.Cipher)), "解密", algo, work);
            Verdict(tally, Same(work.DecryptedPlain, AntibotCipher.Decrypt(algo, work.Seq, work.Plain)), "解密方向变换", algo, work);
            if (work.Envelope != null)
            {
                bool ok = AntibotEnvelope.TryUnwrap(work.Envelope, out byte[] inner, out AntibotEnvelopeStatus status);
                Verdict(tally, ok && Same(work.Inner, inner), "拆信封:" + status, algo, work);
            }
        }
        finally
        {
            tally.Leave();
        }
    }

    private static void Verdict(Tally tally, bool consistent, string kind, int algo, Work work)
    {
        if (consistent)
        {
            tally.Pass();
        }
        else
        {
            tally.Fail(kind, Describe(algo, work));
        }
    }

    // ---- 900:算法 9 并行拆信封压力 ----

    /// <summary>
    /// 900:16 个线程同时反复拆算法 9 的合成信封(每个线程的序号不同,所以密钥不同;内层包体长短混合),每次结果都与期望的内层包逐字节比。
    /// 未修复时轮密钥互相覆盖,绝大多数拆包以 CrcMismatch 失败;修复后一次都不会不一致。
    /// </summary>
    [Fact]
    [Trait("case", "900")]
    public void ParallelUnwrapOfAlgorithm9EnvelopesIsByteIdenticalToTheSerialResult()
    {
        byte[][] envelopes = new byte[StressThreads][];
        byte[][] expectedInner = new byte[StressThreads][];
        for (int t = 0; t < StressThreads; t++)
        {
            int body = StressBodyLengths[t % StressBodyLengths.Length];
            expectedInner[t] = InnerPacket(1, (ushort)(0x0900 + t), body, 900 + t);
            envelopes[t] = ValidEnvelope(9, 900u + (uint)t, Concat(AlgorithmPacket(t % 5), expectedInner[t]));
        }

        // 串行基线:每个信封单独拆都对,并行结果拿它做参照
        for (int t = 0; t < StressThreads; t++)
        {
            Assert.True(AntibotEnvelope.TryUnwrap(envelopes[t], out byte[] inner, out AntibotEnvelopeStatus status), "串行基线拆包失败:" + status + "(序号 " + (900 + t) + ")");
            Assert.Equal(Hex(expectedInner[t]), Hex(inner));
        }

        Tally tally = new();
        RunConcurrently(StressThreads, t =>
        {
            for (int round = 0; round < StressRounds; round++)
            {
                int index = (t + round) % StressThreads;
                tally.Enter();
                bool ok = AntibotEnvelope.TryUnwrap(envelopes[index], out byte[] inner, out AntibotEnvelopeStatus status);
                tally.Leave();
                if (ok && Same(expectedInner[index], inner))
                {
                    tally.Pass();
                }
                else
                {
                    tally.Fail(ok ? "内层包不一致" : status.ToString(), "序号" + (900 + index));
                }
            }
        });

        Assert.True(tally.Failures == 0, "并行拆算法 9 信封与串行结果不一致 " + tally.Failures + "/" + tally.Checks + " 次:" + tally.Describe());
        Assert.Equal((long)StressThreads * StressRounds, tally.Checks);
        tally.AssertOverlapped("算法 9 拆信封");
    }

    // ---- 901:算法 9 并行黄金向量 ----

    /// <summary>901:12 个线程同时对算法 9 的 6 条外部黄金向量(两个序号、三种明文)做加密与解密,逐字节比对向量文件里的密文与明文。</summary>
    [Fact]
    [Trait("case", "901")]
    public void ParallelEncryptAndDecryptOfAlgorithm9GoldenVectorsAreByteIdentical()
    {
        List<GoldenRow> rows = Golden.Where(r => r.Algo == 9).ToList();
        Assert.Equal(6, rows.Count);
        Assert.Equal(2, rows.Select(r => r.Seq).Distinct().Count());

        Tally tally = new();
        RunConcurrently(GoldenThreads, t =>
        {
            for (int round = 0; round < GoldenRounds; round++)
            {
                GoldenRow row = rows[(t + round) % rows.Count];
                tally.Enter();
                byte[] encrypted = AntibotCipher.Encrypt(9, row.Seq, row.Plain);
                byte[] decrypted = AntibotCipher.Decrypt(9, row.Seq, row.Cipher);
                tally.Leave();
                if (Same(row.Cipher, encrypted))
                {
                    tally.Pass();
                }
                else
                {
                    tally.Fail("加密", row.Name + " 序号" + row.Seq);
                }

                if (Same(row.Plain, decrypted))
                {
                    tally.Pass();
                }
                else
                {
                    tally.Fail("解密", row.Name + " 序号" + row.Seq);
                }
            }
        });

        Assert.True(tally.Failures == 0, "并行加解密算法 9 的黄金向量与向量文件不一致 " + tally.Failures + "/" + tally.Checks + " 次:" + tally.Describe());
        Assert.Equal((long)GoldenThreads * GoldenRounds * 2, tally.Checks);
        tally.AssertOverlapped("算法 9 黄金向量加解密");
    }

    // ---- 902:19 个算法逐个并行 ----

    /// <summary>
    /// 902:对每个算法(1 到 19),先串行建立期望(合成项取串行结果,黄金项取外部向量),再起 8 个线程把该算法的全部工作项
    /// (10 个合成项加该算法的 6 条黄金向量)反复做加密、解密、解密方向变换与拆信封,每次都与期望逐字节比。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    [Trait("case", "902")]
    public void ParallelWorkMatchesTheSerialResultForEveryAlgorithm(int algo)
    {
        List<Work> items = BuildWork(algo, SyntheticLengths.Length);
        Assert.Equal(SyntheticLengths.Length + 6, items.Count);

        Tally baseline = new();
        foreach (Work work in items)
        {
            CheckWork(algo, work, baseline);
        }

        Assert.True(baseline.Failures == 0, "算法 " + algo + " 串行基线就不一致:" + baseline.Describe());

        Tally tally = new();
        RunConcurrently(PerAlgorithmThreads, t =>
        {
            for (int round = 0; round < PerAlgorithmRounds; round++)
            {
                for (int k = 0; k < items.Count; k++)
                {
                    CheckWork(algo, items[(k + t * 3 + round) % items.Count], tally);
                }
            }
        });

        Assert.True(tally.Failures == 0, "算法 " + algo + " 并行结果与串行不一致 " + tally.Failures + "/" + tally.Checks + " 次:" + tally.Describe());
        tally.AssertOverlapped("算法 " + algo);
    }

    // ---- 905:全部算法混合并行 ----

    /// <summary>905:19 个算法的工作项混在一起,12 个线程各按不同的次序同时过一遍,专门抓"算法之间共用了某个静态状态"。</summary>
    [Fact]
    [Trait("case", "905")]
    public void ParallelMixOfAllNineteenAlgorithmsMatchesTheSerialResult()
    {
        List<(int Algo, Work Work)> all = new();
        for (int algo = 1; algo <= 19; algo++)
        {
            foreach (Work work in BuildWork(algo, 4))
            {
                all.Add((algo, work));
            }
        }

        Assert.Equal(19 * (4 + 6), all.Count);

        Tally baseline = new();
        foreach ((int algo, Work work) in all)
        {
            CheckWork(algo, work, baseline);
        }

        Assert.True(baseline.Failures == 0, "混合工作项的串行基线就不一致:" + baseline.Describe());

        Tally tally = new();
        RunConcurrently(MixedThreads, t =>
        {
            for (int round = 0; round < MixedRounds; round++)
            {
                for (int k = 0; k < all.Count; k++)
                {
                    (int algo, Work work) = all[(k * 7 + t * 5 + round) % all.Count];
                    CheckWork(algo, work, tally);
                }
            }
        });

        Assert.True(tally.Failures == 0, "混合并行与串行不一致 " + tally.Failures + "/" + tally.Checks + " 次:" + tally.Describe());
        tally.AssertOverlapped("19 个算法混合");
    }

    // ---- 903、904:静态状态审查 ----

    /// <summary>
    /// 903:结构审查(单线程、不靠运气)。Antibot* 各类与算法 4 的块核里所有静态字段都必须是 readonly(常量不算)、不是线程静态,
    /// 且类型是"常量表"(基元、字符串、数组,或字段全是 readonly 的小对象),不能是字典、列表这类可变容器。
    /// 可变静态字段正是这类竞态的根:未修复时 <c>AntibotMmbCore._roundKey</c> 在这里就被点名。
    /// 这是本目录的设计约束(状态随调用走,不共享);将来若确需引入静态缓存或锁对象,应当有意识地改这里并写明理由,而不是绕过它。
    /// </summary>
    [Fact]
    [Trait("case", "903")]
    public void NoAntibotTypeKeepsMutableStaticState()
    {
        List<Type> types = AntibotStaticState.AuditedTypes();
        Assert.True(types.Count >= 30, "审查范围里只找到 " + types.Count + " 个类型,反射扫描可能失效");
        Assert.Contains(typeof(AntibotMmbCore), types);
        Assert.Contains(typeof(AntibotTwofishCore), types);
        Assert.Contains(typeof(Cast5Cipher), types);

        List<string> offenders = new();
        int constantTables = 0;
        foreach (Type type in types)
        {
            foreach (FieldInfo field in AntibotStaticState.StaticFields(type))
            {
                List<string> reasons = new();
                if (!field.IsInitOnly)
                {
                    reasons.Add("非 readonly");
                }

                if (field.GetCustomAttribute<ThreadStaticAttribute>() != null)
                {
                    reasons.Add("ThreadStatic");
                }

                if (!AntibotStaticState.IsConstantTable(field.FieldType))
                {
                    reasons.Add("类型 " + field.FieldType.Name + " 不是常量表");
                }

                if (reasons.Count == 0)
                {
                    constantTables++;
                }
                else
                {
                    offenders.Add(type.FullName + "." + field.Name + "(" + string.Join(",", reasons) + ")");
                }
            }
        }

        Assert.True(constantTables >= 30, "审查到的常量表静态字段只有 " + constantTables + " 个,反射扫描可能失效");
        Assert.True(offenders.Count == 0, "这些静态字段是可变状态,并发拆包会互相踩踏:" + string.Join("; ", offenders));
    }

    /// <summary>
    /// 904:readonly 的静态表本身是数组,元素仍可能被写。对全部审查类型的每个静态字段取值图的 SHA-256,
    /// 把 19 个算法两个方向、多种序号与长度(含 0 到 40 的每个尾长)、合法与非法信封都用一遍,再取一次,逐字段比对,一个字节都不许变。
    /// </summary>
    [Fact]
    [Trait("case", "904")]
    public void UsingEveryAlgorithmLeavesEveryAntibotStaticFieldUntouched()
    {
        SortedDictionary<string, string> before = AntibotStaticState.Snapshot();
        Assert.True(before.Count >= 30, "快照里只有 " + before.Count + " 个静态字段,反射扫描可能失效");

        int calls = 0;
        for (int algo = 1; algo <= 19; algo++)
        {
            foreach (uint seq in new[] { 0u, 900u, 901u, 0xFFFFFFFFu })
            {
                for (int length = 0; length <= 40; length++)
                {
                    byte[] data = Synth(900 + algo, length);
                    byte[] encrypted = AntibotCipher.Encrypt(algo, seq, data);
                    byte[] decrypted = AntibotCipher.Decrypt(algo, seq, encrypted);
                    Assert.True(Same(data, decrypted), "算法 " + algo + " 序号 " + seq + " 长度 " + length + ":往返没有还原");
                    calls += 2;
                }

                foreach (int length in new[] { 100, 257, 1000 })
                {
                    byte[] data = Synth(900 + algo, length);
                    Assert.True(Same(data, AntibotCipher.Decrypt(algo, seq, AntibotCipher.Encrypt(algo, seq, data))), "算法 " + algo + " 长度 " + length + ":往返没有还原");
                    calls += 2;
                }
            }

            byte[] inner = InnerPacket(1, 0x0900, 40, algo);
            byte[] envelope = ValidEnvelope(algo, 900u, Concat(AlgorithmPacket(2), inner));
            Assert.True(AntibotEnvelope.TryUnwrap(envelope, out byte[] unwrapped, out AntibotEnvelopeStatus status), "算法 " + algo + " 拆包失败:" + status);
            Assert.True(Same(inner, unwrapped));
            envelope[envelope.Length - 1] ^= 1;
            Assert.False(AntibotEnvelope.TryUnwrap(envelope, out _, out status));
            Assert.Equal(AntibotEnvelopeStatus.CrcMismatch, status);
        }

        Assert.True(calls > 5000);
        SortedDictionary<string, string> after = AntibotStaticState.Snapshot();
        Assert.Equal(before.Keys, after.Keys);
        List<string> changed = before.Where(p => after[p.Key] != p.Value).Select(p => p.Key).ToList();
        Assert.True(changed.Count == 0, "用过之后这些静态字段的内容变了:" + string.Join(", ", changed));
    }
}

/// <summary>Antibot 路径上静态字段的反射审查与内容指纹(见 <see cref="AntibotConcurrencyTests"/> 的 903、904)。</summary>
internal static class AntibotStaticState
{
    /// <summary>
    /// 被审查的类型:Dnf.Net.Session 里所有 Antibot* 类(含嵌套类),再加算法 4 的块核 <see cref="Cast5Cipher"/>(AntibotCast 直接调它)。
    /// 编译器生成的类(闭包缓存之类)不算。
    /// </summary>
    public static List<Type> AuditedTypes()
    {
        List<Type> types = new();
        foreach (Type type in typeof(AntibotCipher).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
            {
                continue;
            }

            Type outer = type;
            while (outer.DeclaringType != null)
            {
                outer = outer.DeclaringType;
            }

            if (outer.Namespace == "Dnf.Net.Session" && outer.Name.StartsWith("Antibot", StringComparison.Ordinal))
            {
                types.Add(type);
            }
        }

        types.Add(typeof(Cast5Cipher));
        types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return types;
    }

    /// <summary>类型自己声明的全部静态字段(任何可见性),常量不算。</summary>
    public static IEnumerable<FieldInfo> StaticFields(Type type)
    {
        return type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsLiteral)
            .OrderBy(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// 字段类型是不是"常量表":基元、枚举、字符串;元素也是常量表的数组;实例字段全是 readonly 且类型也是常量表的类或结构(如 AntibotTailBox 的 BoxShape)。
    /// 字典、列表、委托、延迟对象、接口、object 这类可变容器都不是。数组元素本身是可写的,那一层由内容指纹测试(904)兜底。
    /// </summary>
    public static bool IsConstantTable(Type type)
    {
        return IsConstantTable(type, new HashSet<Type>());
    }

    private static bool IsConstantTable(Type type, HashSet<Type> visited)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
        {
            return true;
        }

        if (type.IsArray)
        {
            return IsConstantTable(type.GetElementType(), visited);
        }

        if (type.IsInterface || type.IsPointer || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type))
        {
            return false;
        }

        if (!visited.Add(type))
        {
            return true;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (!field.IsInitOnly || !IsConstantTable(field.FieldType, visited))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>每个静态字段(类型全名.字段名)的取值图指纹。读取会触发类型的静态初始化,所以拍快照时表一定已建好。</summary>
    public static SortedDictionary<string, string> Snapshot()
    {
        SortedDictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (Type type in AuditedTypes())
        {
            foreach (FieldInfo field in StaticFields(type))
            {
                map[type.FullName + "." + field.Name] = Digest(field.GetValue(null));
            }
        }

        return map;
    }

    private static string Digest(object value)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, value, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
    }

    private static void Append(IncrementalHash hash, object value, HashSet<object> seen)
    {
        if (value == null)
        {
            AppendText(hash, "null");
            return;
        }

        Type type = value.GetType();
        if (value is Delegate del)
        {
            AppendText(hash, "delegate:" + del.Method.DeclaringType?.FullName + "." + del.Method.Name);
            return;
        }

        if (type.IsEnum || type.IsPrimitive || value is string || value is decimal)
        {
            AppendText(hash, type.Name + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (value is Array array)
        {
            if (!seen.Add(array))
            {
                AppendText(hash, "seen-array");
                return;
            }

            Type element = type.GetElementType();
            AppendText(hash, "array:" + element.FullName + ":" + string.Join("x", Enumerable.Range(0, array.Rank).Select(array.GetLength)));
            if (element.IsPrimitive)
            {
                byte[] raw = new byte[Buffer.ByteLength(array)];
                Buffer.BlockCopy(array, 0, raw, 0, raw.Length);
                hash.AppendData(raw);
                return;
            }

            foreach (object item in array)
            {
                Append(hash, item, seen);
            }

            return;
        }

        if (!type.IsValueType && !seen.Add(value))
        {
            AppendText(hash, "seen-object");
            return;
        }

        AppendText(hash, "object:" + type.FullName);
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                AppendText(hash, field.Name);
                Append(hash, field.GetValue(value), seen);
            }
        }
    }
}
