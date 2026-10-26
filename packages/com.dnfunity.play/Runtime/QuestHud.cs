using System;
using System.Collections.Generic;

// 本项目自己的占位设计(M31):任务提示条(接了、有进展、可以交了、完成了)与右上角的任务追踪,版式见 QuestHudArt。
namespace Dnf.Play
{
    /// <summary>
    /// 任务提示与追踪的状态(按墙钟走,窗口开着、游戏暂停时也照常淡出):
    /// 提示条从 <see cref="QuestBook.TakeNotices"/> 取来,每条停 <see cref="ToastLifeMs"/> 毫秒、最后 <see cref="ToastFadeMs"/> 毫秒淡出,同时最多 <see cref="MaxToasts"/> 条
    /// (多出来的把最老的挤掉);同一个任务的进度提示合并成一条最新的(重新计时)。
    /// 追踪是日志里最靠前的几个任务(可以交的排在前面),隔 <see cref="TrackerRefreshMs"/> 毫秒或有新提示时重新查一次(查一次要算每个任务的奖励,不是每帧都做)。
    /// </summary>
    public sealed class QuestHud
    {
        /// <summary>一条提示停留的毫秒数,其中最后一段淡出。</summary>
        public const int ToastLifeMs = 4500;

        public const int ToastFadeMs = 700;

        /// <summary>同时最多几条提示。</summary>
        public const int MaxToasts = 4;

        /// <summary>追踪里最多列几个任务,与重新查的间隔。</summary>
        public const int TrackedMax = 3;

        public const int TrackerRefreshMs = 500;

        /// <summary>一条正在显示的提示。</summary>
        public sealed class Toast
        {
            internal Toast(QuestNotice notice)
            {
                Notice = notice;
            }

            public QuestNotice Notice { get; internal set; }

            /// <summary>已经停了多久(毫秒)。</summary>
            public double AgeMs { get; internal set; }

            /// <summary>画的时候用的不透明度(0 到 255):前面是 255,最后 <see cref="ToastFadeMs"/> 毫秒线性降到 0。</summary>
            public int Alpha
            {
                get
                {
                    double left = ToastLifeMs - AgeMs;
                    if (left >= ToastFadeMs)
                    {
                        return 255;
                    }
                    return left <= 0 ? 0 : (int)Math.Round(255.0 * left / ToastFadeMs);
                }
            }
        }

        private readonly List<Toast> _toasts = new List<Toast>();
        private readonly List<QuestRow> _tracked = new List<QuestRow>();
        private double _trackerAgeMs = TrackerRefreshMs;
        private bool _trackerDirty = true;

        /// <summary>正在显示的提示,最老的在前。</summary>
        public IReadOnlyList<Toast> Toasts => _toasts;

        /// <summary>追踪里的任务行(最多 <see cref="TrackedMax"/> 个)。</summary>
        public IReadOnlyList<QuestRow> Tracked => _tracked;

        /// <summary>累计显示过的提示条数(合并掉的不算;日志用)。</summary>
        public int ToastsShown { get; private set; }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒(墙钟):取走任务系统新发的提示、给已有的提示计时并去掉过期的、需要时重新查追踪。
        /// <paramref name="book"/> 为 null 时只计时。
        /// </summary>
        public void Advance(double deltaMs, QuestBook book)
        {
            if (deltaMs < 0)
            {
                deltaMs = 0;
            }
            if (book != null && book.PendingNotices > 0)
            {
                foreach (QuestNotice notice in book.TakeNotices())
                {
                    Add(notice);
                }
                _trackerDirty = true;
            }
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                _toasts[i].AgeMs += deltaMs;
                if (_toasts[i].AgeMs >= ToastLifeMs)
                {
                    _toasts.RemoveAt(i);
                }
            }
            _trackerAgeMs += deltaMs;
            if (book != null && (_trackerDirty || _trackerAgeMs >= TrackerRefreshMs))
            {
                RefreshTracker(book);
            }
        }

        /// <summary>加一条提示(同一个任务的进度提示合并成一条、重新计时;超过 <see cref="MaxToasts"/> 条时挤掉最老的)。</summary>
        public void Add(QuestNotice notice)
        {
            if (notice == null)
            {
                return;
            }
            if (notice.Kind == QuestNoticeKind.Progress)
            {
                foreach (Toast old in _toasts)
                {
                    if (old.Notice.Kind == QuestNoticeKind.Progress && old.Notice.QuestId == notice.QuestId)
                    {
                        old.Notice = notice;
                        old.AgeMs = 0;
                        return;
                    }
                }
            }
            _toasts.Add(new Toast(notice));
            ToastsShown++;
            while (_toasts.Count > MaxToasts)
            {
                _toasts.RemoveAt(0);
            }
        }

        /// <summary>立刻重新查一遍追踪。</summary>
        public void RefreshTracker(QuestBook book)
        {
            _trackerDirty = false;
            _trackerAgeMs = 0;
            _tracked.Clear();
            if (book == null || book.Log.Active.Count == 0)
            {
                return;
            }
            List<QuestRow> rows = book.LogRows();
            foreach (QuestRow row in rows)
            {
                if (row.State == QuestRowState.Ready)
                {
                    _tracked.Add(row);                       // 可以交的排在前面
                }
            }
            foreach (QuestRow row in rows)
            {
                if (_tracked.Count >= TrackedMax)
                {
                    break;
                }
                if (row.State != QuestRowState.Ready)
                {
                    _tracked.Add(row);
                }
            }
            if (_tracked.Count > TrackedMax)
            {
                _tracked.RemoveRange(TrackedMax, _tracked.Count - TrackedMax);
            }
        }

        /// <summary>丢掉所有提示与追踪(换了角色、重新开始)。</summary>
        public void Clear()
        {
            _toasts.Clear();
            _tracked.Clear();
            _trackerDirty = true;
            _trackerAgeMs = TrackerRefreshMs;
        }
    }
}
