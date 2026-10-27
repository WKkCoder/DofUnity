using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的占位设计(M35):原版技能窗口的版式没有读到(客户端界面脚本里有定义,还没解);
// 学技能的规则来自服务端静态读码(证据 E-127,非 REA),窗口只是把规则摆给玩家看、让玩家点学习/退还/重置、给快捷栏摆技能。
namespace Dnf.Play
{
    /// <summary>技能行的状态(学没学过看 <see cref="SkillRow.Level"/>)。</summary>
    public enum SkillRowState
    {
        /// <summary>已经学到这个成长类型的最高级。</summary>
        Maxed = 0,

        /// <summary>现在就能再学一级(等级够、点数够、前置技能够)。</summary>
        Learnable = 1,

        /// <summary>现在学不了下一级(原因看 <see cref="SkillRow.Reason"/>)。</summary>
        Locked = 2,
    }

    /// <summary>一条前置技能要求:要先学到某个技能的某一级(<see cref="Met"/> 是现在满足了没有)。</summary>
    public sealed class SkillPrereqRow
    {
        public int SkillId { get; set; }

        /// <summary>技能名(数据文字;取不到是 null)。</summary>
        public string Name { get; set; }

        public int Level { get; set; }

        public bool Met { get; set; }
    }

    /// <summary>技能窗口列表里的一行:一个技能现在的状态(宿主层只认这些基本类型,不引用游戏数据层)。</summary>
    public sealed class SkillRow
    {
        public int SkillId { get; set; }

        /// <summary>技能名(数据文字,运行时才从字符串表取;取不到是 null,画法退回占位的 <c>SKILL 编号</c>)。</summary>
        public string Name { get; set; }

        /// <summary>现在学到几级(0 = 没学)。</summary>
        public int Level { get; set; }

        /// <summary>这个成长类型能学到的最高级。</summary>
        public int Cap { get; set; }

        /// <summary>主角这个等级最多能学到几级(规则 <see cref="SkillRules.MaxLearnable"/>)。</summary>
        public int MaxNow { get; set; }

        /// <summary>学下一级要花的点数;已经满级或没有花费表是 -1。</summary>
        public int NextCost { get; set; }

        /// <summary>学下一级需要的主角等级;已经满级时是 0。</summary>
        public int NextHeroLevel { get; set; }

        /// <summary>特殊技能(花觉醒点)。</summary>
        public bool Special { get; set; }

        /// <summary>主动技能(能放进快捷栏)。</summary>
        public bool Active { get; set; }

        /// <summary>在快捷栏第几格(0 到 5,键 A 到 H);不在栏里是 -1。</summary>
        public int Quick { get; set; }

        public SkillRowState State { get; set; }

        /// <summary>学下一级的检查结果(<see cref="SkillOpResult.Ok"/> 表示能学);满级的技能是 <see cref="SkillOpResult.NotAllowed"/>。</summary>
        public SkillOpResult Reason { get; set; }

        /// <summary>技能图标(.skl [icon] 的图像与帧;没有是 null 与 -1)。</summary>
        public string IconImage { get; set; }

        public int IconFrame { get; set; }

        public string IconDisabledImage { get; set; }

        public int IconDisabledFrame { get; set; }

        /// <summary>前置技能要求。</summary>
        public IReadOnlyList<SkillPrereqRow> Prereqs { get; set; } = Array.Empty<SkillPrereqRow>();
    }

    /// <summary>窗口底部的按钮(从左到右)。</summary>
    public enum SkillButton
    {
        Learn = 0,
        Refund = 1,
        Reset = 2,
    }

