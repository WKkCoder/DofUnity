using System;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 联网选角界面(真名册)在宿主一侧的装配(r18-charui):不碰引擎,TownDemo 的 <c>-dnfonline</c> 分支把结果交给 <see cref="OnlineShellHost.Create"/>。
    /// 只有装上了汉字字体(汉字真能画出来)才读游戏数据里的职业与成长类型中文名;没有字体时职业称号用英文占位
    /// (点阵字库画不出汉字,读了也只会显示成问号),界面其余文字同样退回英文占位。
    /// </summary>
    public static class OnlineCharacterSelectSetup
    {
        /// <param name="assets">用户自备的游戏数据(读角色脚本与角色文字表)。</param>
        /// <param name="hasFonts">汉字字体是否已经装好(<see cref="Dnf.Render.OverlayArt.SharedFonts"/> 非空)。</param>
        public static OnlineCharacterSelectOptions Create(DnfAssets assets, bool hasFonts)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            ICharacterTitles titles = null;
            if (hasFonts)
            {
                PvfArchive pvf = assets.Pvf;
                var library = new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry entry) ? pvf.Read(entry) : null);
                titles = new DnfCharacterTitles(assets.ReadScript, library);
            }
            return new OnlineCharacterSelectOptions { Titles = titles };
        }
    }
}
