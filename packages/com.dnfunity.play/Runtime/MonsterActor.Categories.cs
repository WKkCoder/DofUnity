using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dnf.Game.Act;

[assembly: InternalsVisibleTo("Dnf.Play.Tests")]

namespace Dnf.Play
{
    public sealed partial class MonsterActor
    {
        private readonly HashSet<int> _actCategories = new HashSet<int>();

        /// <summary>[ADD CATEGORY] id 0x1a([fixture]) 时写的锚点标志(字段 +0x4f94,E-171)。</summary>
        internal bool CategoryAnchorActive { get; private set; }

        internal float CategoryAnchorX { get; private set; }

        internal float CategoryAnchorY { get; private set; }

        /// <summary>把 .mob [category] 块里的类别挂进运行时 bitset(与脚本 ADD/REMOVE 共用表,E-171)。</summary>
        internal void SeedActCategories(IEnumerable<string> categoryNames)
        {
            if (categoryNames == null)
            {
                return;
            }
            foreach (string name in categoryNames)
            {
                int id = ActObjectCategories.Resolve(name);
                if ((uint)id >= (uint)ActObjectCategories.InvalidId || !_actCategories.Add(id))
                {
                    continue;
                }
                if (id == ActObjectCategories.AnchorCategoryId)
                {
                    CategoryAnchorActive = true;
                    CategoryAnchorX = (float)X;
                    CategoryAnchorY = (float)Y;
                }
            }
        }

        internal bool HasActCategory(int categoryId) => _actCategories.Contains(categoryId);

        /// <summary>当前挂着的类别编号(升序,与客户端 +0x27c 向量一致;E-212)。</summary>
        internal IReadOnlyList<int> GetActCategoryIds()
        {
            if (_actCategories.Count == 0)
            {
                return Array.Empty<int>();
            }
            var list = new int[_actCategories.Count];
            _actCategories.CopyTo(list);
            Array.Sort(list);
            return list;
        }

        internal void AddActCategory(int categoryId)
        {
            if ((uint)categoryId >= (uint)ActObjectCategories.InvalidId)
            {
                return;
            }
            if (!_actCategories.Add(categoryId))
            {
                return;
            }
            if (categoryId == ActObjectCategories.AnchorCategoryId)
            {
                CategoryAnchorActive = true;
                CategoryAnchorX = (float)X;
                CategoryAnchorY = (float)Y;
            }
            SyncDefenseCategoryIds();
        }

        internal void RemoveActCategory(int categoryId)
        {
            if (!_actCategories.Remove(categoryId))
            {
                return;
            }
            if (categoryId == ActObjectCategories.AnchorCategoryId)
            {
                CategoryAnchorActive = false;
            }
            SyncDefenseCategoryIds();
        }

        /// <summary>把运行时类别集写进 <see cref="Defense"/> 面板(E-212)。</summary>
        internal void SyncDefenseCategoryIds()
        {
            if (Defense == null)
            {
                return;
            }
            Defense.CategoryIds = GetActCategoryIds();
        }
    }
}
