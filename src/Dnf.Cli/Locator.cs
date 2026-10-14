namespace Dnf.Cli;

/// <summary>用户自备数据与本仓库输出目录的位置。仓库里不含也不提交其中任何内容。</summary>
internal sealed record DnfLocation(string DnfDir, string PvfPath, string NpkDir, string RepoRoot, string OutDir);

internal static class Locator
{
    /// <summary>从当前目录与程序所在目录向上找含 DnfUnity.slnx 的目录。</summary>
    public static string? FindRepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "DnfUnity.slnx")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        return null;
    }

    /// <summary>解析顺序:命令行 --dnf-dir,环境变量 DNF_DIR,仓库根的 local.env 里的 DNF_DIR=。</summary>
    public static DnfLocation Resolve(string? dnfDirOverride, string? outOverride)
    {
        string repoRoot = FindRepoRoot()
            ?? throw new UserError("找不到仓库根目录(应含 DnfUnity.slnx)。请在仓库内运行。");
        string? dnfDir = dnfDirOverride ?? Environment.GetEnvironmentVariable("DNF_DIR") ?? ReadLocalEnv(repoRoot);
        if (string.IsNullOrWhiteSpace(dnfDir) || !Directory.Exists(dnfDir))
        {
            throw new UserError("找不到 DNF 数据目录:请用 --dnf-dir、环境变量 DNF_DIR,或在仓库根的 local.env 里写一行 DNF_DIR=/你的/DNF/目录");
        }
        string pvf = Path.Combine(dnfDir, "Script.pvf");
        string npk = Path.Combine(dnfDir, "ImagePacks2");
        if (!File.Exists(pvf))
        {
            throw new UserError("数据目录里没有 Script.pvf: " + dnfDir);
        }
        if (!Directory.Exists(npk))
        {
            throw new UserError("数据目录里没有 ImagePacks2/: " + dnfDir);
        }
        string outDir = outOverride ?? Path.Combine(repoRoot, "out");
        Directory.CreateDirectory(outDir);
        return new DnfLocation(dnfDir, pvf, npk, repoRoot, outDir);
    }

    private static string? ReadLocalEnv(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "local.env");
        if (!File.Exists(path))
        {
            return null;
        }
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("DNF_DIR=", StringComparison.Ordinal))
            {
                return line.Substring("DNF_DIR=".Length).Trim().Trim('"').Trim('\'');
            }
        }
        return null;
    }
}

/// <summary>可预期的、给用户看的错误(不需要堆栈)。</summary>
internal sealed class UserError : Exception
{
    public UserError(string message) : base(message)
    {
    }
}
