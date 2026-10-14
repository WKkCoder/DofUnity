using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 销毁 Unity 对象。编辑器的 EditMode 里不能调用 Object.Destroy,播放时不能调用 DestroyImmediate,这里按当前模式选。
    /// 播放时的 Destroy 要到帧末才生效,所以游戏对象先停用:否则同一帧里创建的下一个舞台会把它的精灵也画进去。
    /// </summary>
    public static class UnityObjects
    {
        public static void Destroy(Object o)
        {
            if (o == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                if (o is GameObject go)
                {
                    go.SetActive(false);
                }
                Object.Destroy(o);
            }
            else
            {
                Object.DestroyImmediate(o);
            }
        }
    }
}
