using System.Collections.Generic;

// M107:符号、队伍、区域、受击开关一族条件与行为。读码依据是 DNF.exe 的静态反汇编(本机脚本扫描,非 REA);
// 每条规则旁写着对应的客户端地址,没读通的地方在注释里写明"没读通"或"推断"。
//   条件求值总入口 checkTriggerEvent 0x010d3630(真标签 0x010d933d,假标签 0x010d3ed3);参数读取 0x0107b2e0(第 n 项,越界 -1);
//   类型转换 0x013d76df(碰撞体描述符 0x019d91e8);拥有者 = 动作对象 +0x94。
namespace Dnf.Game.Act
{
    /// <summary>动作脚本里的对象:符号与受击开关的读写面(M107)。默认实现 = 没有这份存储。</summary>
    public partial interface IActEntity
    {
        /// <summary>
        /// 对象符号(碰撞体对象 +0x200 的整数,[CHECK SYMBOL] 读、[SET SYMBOL] 写)。
        /// 出生值 -1 是推断(重置函数 0x0086cc60 把它写成 -1、真实脚本里常见 [CHECK SYMBOL] 1 -1),
        /// 没有存储的对象恒读 -1、写入丢弃。
        /// </summary>
        int ActSymbol => -1;

        void SetActSymbol(int value)
        {
        }

        /// <summary>对象的第二个符号整数(+0x204,[SET SYMBOL OPTION] 写;读它的只有重置函数 0x0086cc60,谁在什么时候调没读通)。</summary>
        int ActSymbolOption => -1;

        void SetActSymbolOption(int value)
        {
        }

        /// <summary>
        /// [IS DAMAGE ENABLE] 读的受击开关(虚槽 0x36c,写它的是 [SET DAMAGE BOX] 用的虚槽 0x368)。
        /// 出生值没读通(碰撞体构造函数把它写成 0,谁把它打开没找到),所以不知道开关状态的对象(主角、被动对象)返回 null:
        /// 工作集里有 null 时整条触发器不求值,与没实现这条条件之前一样。
        /// </summary>
        bool? ActDamageEnabled => null;
    }

    /// <summary>运行器对世界的请求:队伍与表现层(M107)。</summary>
    public partial interface IActHost
    {
        /// <summary>
        /// 队伍符号(队伍对象 +0x30;读 0x009265c0、写 0x009265d0;[CHECK PARTY SYMBOL] / [SET PARTY SYMBOL])。
        /// 出生值 -1(队伍构造函数 0x00926e00 调重置 0x009269c0 写 -1,观察);队伍对象跨房间保留与否没读通,宿主自己定。没有存储的宿主恒读 -1。
        /// </summary>
        int PartySymbol => -1;

        void SetPartySymbol(int value)
        {
        }

        /// <summary>
        /// 队伍第 <paramref name="slot"/> 个槽里的角色(0 到 <see cref="ActRunner.PartySlotCount"/> - 1;客户端槽表项 0x00926500、角色 0x00926520);空槽或角色空是 null。
        /// 单机只有主角占槽 0(槽布局是观察;"单机只有主角"是推断)。
        /// </summary>
        IActEntity PartyMember(int slot) => slot == 0 ? ResolvePlayableCharacter(null) : null;

        /// <summary>槽被占着(槽表项非空)。槽占着而角色空是客户端允许的状态([CHECK PARTYMEMBERS STATE] 对它有单独的分支)。</summary>
        bool PartySlotOccupied(int slot) => PartyMember(slot) != null;

        /// <summary>
        /// [ABS CASTING](行为码 0x7f,块 0x010bd541):拥有者头上起一根绝对读条条(客户端 0x280 字节的读条条对象、按拥有者标识登记)。
        /// 只管表现、不排行为组;两个整数的含义没读通。默认什么都不做。
        /// </summary>
        void ShowAbsCasting(IActEntity owner, int first, int second)
        {
        }
    }

    public sealed partial class ActRunner
    {
        /// <summary>队伍槽数:队伍对象 +0x18 起四个槽。</summary>
        public const int PartySlotCount = 4;

        // ------------------------------------------------------------------ 符号

