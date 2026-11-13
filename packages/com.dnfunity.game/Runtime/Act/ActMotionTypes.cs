// [MOTION] 里的叠加层([SUB ANI] 一族)与声音([SOUND]、[SOUND PROBABILITY])登记:结构与运行期事件。
// 条目的"形状"(一条几个值)来自客户端加载函数 importActionScript(0x01333530)的读法(静态读码,本机笔记):
//   [SUB ANI]            每条 字符串 + 2 个整数;[SUB ANI WITH XY] 每条 字符串 + 4 个整数;[SUB ANI WITH XYZ] 每条 字符串 + 5 个整数;
//   [SOUND]              每条 字符串 + 1 个整数(概率固定 100);[SOUND PROBABILITY] 每条 字符串 + 2 个整数。
// 每个整数"是什么"没有从客户端读通,下面写的含义都是本机样本统计出来的推断(汇总数,非 REA),各处注释里分开写"观察"与"推断"。
namespace Dnf.Game.Act
{
    /// <summary>叠加层条目来自哪一种标签:决定一条后面带几个整数。</summary>
    public enum ActOverlayForm
    {
        /// <summary>[SUB ANI]:路径 起始帧 层序键。</summary>
        Plain = 0,

        /// <summary>[SUB ANI WITH XY]:路径 起始帧 层序键 x y。</summary>
        WithXY = 1,

        /// <summary>[SUB ANI WITH XYZ]:路径 起始帧 层序键 x y z。</summary>
        WithXYZ = 2,
    }

    /// <summary>
    /// 动作的事件码:事件流里每条事件的种类。数值是本实现自己定的编号(客户端没有对应的编号表,没读到),只用来在日志与测试里区分。
    /// </summary>
    public enum ActMotionEventCode
    {
        /// <summary>叠加层起:主层放到叠加层登记的起始帧(同一个动作里每层只起一次)。</summary>
        LayerStart = 1,

        /// <summary>叠加层止:动作结束(或重来)时,已经起了的叠加层一起收掉;叠加层自己的动画播完不在事件流里,由宿主按它的 .ani 算。</summary>
        LayerEnd = 2,

        /// <summary>声音:主层放到声音登记的帧(循环动画每绕一圈再响一次)。</summary>
        Sound = 3,
    }

    /// <summary>
    /// 一个叠加层条目([SUB ANI] 一族的一条)。<see cref="StartFrame"/> 与 <see cref="Depth"/> 的含义是推断:
    /// 观察(本机样本,第一个整数):99.3% 是 0,最大 18,从不为负,非 0 的全部小于主动画的帧数;第二个整数:取值 −100 到 200,小整数居多(0、1、2、−1、10……),
    /// 一块里有几条不同取值时,文件顺序按它升序的相邻对占 94%;推断:前者是叠加层在主层第几帧起(主层帧号),后者是叠加层的前后层序键(越小越靠后);
    /// 没有读通:取值相同时与主层谁前谁后、键的绝对基准(0 是不是主层所在的位置)。
    /// </summary>
    public sealed class ActOverlayLayer
    {
        internal ActOverlayLayer(int index, ActOverlayForm form, string path, int startFrame, int depth, int x, int y, int z)
        {
            Index = index;
            Form = form;
            Path = path;
            StartFrame = startFrame;
            Depth = depth;
            OffsetX = x;
            OffsetY = y;
            OffsetZ = z;
            Rank = index;
        }

        /// <summary>在这个 [MOTION] 里的出现顺序(所有 [SUB ANI*] 块按文件顺序、块内按条目顺序,从 0 起)。</summary>
        public int Index { get; }

        public ActOverlayForm Form { get; }

        /// <summary>叠加动画的路径(相对 .act 所在目录,原样)。</summary>
        public string Path { get; }

        /// <summary>起始帧:主层放到这一帧时叠加层开始(推断)。</summary>
        public int StartFrame { get; }