    /// <summary>
    /// 技能窗口(K 键,只在城镇里能开;本项目自己的占位设计,证据 E-127):上面四个分页(通用、职业、其他、觉醒,规则 <see cref="SkillRules.UiGroup"/>),
    /// 左边选中分页里这个成长类型能学的技能(图标、名字、等级),右边选中技能的详情(等级、下一级的花费与要求的主角等级、说明、前置技能),
    /// 底下是快捷栏六格(键 A S D F G H)与学习、退还、重置三个按钮。
    /// 键位:上下选技能,左右(或 Q / E、Tab)换分页,回车或 + 学一级,退格或 - 退一级,R 重置(第一下只是预备,再按一次才重置),
    /// A S D F G H 把选中的主动技能放进对应的快捷栏格(已经在别的格就互换),Delete 把它从快捷栏拿掉,K 或 Esc 关上;鼠标点分页、技能行、按钮、快捷栏格。
    /// 开着的时候游戏暂停(宿主负责,见 <see cref="GameFlow.Skills"/>)。窗口只管状态、判定与点击判定,画面由 <see cref="SkillWindowArt"/> 画,规则与状态在 <see cref="HeroProgress.Skills"/> 里。
    /// 文字是占位英文(有字体时 <see cref="UiText"/> 换成中文),技能名与说明是数据文字(原版字符串表,运行时才读)。
    /// 副本里不能开:战斗会话在开局时按快捷栏建技能槽,中途改栏会和正在放的技能打架(这一块没做)。
    /// </summary>
    public sealed class SkillWindow
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>窗口的位置与大小(画布像素,居中,和背包、商店、任务窗口同一块)。</summary>
        public const int WindowX = 98;
        public const int WindowY = 30;
        public const int WindowWidth = 604;
        public const int WindowHeight = 540;

        /// <summary>标题条的高。</summary>
        public const int TitleHeight = 44;

        /// <summary>分页:个数(与 <see cref="SkillRules.UiGroupCount"/> 一致)、左上角、每个的宽高与间隔。</summary>
        public const int PageCount = SkillRules.UiGroupCount;
        public const int TabX = 114;
        public const int TabY = 80;
        public const int TabWidth = 138;
        public const int TabHeight = 28;
        public const int TabGap = 6;

        /// <summary>左边技能列表:左上角、宽、每行的高与间隔、一屏看到的行数。</summary>
        public const int ListX = 114;
        public const int ListY = 116;
        public const int ListWidth = 268;
        public const int RowHeight = 40;
        public const int RowGap = 3;
        public const int VisibleRows = 8;

        /// <summary>右边详情栏:左上角、宽高。</summary>
        public const int DetailX = 394;
        public const int DetailY = 116;
        public const int DetailWidth = 292;
        public const int DetailHeight = 341;

        /// <summary>快捷栏的格数(键 A S D F G H)。</summary>
        public const int QuickSlotCount = SkillBook.QuickSlots;

        /// <summary>快捷栏六格:左上角、边长、间隔;按钮:左上角、宽高、间隔(按钮在快捷栏的右边)。</summary>
        public const int QuickX = 114;
        public const int QuickY = 466;
        public const int QuickSize = 44;
        public const int QuickGap = 6;
        public const int ButtonX = 410;
        public const int ButtonY = 466;
        public const int ButtonWidth = 88;
        public const int ButtonHeight = 44;
        public const int ButtonGap = 6;

        private HeroProgress _hero;
        private readonly List<SkillRow> _rows = new List<SkillRow>();
        private int _builtVersion = -1;
        private int _builtLevel = -1;
        private int _builtPage = -1;

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        /// <summary>当前分页(0 到 <see cref="PageCount"/> − 1)。</summary>
        public int Page { get; private set; }

        /// <summary>当前分页里的技能行(开着时随状态变化自动重新排)。</summary>
        public IReadOnlyList<SkillRow> Rows
        {
            get
            {
                Refresh();
                return _rows;
            }
        }

        /// <summary>选中的行号;没有行是 -1。</summary>
        public int Selected { get; private set; } = -1;

        /// <summary>一屏最上面一行是第几行。</summary>
        public int ScrollRow { get; private set; }

        /// <summary>最近一次操作的结果文字(占位英文,显示在窗口底部);没有是空串。</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>最近一次学、退的结果(没做过是 <see cref="SkillOpResult.Ok"/>;日志与探针用)。</summary>
        public SkillOpResult LastResult { get; private set; }

        /// <summary><see cref="LastResult"/> 的枚举名(宿主层不引用游戏数据层,用名字交接)。</summary>
        public string LastResultName => LastResult.ToString();

        /// <summary>按过一次重置、等着再按一次确认。</summary>
        public bool ResetArmed { get; private set; }

