using System;
using System.IO;
using System.Text;

// 本项目自己的设计(M26):存档放在本机的一个文本文件里;没有原版证据可依。
namespace Dnf.Play
{
    /// <summary>存档存放的地方(文件、内存……):读出来是 <see cref="HeroSave"/>,写进去也是。</summary>
    public interface IHeroSaveStore
    {
        /// <summary>读存档;没有存档返回 null(<paramref name="note"/> 为 null);有但读不懂返回 null、<paramref name="note"/> 写原因。</summary>
        HeroSave Load(out string note);

        /// <summary>写存档;失败返回 false、<paramref name="error"/> 写原因(不抛异常)。</summary>
        bool Save(HeroSave save, out string error);
    }

    /// <summary>
    /// 把存档放在一个 UTF-8 文本文件里。写的时候先写同目录的 <c>.tmp</c>、再换进去,写到一半断电不会把旧存档弄坏;
    /// 读到读不懂的文件时把它原样复制成 <c>.bad</c>(已经有就不覆盖,留下最早坏掉的那份),再当作没有存档。
    /// </summary>
    public sealed class FileHeroSaveStore : IHeroSaveStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public FileHeroSaveStore(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("需要存档路径", nameof(path));
            }
            Path = path;
        }

        public string Path { get; }

        public HeroSave Load(out string note)
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
                note = "读存档失败:" + e.Message;
                return null;
            }
            if (HeroSave.TryParse(text, out HeroSave save, out string error))
            {
                return save;
            }
            note = "存档读不懂(" + error + "),已复制成 " + System.IO.Path.GetFileName(Path) + ".bad";
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

        public bool Save(HeroSave save, out string error)
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
                error = "写存档失败:" + e.Message;
                return false;
            }
        }
    }

    /// <summary>放在内存里的存档(测试用;也记录写了几次)。</summary>
    public sealed class MemoryHeroSaveStore : IHeroSaveStore
    {
        /// <summary>现在存的文本;没有存档是 null,测试可以直接塞坏文本。</summary>
        public string Text { get; set; }

        /// <summary>写过几次。</summary>
        public int Writes { get; private set; }

        /// <summary>下一次写要不要失败(测试用)。</summary>
        public bool FailWrites { get; set; }

        public HeroSave Load(out string note)
        {
            note = null;
            if (Text == null)
            {
                return null;
            }
            if (HeroSave.TryParse(Text, out HeroSave save, out string error))
            {
                return save;
            }
            note = "存档读不懂(" + error + ")";
            return null;
        }

        public bool Save(HeroSave save, out string error)
        {
            error = null;
            if (FailWrites)
            {
                error = "写存档失败(测试)";
                return false;
            }
            Text = save.ToText();
            Writes++;
            return true;
        }
    }

    /// <summary>
    /// 给宿主用的存档员:盯着一个 <see cref="HeroProgress"/>,<see cref="Flush"/> 立刻存(内容和上次存的一样就不写),
    /// <see cref="Tick"/> 每帧调一下、隔 <see cref="IntervalSeconds"/> 秒查一次有没有变。时间由宿主给(秒),所以测试里不用等。
    /// </summary>
    public sealed class HeroSaver
    {
        private readonly IHeroSaveStore _store;
        private readonly HeroProgress _progress;
        private string _lastText;
        private double _lastCheck = double.NegativeInfinity;

        public HeroSaver(IHeroSaveStore store, HeroProgress progress, HeroSave loaded = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _lastText = loaded?.ToText();
        }

        /// <summary>自动存档的检查间隔(秒)。</summary>
        public double IntervalSeconds { get; set; } = 10.0;

        /// <summary>
        /// 每次拍好存档快照(<see cref="HeroProgress.Snapshot"/>)之后、与上次存的内容比较之前调一下,让 <see cref="HeroProgress"/> 之外的状态把自己写进快照
        /// (装扮栏用 <see cref="AvatarSession.Capture"/>)。是多播委托,几路状态可以各自 <c>+=</c> 一个;在比较之前调,所以只有这类状态变了也会被存下来。
        /// 读档时没接上的话,存档里那一部分会被下一次存档抹掉——宿主要在造存档员的同时接上。
        /// </summary>
        public Action<HeroSave> Capture { get; set; }

        /// <summary>写成功过几次。</summary>
        public int Saves { get; private set; }

        /// <summary>最近一次写失败的原因;成功之后清掉。</summary>
        public string LastError { get; private set; }

        /// <summary>立刻存一次:内容没变返回 false(不写);写成功返回 true;写失败返回 false 并留下 <see cref="LastError"/>(下次还会再试)。</summary>
        public bool Flush(double nowSeconds)
        {
            _lastCheck = nowSeconds;
            HeroSave save = _progress.Snapshot();
            Capture?.Invoke(save);
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
