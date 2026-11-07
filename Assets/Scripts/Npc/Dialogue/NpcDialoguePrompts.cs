// NPC 对话的提示词与确定性兜底台词。
// 设计文档要求：NPC 回复要连贯、有人格、不跑题；交给剧情 Agent 的必须是「概括转述」的第三人称简报，
// 避免剧情 Agent 误以为自己是 NPC（对话主体混乱）。
using System;
using System.Collections.Generic;
using System.Text;
using EpicSpark.Core;

namespace EpicSpark.Npc.Dialogue
{
    /// <summary>提示词与兜底台词库。纯静态。</summary>
    public static class NpcDialoguePrompts
    {
        // ─────────────────────────── System / User 提示词 ───────────────────────────

        public static string ReplySystemPrompt(NpcSpec npc, string storyContext)
        {
            var sb = new StringBuilder(900);
            sb.Append("你正在扮演 EpicSpark 历史剧情中的一个 NPC，与玩家对话。\n");
            sb.Append("【身份】").Append(Safe(npc.displayName, "无名者")).Append('\n');
            sb.Append("【身份标签】").Append(Safe(npc.tag, "平民")).Append('\n');
            sb.Append("【人物设定】").Append(Safe(npc.persona, "来历不明的当地人")).Append('\n');
            if (npc != null && !string.IsNullOrEmpty(npc.storyRole))
                sb.Append("【剧情作用】").Append(npc.storyRole).Append('\n');
            if (!string.IsNullOrEmpty(storyContext))
            {
                string context = storyContext.Length > 600 ? storyContext.Substring(0, 600) + "…" : storyContext;
                sb.Append("【当前局势】").Append(context).Append('\n');
            }
            sb.Append("\n【铁律】\n");
            sb.Append("1. 你只以这个 NPC 的身份说话，永远不要替玩家说话，不要写「玩家说」之类的旁白。\n");
            sb.Append("2. 不要跳出角色解释规则，不要提到 AI、模型、提示词、JSON。\n");
            sb.Append("3. 用简体中文口语，符合这个身份与时代，不要用现代网络用语。\n");
            sb.Append("4. 每次回复 1~3 句、不超过 80 字；可以有一句动作或神态描写（放在括号里）。\n");
            sb.Append("5. 不跑题：只回应玩家这句话与当前局势；不知道的事就说不知道，不要编造超出身份的信息。\n");
            sb.Append("6. 若玩家威胁或示好，按人物设定给出真实反应，不要一味顺从。\n");
            return sb.ToString();
        }

        public static string ReplyUserPrompt(string playerLine, IReadOnlyList<string> history)
        {
            var sb = new StringBuilder(700);
            sb.Append("【此前的对话】\n").Append(FormatHistory(history)).Append('\n');
            sb.Append("【玩家刚刚说的】").Append(string.IsNullOrEmpty(playerLine) ? "（沉默地看着你）" : playerLine.Trim()).Append('\n');
            sb.Append("\n直接输出这个 NPC 的下一句回复，不要加名字前缀，不要加引号。");
            return sb.ToString();
        }

        public static string SummarizeSystemPrompt(NpcSpec npc)
        {
            var sb = new StringBuilder(700);
            sb.Append("你是剧情记录员。把玩家与 NPC 的一段对话，概括并转述成第三人称简报，交给后续的剧情 Agent。\n");
            sb.Append("【对话双方】玩家；").Append(Safe(npc != null ? npc.displayName : "", "无名者"))
              .Append("（").Append(Safe(npc != null ? npc.tag : "", "平民")).Append("）\n");
            sb.Append("\n【铁律】\n");
            sb.Append("1. 必须使用第三人称转述，不能保留直接引语原句，不要出现「我」「你」这种对话口吻。\n");
            sb.Append("2. 主体必须分清：谁说了什么、谁答应了什么，绝不能让人误以为剧情 Agent 自己就是 NPC。\n");
            sb.Append("3. 只概括有信息量的内容（身份、诉求、线索、承诺、威胁、态度变化），删掉寒暄。\n");
            sb.Append("4. 输出 4~6 行，格式：\n");
            sb.Append("   场景与时间：…\n   对话对象：…\n   玩家意图：…\n   关键信息：…\n   对方态度：…\n   剧情提示：…\n");
            sb.Append("5. 用简体中文，每行不超过 60 字。");
            return sb.ToString();
        }

        public static string SummarizeUserPrompt(NpcSpec npc, IReadOnlyList<string> history, string storyContext)
        {
            var sb = new StringBuilder(900);
            if (!string.IsNullOrEmpty(storyContext))
            {
                string context = storyContext.Length > 400 ? storyContext.Substring(0, 400) + "…" : storyContext;
                sb.Append("【当前局势】").Append(context).Append("\n\n");
            }
            sb.Append("【对话记录】\n").Append(FormatHistory(history)).Append('\n');
            sb.Append("按系统要求的 4~6 行格式输出简报。");
            return sb.ToString();
        }

        // ─────────────────────────── 确定性兜底 ───────────────────────────