        /// <summary>累计(窗口对象一直在,不随每次打开清零)打开过几次、学了几级、退了几级、重置过几次、摆过几次快捷栏。</summary>
        public int Opens { get; private set; }

        public int Learns { get; private set; }

        public int Refunds { get; private set; }

        public int Resets { get; private set; }

        public int Assigns { get; private set; }

        /// <summary>
        /// 把"文字引用(种类编号 + 键)"换成文字的函数;没设就用全进程共用的 <see cref="WorldTexts.Shared"/>(宿主有字体时才装),都没有就不取名字与说明。
        /// </summary>
        public Func<int, string, string> TextSource { get; set; }

        /// <summary>接上主角的进度(技能簿在里面);没接就打不开。</summary>
        public HeroProgress Hero => _hero;

        public void Use(HeroProgress hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _builtVersion = -1;
        }

        /// <summary>选中的那一行;没有是 null。</summary>
        public SkillRow SelectedRow
        {
            get
            {
                Refresh();
                return (uint)Selected < (uint)_rows.Count ? _rows[Selected] : null;
            }
        }

        /// <summary>选中技能的说明(数据文字,取不到是 null)。</summary>
        public string SelectedExplain
        {
            get
            {
                SkillRow row = SelectedRow;
                if (row == null)
                {
                    return null;
                }
                SkillDef def = _hero.Skills.Catalog.Find(row.SkillId);
                return def == null ? null : TextOf(def.Explain);
            }
        }

        /// <summary>主角现在的技能点(普通)与觉醒点;画标题条用。</summary>
        public int SkillPoints => _hero == null ? 0 : _hero.SkillPoints;

        public int AwakeningPoints => _hero == null ? 0 : _hero.AwakeningPoints;

        // ------------------------------------------------------------------ 打开与关闭

        /// <summary>
        /// 打开技能窗口:只有城镇有;没接进度或没有技能数据(<see cref="HeroProgress.HasSkillData"/>)打不开。返回有没有打开。
        /// </summary>
        public bool Show(GameStage stage)
        {
            if (Open)
            {
                return false;
            }
            if (stage != GameStage.Town || _hero == null || !_hero.HasSkillData)
            {
                return false;
            }
            Open = true;
            Opens++;
            Message = string.Empty;
            ResetArmed = false;
            _builtVersion = -1;
            Refresh();
            return true;
        }

        public void Close()
        {
            Open = false;
            ResetArmed = false;
        }

        // ------------------------------------------------------------------ 光标

        /// <summary>方向键:<paramref name="dy"/> 在列表里上下选技能,<paramref name="dx"/> 换分页(到头不绕回)。</summary>
        public void Move(int dx, int dy)
        {
            if (!Open)
            {
                return;
            }
            ResetArmed = false;
            if (dx != 0)
            {
                SetPage(Page + (dx < 0 ? -1 : 1));
                return;
            }
            Refresh();
            if (dy != 0 && _rows.Count > 0)
            {
                Select(Selected + (dy < 0 ? -1 : 1));
            }
        }

        /// <summary>换分页(越界夹住);换了页选中第一行。</summary>
        public void SetPage(int page)
        {
            if (!Open)
            {
                return;
            }
            int next = Math.Max(0, Math.Min(PageCount - 1, page));
            if (next == Page)
            {
                return;
            }
            Page = next;
            Selected = -1;
            ScrollRow = 0;
            _builtVersion = -1;
            Refresh();
        }

        /// <summary>选中第 <paramref name="index"/> 行(夹在范围内),并让它出现在一屏里。</summary>
        public void Select(int index)
        {
            Refresh();
            if (_rows.Count == 0)
            {
                Selected = -1;
                return;
            }
            Selected = Math.Max(0, Math.Min(_rows.Count - 1, index));
            if (Selected < ScrollRow)
            {
                ScrollRow = Selected;
            }
            else if (Selected >= ScrollRow + VisibleRows)
            {
                ScrollRow = Selected - VisibleRows + 1;
            }
            ResetArmed = false;
        }

        // ------------------------------------------------------------------ 学、退、重置

