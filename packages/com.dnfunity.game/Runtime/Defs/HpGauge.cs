using System;
using Dnf.Data;

// 证据:目标怪物血条的长度与填充见 E-106(静态读码;只有一个普通怪物的基线能核对,首领与精英的系数、行号取法是推断)。
namespace Dnf.Game
{
    /// <summary>
    /// 目标怪物血条的"长度"与"填充"算法(证据 E-106)。
    /// 客户端的做法:血条的总长 G 是一个整数像素数,不是固定值——
    /// G = (int)((float)怪物最大生命 / (float)max(层数 L, 1) × (float)每层像素 n),单精度、向零取整;
    /// 当前生命对应的填充 = 单精度的"线性插值"(c &lt; d 时 ((b − a) × c) / d + a,再向零取整;c ≥ d 取 b)。
    /// 窗口一次最多画 <see cref="LayerWidth"/> 个像素,超出的部分用叠层(一层 = 这么宽)表示。
    /// 没有难度表时用默认值(每层像素 300、层数 100);有表时每层像素 n = (int)(系数 × 160.0f),
    /// 系数按怪物种类取([adjust hp gauge] 的普通、精英、首领三项),层数取"怪物公共基础属性表某一行的生命基数"。
    /// 层数该取哪一行(客户端那个参数的含义没有读通),这里按怪物等级找行,属推断。
    /// </summary>
    public static class HpGauge
    {
        /// <summary>系数换算成每层像素数的比例(客户端代码段里的单精度常数)。</summary>
        public const float PixelsPerUnit = 160.0f;

        /// <summary>没有难度表时的每层像素数。</summary>
        public const int DefaultScale = 300;

        /// <summary>没有难度表时的层数。</summary>
        public const int DefaultLayers = 100;

        /// <summary>一层的像素宽(窗口里血条一次画的最大长度)。</summary>
        public const int LayerWidth = 540;

        /// <summary>系数换算成每层像素数:(int)(factor × 160),单精度、向零取整。</summary>
        public static int Scale(float factor)
        {
            float pixels = factor * PixelsPerUnit;
            return (int)pixels;
        }

        /// <summary>血条总长(像素):(int)((float)maxHp / (float)max(layers, 1) × (float)scale)。</summary>
        public static int Length(int maxHp, int layers, int scale)
        {
            float perLayer = (float)maxHp / (float)(layers > 1 ? layers : 1);
            float pixels = perLayer * (float)scale;
            return (int)pixels;
        }

        /// <summary>
        /// 客户端的整数线性插值(单精度、向零取整):c &lt; d 时,c &lt; 1 返回 a,否则 ((b - a) × c) / d + a;c ≥ d 返回 b。
        /// 血条的填充、缩短与闪光的透明度都用它(证据 E-106)。
        /// </summary>
        public static int Lerp(int a, int b, int c, int d)
        {
            float result;
            if (c < d)
            {
                if (c < 1)
                {
                    return a;
                }
                float span = (float)(b - a) * (float)c;
                float step = span / (float)d;
                result = step + (float)a;
            }
            else
            {
                result = (float)b;
            }
            return (int)result;
        }

        /// <summary>当前生命对应的填充像素(0 到 <paramref name="length"/>):<c>Lerp(0, length, hp, maxHp)</c>,hp 不小于 maxHp 取满长、小于 1 取 0。</summary>
        public static int Fill(int length, int hp, int maxHp)
        {
            return Lerp(0, length, hp, maxHp);
        }

        /// <summary>
        /// 一段掉下去的血在"闪完之后"缩掉了多少像素:<c>Lerp(0, length, sinceFlashMs, <see cref="ShrinkSpanMs"/>)</c>
        /// (经过时间不到 1 毫秒算 0,到了或超过跨度取满长)。
        /// </summary>
        public static int Shrunk(int length, int sinceFlashMs)
        {
            return Lerp(0, length, sinceFlashMs, ShrinkSpanMs(length));
        }

