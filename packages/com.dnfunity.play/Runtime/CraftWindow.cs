using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计:制作窗口的数据模型。原版制作界面的版式没有读到,这一线也不做界面美术;
// 这里只有"窗口该显示什么、按键该做什么"的状态与判定(列表、选中、份数、预览、学会与制作两个动作及其结果),画面由以后的窗口美术接上。
namespace Dnf.Play
{
    /// <summary>列表里一行配方的状态。</summary>
    public enum CraftRowState
    {
        /// <summary>配方簿里已经学会,可以制作。</summary>
        Learned = 0,

        /// <summary>背包里有配方物品、还没学会:能"学习",学会之前不能制作。</summary>
        Learnable = 1,
    }

    /// <summary>制作窗口列表里的一行。</summary>
    public sealed class CraftRow
    {
        internal CraftRow(RecipeDef recipe, ItemInfo item, CraftRowState state)
        {
            Recipe = recipe;
            Item = item;
            State = state;
        }

        public RecipeDef Recipe { get; }

        /// <summary>配方物品(名字、图标等从它取);目录里查不到是 null。</summary>
        public ItemInfo Item { get; }

        public CraftRowState State { get; }

        public int Code => Recipe.Code;

        /// <summary>制作类别名(如手工、织造;数据文字);没有是 null。</summary>
        public string Category => Recipe.Category;
    }

    /// <summary>
    /// 制作窗口的数据模型:列表 = 已学的配方(编号从小到大)加上背包里有配方物品、还没学会的配方(编号从小到大)。
    /// 选一行、调份数,窗口给出这一行按这个份数的预览(<see cref="Preview"/>:能不能做、各行材料够不够、按材料最多做几份);
    /// 回车(<see cref="Activate"/>)对已学的行是制作、对还没学会的行是学习。学习与制作之后列表与预览自动刷新。
    /// 窗口不画东西,也不规定版式。
    /// </summary>
    public sealed class CraftWindow
    {
        private readonly List<CraftRow> _rows = new List<CraftRow>();
        private CraftSession _session;
        private Inventory _bag;
        private ICraftPurse _purse;
        private int _selectedCode = -1;

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        /// <summary>列表里的行(打开时刚查的;学习、制作之后自动重新查)。</summary>
        public IReadOnlyList<CraftRow> Rows => _rows;

        /// <summary>选中的行号;没有行时是 −1。</summary>
        public int Selected { get; private set; } = -1;

        public CraftRow SelectedRow => (uint)Selected < (uint)_rows.Count ? _rows[Selected] : null;

        /// <summary>要做几份(1 到 <see cref="CraftBench.MaxCount"/>)。</summary>
        public int Count { get; private set; } = 1;

        /// <summary>选中的行按 <see cref="Count"/> 份的预览;没有选中的行是 null。还没学会的行也有预览(材料行照常给,结果是 <see cref="CraftResult.NotLearned"/>)。</summary>
        public CraftPreview Preview { get; private set; }

        /// <summary>最近一次制作的报告(没做过是 null)。</summary>
        public CraftReport LastReport { get; private set; }

        /// <summary>最近一次学习的结果(没学过是 <see cref="LearnResult.Ok"/>,看 <see cref="Learns"/> 区分)。</summary>
        public LearnResult LastLearn { get; private set; }

        /// <summary>累计(窗口对象一直在,不随每次打开清零)打开过几次、学会过几个配方、制作成功执行过几次(不论掷骰成败,材料扣了就算)。</summary>
        public int Opens { get; private set; }

        public int Learns { get; private set; }

        public int Crafts { get; private set; }

