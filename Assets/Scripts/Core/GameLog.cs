// 玩家行为日志与游戏内时间。
// 设计文档：玩家在场景中的所有操作及关联影响都被日志记录并打印，剧情&核心 Agent 读日志推理意图。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace EpicSpark.Core
{
    /// <summary>逐条记录主观能动者的操作，落盘为 JSONL 并打印到 Console。</summary>
    public static class GameLog
    {
        private static readonly object Gate = new object();
        private static readonly List<PlayerActionRecord> Buffer = new List<PlayerActionRecord>();
        private static string _sessionDir;

        /// <summary>单条记录在内存中保留的上限，避免长局内存膨胀。</summary>
        public const int MaxBuffer = 4000;

        public static string SessionDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(_sessionDir))
                {
                    _sessionDir = Path.Combine(Application.persistentDataPath, "EpicSpark", "logs",
                        DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                    try { Directory.CreateDirectory(_sessionDir); }
                    catch (Exception e) { Debug.LogWarning("[EpicSpark] 无法创建日志目录: " + e.Message); }
                }
                return _sessionDir;
            }
        }

        public static void Record(PlayerActionRecord record)
        {
            if (record == null) return;
            if (string.IsNullOrEmpty(record.timestampUtc))
                record.timestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (record.gameMinutes == 0) record.gameMinutes = GameClock.TotalMinutes;

            lock (Gate)
            {
                Buffer.Add(record);
                if (Buffer.Count > MaxBuffer) Buffer.RemoveRange(0, Buffer.Count - MaxBuffer);
            }

            Debug.Log(string.Format("[行为] {0} | {1} -> {2} | {3}", record.sceneId, record.actionType,
                string.IsNullOrEmpty(record.targetId) ? "-" : record.targetId, record.detail));

            AppendLine(record);
            GameEvents.RaiseAction(record);
        }

        public static void Record(string sceneId, string actionType, string targetId, string detail)
        {
            Record(new PlayerActionRecord
            {
                sceneId = sceneId,
                actorId = "player",
                actionType = actionType,
                targetId = targetId ?? "",
                detail = detail ?? "",
                gameMinutes = GameClock.TotalMinutes,
            });
        }

        /// <summary>取某场景最近的若干条操作，供剧情&核心 Agent 推断玩家意图。</summary>
        public static List<PlayerActionRecord> Recent(string sceneId, int max = 40)
        {
            var result = new List<PlayerActionRecord>();
            lock (Gate)
            {
                for (int i = Buffer.Count - 1; i >= 0 && result.Count < max; i--)
                {
                    if (string.IsNullOrEmpty(sceneId) || Buffer[i].sceneId == sceneId)
                        result.Insert(0, Buffer[i]);
                }
            }
            return result;
        }

        public static int Count
        {
            get { lock (Gate) { return Buffer.Count; } }
        }

        /// <summary>
        /// 读档时把存档里的历史行为灌回内存缓冲，使续档后的剧情 Agent 仍能读到之前的玩家意图。
        /// 只入内存，不重复写 JSONL。
        /// </summary>
        public static void Seed(IReadOnlyList<PlayerActionRecord> records)
        {
            if (records == null) return;
            lock (Gate)
            {
                Buffer.Clear();
                int start = records.Count > MaxBuffer ? records.Count - MaxBuffer : 0;
                for (int i = start; i < records.Count; i++)
                {
                    if (records[i] != null) Buffer.Add(records[i]);
                }
            }
            Debug.Log("[行为] 读档恢复历史行为 " + records.Count + " 条");
        }

        /// <summary>把最近日志压成给 LLM 读的纯文本摘要。</summary>
        public static string Summarize(string sceneId, int max = 25)
        {
            var list = Recent(sceneId, max);
            if (list.Count == 0) return "（尚无玩家行为记录）";
            var sb = new StringBuilder();
            foreach (var r in list)
            {
                sb.Append("- [").Append(r.actionType).Append("] ");
                if (!string.IsNullOrEmpty(r.targetId)) sb.Append(r.targetId).Append(" ");
                sb.Append(r.detail).Append('\n');
            }
            return sb.ToString();
        }

        private static void AppendLine(PlayerActionRecord r)
        {
            try
            {
                string path = Path.Combine(SessionDirectory, "actions.jsonl");
                File.AppendAllText(path, JsonUtility.ToJson(r) + "\n", Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EpicSpark] 写日志失败: " + e.Message);
            }
        }
    }

    /// <summary>游戏内时间。传送锚点带 timeDelta（y/d/h/min）实现时间跳转。</summary>
    public static class GameClock
    {
        private const long MinutesPerYear = 365L * 24L * 60L;
        private const long MinutesPerDay = 24L * 60L;

        /// <summary>历史事件起始年份（公元前用负数）。</summary>
        public static int BaseYear = 1789;

        /// <summary>自起始年 1 月 1 日 00:00 起经过的分钟数。</summary>
        public static long TotalMinutes;

        public static void Reset(int baseYear)
        {
            BaseYear = baseYear;
            TotalMinutes = 0;
        }

        public static void SetFromSave(long minutes, int baseYear)
        {
            BaseYear = baseYear;
            TotalMinutes = minutes < 0 ? 0 : minutes;
        }

        /// <summary>解析 "10d" / "2h" / "5min" / "1y" 并推进时间；无法解析返回 false。</summary>
        public static bool AddDelta(string delta)
        {
            long minutes;
            if (!TryParseDelta(delta, out minutes)) return false;
            TotalMinutes += minutes;
            Debug.Log("[时间] +" + delta + " → " + Display());
            return true;
        }

        public static bool TryParseDelta(string delta, out long minutes)
        {
            minutes = 0;
            if (string.IsNullOrEmpty(delta)) return false;
            string s = delta.Trim().ToLowerInvariant();
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i == 0) return false;
            double value;
            if (!double.TryParse(s.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            string unit = s.Substring(i).Trim();
            switch (unit)
            {
                case "y": case "yr": case "year": case "年":
                    minutes = (long)Math.Round(value * MinutesPerYear); return true;
                case "d": case "day": case "日": case "天":
                    minutes = (long)Math.Round(value * MinutesPerDay); return true;
                case "h": case "hr": case "hour": case "小时":
                    minutes = (long)Math.Round(value * 60d); return true;
                case "min": case "m": case "分钟": case "分":
                    minutes = (long)Math.Round(value); return true;
                default:
                    return false;
            }
        }

        public static int CurrentYear
        {
            get { return BaseYear + (int)(TotalMinutes / MinutesPerYear); }
        }

        /// <summary>形如「1793年7月14日 08:30」。</summary>
        public static string Display()
        {
            long remainder = TotalMinutes % MinutesPerYear;
            int day = (int)(remainder / MinutesPerDay) + 1;
            int minuteOfDay = (int)(remainder % MinutesPerDay);
            int month = 1, dayOfMonth = day;
            int[] monthDays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
            for (int m = 0; m < 12; m++)
            {
                if (dayOfMonth <= monthDays[m]) { month = m + 1; break; }
                dayOfMonth -= monthDays[m];
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}年{1}月{2}日 {3:00}:{4:00}",
                CurrentYear, month, dayOfMonth, minuteOfDay / 60, minuteOfDay % 60);
        }
    }
}
