namespace Dnf.Render
{
    /// <summary>
    /// 精灵怎样与画面已有的像素混合。
    /// <see cref="Normal"/> 就是 alpha = 255 覆盖、alpha = 0 跳过、中间值按 over 混合(取整方式没有与原版比较过);
    /// <see cref="Additive"/> 对应 .ani 的图形特效类型 2,已用城镇光效对原版截图核对(证据 E-030)。
    /// 其余特效类型(1、3、4、5、6)的含义未知,没有实现。
    /// </summary>
    public enum SpriteBlend
    {
        Normal = 0,

        /// <summary>dst' = min(255, round(dst + src.rgb × src.a/255 × tintAlpha/255)),整个式子只在最后取整一次。</summary>
        Additive = 1,
    }
}
