using Dnf.Net.Session;
using static Dnf.Net.Tests.SyntheticPeImage;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>client-version</c> 子命令的逻辑(台账 E-453):命令行判定、读文件、折算与输出行、退出码。
/// 文件内容全是 <see cref="SyntheticPeImage"/> 的合成字节,路径都是合成的假路径;输出必须只含两个数与固定说明,绝不回显路径或异常文字。
/// </summary>
public sealed class ClientVersionToolTests
{
    private const string FakePath = "/synthetic-dir/some-very-private-name.bin";

    private sealed class Capture
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Reads = new();
        public int Code;

        public string All => string.Join("\n", Lines);
    }

    private static Capture Run(IReadOnlyList<string> args, Func<string, byte[]> read)
    {
        var capture = new Capture();
        capture.Code = ClientVersionTool.Run(args, path =>
        {
            capture.Reads.Add(path);
            return read(path);
        }, capture.Lines.Add);
        return capture;
    }

    [Fact]
    public void A_good_file_prints_the_label_then_the_folded_number_and_nothing_else()
    {
        byte[] image = Build(3, 20, 105, 7);
        Capture run = Run(new[] { FakePath }, _ => image);

        Assert.Equal(ClientVersionTool.ExitOk, run.Code);
        Assert.Equal(new[] { "文件版本 3.20.105.7", "客户端版本号 32010507" }, run.Lines);
        Assert.Equal(new[] { FakePath }, run.Reads);
    }

    [Fact]
    public void The_label_line_shows_the_four_original_segments_and_the_number_line_the_folded_value()
    {
        Capture run = Run(new[] { FakePath }, _ => Build(6, 150, 23, 8, new Options { Pe32Plus = true }));

        Assert.Equal(0, run.Code);
        Assert.Equal("文件版本 6.150.23.8", run.Lines[0]);
        Assert.Equal("客户端版本号 " + ClientVersionFolding.Fold(6, 150, 23, 8), run.Lines[1]);
        Assert.Equal("客户端版本号 75002308", run.Lines[1]);                 // ((((6×100+150)×10+0)×100+23)×100+8)
    }

    [Fact]
    public void A_value_that_wraps_past_32_bits_gets_an_extra_note_line_after_the_two_lines()
    {
        Capture run = Run(new[] { FakePath }, _ => Build(429, 49, 672, 96));

        Assert.Equal(0, run.Code);
        Assert.Equal(3, run.Lines.Count);
        Assert.Equal("文件版本 429.49.672.96", run.Lines[0]);
        Assert.Equal("客户端版本号 0", run.Lines[1]);
        Assert.StartsWith("注意", run.Lines[2]);

        Capture exact = Run(new[] { FakePath }, _ => Build(429, 49, 672, 95));
        Assert.Equal(new[] { "文件版本 429.49.672.95", "客户端版本号 4294967295" }, exact.Lines);
    }

    [Fact]
    public void Output_never_echoes_the_path_on_any_outcome()
    {
        var outcomes = new List<Capture>
        {
            Run(new[] { FakePath }, _ => Build(3, 20, 105, 7)),
            Run(new[] { FakePath }, _ => new byte[16]),
            Run(new[] { FakePath }, _ => throw new FileNotFoundException("missing " + FakePath, FakePath)),
            Run(new[] { FakePath }, _ => throw new UnauthorizedAccessException("denied " + FakePath)),
            Run(new[] { FakePath }, _ => null),
        };
        foreach (Capture run in outcomes)
        {
            Assert.DoesNotContain("some-very-private-name", run.All);
            Assert.DoesNotContain("synthetic-dir", run.All);
            Assert.DoesNotContain("missing", run.All);
            Assert.DoesNotContain("denied", run.All);
        }
    }

    [Fact]
    public void Wrong_argument_shapes_print_the_usage_line_and_return_64_without_reading_anything()
    {
        var shapes = new IReadOnlyList<string>[]
        {
            null,
            Array.Empty<string>(),
            new[] { "a", "b" },
            new[] { "a", "b", "c" },
            new[] { string.Empty },
            new[] { "-x" },
            new[] { "--help" },
            new[] { "-" },
            new string[] { null },
        };
        foreach (IReadOnlyList<string> args in shapes)
        {
            Capture run = Run(args, _ => throw new InvalidOperationException("不该读文件"));

            Assert.Equal(ClientVersionTool.ExitUsage, run.Code);
            Assert.Equal(64, run.Code);
            Assert.Equal(new[] { ClientVersionTool.UsageLine }, run.Lines);
            Assert.Empty(run.Reads);
        }
        Assert.Contains("client-version", ClientVersionTool.UsageLine);
    }

    [Fact]
    public void A_path_that_merely_contains_a_dash_is_a_path()
    {
        Capture run = Run(new[] { "./-name-with-dash" }, _ => Build(1, 2, 3, 4));
        Assert.Equal(0, run.Code);
        Assert.Equal(new[] { "./-name-with-dash" }, run.Reads);
    }

    [Fact]
    public void File_system_failures_become_exit_66_with_a_fixed_sentence()
    {
        var failures = new Func<string, byte[]>[]
        {
            _ => throw new IOException("boom"),
            _ => throw new FileNotFoundException("boom"),
            _ => throw new DirectoryNotFoundException("boom"),
            _ => throw new UnauthorizedAccessException("boom"),
            _ => throw new ArgumentException("boom"),
            _ => throw new NotSupportedException("boom"),
            _ => null,
        };
        foreach (Func<string, byte[]> read in failures)
        {
            Capture run = Run(new[] { FakePath }, read);

            Assert.Equal(ClientVersionTool.ExitUnreadable, run.Code);
            Assert.Equal(66, run.Code);
            Assert.Equal(new[] { "读不到指定的文件" }, run.Lines);
        }
    }

    [Fact]
    public void An_unexpected_exception_is_not_swallowed()
    {
        Assert.Throws<InvalidOperationException>(() => Run(new[] { FakePath }, _ => throw new InvalidOperationException("bug")));
    }

    [Fact]
    public void Content_problems_become_exit_65_with_exactly_the_problem_description()
    {
        var cases = new (byte[] Image, ClientVersionFileProblem Problem)[]
        {
            (new byte[8], ClientVersionFileProblem.NotPeImage),
            (Build(TypicalTree(VersionBlob(1, 2, 3, 4)), new Options { DeclareResourceDirectory = false }), ClientVersionFileProblem.NoResourceDirectory),
            (Build(Node.Dir(Id(16, Node.Dir()))), ClientVersionFileProblem.NoVersionResource),
            (Build(TypicalTree(VersionBlob(1, 2, 3, 4, signature: 1u))), ClientVersionFileProblem.BadVersionResource),
            (Build(Node.Dir(Id(16, Node.Leaf(VersionBlob(1, 2, 3, 4))))), ClientVersionFileProblem.BadResourceDirectory),
        };
        foreach ((byte[] image, ClientVersionFileProblem problem) in cases)
        {
            Capture run = Run(new[] { FakePath }, _ => image);

            Assert.Equal(ClientVersionTool.ExitBadImage, run.Code);
            Assert.Equal(65, run.Code);
            Assert.Equal(new[] { ClientVersionFile.Describe(problem) }, run.Lines);
        }
    }

    [Fact]
    public void The_exit_codes_are_the_documented_distinct_values()
    {
        Assert.Equal(new[] { 0, 64, 65, 66 }, new[] { ClientVersionTool.ExitOk, ClientVersionTool.ExitUsage, ClientVersionTool.ExitBadImage, ClientVersionTool.ExitUnreadable });
    }

    // ---------------- 真文件读取 ----------------

    private sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnf-client-version-" + Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }

    [Fact]
    public void ReadBounded_returns_the_bytes_of_a_file_up_to_the_cap_and_refuses_bigger_ones_before_allocating()
    {
        using var dir = new TempDir();
        string file = System.IO.Path.Combine(dir.Path, "f.bin");
        byte[] content = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray();
        File.WriteAllBytes(file, content);

        Assert.Equal(content, ClientVersionTool.ReadBounded(file, 16));
        Assert.Equal(content, ClientVersionTool.ReadBounded(file, 1000));
        Assert.Throws<IOException>(() => ClientVersionTool.ReadBounded(file, 15));

        string empty = System.IO.Path.Combine(dir.Path, "empty.bin");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Empty(ClientVersionTool.ReadBounded(empty, 0));
        Assert.Throws<IOException>(() => ClientVersionTool.ReadBounded(file, 0));
    }

    [Fact]
    public void ReadBounded_throws_file_system_exceptions_for_a_missing_file_and_for_a_directory()
    {
        using var dir = new TempDir();
        Assert.ThrowsAny<IOException>(() => ClientVersionTool.ReadBounded(System.IO.Path.Combine(dir.Path, "nope.bin"), 100));
        Exception directoryOpen = Record.Exception(() => ClientVersionTool.ReadBounded(dir.Path, 100));
        Assert.True(directoryOpen is IOException || directoryOpen is UnauthorizedAccessException, directoryOpen?.GetType().Name);
    }

    [Fact]
    public void RunFile_reads_a_real_file_end_to_end_and_never_prints_its_path()
    {
        using var dir = new TempDir();
        string file = System.IO.Path.Combine(dir.Path, "private-looking-name.exe");
        File.WriteAllBytes(file, Build(3, 20, 105, 7, new Options { Pe32Plus = true }));

        var lines = new List<string>();
        int code = ClientVersionTool.RunFile(new[] { file }, lines.Add);

        Assert.Equal(0, code);
        Assert.Equal(new[] { "文件版本 3.20.105.7", "客户端版本号 32010507" }, lines);
        Assert.DoesNotContain(lines, line => line.Contains("private-looking-name") || line.Contains(dir.Path));

        var missing = new List<string>();
        Assert.Equal(66, ClientVersionTool.RunFile(new[] { System.IO.Path.Combine(dir.Path, "gone.exe") }, missing.Add));
        Assert.Equal(new[] { "读不到指定的文件" }, missing);

        string notPe = System.IO.Path.Combine(dir.Path, "text.txt");
        File.WriteAllText(notPe, "just some text, definitely not an executable image of any kind");
        var bad = new List<string>();
        Assert.Equal(65, ClientVersionTool.RunFile(new[] { notPe }, bad.Add));
        Assert.Equal(new[] { ClientVersionFile.Describe(ClientVersionFileProblem.NotPeImage) }, bad);
    }

    [Fact]
    public void The_size_cap_constant_is_generous_but_bounded()
    {
        Assert.True(ClientVersionTool.MaxFileBytes >= 64L * 1024 * 1024);
        Assert.True(ClientVersionTool.MaxFileBytes <= 1024L * 1024 * 1024);
    }
}