        /// <summary>按 tag 选一组符合身份的台词，并按 cursor 轮换。</summary>
        public static string FallbackReply(NpcSpec npc, string playerLine, int cursor)
        {
            string tag = npc != null ? (npc.tag ?? "") : "";
            string[] pool = LinesForTag(tag);
            string line = pool[Math.Abs(cursor) % pool.Length];
            string gesture = GestureFor(playerLine);
            string name = npc != null ? npc.displayName : "";
            if (!string.IsNullOrEmpty(name) && tag == "信使" && cursor % 4 == 3)
                line = name + "压低声音：" + line;
            return gesture + line;
        }

        /// <summary>结构化第三人称简报（兜底）。</summary>
        public static string FallbackSummary(NpcSpec npc, IReadOnlyList<string> history)
        {
            string name = Safe(npc != null ? npc.displayName : "", "无名者");
            string tag = Safe(npc != null ? npc.tag : "", "平民");
            string persona = Safe(npc != null ? npc.persona : "", "来历不明的当地人");
            if (persona.Length > 50) persona = persona.Substring(0, 50) + "…";

            int turns = history != null ? history.Count : 0;
            string lastPlayer = LastLineBy(history, "玩家");
            string lastNpc = StripSpeaker(LastLineBy(history, name));
            string intent = IntentOf(lastPlayer + " " + AllPlayerLines(history));
            string attitude = AttitudeOf(tag);

            var sb = new StringBuilder(700);
            sb.Append("场景与时间：玩家与").Append(name).Append("（").Append(tag).Append("）就地交谈，共 ").Append(turns).Append(" 句往来。\n");
            sb.Append("对话对象：").Append(name).Append("。").Append(persona).Append('\n');
            sb.Append("玩家意图：").Append(intent).Append('\n');
            sb.Append("关键信息：");
            if (!string.IsNullOrEmpty(lastNpc))
                sb.Append("对方最后提到「").Append(Shorten(lastNpc, 46)).Append("」。");
            else
                sb.Append("对方没有给出明确信息，只做了简短回应。");            sb.Append('\n');
            sb.Append("对方态度：").Append(attitude).Append('\n');
            sb.Append("剧情提示：后续剧情可把这次交谈当作").Append(name).Append("与玩家之间的一次接触，")
              .Append("不要复述原话；若玩家在交谈中透露了意图，下一幕可由").Append(name)
              .Append("或旁人把这件事传开。");
            return sb.ToString();
        }

        // ─────────────────────────── 台词库 ───────────────────────────

        private static string[] LinesForTag(string tag)
        {
            switch (NormalizeTag(tag))
            {
                case "信使":
                    return new[]
                    {
                        "别在这儿站着——传令的人最怕被问第二遍。跟我走，路上说。",
                        "上面的命令刚下来，我要把话带到每一处岗哨。天黑之前，别走大街。",
                        "我只管送信，不管信里写什么。你要打听，得先让我看看有什么值得换的。",
                        "今天已经跑了三趟，腿都不听使唤。有话快说，下一站还有两条街。",
                    };
                case "士兵":
                    return new[]
                    {
                        "站住。手放在看得见的地方。你从哪条街过来的？",
                        "上头说今晚不许聚众。你要么回屋，要么跟我去值房说清楚。",
                        "别跟我讲道理，我只认口令。没有口令，就离这条路远点。",
                        "我见过太多你这样的人。真想活着，就少说话、少回头。",
                    };
                case "商人":
                    return new[]
                    {
                        "价钱就这个价，今天什么都不便宜。要买趁早。",
                        "你问的这事儿……我劝你别在这儿问，来来往往的人太多。",
                        "我在这条街上做了十几年买卖，什么风浪都见过。想打听消息，先买点东西。",
                        "粮食、盐、灯油，什么都在涨。手里有钱，就比什么都强。",
                    };
                case "神职":
                    return new[]
                    {
                        "愿主保佑你。你看起来心里有事，坐下说吧，这里没人会赶你走。",
                        "我不问你来做什么，只问你还愿不愿意听我一句话。",
                        "这一带的人都在害怕，害怕的时候最容易做错事。你要冷静。",
                        "有些事我不能说，但我可以听。说出来，也许会好受一点。",
                    };
                case "贵族":
                    return new[]
                    {
                        "你知道你在跟谁说话吗？……算了，这个时候讲身份也没什么用。",
                        "把话说清楚些，这地方隔墙有耳。",
                        "局势变得很快。你站的位置，今天对，明天可能就错了。",
                        "我手里还有些能用的东西，但要看你能给我什么。",
                    };
                case "工匠":
                    return new[]
                    {
                        "别碰那个，还没修好。你要真想帮忙，把那边那根绳递给我。",
                        "做什么都得趁手。你看这把工具，用久了就知道毛病在哪。",
                        "这活计我干了半辈子。你要想学，先学会别怕脏。",
                        "东西坏了能修，人坏了就难了。这地方坏的东西太多。",
                    };
                case "告密者":
                    return new[]
                    {
                        "小声点。你刚才那句话，已经有三个人听见了。",
                        "我什么都不知道，我只是路过。……不过你要是愿意付点钱，我可以想起来一点。",
                        "你站哪边？别急着回答，想清楚再说。",
                        "这地方的消息比钱值钱。你今天说的话，明天就可能变成一张纸。",
                    };
                default:
                    return new[]
                    {
                        "这会儿不太平，你还是少说话。",
                        "我只是路过，什么都不知道。",
                        "你要问路吗？往那边走，别回头。",
                        "这地方最近变了很多，人也变了很多。",
                    };
            }
        }