        /// <summary>白闪的不透明度(0 到 255):刚掉血时 255,在 <see cref="FlashMs"/> 毫秒里线性降到 0。</summary>
        public static int FlashAlpha(int ageMs)
        {
            return Lerp(255, 0, ageMs, FlashMs);
        }

        /// <summary>掉血后白闪(与变暗的红段一起)持续的毫秒数。</summary>
        public const int FlashMs = 500;

        /// <summary>缩短的时间跨度(毫秒):min(5 × 总长, 6000),即每 5 毫秒缩 1 像素,长血条封顶 6 秒。</summary>
        public static int ShrinkSpanMs(int length)
        {
            long span = (long)length * 5;
            return (int)(span < 6000 ? span : 6000);
        }

        /// <summary>
        /// 按怪物种类、最大生命、公共基础属性表算血条总长。层数 = 表里第 min(行数, d) 行(从 1 数起)的生命基数,d 取怪物等级(不小于 1;推断),
        /// 每层像素 = Scale(<paramref name="factors"/> 里该种类的系数);没有表或表是空的,用默认的每层像素与层数。
        /// </summary>
        public static int LengthFor(MonsterKind kind, int maxHp, int level, MonsterBaseTable common, HpGaugeFactors factors)
        {
            int rows = common == null ? 0 : common.Count;
            if (rows == 0)
            {
                return Length(maxHp, DefaultLayers, DefaultScale);
            }
            int d = level < 1 ? 1 : level;
            int row = (rows < d ? rows : d) - 1;
            HpGaugeFactors f = factors ?? HpGaugeFactors.Default;
            return Length(maxHp, common.Rows[row].HpBase, Scale(f.For(kind)));
        }
    }

    /// <summary>
    /// 血条长度的三个系数(monster/monsterapcdifficultybonus.tbl 里 [adjust hp gauge] 块的 [normal]、[named]、[boss];证据 E-106)。
    /// 文件或某一项缺失时取 1(客户端静态初始化给的默认值)。
    /// </summary>
    public sealed class HpGaugeFactors
    {
        public const string Path = "monster/monsterapcdifficultybonus.tbl";

        public static readonly HpGaugeFactors Default = new HpGaugeFactors(1f, 1f, 1f);

        public float Normal { get; }
        public float Named { get; }
        public float Boss { get; }

        public HpGaugeFactors(float normal, float named, float boss)
        {
            Normal = normal;
            Named = named;
            Boss = boss;
        }

        /// <summary>
        /// 客户端按怪物种类选系数:精英(1、2)用 Named,首领(3)用 Boss,其余(普通与"其他")用 Normal。
        /// (客户端判断的是一个更宽的种类编号集合,{1,2,6,7} 与 {3,8};6、7、8 本移植没有对应的怪物种类。)
        /// </summary>
        public float For(MonsterKind kind)
        {
            switch (kind)
            {
                case MonsterKind.CommonChampion:
                case MonsterKind.SuperChampion:
                    return Named;
                case MonsterKind.Boss:
                    return Boss;
                default:
                    return Normal;
            }
        }

        /// <summary>从该表的脚本行里读 [adjust hp gauge] 块;null 或没有该块返回 <see cref="Default"/>。数值可以写成整数或浮点。</summary>
        public static HpGaugeFactors Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Default;
            }
            float normal = 1f, named = 1f, boss = 1f;
            bool inBlock = false;
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs("[adjust hp gauge]"))
                {
                    inBlock = true;
                    continue;
                }
                if (line.TagIs("[/adjust hp gauge]"))
                {
                    inBlock = false;
                    continue;
                }
                if (!inBlock)
                {
                    continue;
                }
                double? number = line.FirstNumber();
                if (number == null)
                {
                    continue;
                }
                float value = (float)number.Value;
                if (line.TagIs("[normal]"))
                {
                    normal = value;
                }
                else if (line.TagIs("[named]"))
                {
                    named = value;
                }
                else if (line.TagIs("[boss]"))
                {
                    boss = value;
                }
            }
            return new HpGaugeFactors(normal, named, boss);
        }
    }
}