        /// <summary>学一级选中的技能;返回有没有学成(结果与提示看 <see cref="LastResult"/>、<see cref="Message"/>)。</summary>
        public bool Learn()
        {
            SkillRow row = SelectedRow;
            if (!Open || row == null)
            {
                return false;
            }
            ResetArmed = false;
            SkillOpResult result = _hero.LearnSkill(row.SkillId, 1, out int spent);
            Finish(result, result == SkillOpResult.Ok ? "SKILL LEARNED" : null);
            if (result == SkillOpResult.Ok)
            {
                Learns++;
            }
            return result == SkillOpResult.Ok;
        }

        /// <summary>退一级选中的技能;返回有没有退成。</summary>
        public bool Refund()
        {
            SkillRow row = SelectedRow;
            if (!Open || row == null)
            {
                return false;
            }
            ResetArmed = false;
            SkillOpResult result = _hero.RefundSkill(row.SkillId, 1, out int back);
            Finish(result, result == SkillOpResult.Ok ? "SKILL REFUNDED" : null);
            if (result == SkillOpResult.Ok)
            {
                Refunds++;
            }
            return result == SkillOpResult.Ok;
        }

        /// <summary>
        /// 重置所有技能(技能清回白送的、点数回到该级总额、快捷栏重排):第一次调用只是预备(提示再按一次),连着第二次才真的重置。返回有没有真的重置。
        /// </summary>
        public bool Reset()
        {
            if (!Open)
            {
                return false;
            }
            if (!ResetArmed)
            {
                ResetArmed = true;
                Message = "PRESS RESET AGAIN";
                return false;
            }
            ResetArmed = false;
            _hero.ResetSkills();
            Resets++;
            _builtVersion = -1;
            Message = "SKILLS RESET";
            LastResult = SkillOpResult.Ok;
            Refresh();
            return true;
        }

        // ------------------------------------------------------------------ 快捷栏

        /// <summary>把选中的技能放进快捷栏第 <paramref name="slot"/> 格(0 到 5);只有学会的主动技能能放。返回有没有放成。</summary>
        public bool Assign(int slot)
        {
            SkillRow row = SelectedRow;
            if (!Open || row == null)
            {
                return false;
            }
            ResetArmed = false;
            bool ok = row.Level > 0 && row.Active && _hero.Skills.AssignQuick(slot, row.SkillId);
            Message = ok ? "SKILL SLOTTED" : "CANNOT SLOT THIS";
            if (ok)
            {
                Assigns++;
                _builtVersion = -1;
                Refresh();
            }
            return ok;
        }

        /// <summary>把选中的技能从快捷栏拿掉;它不在栏里返回 false。</summary>
        public bool ClearSlot()
        {
            SkillRow row = SelectedRow;
            if (!Open || row == null)
            {
                return false;
            }
            ResetArmed = false;
            int slot = row.Quick;
            bool ok = slot >= 0 && _hero.Skills.ClearQuick(slot);
            Message = ok ? "SLOT CLEARED" : "NOT IN A SLOT";
            if (ok)
            {
                Assigns++;
                _builtVersion = -1;
                Refresh();
            }
            return ok;
        }

        /// <summary>快捷栏第 <paramref name="slot"/> 格里的技能编号;空格是 0。</summary>
        public int QuickAt(int slot)
        {
            return _hero == null ? 0 : _hero.Skills.QuickAt(slot);
        }

        /// <summary>某个技能的图标信息(快捷栏格子画图标用;找不到是 null)。</summary>
        public SkillIconRef IconOf(int skillId)
        {
            SkillDef def = _hero?.Skills.Catalog.Find(skillId);
            return def == null ? null : new SkillIconRef(def.IconImage, def.IconFrame, def.IconDisabledImage, def.IconDisabledFrame);
        }

        // ------------------------------------------------------------------ 鼠标

