using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 能给出某一时刻整屏绘制列表的东西:有主角的场景(城镇、副本房间,<see cref="IHeroSession"/>)和没有主角的整屏界面(副本选择、选角)。
    /// 视图层(Unity 的 TownView)只依赖它:取到列表后交给 SpriteStage 画,CPU 合成器按同一份列表画出逐字节一致的参照。
    /// </summary>
    public interface ISceneSource
    {
        /// <summary>
        /// 这一时刻的整屏绘制列表(屏幕左上角为原点的 800×600 画布)。<paramref name="timeMs"/> 是墙钟动画时间;
        /// 带主角的场景里主角与怪物按自己的状态计时,不用它。
        /// </summary>
        List<SceneSprite> Build(long timeMs);
    }

    /// <summary>
    /// 带地图 [sound] 的场景(城镇区域,证据 E-097):背景音乐与环境声的标签(audio.xml 的 ID)按地图里写的顺序。
    /// 流程进这块区域时据此发房间声音提示;没有 [sound] 的地图是空列表。
    /// </summary>
    public interface IMapSoundSource
    {
        IReadOnlyList<string> MapSounds { get; }
    }
}
