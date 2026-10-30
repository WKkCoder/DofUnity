using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 一只怪物自己的一套动画(M57,证据 E-151):开头就是 <see cref="MonsterModel"/> 里读出来的那一套;脚本的 [CHANGE ACTION FILE] 换动作文件时
    /// 只改这一只的这一份,同种怪物共用的模型不动。换进来的动画的路径另记一份(绘制列表按路径标记动画)。
    /// 数据一律从用户自备的 PVF 里读,本类不含原版内容。
    /// </summary>
    internal sealed class MonsterAnis
    {
        private readonly MonsterModel _model;
        private Dictionary<AniFile, string> _swappedPaths;

        public MonsterAnis(MonsterModel model)
        {
            _model = model;
            Waiting = model.Waiting;
            Move = model.Move;
            Damage1 = model.Damage1;
            Damage2 = model.Damage2;
            Down = model.Down;
            Overturn = model.Overturn;
            Attacks = new AniFile[model.Attacks.Count];
            for (int i = 0; i < Attacks.Length; i++)
            {
                Attacks[i] = model.Attacks[i];
            }
        }

        public AniFile Waiting;
        public AniFile Move;
        public AniFile Damage1;
        public AniFile Damage2;
        public AniFile Down;
        public AniFile Overturn;

        /// <summary>各招的动画(与 <see cref="MonsterModel.Attacks"/> 同下标;没有动画的招是 null)。个数固定,只换元素。</summary>
        public readonly AniFile[] Attacks;

        /// <summary>这一族现在的动画;没有动画的族(坐下、最后一幕、过程)是 null。</summary>
        public AniFile Get(MonsterActFamily family)
        {
            switch (family)
            {
                case MonsterActFamily.Stay:
                    return Waiting;
                case MonsterActFamily.Move:
                    return Move;
                case MonsterActFamily.Damage1:
                    return Damage1;
                case MonsterActFamily.Damage2:
                    return Damage2;
                case MonsterActFamily.Down:
                    return Down;
                case MonsterActFamily.Overturn:
                    return Overturn;
                default:
                    return null;
            }
        }

        /// <summary>换这一族的动画;没有动画的族什么都不做。</summary>
        public void Set(MonsterActFamily family, AniFile ani)
        {
            switch (family)
            {
                case MonsterActFamily.Stay:
                    Waiting = ani;
                    break;
                case MonsterActFamily.Move:
                    Move = ani;
                    break;
                case MonsterActFamily.Damage1:
                    Damage1 = ani;
                    break;
                case MonsterActFamily.Damage2:
                    Damage2 = ani;
                    break;
                case MonsterActFamily.Down:
                    Down = ani;
                    break;
                case MonsterActFamily.Overturn:
                    Overturn = ani;
                    break;
            }
        }

        /// <summary>记下换进来的动画的路径(同一个动画对象换过多次也只记一份)。</summary>
        public void Remember(AniFile ani, string path)
        {
            if (ani == null || path == null || _model.PathOf(ani) != null)
            {
                return;
            }
            if (_swappedPaths == null)
            {
                _swappedPaths = new Dictionary<AniFile, string>();
            }
            _swappedPaths[ani] = path;
        }

        /// <summary>动画的 PVF 路径:模型里读出来的,或换进来的;不认识的是 null。</summary>
        public string PathOf(AniFile ani)
        {
            string path = _model.PathOf(ani);
            if (path != null || ani == null || _swappedPaths == null)
            {
                return path;
            }
            return _swappedPaths.TryGetValue(ani, out string swapped) ? swapped : null;
        }
    }
}