        /// <summary>
        /// 符号比较:模式 1 要相等、模式 0 要不等,其余模式一律不满足(客户端块 0x010d69d2 起,[CHECK PARTY SYMBOL] 的 0x010d6adc 起同样)。
        /// </summary>
        public static bool SymbolMatches(int actual, int mode, int expected)
        {
            if (mode == 1)
            {
                return actual == expected;
            }
            return mode == 0 && actual != expected;
        }

        /// <summary>[CHECK SYMBOL]:逐对象留下符号满足的(目标转成碰撞体失败的剔除——我们的对象都是碰撞体)。</summary>
        private bool KeepSymbol(ActRecord rec, IActEntity e) => SymbolMatches(e.ActSymbol, rec.A, rec.B);

        // ------------------------------------------------------------------ 区域

        /// <summary>
        /// [AREA] a b c d(块 0x010d465a):以"拥有者位置 + (a, b)"为中心、(c, d) 为半宽半高的横向 X / Y 轴对齐框,含边界;
        /// 位置按整数比较,不看朝向与高度;半宽或半高为负时框是空的。
        /// </summary>
        private bool KeepArea(ActRecord rec, IActEntity e)
        {
            int[] v = rec.Ints;
            int cx = (int)_owner.X + v[0];
            int cy = (int)_owner.Y + v[1];
            int tx = (int)e.X;
            int ty = (int)e.Y;
            return tx >= cx - v[2] && tx <= cx + v[2] && ty >= cy - v[3] && ty <= cy + v[3];
        }

        /// <summary>
        /// [IS IN AREA](块 0x010d5fbd–0x010d6791):中心 (x, y, z)、半宽 (rx, ry, rz) 的三轴对齐盒,含边界,整数比较;
        /// 相对写法给中心加上拥有者的整数坐标。坐标写成别的字符串时整条条件为假(不看工作集,空集合也假)。返回假表示条件整体不通过。
        /// </summary>
        private bool ApplyIsInArea(ActRecord rec)
        {
            if (rec.Number == ActCompiler.CoordinateInvalid)
            {
                return false;
            }
            int[] v = rec.Ints;
            int cx = v[0];
            int cy = v[1];
            int cz = v[2];
            if (rec.Number == ActCompiler.CoordinateRelative)
            {
                cx += (int)_owner.X;
                cy += (int)_owner.Y;
                cz += (int)_owner.Z;
            }
            int w = 0;
            for (int i = 0; i < _set.Count; i++)
            {
                IActEntity e = _set[i];
                int tx = (int)e.X;
                int ty = (int)e.Y;
                int tz = (int)e.Z;
                if (tx >= cx - v[3] && tx <= cx + v[3]
                    && ty >= cy - v[4] && ty <= cy + v[4]
                    && tz >= cz - v[5] && tz <= cz + v[5])
                {
                    _set[w++] = e;
                }
            }
            _set.RemoveRange(w, _set.Count - w);
            return true;
        }

        // ------------------------------------------------------------------ 受击开关

        /// <summary>
        /// [IS DAMAGE ENABLE](块 0x010d5c09):逐对象留下受击开关为开的(拥有者与对象都要转得成碰撞体;空集合通过)。
        /// 工作集里只要有一个对象不知道开关状态(<see cref="IActEntity.ActDamageEnabled"/> 为 null)就整条触发器不求值,返回假。
        /// </summary>
        private bool ApplyIsDamageEnable()
        {
            for (int i = 0; i < _set.Count; i++)
            {
                if (!_set[i].ActDamageEnabled.HasValue)
                {
                    return false;
                }
            }
            int w = 0;
            for (int i = 0; i < _set.Count; i++)
            {
                if (_set[i].ActDamageEnabled == true)
                {
                    _set[w++] = _set[i];
                }
            }
            _set.RemoveRange(w, _set.Count - w);
            return true;
        }

        // ------------------------------------------------------------------ 队伍(整体真假)

        private bool PartyConditionPasses(ActRecord rec)
        {
            switch (rec.Kind)
            {
                case ActRecordKind.CheckPartySymbol:
                    return SymbolMatches(_host.PartySymbol, rec.A, rec.B);
                case ActRecordKind.CheckPartySlot:
                    return PartySlotResult(PartySlotTaken(rec.A, rec.B), rec.B);
                case ActRecordKind.CheckPlayerNum:
                    // 子标签一个也没有:客户端报错并判假(Ints 为空);否则 人数(是否含死者)== [COUNT]
                    return rec.Ints.Length >= 2 && PlayerCount(rec.Ints[1] != 0) == rec.Ints[0];
                case ActRecordKind.CheckPartyMembersState:
                    return PartyStatePasses(rec.Number);
                default:
                    return false;
            }
        }