        /// <summary>
        /// 点了画布坐标 (<paramref name="x"/>, <paramref name="y"/>):分页、技能行(再点一下已选中的行等于学一级)、按钮、快捷栏格(把选中的技能放进去)。返回有没有点中什么。
        /// </summary>
        public bool Click(int x, int y)
        {
            if (!Open)
            {
                return false;
            }
            for (int i = 0; i < PageCount; i++)
            {
                if (Inside(TabRect(i), x, y))
                {
                    SetPage(i);
                    return true;
                }
            }
            Refresh();
            for (int v = 0; v < VisibleRows; v++)
            {
                int index = ScrollRow + v;
                if (index >= _rows.Count)
                {
                    break;
                }
                if (Inside(RowRect(v), x, y))
                {
                    if (index == Selected)
                    {
                        Learn();
                    }
                    else
                    {
                        Select(index);
                    }
                    return true;
                }
            }
            for (int i = 0; i < 3; i++)
            {
                if (Inside(ButtonRect(i), x, y))
                {
                    switch ((SkillButton)i)
                    {
                        case SkillButton.Learn:
                            Learn();
                            break;
                        case SkillButton.Refund:
                            Refund();
                            break;
                        default:
                            Reset();
                            break;
                    }
                    return true;
                }
            }
            for (int i = 0; i < SkillBook.QuickSlots; i++)
            {
                if (Inside(QuickRect(i), x, y))
                {
                    Assign(i);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ 版式(画与点击判定共用)

        /// <summary>第 <paramref name="index"/> 个分页按钮的矩形(x, y, 宽, 高)。</summary>
        public static (int X, int Y, int W, int H) TabRect(int index)
        {
            return (TabX + index * (TabWidth + TabGap), TabY, TabWidth, TabHeight);
        }

        /// <summary>一屏里第 <paramref name="visible"/> 行(从 0 数)的矩形。</summary>
        public static (int X, int Y, int W, int H) RowRect(int visible)
        {
            return (ListX, ListY + visible * (RowHeight + RowGap), ListWidth, RowHeight);
        }

        /// <summary>第 <paramref name="index"/> 个按钮(学习、退还、重置)的矩形。</summary>
        public static (int X, int Y, int W, int H) ButtonRect(int index)
        {
            return (ButtonX + index * (ButtonWidth + ButtonGap), ButtonY, ButtonWidth, ButtonHeight);
        }

        /// <summary>快捷栏第 <paramref name="slot"/> 格的矩形。</summary>
        public static (int X, int Y, int W, int H) QuickRect(int slot)
        {
            return (QuickX + slot * (QuickSize + QuickGap), QuickY, QuickSize, QuickSize);
        }

        private static bool Inside((int X, int Y, int W, int H) r, int x, int y)
        {
            return x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;
        }

        // ------------------------------------------------------------------ 内部

        private void Finish(SkillOpResult result, string okMessage)
        {
            LastResult = result;
            Message = result == SkillOpResult.Ok ? okMessage : MessageOf(result);
            _builtVersion = -1;
            Refresh();
        }

        /// <summary>操作结果对应的占位英文提示(<see cref="UiText"/> 有中文)。</summary>
        public static string MessageOf(SkillOpResult result)
        {
            switch (result)
            {
                case SkillOpResult.Ok:
                    return string.Empty;
                case SkillOpResult.NotAllowed:
                    return "CANNOT LEARN YET";
                case SkillOpResult.NotEnoughPoints:
                    return "NOT ENOUGH POINTS";
                case SkillOpResult.NotLearned:
                    return "NOT LEARNED";
                case SkillOpResult.BelowGiven:
                    return "BELOW STARTING LEVEL";
                case SkillOpResult.NeededByOther:
                    return "ANOTHER SKILL NEEDS IT";
                case SkillOpResult.InvalidCost:
                    return "NO COST DATA";
                default:
                    return "NO SUCH SKILL";
            }
        }

        private string TextOf(SkillText text)
        {
            if (text.IsEmpty)
            {
                return null;
            }
            Func<int, string, string> source = TextSource;
            if (source != null)
            {
                return source(text.Kind, text.Key);
            }
            return WorldTexts.Shared?.Text(text.Kind, text.Key);
        }

        /// <summary>状态变了(学、退、重置、升级、换页)就按当前分页重排行;没变不做事。</summary>
        private void Refresh()
        {
            if (!Open || _hero == null)
            {
                return;
            }
            if (_builtVersion == _hero.SkillsVersion && _builtLevel == _hero.Level && _builtPage == Page)
            {
                return;
            }
            _builtVersion = _hero.SkillsVersion;
            _builtLevel = _hero.Level;
            _builtPage = Page;
            int keep = (uint)Selected < (uint)_rows.Count ? _rows[Selected].SkillId : -1;
            _rows.Clear();
            SkillBook book = _hero.Skills;
            SkillCatalog catalog = book.Catalog;
            var defs = new List<SkillDef>();
            foreach (SkillDef def in catalog.ForGrowtype(_hero.Growtype))
            {
                if (SkillRules.UiGroup(def) == Page)
                {
                    defs.Add(def);
                }
            }
            foreach (KeyValuePair<int, int> learned in book.Learned)
            {
                SkillDef def = catalog.Find(learned.Key);
                if (def != null && SkillRules.UiGroup(def) == Page && !defs.Contains(def))
                {
                    defs.Add(def);
                }
            }
            defs.Sort(CompareDefs);
            foreach (SkillDef def in defs)
            {
                _rows.Add(MakeRow(def, book));
            }
            if (_rows.Count == 0)
            {
                Selected = -1;
                ScrollRow = 0;
                return;
            }
            int found = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].SkillId == keep)
                {
                    found = i;
                    break;
                }
            }
            Selected = found >= 0 ? found : Math.Max(0, Math.Min(_rows.Count - 1, Selected < 0 ? 0 : Selected));
            int maxScroll = Math.Max(0, _rows.Count - VisibleRows);
            ScrollRow = Math.Max(0, Math.Min(maxScroll, ScrollRow));
            if (Selected < ScrollRow)
            {
                ScrollRow = Selected;
            }
            else if (Selected >= ScrollRow + VisibleRows)
            {
                ScrollRow = Selected - VisibleRows + 1;
            }
        }

