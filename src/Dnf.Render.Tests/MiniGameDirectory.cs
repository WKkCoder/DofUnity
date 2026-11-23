using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 在临时目录里搭一个"最小游戏目录":合成的 Script.pvf(只有一张空字符串表)与空的 ImagePacks2,
/// 再往里放合成的客户端可执行文件(<see cref="SyntheticPe"/> 造的 PE 映像,资源是合成的"编号>文字"表)。数据全是合成的,用完整个删掉。
/// </summary>
internal sealed class MiniGameDirectory : IDisposable
{
    public MiniGameDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-clienttext-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));
        var files = new List<TestData.PvfFileSpec>
        {
            new("stringtable.bin", TestData.BuildStringTable(new List<byte[]>()), 0),
        };
        (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
        File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);
    }

    public string Root { get; }

    /// <summary>把字节写到目录下的指定文件名(保留调用方给的大小写),返回完整路径。</summary>
    public string Write(string fileName, byte[] bytes)
    {
        string path = Path.Combine(Root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// 合成的"客户端可执行文件":PE 映像,资源里有命名类型 FILE 的编号 157(内容是 SC01 容器包着的 <paramref name="tableText"/>),
    /// 外加两个干扰项(别的类型名、数字类型 3,同样编号 157,内容不是表)。
    /// </summary>
    public static byte[] Executable(string tableText, bool pe32Plus = false)
    {
        var spec = new SyntheticPe.Spec
        {
            Pe32Plus = pe32Plus,
            Types = new[]
            {
                SyntheticPe.ResType.Named("OTHER", new SyntheticPe.Item(ClientTextTable.DefaultResourceId, new SyntheticPe.Leaf(0, Encoding.ASCII.GetBytes("not-the-table")))),
                SyntheticPe.ResType.Named(
                    ClientTextTable.ResourceTypeName,
                    new SyntheticPe.Item(ClientTextTable.DefaultResourceId, new SyntheticPe.Leaf(0x0804, EmbeddedTextContainer.Encode(tableText)))),
                SyntheticPe.ResType.Numbered(3, new SyntheticPe.Item(ClientTextTable.DefaultResourceId, new SyntheticPe.Leaf(0, Encoding.ASCII.GetBytes("not-the-table-either")))),
            },
        };
        return SyntheticPe.Build(spec).Bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