        /// <summary>
        /// [CHECK PARTY SLOT] 槽 标志(块 0x010d4ea9–0x010d4f06):整体真假,结果 = (槽被占着) == (标志 == 1)。
        /// 槽被占着 = 队伍存在、槽与标志都不是 -1(-1 是"没写"的读数)、槽在 0 到 3 之内(0x00926500 对越界给空)且槽表项非空。
        /// 所以标志 1 要槽有人、标志 0(以及 1 以外的任何值)要槽没人;角色死活不看。
        /// </summary>
        public static bool PartySlotResult(bool taken, int flag) => taken == (flag == 1);

        private bool PartySlotTaken(int slot, int flag) =>
            slot != -1 && flag != -1 && slot >= 0 && slot < PartySlotCount && _host.PartySlotOccupied(slot);

        /// <summary>队伍里有角色的槽数(客户端 0x009266c0);不含死者时只数活着的。</summary>
        private int PlayerCount(bool includeDead)
        {
            int n = 0;
            for (int slot = 0; slot < PartySlotCount; slot++)
            {
                IActEntity member = _host.PartyMember(slot);
                if (member != null && (includeDead || member.IsAlive))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// [CHECK PARTYMEMBERS STATE] 的三个子码(块 0x010d7221–0x010d7338),只看被占着的槽:
        /// 0x4e [ALL ALIVE] 槽里角色空或已死 → 假;0x4f [ALL DEATH] 角色空的跳过、活着的 → 假;0x50 [EVEN ONE DEATH] 角色空或已死 → 真;
        /// 循环走完:0x4e、0x4f 为真,0x50 为假。
        /// </summary>
        private bool PartyStatePasses(int code)
        {
            for (int slot = 0; slot < PartySlotCount; slot++)
            {
                if (!_host.PartySlotOccupied(slot))
                {
                    continue;
                }
                IActEntity member = _host.PartyMember(slot);
                switch (code)
                {
                    case ActCompiler.PartyAllAlive:
                        if (member == null || !member.IsAlive)
                        {
                            return false;
                        }
                        break;
                    case ActCompiler.PartyAllDeath:
                        if (member != null && member.IsAlive)
                        {
                            return false;
                        }
                        break;
                    case ActCompiler.PartyEvenOneDeath:
                        if (member == null || !member.IsAlive)
                        {
                            return true;
                        }
                        break;
                }
            }
            return code != ActCompiler.PartyEvenOneDeath;
        }

        // ------------------------------------------------------------------ 行为

        /// <summary>
        /// [SET SYMBOL] / [SET SYMBOL OPTION](块 0x010c8310 / 0x010c83db):给目标集合的每个对象写符号,不筛活着与种类;
        /// [SET PARTY SYMBOL](0x010c849b)写队伍符号,不看目标集合;[ABS CASTING]只交给宿主,两个整数有负数时客户端不起读条条。
        /// 客户端的"拥有者为空则整条跳过"在这里碰不到(运行器构造时就要求拥有者非空)。
        /// </summary>
        private void ExecuteSymbolBehavior(ActBehavior b, List<IActEntity> targets)
        {
            switch (b.Kind)
            {
                case ActBehaviorKind.SetSymbol:
                    foreach (IActEntity t in targets)
                    {
                        t.SetActSymbol(b.SymbolValue);
                    }
                    break;
                case ActBehaviorKind.SetSymbolOption:
                    foreach (IActEntity t in targets)
                    {
                        t.SetActSymbolOption(b.SymbolValue);
                    }
                    break;
                case ActBehaviorKind.SetPartySymbol:
                    _host.SetPartySymbol(b.SymbolValue);
                    break;
                case ActBehaviorKind.AbsCasting:
                    if (b.AbsCastFirst >= 0 && b.AbsCastSecond >= 0)
                    {
                        _host.ShowAbsCasting(_owner, b.AbsCastFirst, b.AbsCastSecond);
                    }
                    break;
            }
        }
    }
}