        /// <summary>层序键(推断):合成时按它从小到大、同键按文件顺序,先画的在下。</summary>
        public int Depth { get; }

        /// <summary>[SUB ANI WITH XY]、[SUB ANI WITH XYZ] 带的偏移(推断:像素;样本里 XYZ 的 x 与 z 全是 0,y 在 −100 到 −5);没有这一项是 0。</summary>
        public int OffsetX { get; }

        public int OffsetY { get; }

        public int OffsetZ { get; }

        /// <summary>
        /// 合成名次:全部叠加层按 (<see cref="Depth"/> 升序, <see cref="Index"/> 升序) 排出的位置,0 最先画(最靠后)。
        /// 这是"渲染侧合成顺序"的数据结论:宿主现在不必显示,要显示时按名次从小到大画即可。
        /// </summary>
        public int Rank { get; internal set; }

        public override string ToString() => "layer#" + Index + "(" + Form + ",start " + StartFrame + ",depth " + Depth + ")";
    }

    /// <summary>
    /// 一个声音条目([SOUND] 或 [SOUND PROBABILITY] 的一条)。
    /// 观察(本机样本):字符串是 audio.xml 的 ID(不是文件路径;大小写精确匹配 99.3%),整数 t 在主动画帧数以内的占 99.6%;
    /// 推断:t 是主层帧号,主层放到这一帧时响;[SOUND PROBABILITY] 的第二个整数是百分比概率(0 到 100)。
    /// </summary>
    public sealed class ActSoundEvent
    {
        internal ActSoundEvent(int index, string key, int frame, int percent, bool probabilistic)
        {
            Index = index;
            Key = key;
            Frame = frame;
            Percent = percent;
            Probabilistic = probabilistic;
        }

        /// <summary>在这个 [MOTION] 里的出现顺序([SOUND] 与 [SOUND PROBABILITY] 按文件顺序、块内按条目顺序,从 0 起);同一帧的多条声音按它排。</summary>
        public int Index { get; }

        /// <summary>声音标签(audio.xml 的 ID,原样)。</summary>
        public string Key { get; }

        /// <summary>主层帧号。</summary>
        public int Frame { get; }

        /// <summary>响的概率(百分比);[SOUND] 恒为 100。事件流只带这个数,谁掷骰由宿主定。</summary>
        public int Percent { get; }

        /// <summary>来自 [SOUND PROBABILITY](带概率的写法)。</summary>
        public bool Probabilistic { get; }

        public override string ToString() => "sound#" + Index + "(" + Key + " @" + Frame + " " + Percent + "%)";
    }

    /// <summary>事件流里的一条:事件码、对齐的主层帧号,以及它说的是哪个叠加层或哪条声音。</summary>
    public readonly struct ActMotionEvent
    {
        public ActMotionEvent(ActMotionEventCode code, int frame, ActOverlayLayer layer, ActSoundEvent sound)
        {
            Code = code;
            Frame = frame;
            Layer = layer;
            Sound = sound;
        }

        public ActMotionEventCode Code { get; }

        /// <summary>对齐的主层帧号。叠加层起、声音是登记的那一帧(补帧时也按登记的帧报,不是补到的那一刻);叠加层止是动作结束时主层停在的帧。</summary>
        public int Frame { get; }

        /// <summary>叠加层事件说的层;声音事件是 null。</summary>
        public ActOverlayLayer Layer { get; }

        /// <summary>声音事件说的条目;叠加层事件是 null。</summary>
        public ActSoundEvent Sound { get; }

        /// <summary>叠加层的路径或声音标签。</summary>
        public string Key => Layer != null ? Layer.Path : Sound?.Key;

        public override string ToString()
        {
            string what = Layer != null ? "layer#" + Layer.Index : Sound != null ? "sound#" + Sound.Index : "-";
            return Code + "@" + Frame + ":" + what;
        }
    }
}
