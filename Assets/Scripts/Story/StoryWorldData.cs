// 确定性剧情兜底所需的「时代 / 模板 / 随机数」基础数据。
// 设计文档的剧情&核心 Agent 必须在没有 API Key 时也能开局，所以这里内置了六大时代的世界观词库。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EpicSpark.Story
{
    /// <summary>一幕在整条剧情弧中的位置。</summary>
    public enum StoryStage
    {
        Opening = 0,
        Conflict = 1,
        Turn = 2,
        Climax = 3,
        Ending = 4,
    }

    /// <summary>确定性兜底可识别的历史时代（按地域拆开，避免「古罗马」被写成「县衙」）。</summary>
    public enum StoryEra
    {
        AncientChina = 0,
        AncientEurope = 1,
        MedievalEurope = 2,
        RevolutionEurope = 3,
        ModernEurope = 4,
        ModernChina = 5,
        /// <summary>没有可靠时代/地域依据时的诚实兜底，不冒充特定文明。</summary>
        Unknown = 6,
    }

    /// <summary>场景模板族：城镇广场 / 乡村 / 港口 / 宫廷 / 战场 / 教堂区 / 集市。</summary>
    public enum StoryTemplate
    {
        Square = 0,
        Country = 1,
        Harbor = 2,
        Court = 3,
        Battlefield = 4,
        Church = 5,
        Market = 6,
    }

    /// <summary>可复现的轻量 PRNG（xorshift32）。同一个种子必然得到同一串剧情。</summary>
    internal sealed class SeedRandom
    {
        private uint state;

        public SeedRandom(int seed)
        {
            state = unchecked((uint)seed);
            if (state == 0u) state = 0x9E3779B9u;
            // 先扰动几轮，避免相近种子开头过于接近
            for (int i = 0; i < 3; i++) NextUInt();
        }

        public uint NextUInt()
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        /// <summary>[min, maxExclusive)</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextUInt() % (uint)(maxExclusive - minInclusive));
        }

        public float NextFloat()
        {
            return (NextUInt() % 10000u) / 10000f;
        }

        public bool Chance(float probability)
        {
            return NextFloat() < probability;
        }

        public T Pick<T>(IList<T> items)
        {
            if (items == null || items.Count == 0) return default(T);
            return items[Next(0, items.Count)];
        }

        public string Pick(string[] items)
        {
            if (items == null || items.Length == 0) return "";
            return items[Next(0, items.Length)];
        }
    }

    /// <summary>一个时代的用词与外显风格。模板文本里 {token} 会被替换成对应词。</summary>
    internal sealed class EraProfile
    {
        public StoryEra era;
        public string eraName = "";
        public string eraBrief = "";
        public string regionName = "";
        public string styleTag = "";
        public string ground = "";
        public string houseStyle = "";
        public string authority = "";
        public string crowd = "";
        public string soldier = "";
        public string messenger = "";
        public string priest = "";
        public string merchant = "";
        public string noble = "";
        public string commoner = "";
        public string artisan = "";
        public string transport = "";
        public string church = "";
        public string townhall = "";
        public string gate = "";
        public string market = "";
        public string street = "";
        public string notice = "";
        public string landmark = "";
        public string weaponName = "";
        public string warName = "";
        public string[] npcNames = new string[0];
        public string[] placeNames = new string[0];
        public string styleHint = "";
        public string endingTone = "";

        /// <summary>把模板文本里的 {token} 替换成本时代的词。</summary>
        public string Fill(string template)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return template
                .Replace("{era}", eraName)
                .Replace("{eraBrief}", eraBrief)
                .Replace("{region}", regionName)
                .Replace("{style}", styleTag)
                .Replace("{ground}", ground)
                .Replace("{house}", houseStyle)
                .Replace("{authority}", authority)
                .Replace("{crowd}", crowd)
                .Replace("{soldier}", soldier)
                .Replace("{messenger}", messenger)
                .Replace("{priest}", priest)
                .Replace("{merchant}", merchant)
                .Replace("{noble}", noble)
                .Replace("{commoner}", commoner)
                .Replace("{artisan}", artisan)
                .Replace("{transport}", transport)
                .Replace("{church}", church)
                .Replace("{townhall}", townhall)
                .Replace("{gate}", gate)
                .Replace("{market}", market)
                .Replace("{street}", street)
                .Replace("{notice}", notice)
                .Replace("{landmark}", landmark)
                .Replace("{weapon}", weaponName)
                .Replace("{war}", warName)
                .Replace("{styleHint}", styleHint);
        }
    }

    /// <summary>时代识别 + 词库。纯静态、无 Unity 依赖，便于编辑器测试。</summary>
    internal static class StoryWorld
    {
        private static readonly EraProfile[] Profiles = BuildProfiles();

        // 用词命中计分表；同一关键词命中一次加 1 分。
        private static readonly string[] AncientKeywords =
        {
            "古代", "古罗马", "罗马", "希腊", "雅典", "斯巴达", "波斯", "埃及", "金字塔", "秦", "汉朝", "汉",
            "唐", "宋朝", "宋", "明朝", "明", "皇帝", "长城", "运河", "郡县", "王朝", "春秋", "战国", "先秦",
            "诸侯", "匈奴", "帝国", "神庙", "元老院", "法老", "巴比伦", "亚历山大", "诗经", "青铜",
        };

        private static readonly string[] MedievalKeywords =
        {
            "中世纪", "十字军", "骑士", "城堡", "领主", "农奴", "封建", "修道院", "黑死病", "百年战争",
            "拜占庭", "教皇", "攻城", "采邑", "神圣罗马", "骑士团", "行会", "大教堂", "封臣", "教皇国",
        };

        private static readonly string[] RevolutionKeywords =
        {
            "大革命", "巴士底", "断头台", "1789", "1793", "三级会议", "雅各宾", "起义", "攻占",
            "人权宣言", "自由平等", "国民公会", "无套裤汉", "1788", "1791", "1792", "1794", "革命军",
            "独立宣言", "独立战争", "革命委员会",
        };

        private static readonly string[] ModernKeywords =
        {
            "工业", "蒸汽", "铁路", "工厂", "殖民", "鸦片", "1840", "1860", "甲午", "辛亥", "明治", "维新",
            "洋务", "电报", "轮船", "钢铁", "资本", "民族", "1911", "1912", "火车", "汽笛", "罢工", "工人",
            "租界", "洋行", "太平天国", "义和团", "民国", "清朝末年", "清末",
        };

        private static readonly string[] ChinaKeywords =
        {
            "中国", "秦朝", "汉朝", "唐朝", "宋朝", "明朝", "清朝", "先秦", "秦汉", "隋唐", "明清",
            "皇帝", "朝", "郡", "县", "京城", "长安", "洛阳", "汴", "临安", "江南", "关中", "中原", "华夏",
            "民国", "洋务", "辛亥", "太平天国", "义和团",
            "抗日", "上海", "广州", "天津", "汉口", "南京", "苏州", "儒", "科举", "衙门",
            "鸦片", "甲午", "北洋", "军阀", "租界", "长城", "诸侯", "六国", "三国", "元朝",
        };

        private static readonly string[] EuropeKeywords =
        {
            "欧洲", "法国", "英国", "德国", "罗马", "希腊", "巴黎", "伦敦", "俄国", "西班牙", "意大利",
            "骑士", "教会", "教皇", "领主", "大革命", "工业革命", "维多利亚", "拿破仑", "巴士底",
            "凡尔赛", "马赛", "曼彻斯特", "资产阶级", "殖民",
        };

        // ─────────────────────────── 对外查询 ───────────────────────────

        public static EraProfile Profile(StoryEra era)
        {
            int index = (int)era;
            if (index < 0 || index >= Profiles.Length) return Profiles[(int)StoryEra.RevolutionEurope];
            return Profiles[index];
        }

        public static string StageLabel(StoryStage stage)
        {
            switch (stage)
            {
                case StoryStage.Opening: return "开端";
                case StoryStage.Conflict: return "冲突";
                case StoryStage.Turn: return "转折";
                case StoryStage.Climax: return "高潮";
                case StoryStage.Ending: return "结局";
                default: return "推进";
            }
        }

        public static StoryStage StageForIndex(int index)
        {
            if (index <= 0) return StoryStage.Opening;
            if (index == 1) return StoryStage.Conflict;
            if (index == 2) return StoryStage.Turn;
            if (index == 3) return StoryStage.Climax;
            return StoryStage.Ending;
        }

        public static string TemplateDisplay(StoryTemplate template)
        {
            switch (template)
            {
                case StoryTemplate.Square: return "城镇广场";
                case StoryTemplate.Country: return "乡村田野";
                case StoryTemplate.Harbor: return "港口码头";
                case StoryTemplate.Court: return "宫廷官署";
                case StoryTemplate.Battlefield: return "战场前沿";
                case StoryTemplate.Church: return "教堂街区";
                case StoryTemplate.Market: return "集市街市";
                default: return "街区";
            }
        }

        public static string TemplateModelHint(StoryTemplate template, EraProfile profile)
        {
            switch (template)
            {
                case StoryTemplate.Square: return "open town square, cobblestone plaza, central monument";
                case StoryTemplate.Country: return "rural field, dirt road, farmhouse, barn, haystack";
                case StoryTemplate.Harbor: return "harbor quay, wooden dock, moored boat, warehouse";
                case StoryTemplate.Court: return "courtyard of palace, stone steps, colonnade, guards";
                case StoryTemplate.Battlefield: return "battlefield trench, earthwork, cannon, military tent";
                case StoryTemplate.Church: return "church square, stone church, graveyard, bell tower";
                case StoryTemplate.Market: return "market street, stalls with awnings, crates, crowd";
                default: return profile.styleHint;
            }
        }

        /// <summary>从事件名 + 知识库上下文识别时代。事件名优先，上下文只作补充。</summary>
        public static StoryEra Detect(string eventName, string knowledgeContext)
        {
            string name = eventName ?? "";
            string context = knowledgeContext ?? "";
            if (context.Length > 4000) context = context.Substring(0, 4000);
            bool weakKnowledge = context.IndexOf("知识库未收录该事件", StringComparison.Ordinal) >= 0;

            int[] nameScores =
            {
                Score(name, AncientKeywords),
                Score(name, MedievalKeywords),
                Score(name, RevolutionKeywords),
                Score(name, ModernKeywords),
            };
            string[][] conceptKeywords =
            {
                AncientKeywords, MedievalKeywords, RevolutionKeywords, ModernKeywords,
            };

            // 事件名只要命中某个时代，就直接采用（用户选的事件名是最强信号）
            int concept = -1;
            int bestName = 0;
            for (int i = 0; i < nameScores.Length; i++)
            {
                if (nameScores[i] > bestName)
                {
                    bestName = nameScores[i];
                    concept = i;
                }
            }

            if (concept < 0 && !weakKnowledge)
            {
                // 事件名没有线索：只有可靠上下文才能补足；“未收录”的泛化相近战役不能拿来定型。
                int bestContext = 0;
                for (int i = 0; i < conceptKeywords.Length; i++)
                {
                    int contextScore = Math.Min(Score(context, conceptKeywords[i]), 6);
                    if (contextScore > bestContext)
                    {
                        bestContext = contextScore;
                        concept = i;
                    }
                }
                if (concept < 0)
                {
                    // 完全没有可靠线索：明确未知，不再默认法国大革命或某个地域。
                    return StoryEra.Unknown;
                }
            }

            // 地域同样以事件名为准；事件名没有地域线索时才看上下文
            int nameChina = Score(name, ChinaKeywords);
            int nameEurope = Score(name, EuropeKeywords);
            bool isChina;
            bool isEurope;
            if (nameChina > nameEurope)
            {
                isChina = true;
                isEurope = false;
            }
            else if (nameEurope > nameChina)
            {
                isChina = false;
                isEurope = true;
            }
            else
            {
                int china = nameChina * 3 + Math.Min(Score(context, ChinaKeywords), 6);
                int europe = nameEurope * 3 + Math.Min(Score(context, EuropeKeywords), 6);
                isChina = china > europe;
                isEurope = europe > china;
            }

            if (!isChina && !isEurope)
            {
                // 两河流域、蒙古、美洲等未被粗分类覆盖的地域必须保持未知，不能默认成中国。
                return StoryEra.Unknown;
            }

            switch (concept)
            {
                case 0:
                    if (isEurope) return StoryEra.AncientEurope;
                    return StoryEra.AncientChina;
                case 1:
                    return StoryEra.MedievalEurope;
                case 2:
                    return isChina ? StoryEra.ModernChina : StoryEra.RevolutionEurope;
                default:
                    return isChina ? StoryEra.ModernChina : StoryEra.ModernEurope;
            }
        }

        private static int Score(string text, string[] keywords)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int score = 0;
            for (int i = 0; i < keywords.Length; i++)
            {
                string keyword = keywords[i];
                if (keyword.Length == 0) continue;
                if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) score++;
            }
            return score;
        }

        // ─────────────────────────── 词库 ───────────────────────────

        private static EraProfile[] BuildProfiles()
        {
            var list = new List<EraProfile>(6);

            list.Add(new EraProfile
            {
                era = StoryEra.AncientChina,
                eraName = "中国古代王朝时期",
                eraBrief = "古代",
                regionName = "关中",
                styleTag = "中国古代木构城市：夯土城墙、灰瓦坡屋顶、木格窗、青砖铺地",
                ground = "夯实黄土路面，主路铺青砖，雨后留有车辙",
                houseStyle = "木构架、灰瓦坡屋顶、白灰墙",
                authority = "县衙",
                crowd = "挑担的百姓、脚夫与游学的士子",
                soldier = "披甲的郡兵",
                messenger = "驿卒",
                priest = "寺里的僧人",
                merchant = "行商",
                noble = "县丞",
                commoner = "农户",
                artisan = "铁匠",
                transport = "双马拉的棚车",
                church = "城东佛寺",
                townhall = "县衙大堂",
                gate = "南城门",
                market = "东市",
                street = "朱雀大街",
                notice = "张贴榜文的木牌",
                landmark = "县衙新立的石碑",
                weaponName = "长枪",
                warName = "兵乱",
                npcNames = new[] { "王二", "李顺", "赵大", "陈阿福", "张五", "刘氏", "周文", "孙七" },
                placeNames = new[] { "长安", "洛阳", "汴梁", "临安", "关中", "河东", "江南", "蜀中" },
                styleHint = "ancient chinese town, timber frame, grey tile roof, earthen wall, hanfu",
                endingTone = "史书只留下一行字，而你在那一行字里",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.AncientEurope,
                eraName = "古典时代（古希腊—罗马）",
                eraBrief = "古典时代",
                regionName = "地中海沿岸",
                styleTag = "古典地中海城市：白色石灰岩、柱廊、红陶瓦顶、石板街道",
                ground = "石板与碎石路面，中央排水沟槽清晰可见",
                houseStyle = "白灰泥墙面、红陶瓦坡顶、石砌基座",
                authority = "议事厅",
                crowd = "市民、商贩与神庙侍从",
                soldier = "重装步兵",
                messenger = "传令使",
                priest = "神庙祭司",
                merchant = "粮商",
                noble = "元老院议员",
                commoner = "自由民",
                artisan = "陶匠",
                transport = "骡拉的货车",
                church = "神庙",
                townhall = "议事厅柱廊",
                gate = "城门",
                market = "市集广场",
                street = "石柱大道",
                notice = "刻字的石碑",
                landmark = "还愿石柱",
                weaponName = "长矛",
                warName = "战事",
                npcNames = new[] { "狄奥多罗斯", "克拉提斯", "尼基亚斯", "卢基乌斯", "马库斯", "塞维鲁", "阿波罗尼娅", "菲洛" },
                placeNames = new[] { "雅典", "科林斯", "以弗所", "罗马", "卡普阿", "叙拉古", "迦太基" },
                styleHint = "ancient greek roman city, marble columns, tiled roof, stone street, toga",
                endingTone = "后人把这一段写进了编年史，却删掉了你的名字",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.MedievalEurope,
                eraName = "中世纪欧洲",
                eraBrief = "中世纪",
                regionName = "勃艮第",
                styleTag = "中世纪城镇：半木结构木筋墙、陡坡茅草或石板顶、泥泞街道",
                ground = "夯土与泥泞路面，散落着马蹄印与草料",
                houseStyle = "木筋墙、白灰填充、陡坡瓦顶",
                authority = "领主法庭",
                crowd = "农奴、行会学徒与朝圣者",
                soldier = "领主的骑士",
                messenger = "传令骑士",
                priest = "教区神父",
                merchant = "行会商人",
                noble = "城堡总管",
                commoner = "佃农",
                artisan = "铁匠",
                transport = "四轮牛车",
                church = "圣米迦勒教堂",
                townhall = "市政会堂",
                gate = "有吊闸的城门",
                market = "谷市",
                street = "石匠街",
                notice = "钉在教堂门上的公告",
                landmark = "石砌十字架",
                weaponName = "长戟",
                warName = "战乱",
                npcNames = new[] { "皮埃尔", "让娜", "雅克", "玛格丽特", "托马", "纪尧姆", "阿涅丝", "埃蒂安" },
                placeNames = new[] { "勃艮第", "诺曼底", "香槟", "弗兰德斯", "阿基坦", "图卢兹" },
                styleHint = "medieval european town, half timber house, muddy street, stone church, knight",
                endingTone = "晚钟敲响的时候，没有人记得你的名字",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.RevolutionEurope,
                eraName = "欧洲大革命时期（1789—1794）",
                eraBrief = "大革命",
                regionName = "巴黎",
                styleTag = "18 世纪欧洲城市：灰白石灰岩外墙、三至四层、陡坡石板瓦、铁艺招牌、木百叶窗",
                ground = "夯土与碎石路面，局部是被撬起来垒成路障的鹅卵石",
                houseStyle = "石灰岩外墙、木质百叶窗、陡坡石板瓦",
                authority = "革命委员会",
                crowd = "无套裤汉、洗衣妇与码头工人",
                soldier = "国民自卫军士兵",
                messenger = "传令兵",
                priest = "宣誓派神父",
                merchant = "面包师",
                noble = "前贵族",
                commoner = "市民",
                artisan = "枪匠",
                transport = "两轮出租马车",
                church = "圣雅各教堂",
                townhall = "市政厅",
                gate = "圣但尼城门",
                market = "中央集市",
                street = "圣奥诺雷街",
                notice = "油印传单与告示木牌",
                landmark = "新竖起来的断头台",
                weaponName = "滑膛枪",
                warName = "革命",
                npcNames = new[] { "皮埃尔", "玛丽", "让", "塞西尔", "雅克", "安托万", "玛德琳", "路易" },
                placeNames = new[] { "巴黎", "马赛", "里昂", "凡尔赛", "圣安托万", "巴士底" },
                styleHint = "french revolution paris 1789, limestone facade, cobblestone square, guillotine, tricolor",
                endingTone = "历史记住了这一天，却没有记住你",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.ModernEurope,
                eraName = "近代工业时期（19 世纪）",
                eraBrief = "工业时代",
                regionName = "曼彻斯特",
                styleTag = "19 世纪工业城市：红砖厂房、铁框玻璃窗、高烟囱、煤气路灯、铸铁栏杆",
                ground = "碎石路与煤渣，低洼处积着黑色的水",
                houseStyle = "红砖承重墙、铁框窗、瓦楞铁皮雨棚",
                authority = "市政厅与工厂主",
                crowd = "纺织工人、童工与报童",
                soldier = "警察与志愿兵",
                messenger = "电报局信差",
                priest = "教堂牧师",
                merchant = "棉纺厂主",
                noble = "议员",
                commoner = "工人",
                artisan = "机修工",
                transport = "蒸汽机车与货运马车",
                church = "圣保罗教堂",
                townhall = "市政厅",
                gate = "火车站大门",
                market = "煤市",
                street = "棉纺厂大道",
                notice = "工厂招工告示与报纸",
                landmark = "工厂高烟囱",
                weaponName = "来复枪",
                warName = "罢工与骚动",
                npcNames = new[] { "托马斯", "伊丽莎白", "威廉", "安妮", "乔治", "玛莎", "罗伯特", "简" },
                placeNames = new[] { "曼彻斯特", "伯明翰", "利物浦", "伦敦东区", "格拉斯哥", "里兹" },
                styleHint = "victorian industrial town, red brick factory, chimney, gas lamp, iron railing",
                endingTone = "蒸汽散去，城市继续轰鸣，仿佛什么都没发生",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.ModernChina,
                eraName = "近代中国（1840—1911）",
                eraBrief = "清末民初",
                regionName = "上海",
                styleTag = "清末民初口岸城市：青砖山墙与红砖洋楼混杂、木格窗、铁艺阳台、石板街、石库门",
                ground = "青石板与碎石路面，雨天泥泞，晴天扬尘",
                houseStyle = "青砖山墙、木格窗、局部西式拱券",
                authority = "道台衙门与商会",
                crowd = "苦力、学徒与穿长衫的先生",
                soldier = "巡防营兵丁",
                messenger = "跑街的伙计",
                priest = "教堂牧师",
                merchant = "洋行买办",
                noble = "道台",
                commoner = "码头苦力",
                artisan = "机器局工匠",
                transport = "东洋车与马车",
                church = "天主堂",
                townhall = "道台衙门",
                gate = "城门与水门",
                market = "南市",
                street = "四马路",
                notice = "张贴的告示与《申报》",
                landmark = "石库门牌坊",
                weaponName = "洋枪",
                warName = "教案与兵变",
                npcNames = new[] { "阿宝", "金福", "王先生", "翠莲", "周掌柜", "小六子", "陈先生", "顺子" },
                placeNames = new[] { "上海", "广州", "天津", "汉口", "南京", "苏州" },
                styleHint = "late qing dynasty chinese city, brick lane, wood lattice window, western arcade, changshan",
                endingTone = "旧朝崩塌的声音里，你只听见自己的脚步",
            });

            list.Add(new EraProfile
            {
                era = StoryEra.Unknown,
                eraName = "离线未验证背景",
                eraBrief = "未验证",
                regionName = "未确认地点",
                styleTag = "通用简单几何环境，不代表任何特定历史建筑或文明",
                ground = "平整土地与自然踩踏路面",
                houseStyle = "无特定时代样式",
                authority = "临时议事点",
                crowd = "身份未确认的当地人",
                soldier = "身份未确认的守卫",
                messenger = "当地向导",
                priest = "当地仪式主持者",
                merchant = "流动交换者",
                noble = "当地负责人",
                commoner = "当地居民",
                artisan = "当地工匠",
                transport = "简易运载工具",
                church = "当地集会处",
                townhall = "公共议事处",
                gate = "场地出口",
                market = "临时交换点",
                street = "土路",
                notice = "无文字方向标",
                landmark = "通用方向标",
                weaponName = "简易工具",
                warName = "未知冲突",
                npcNames = new[] { "当地向导", "当地居民", "场地守卫", "当地工匠" },
                placeNames = new string[0],
                styleHint = "generic primitive environment, explicitly no historical claim",
                endingTone = "这段经历尚未形成可验证的历史结论",
            });

            return list.ToArray();
        }
    }
}
