using System;
using System.IO;
using System.Text;

// 本项目自己的设计(r15-storage):账号级的仓库存档放在本机一个文本文件里,与角色存档(HeroSaveStore)是两个文件、两套存取;没有原版证据可依。
namespace Dnf.Play
{
    /// <summary>仓库存档存放的地方(文件、内存……):读出来是 <see cref="StorageSave"/>,写进去也是。</summary>
    public interface IStorageSaveStore
    {
        /// <summary>读存档;没有存档返回 null(<paramref name="note"/> 为 null);有但读不懂返回 null、<paramref name="note"/> 写原因。</summary>
        StorageSave Load(out string note);

        /// <summary>写存档;失败返回 false、<paramref name="error"/> 写原因(不抛异常)。</summary>
        bool Save(StorageSave save, out string error);
    }

    /// <summary>
    /// 把仓库存档放在一个 UTF-8 文本文件里,办法同 <see cref="FileHeroSaveStore"/>:先写同目录的 <c>.tmp</c> 再换进去;读到读不懂的文件时原样复制成 <c>.bad</c>(已经有就不覆盖)再当作没有存档。
    /// </summary>
    public sealed class FileStorageSaveStore : IStorageSaveStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public FileStorageSaveStore(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("需要存档路径", nameof(path));
            }
            Path = path;
        }

        public string Path { get; }

        public StorageSave Load(out string note)
        {
            note = null;
            string text;
            try
            {
                if (!File.Exists(Path))
                {
                    return null;
                }
                text = File.ReadAllText(Path, Utf8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                note = "读仓库存档失败:" + e.Message;
                return null;
            }
            if (StorageSave.TryParse(text, out StorageSave save, out string error))
            {
                return save;
            }
            note = "仓库存档读不懂(" + error + "),已复制成 " + System.IO.Path.GetFileName(Path) + ".bad";
            try
            {
                string bad = Path + ".bad";
                if (!File.Exists(bad))
                {
                    File.Copy(Path, bad);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                note += "(复制失败:" + e.Message + ")";
            }
            return null;
        }

        public bool Save(StorageSave save, out string error)
        {
            error = null;
            if (save == null)
            {
                error = "没有要存的内容";
                return false;
            }
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string temp = Path + ".tmp";
                File.WriteAllText(temp, save.ToText(), Utf8);
                if (File.Exists(Path))
                {
                    try
                    {
                        File.Replace(temp, Path, null);
                    }
                    catch (Exception e) when (e is PlatformNotSupportedException || e is NotSupportedException)
                    {
                        File.Copy(temp, Path, true);        // 个别平台没有 File.Replace:退回覆盖复制
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, Path);
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                error = "写仓库存档失败:" + e.Message;
                return false;
            }
        }
    }

    /// <summary>放在内存里的仓库存档(测试用;也记录写了几次)。</summary>
    public sealed class MemoryStorageSaveStore : IStorageSaveStore
    {
        /// <summary>现在存的文本;没有存档是 null,测试可以直接塞坏文本。</summary>
        public string Text { get; set; }

        /// <summary>写过几次。</summary>
        public int Writes { get; private set; }

        /// <summary>下一次写要不要失败(测试用)。</summary>
        public bool FailWrites { get; set; }

        public StorageSave Load(out string note)
        {
            note = null;
            if (Text == null)
            {
                return null;
            }
            if (StorageSave.TryParse(Text, out StorageSave save, out string error))
            {
                return save;
            }
            note = "仓库存档读不懂(" + error + ")";
            return null;
        }

        public bool Save(StorageSave save, out string error)
        {
            error = null;
            if (FailWrites)
            {
                error = "写仓库存档失败(测试)";
                return false;
            }
            Text = save.ToText();
            Writes++;
            return true;
        }
    }

    /// <summary>
    /// 给宿主用的仓库存档员:盯着一个 <see cref="StorageSession"/>,<see cref="Flush"/> 立刻存(内容和上次存的一样就不写),
    /// <see cref="Tick"/> 每帧调一下、隔 <see cref="IntervalSeconds"/> 秒查一次有没有变。时间由宿主给(秒),所以测试里不用等。
    /// 写入次序见 NOTES:一次存取会同时改背包(角色存档)与仓库(本存档)两个文件,中间断电只能保证"不丢",所以先写东西去的那一边。
    /// </summary>
    public sealed class StorageSaver
    {
        private readonly IStorageSaveStore _store;
        private readonly StorageSession _session;
        private string _lastText;
        private double _lastCheck = double.NegativeInfinity;

        public StorageSaver(IStorageSaveStore store, StorageSession session, StorageSave loaded = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _lastText = loaded?.ToText();
        }

        /// <summary>自动存档的检查间隔(秒)。</summary>
        public double IntervalSeconds { get; set; } = 10.0;

        /// <summary>写成功过几次。</summary>
        public int Saves { get; private set; }

        /// <summary>最近一次写失败的原因;成功之后清掉。</summary>
        public string LastError { get; private set; }

        /// <summary>立刻存一次:内容没变返回 false(不写);写成功返回 true;写失败返回 false 并留下 <see cref="LastError"/>(下次还会再试)。</summary>
        public bool Flush(double nowSeconds)
        {
            _lastCheck = nowSeconds;
            StorageSave save = _session.Snapshot();
            string text = save.ToText();
            if (text == _lastText)
            {
                return false;
            }
            if (!_store.Save(save, out string error))
            {
                LastError = error;
                return false;
            }
            _lastText = text;
            LastError = null;
            Saves++;
            return true;
        }

        /// <summary>每帧调:离上次检查不到间隔就什么也不做,否则 <see cref="Flush"/>。返回这一次有没有真的写。</summary>
        public bool Tick(double nowSeconds)
        {
            if (nowSeconds - _lastCheck < IntervalSeconds)
            {
                return false;
            }
            return Flush(nowSeconds);
        }
    }
}