        private static int CompareDefs(SkillDef a, SkillDef b)
        {
            int byLevel = a.RequiredLevel.CompareTo(b.RequiredLevel);
            return byLevel != 0 ? byLevel : a.Id.CompareTo(b.Id);
        }

        private SkillRow MakeRow(SkillDef def, SkillBook book)
        {
            int level = book.LevelOf(def.Id);
            int cap = SkillRules.TypeMaxLevel(def, _hero.Growtype);
            SkillOpResult reason = book.CheckLearn(def.Id, _hero.Level, _hero.Growtype, 1, out int _);
            bool maxed = level >= cap;
            var prereqs = new List<SkillPrereqRow>();
            foreach (SkillPrereq pre in def.PreRequired)
            {
                SkillDef target = book.Catalog.Find(pre.SkillId);
                prereqs.Add(new SkillPrereqRow
                {
                    SkillId = pre.SkillId,
                    Name = target == null ? null : TextOf(target.Name),
                    Level = pre.Level,
                    Met = book.LevelOf(pre.SkillId) >= pre.Level,
                });
            }
            return new SkillRow
            {
                SkillId = def.Id,
                Name = TextOf(def.Name),
                Level = level,
                Cap = cap,
                MaxNow = SkillRules.MaxLearnable(def, _hero.Level, _hero.Growtype),
                NextCost = maxed ? -1 : SkillRules.CostOfLevel(def, level + 1),
                NextHeroLevel = maxed ? 0 : SkillRules.RequiredHeroLevel(def, level + 1),
                Special = def.IsSpecial,
                Active = def.IsActive,
                Quick = book.QuickSlotOf(def.Id),
                State = maxed ? SkillRowState.Maxed : reason == SkillOpResult.Ok ? SkillRowState.Learnable : SkillRowState.Locked,
                Reason = maxed ? SkillOpResult.NotAllowed : reason,
                IconImage = def.IconImage,
                IconFrame = def.IconFrame,
                IconDisabledImage = def.IconDisabledImage,
                IconDisabledFrame = def.IconDisabledFrame,
                Prereqs = prereqs,
            };
        }
    }

    /// <summary>技能图标的引用(宿主层与画面代码用,不带游戏数据类型)。</summary>
    public sealed class SkillIconRef
    {
        public SkillIconRef(string image, int frame, string disabledImage, int disabledFrame)
        {
            Image = image;
            Frame = frame;
            DisabledImage = disabledImage;
            DisabledFrame = disabledFrame;
        }

        public string Image { get; }

        public int Frame { get; }

        public string DisabledImage { get; }

        public int DisabledFrame { get; }
    }
}
