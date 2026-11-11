using System;
using System.Collections.Generic;
using Dnf.Data;

// 本项目自己的接线:按物品脚本路径读出物品的副职业标签(<see cref="ExpertJobItemTags"/>),按物品编号缓存。规则在 ExpertJobItemTags 里。
namespace Dnf.Game
{
    /// <summary>从游戏数据读物品脚本里的副职业标签;物品为空或脚本读不到时给 <see cref="ExpertJobItemTags.None"/>。</summary>
    public sealed class ExpertJobTagSource
    {
        private readonly GameData _data;
        private readonly Dictionary<int, ExpertJobItemTags> _cache = new Dictionary<int, ExpertJobItemTags>();

        public ExpertJobTagSource(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public ExpertJobItemTags Get(ItemInfo item)
        {
            if (item == null)
            {
                return ExpertJobItemTags.None;
            }
            if (_cache.TryGetValue(item.Code, out ExpertJobItemTags cached))
            {
                return cached;
            }
            ExpertJobItemTags tags = ExpertJobItemTags.None;
            if (item.Path != null)
            {
                ScriptLine[] lines = _data.ReadScript(item.Path);
                if (lines != null)
                {
                    tags = ExpertJobItemTags.Parse(lines);
                }
            }
            _cache[item.Code] = tags;
            return tags;
        }
    }
}