        private static string GestureFor(string playerLine)
        {
            string line = playerLine ?? "";
            if (Contains(line, "杀", "打", "滚", "威胁", "抢")) return "（他后退半步，声音压低）";
            if (Contains(line, "钱", "买", "价", "卖", "换")) return "（他掂了掂手里的东西）";
            if (Contains(line, "帮", "救", "求", "请")) return "（他犹豫了一下）";
            if (Contains(line, "哪", "怎么", "为什么", "什么", "谁")) return "（他左右看了看）";
            return "";
        }

        private static string IntentOf(string text)
        {
            if (Contains(text, "钱", "买", "价", "卖", "换")) return "想通过交易或付钱换取信息与物资。";
            if (Contains(text, "帮", "救", "求", "请")) return "在寻求帮助或庇护，处境被动。";
            if (Contains(text, "杀", "打", "滚", "威胁", "抢")) return "带着威胁与压迫感，试图用武力施压。";
            if (Contains(text, "哪", "怎么", "为什么", "什么", "谁")) return "在打听消息与路线，试图弄清局势。";
            if (string.IsNullOrEmpty(text)) return "没有明确诉求，只是试探性地接触。";
            return "在试探对方的立场，尚未表明真正目的。";
        }

        private static string AttitudeOf(string tag)
        {
            switch (NormalizeTag(tag))
            {
                case "信使": return "戒备但愿意交换消息，急着离开。";
                case "士兵": return "警惕、公事公办，随时准备动武。";
                case "商人": return "客气而算计，先把话留一半。";
                case "神职": return "平静、带着怜悯，愿意听但不愿承诺。";
                case "贵族": return "端着一份体面，暗地里盘算得失。";
                case "工匠": return "冷淡但务实，只关心手上的活。";
                case "告密者": return "闪烁其词，一边套话一边记下细节。";
                default: return "不安、回避，希望尽快结束交谈。";
            }
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string value = tag.Trim();
            if (Contains(value, "信使", "传令", "驿", "差役")) return "信使";
            if (Contains(value, "士兵", "兵", "骑士", "巡防", "卫")) return "士兵";
            if (Contains(value, "商人", "商", "贩", "买办", "掌柜", "店主")) return "商人";
            if (Contains(value, "神职", "神父", "牧师", "僧人", "祭司", "修女", "教士")) return "神职";
            if (Contains(value, "贵族", "官", "绅士", "议员", "领主", "元老")) return "贵族";
            if (Contains(value, "工匠", "匠", "铁匠", "木匠", "机修")) return "工匠";
            if (Contains(value, "告密", "密探", "眼线", "扒手", "间谍")) return "告密者";
            return "平民";
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private static string FormatHistory(IReadOnlyList<string> history)
        {
            if (history == null || history.Count == 0) return "（这是第一次交谈）";
            var sb = new StringBuilder(600);
            int start = Math.Max(0, history.Count - 10);
            for (int i = start; i < history.Count; i++)
            {
                string line = history[i];
                if (string.IsNullOrEmpty(line)) continue;
                sb.Append("- ").Append(Shorten(line, 120)).Append('\n');
            }
            return sb.Length == 0 ? "（这是第一次交谈）" : sb.ToString();
        }

        private static string LastLineBy(IReadOnlyList<string> history, string speaker)
        {
            if (history == null || string.IsNullOrEmpty(speaker)) return "";
            for (int i = history.Count - 1; i >= 0; i--)
            {
                string line = history[i];
                if (string.IsNullOrEmpty(line)) continue;
                if (line.IndexOf(speaker, StringComparison.Ordinal) >= 0) return line;
            }
            return "";
        }

        private static string AllPlayerLines(IReadOnlyList<string> history)
        {
            if (history == null) return "";
            var sb = new StringBuilder(400);
            int start = Math.Max(0, history.Count - 8);
            for (int i = start; i < history.Count; i++)
            {
                string line = history[i];
                if (string.IsNullOrEmpty(line)) continue;
                if (line.IndexOf("玩家", StringComparison.Ordinal) >= 0) sb.Append(line).Append(' ');
            }
            return sb.ToString();
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        /// <summary>去掉「名字：」前缀与句末标点，便于在简报里转述。</summary>
        private static string StripSpeaker(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            string text = line.Trim();
            int colon = text.IndexOf('：');
            if (colon < 0) colon = text.IndexOf(':');
            if (colon > 0 && colon <= 10) text = text.Substring(colon + 1).Trim();
            text = text.TrimEnd('。', '！', '？', '…', '；', ' ');
            return text;
        }

        private static bool Contains(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (!string.IsNullOrEmpty(keywords[i]) &&
                    text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string Safe(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