        /// <summary>接上制作会话;没接就打不开。</summary>
        public void Use(CraftSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public CraftSession Session => _session;

        /// <summary>打开窗口:<paramref name="bag"/> 是要查与扣的背包,<paramref name="purse"/> 是金币来源(没有金币材料的配方用不到,可为 null)。没接会话返回 false。</summary>
        public bool Show(Inventory bag, ICraftPurse purse)
        {
            if (_session == null || bag == null)
            {
                return false;
            }
            _bag = bag;
            _purse = purse;
            Open = true;
            Opens++;
            Count = 1;
            LastReport = null;
            Selected = -1;
            _selectedCode = -1;
            Rebuild();
            return true;
        }

        public void Close()
        {
            Open = false;
        }

        /// <summary>重新查列表与预览(背包或金币在窗口之外变了之后宿主调);选中的配方还在就继续选它。窗口没开什么也不做。</summary>
        public void Refresh()
        {
            if (Open)
            {
                Rebuild();
            }
        }

        /// <summary>上下选行(<paramref name="dy"/> 向下为正),夹在范围内;换行时份数回到 1。</summary>
        public void Move(int dy)
        {
            if (!Open || _rows.Count == 0 || dy == 0)
            {
                return;
            }
            Select(Math.Max(0, Math.Min(_rows.Count - 1, Math.Max(0, Selected) + dy)));
        }

        /// <summary>选第 <paramref name="index"/> 行(超出范围什么也不做);选到不同的行时份数回到 1。</summary>
        public void Select(int index)
        {
            if (!Open || (uint)index >= (uint)_rows.Count || index == Selected)
            {
                return;
            }
            Selected = index;
            _selectedCode = _rows[index].Code;
            Count = 1;
            UpdatePreview();
        }

        /// <summary>把份数调到 <paramref name="count"/>(夹在 1 到 <see cref="CraftBench.MaxCount"/>)。</summary>
        public void SetCount(int count)
        {
            if (!Open)
            {
                return;
            }
            Count = Math.Max(1, Math.Min(CraftBench.MaxCount, count));
            UpdatePreview();
        }

        /// <summary>份数加减。</summary>
        public void AddCount(int delta)
        {
            SetCount((int)Math.Max(1L, Math.Min(CraftBench.MaxCount, (long)Count + delta)));
        }

        /// <summary>把份数调到按材料最多能做的份数(一份也做不了时是 1)。</summary>
        public void SetMaxCount()
        {
            if (Open && Preview != null)
            {
                SetCount(Math.Max(1, Preview.MaxByMaterials));
            }
        }

        /// <summary>回车:选中的行已学就制作、还没学会就学习。返回这一次有没有真的做成(制作结果 Ok、或学习结果 Ok)。</summary>
        public bool Activate()
        {
            CraftRow row = SelectedRow;
            if (!Open || row == null)
            {
                return false;
            }
            return row.State == CraftRowState.Learned ? Craft() : Learn();
        }

        /// <summary>学会选中的行(只有"还没学会"的行能学);返回有没有学会。</summary>
        public bool Learn()
        {
            CraftRow row = SelectedRow;
            if (!Open || row == null || row.State != CraftRowState.Learnable)
            {
                return false;
            }
            LastLearn = _session.Learn(row.Code, _bag);
            bool ok = LastLearn == LearnResult.Ok;
            if (ok)
            {
                Learns++;
            }
            Rebuild();
            return ok;
        }

        /// <summary>制作选中的行(只有已学的行能做)<see cref="Count"/> 份;结果在 <see cref="LastReport"/>。返回有没有做成(结果 Ok;掷骰失败拿失败产物也算做成)。</summary>
        public bool Craft()
        {
            CraftRow row = SelectedRow;
            if (!Open || row == null || row.State != CraftRowState.Learned)
            {
                return false;
            }
            LastReport = _session.Craft(row.Code, _bag, _purse, Count);
            bool ok = LastReport.Ok;
            if (ok)
            {
                Crafts++;
            }
            Rebuild();
            return ok;
        }

        private void Rebuild()
        {
            _rows.Clear();
            RecipeCatalog catalog = _session.Catalog;
            foreach (int code in _session.Book.Codes)
            {
                RecipeDef def = catalog.Get(code);
                if (def != null)
                {
                    _rows.Add(new CraftRow(def, catalog.Items.Get(code), CraftRowState.Learned));
                }
            }
            var learnable = new List<ItemInfo>();
            foreach (ItemInfo item in _session.RecipeItemsIn(_bag))
            {
                if (!_session.Book.IsLearned(item.Code))
                {
                    learnable.Add(item);
                }
            }
            learnable.Sort((a, b) => a.Code.CompareTo(b.Code));
            foreach (ItemInfo item in learnable)
            {
                _rows.Add(new CraftRow(catalog.Get(item.Code), item, CraftRowState.Learnable));
            }

            int keep = Selected;
            Selected = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Code == _selectedCode)
                {
                    Selected = i;
                    break;
                }
            }
            if (Selected < 0 && _rows.Count > 0)
            {
                Selected = Math.Max(0, Math.Min(keep, _rows.Count - 1));
            }
            int newCode = SelectedRow == null ? -1 : SelectedRow.Code;
            if (newCode != _selectedCode)
            {
                Count = 1;
            }
            _selectedCode = newCode;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            CraftRow row = SelectedRow;
            Preview = row == null ? null : _session.Preview(row.Code, _bag, _purse, Count);
        }
    }
}
