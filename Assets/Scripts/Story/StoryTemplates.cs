// 七个场景模板族的「精确布局文字」。
// 设计文档要求：场景描述必须精确到位置（东西放在哪），流程必须精确到每一步如何推进。
// 因此这里全部使用 60×60 米、X 向东 / Z 向北的坐标系给定确切坐标。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EpicSpark.Core;

namespace EpicSpark.Story
{
    internal static class StoryTemplates
    {
        /// <summary>把坐标格式化成 (x, z)，单位米。</summary>
        public static string Pos(float x, float z)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#})", x, z);
        }

        /// <summary>模板对应的文本块（布局 + 锚点/人物/道具落位 + 本幕焦点）。</summary>
        public static string Layout(StoryTemplate template, EraProfile p, SceneSpec scene, StoryStage stage)
        {
            var sb = new StringBuilder(1600);
            sb.Append("【场景】").Append(scene.sceneName).Append("｜").Append(p.eraName)
              .Append("｜区域：").Append(p.regionName).Append('\n');
            sb.Append("【尺寸】宽 60 米（X 轴 -30…+30）、长 60 米（Z 轴 -30…+30）；单位为米。地面：")
              .Append(p.ground).Append("。\n");
            sb.Append("【坐标系】X 正方向为东、Z 正方向为北；玩家出生点 ")
              .Append(Pos(scene.spawnPoint.x, scene.spawnPoint.z))
              .Append("，离地 1.2 米，面朝正北（+Z）。\n");
            sb.Append("【风格】").Append(p.styleTag).Append("。\n");

            if (p.era == StoryEra.Unknown)
            {
                AppendUnknown(sb, p, scene);
            }
            else
            {
                switch (template)
                {
                    case StoryTemplate.Square: AppendSquare(sb, p, scene); break;
                case StoryTemplate.Country: AppendCountry(sb, p, scene); break;
                case StoryTemplate.Harbor: AppendHarbor(sb, p, scene); break;
                case StoryTemplate.Court: AppendCourt(sb, p, scene); break;
                case StoryTemplate.Battlefield: AppendBattlefield(sb, p, scene); break;
                case StoryTemplate.Church: AppendChurch(sb, p, scene); break;
                    case StoryTemplate.Market: AppendMarket(sb, p, scene); break;
                }
            }

            sb.Append("【本幕焦点】").Append(StageNote(template, p, stage)).Append('\n');
            AppendAnchors(sb, scene);
            AppendNpcs(sb, scene);
            AppendProps(sb, scene);
            return sb.ToString();
        }

        // ─────────────────────────── 七个模板 ───────────────────────────

        private static void AppendUnknown(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【性质】这是离线未验证的通用几何场地，不代表 ").Append(p.regionName)
              .Append(" 或任何具体历史文明。\n");
            sb.Append("【地形】平整自然地面，中央 (0,0) 设置通用方向标；一条简易土路由 (0,-24) 通向 (0,24)，")
              .Append("不得因为模板习惯擅自添加县衙、教堂、城市住宅或其他特定时代建筑。\n");
            sb.Append("【内容】只保留剧情单位、通用道具与可通行的开放空间；具体历史内容需要由可用模型或可靠资料补充。\n");
        }

        private static void AppendSquare(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【道路】东西向主路「").Append(p.street)
              .Append("」宽 6 米，沿 Z=0 从 (-30, 0) 贯通到 (+30, 0)；南北向副路宽 4 米，沿 X=0 从 (0, -30) 到 (0, +30)；")
              .Append("两路在 (0, 0) 十字相交，交口铺 8×8 米石板。\n");
            sb.Append("【北侧建筑】").Append(p.townhall)
              .Append(" 正立面在 Z=+21、面阔 18 米（X -9…+9）、进深 10 米，").Append(p.houseStyle)
              .Append("；门前 5 级石阶由 (0, +16) 通到门槛，屋檐下挂 3 盏灯，主入口朝南。\n");
            sb.Append("【东侧建筑】两栋三层连排住宅，占地 (+16, -14)…(+26, -2) 与 (+16, +2)…(+26, +14)，")
              .Append("首层为铺面、门朝西开向广场，二层为木百叶窗，屋顶为坡顶。\n");
            sb.Append("【南侧建筑】两栋两层民宅，占地 (-26, -24)…(-14, -16) 与 (+14, -24)…(+26, -16)，")
              .Append("中间在 X -4…+4 留出 8 米宽缺口，缺口正对出生点 (0, -24)。\n");
            sb.Append("【西侧】").Append(p.market)
              .Append(" 的遮阳棚沿南北向排开，占 (-26, -10)…(-16, +12)，每棚 4×4 米、布顶高 2.6 米，棚下是石台与木箱。\n");
            sb.Append("【地标】广场正中 (0, 0) 立着").Append(p.landmark)
              .Append("，底座 4×4 米、高 3.2 米，四周有 1 米高石栏，四面各留 2 米宽开口。\n");
            sb.Append("【附属物】水井在 (-12, -6)，井台直径 2.4 米；旗杆在 (+10, +6)，高 8 米，挂三角旗；")
              .Append("碎石路障堆在 (-6, +12)，宽 4 米、高 1.2 米；告示墙在 (+11, -16)，宽 3 米。\n");
            sb.Append("【边界】四周由建筑外墙围合，可以离开的缺口只有南侧 (0, -28) 的").Append(p.gate)
              .Append(" 与东北角 (+24, +24) 的巷口，两处各宽 6 米；其余墙体高 12 米不可翻越。\n");
        }

        private static void AppendCountry(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【道路】一条 3.5 米宽土路，从 (-30, -8) 向东北弯到 (+30, +6)，途经 (0, -4) 与 (+12, +2)；")
              .Append("另有一条 2 米宽田埂从 (0, -4) 向南通到 (-8, -22)。\n");
            sb.Append("【田地】西侧 X -28…-8 为麦田，分 4 垄，垄宽 4 米，垄间田埂宽 0.8 米；")
              .Append("东侧 X +8…+28 为休耕地，草垛在 (+16, +10)，草垛直径 3.5 米高 2.5 米。\n");
            sb.Append("【建筑】农舍在 (-4, -18)，面阔 10 米、进深 7 米、").Append(p.houseStyle)
              .Append("，门朝南；谷仓在 (+18, -18)，12×8 米，木板门朝西；")
              .Append(p.church).Append(" 的小礼拜堂在 (+24, +18)，4×6 米，尖顶高 9 米。\n");
            sb.Append("【附属物】水井在 (-14, -2)，井台直径 2 米；柴堆在 (-20, -14)；")
              .Append("木栅栏沿 (-30, -12) 到 (-30, +20)、以及 (+8, -26) 到 (+28, -26) 两段，高 1.2 米。\n");
            sb.Append("【地标】村口石碑在 (6, -6)，高 2.2 米、宽 1.4 米，碑面朝南，碑前是一小片踩实的空地 (4×4 米)。\n");
            sb.Append("【边界】除土路在 (-30, -8) 与 (+30, +6) 两端的出口外，四周由麦田、栅栏与屋舍围合，不可穿越。\n");
        }

        private static void AppendHarbor(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【水域】Z=+6 以北为水面（蓝灰色、反射天光），石砌护岸沿 Z=+6 东西向贯通，岸顶高 2.5 米、宽 1.5 米；")
              .Append("系缆桩沿护岸每 6 米一根，共 10 根。\n");
            sb.Append("【栈桥】三条木栈桥伸入水面，分别在 X=-12、X=0、X=+12，各宽 4 米、长 14 米（Z=+6 到 Z=+20），")
              .Append("板面高出水面 1.1 米，两侧无护栏。\n");
            sb.Append("【建筑】货栈占 (-24, -6)…(-10, +4)，两层砖石、门朝东；海关哨所占 (+16, -8)…(+26, +2)，单层石屋、门朝西；")
              .Append("鱼市棚占 (+8, -22)…(+22, -14)，铁皮顶高 3 米，棚下是木台与鱼筐。\n");
            sb.Append("【附属物】缆绳架在 (-18, -18)，高 2.5 米；油桶堆在 (+4, -10)，共 5 只；")
              .Append("货箱在 (-8, -12)，共 6 只，最高 1.4 米。\n");
            sb.Append("【地标】灯塔在西侧栈桥尽头 (-12, +18)，圆形石塔、直径 4 米、高 12 米，塔顶有常燃的油灯。\n");
            sb.Append("【边界】南侧 Z=-30 是货栈后墙，西侧 X=-30 是石墙，东侧 X=+30 是木栅；唯一陆路出口是西南角 (-28, -28) 的巷道。\n");
        }

        private static void AppendCourt(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【中轴】石板道宽 5 米，沿 X=0 从 (0, -28) 通到 (0, +8)，路面为整块青石板，两侧为碎石散水。\n");
            sb.Append("【正殿】").Append(p.townhall)
              .Append(" 占 (-14, +8)…(+14, +22)，面阔 28 米、进深 14 米、").Append(p.houseStyle)
              .Append("；6 根廊柱沿 Z=+8 一字排开（X = -12.5, -7.5, -2.5, +2.5, +7.5, +12.5），柱高 7 米、柱距 4.5 米；")
              .Append("正中 5 级台阶由 (0, +4) 通到殿门 (0, +8)，殿门宽 3.6 米。\n");
            sb.Append("【东西厢房】西厢占 (-26, -12)…(-16, +6)，东厢占 (+16, -12)…(+26, +6)，均为两层、")
              .Append("面向中轴开窗，底层各有一道朝内的小门：西厢门在 (-16, -4)，东厢门在 (+16, -4)。\n");
            sb.Append("【附属物】旗杆在 (0, -6)，高 10 米；拴马桩在 (-8, -20)，共 4 根；")
              .Append("告示墙在 (+10, -16)，宽 5 米高 2.4 米；铜钟架在 (-6, +4)。\n");
            sb.Append("【地标】正殿台阶两侧有一对石兽，分别在 (-6, +4) 与 (+6, +4)，连基座高 2.2 米。\n");
            sb.Append("【边界】四周 4 米高墙围合，唯一出口是南侧 (0, -29) 的宫门，门洞宽 6 米、有门槛与门钉。\n");
        }

        private static void AppendBattlefield(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【地形】地面为翻浆的泥地与弹坑，弹坑 6 处，直径 2…4 米、深 0.8 米，分布在 (-20, -6)、(-6, -2)、(+8, -8)、")
              .Append("(+18, +4)、(-14, +6)、(+2, +14)。\n");
            sb.Append("【战壕】折线主战壕宽 1.8 米、深 1.5 米，从 (-28, +8) 经 (-10, +12)、( +12, +10) 到 (+28, +14)；")
              .Append("交通壕宽 1.2 米，从 (0, +10) 向南通到 (-2, -4)。\n");
            sb.Append("【土垒】主战壕北侧堆 1.2 米高土垒，宽 2 米，留出 3 处射击缺口，分别在 (-16, +10)、(+2, +11)、(+22, +12)。\n");
            sb.Append("【阵地】火炮 2 门在 (-8, +6) 与 (+8, +6)，炮口朝北；弹药车在 (+16, +2)，4 轮木车、车斗 2.5×1.6 米；")
              .Append("军帐 3 顶在 (-20, -14)…(-12, -6)，圆帐直径 5 米，入口朝南。\n");
            sb.Append("【后送】担架点在 (+20, -18)；救护帐篷在 (+24, -22)，4×5 米、白布顶；水桶队停在西侧 (-24, -20)。\n");
            sb.Append("【地标】折断的军旗插在 (-2, +2)，旗杆高 6 米、向东南倾斜 30 度，旗面破损但仍可辨认。\n");
            sb.Append("【中心】场地中心 (0, 0) 是一处被踩平的集合点，直径 6 米，传令兵与炮兵在此交接命令；")
              .Append("从出生点 (0, -24) 沿 X=0 向北走 24 米即可抵达。\n");
            sb.Append("【边界】南侧 Z=-30 是土坡与鹿砦，东西两侧为无人区（不可通行），北侧越过土垒即敌人方向（不可通行）。\n");
        }

        private static void AppendChurch(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【教堂主体】").Append(p.church)
              .Append(" 占 (-12, +2)…(+12, +22)，中殿宽 24 米、长 20 米、").Append(p.houseStyle)
              .Append("；正面朝南，正门宽 3 米、高 5 米，位于 (0, +2)，门楣上有石雕（高 5 米处）。\n");
            sb.Append("【钟楼】钟楼贴在西侧 (-14, +6)，方形 4×4 米、高 18 米，钟口朝南，钟绳垂到 (-14, +4) 地面。\n");
            sb.Append("【前庭】教堂前石铺小广场占 (-8, -12)…(+8, +2)，长 14 米宽 16 米，四周 0.8 米高矮石墙；")
              .Append("长椅 3 排横向摆在 (0, -4)、(-0, -7)、(0, -10)，每排长 6 米。\n");
            sb.Append("【墓园】西侧墓园占 (-28, -16)…(-6, 0)，1 米高铁栏围合，墓碑 12 块成排、间距 2.5 米、行距 3 米；")
              .Append("掘墓人的工具棚在 (-24, -18)，2×3 米。\n");
            sb.Append("【附属物】圣水井在 (-2, -8)，井口直径 1.2 米；蜡烛摊在 (+8, -10)，木桌 2×1 米；")
              .Append("施舍箱在 (+4, +1)，铁箱 0.6 米见方。\n");
            sb.Append("【边界】东侧 X=+26 是修道院回廊墙，南侧 (0, -30) 是街口可离开，西南 (-24, -28) 是墓园小门。\n");
        }

        private static void AppendMarket(StringBuilder sb, EraProfile p, SceneSpec scene)
        {
            sb.Append("【街道】主街「").Append(p.street)
              .Append("」宽 8 米，沿 Z=0 从 (-30, 0) 贯通到 (+30, 0)；南北向支巷宽 4 米，位于 X=-8 与 X=+10，")
              .Append("从 Z=-30 通到 Z=+30；三条街在 (0, 0)、(-8, 0)、(+10, 0) 交汇。\n");
            sb.Append("【摊位】北排在 Z=+4，从 X=-24 到 X=+24 共 10 摊，每摊 4×3 米、间隔 1 米，遮阳布顶高 2.6 米；")
              .Append("南排在 Z=-4 同样布局，其中 3 摊为肉案、2 摊为布匹。\n");
            sb.Append("【建筑】西端石拱门在 (-28, 0)，门洞宽 6 米、高 5 米；布庄占 (+18, -10)…(+28, +2)，两层；")
              .Append("钱庄占 (-20, -16)…(-8, -6)，两层，门脸朝东。\n");
            sb.Append("【附属物】水井在 (-2, -12)，井台直径 2.4 米；猪笼在 (+14, -8)，3 只；秤台在 (+6, +8)，石台 2×2 米；")
              .Append("垃圾堆在 (-26, -18)。\n");
            sb.Append("【地标】街心石柱在 (0, 0)，高 4 米、柱径 0.8 米，柱顶有一只铁笼。\n");
            sb.Append("【边界】南北两侧是两层店铺山墙，可离开的只有东西主街两端 (-30, 0) 与 (+30, 0)，以及北支巷尽头 (+10, +30)。\n");
        }

        // ─────────────────────────── 本幕焦点 ───────────────────────────

        private static string StageNote(StoryTemplate template, EraProfile p, StoryStage stage)
        {
            switch (template)
            {
                case StoryTemplate.Square:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "玩家从南侧缺口进入，广场只有零星行人，" + p.landmark + " 在 (0, 0) 显得过分安静。";
                        case StoryStage.Conflict: return "东侧铺面前聚起二十来人，" + p.soldier + " 沿 (+21, 0) 列队，与人群隔着 10 米对峙。";
                        case StoryStage.Turn: return "一队人从西侧 " + p.market + " (-20, 0) 涌向中心，" + p.townhall + " 台阶上同时下来三名" + p.noble + "。";
                        case StoryStage.Climax: return "所有人被压缩到 (0, 0) 周围 8 米内，地标四角石栏被推倒。";
                        default: return "人群沿东西主路退到两端，中心 10 米空地只剩玩家与" + p.landmark + "。";
                    }
                case StoryTemplate.Country:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "玩家沿土路从西南 (-30, -8) 走进村口，麦田里只有一两个" + p.commoner + "在割麦。";
                        case StoryStage.Conflict: return "农舍前 (-4, -14) 聚起争吵的人群，" + p.soldier + " 从东侧谷仓 (+18, -14) 列队而来。";
                        case StoryStage.Turn: return "西南麦田被踩出一条新路，" + p.messenger + " 沿土路从 (+28, +6) 直奔村口石碑 (6, -6)。";
                        case StoryStage.Climax: return "村口石碑 (6, -6) 周围挤满人，谷仓方向传来火把的光。";
                        default: return "人群散回各自屋舍，土路上只剩车辙与一只倒扣的木桶。";
                    }
                case StoryTemplate.Harbor:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "退潮后的码头只有几艘空船，栈桥 (0, +6) 入口处有人在点名。";
                        case StoryStage.Conflict: return "货栈门口 (-17, -1) 与海关哨所 (+21, -3) 之间来回跑着人，护岸上站满了看客。";
                        case StoryStage.Turn: return "一条小船从西侧靠上 (-12, +12) 栈桥，船头跳下三个人。";
                        case StoryStage.Climax: return "所有人挤到中央栈桥 (0, +6)…(0, +20)，水面反射着火把的光。";
                        default: return "水面重新平静，只剩缆绳拍打木桩的声音。";
                    }
                case StoryTemplate.Court:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "前院空旷，只有廊柱下的两名" + p.soldier + "在低声交谈，中轴石板道反射天光。";
                        case StoryStage.Conflict: return "东厢门 (+16, -4) 与西厢门 (-16, -4) 各有三人对峙，正殿殿门 (0, +8) 紧闭。";
                        case StoryStage.Turn: return "殿门 (0, +8) 从里面被推开一半，一名" + p.noble + "独自走到台阶 (0, +4)。";
                        case StoryStage.Climax: return "台阶 (0, +4)…(0, +8) 之间只剩一条两米宽的通道，廊柱间全是人。";
                        default: return "正殿门重新合上，前院只剩被踩乱的碎石与一面倒下的旗。";
                    }
                case StoryTemplate.Battlefield:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "清晨的阵地很静，2 门火炮 (-8, +6) 与 (+8, +6) 还盖着油布，泥地里冒着白气。";
                        case StoryStage.Conflict: return "土垒射击缺口 (+2, +11) 方向开始有零星的" + p.weaponName + "声，军官在 (-2, +2) 挥旗。";
                        case StoryStage.Turn: return "后方辎重 (+16, +2) 起火，军帐 (-16, -10) 里的伤员被抬出来。";
                        case StoryStage.Climax: return "北侧土垒 (-16, +10)…(+22, +12) 全线上人，交通壕 (0, +10) 挤得只能侧身通过。";
                        default: return "阵地安静下来，泥地上散落着断裂的" + p.weaponName + "与一只靴子。";
                    }
                case StoryTemplate.Church:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "前庭只有扫地的人和一名" + p.priest + "，钟楼的钟绳 (-14, +4) 被风吹得轻晃。";
                        case StoryStage.Conflict: return "教堂正门 (0, +2) 前站着一排人，墓园铁栏 (-6, -16) 外有人喊话。";
                        case StoryStage.Turn: return "钟楼钟声突然响起，所有人抬头看向 (-14, +6) 的钟口。";
                        case StoryStage.Climax: return "前庭 (-8, -12)…(+8, +2) 里挤满人，正门 (0, +2) 被从里面顶住。";
                        default: return "钟声停了，前庭只剩一张翻倒的长椅和半截蜡烛。";
                    }
                default:
                    switch (stage)
                    {
                        case StoryStage.Opening: return "开市未久，南排摊位还有一半没支起来，街心石柱 (0, 0) 的影子很长。";
                        case StoryStage.Conflict: return "北排 (-24, +4)…(+24, +4) 摊主开始收摊，西端石拱门 (-28, 0) 有人把守。";
                        case StoryStage.Turn: return "布庄 (+18, -10) 大门被撞开，一匹布滚到主街 (0, -2)。";
                        case StoryStage.Climax: return "主街 (0, 0) 两侧摊位被掀翻，街心石柱周围三米形成空场。";
                        default: return "街道重新开市，只是南排空了一半摊位。";
                    }
            }
        }

        // ─────────────────────────── 落位清单 ───────────────────────────

        private static void AppendAnchors(StringBuilder sb, SceneSpec scene)
        {
            if (scene.anchors == null || scene.anchors.Count == 0) return;
            sb.Append("【锚点载体落位】");
            for (int i = 0; i < scene.anchors.Count; i++)
            {
                SceneAnchorSpec anchor = scene.anchors[i];
                if (i > 0) sb.Append("；");
                sb.Append(anchor.displayName).Append(' ')
                  .Append(Pos(anchor.localPosition.x, anchor.localPosition.z))
                  .Append("（载体要求：").Append(anchor.requirement).Append('）');
            }
            sb.Append("。玩家踏入载体外侧 1.5 米范围即触发。\n");
        }

        private static void AppendNpcs(StringBuilder sb, SceneSpec scene)
        {
            if (scene.npcs == null || scene.npcs.Count == 0) return;
            sb.Append("【人物落位】");
            for (int i = 0; i < scene.npcs.Count; i++)
            {
                NpcSpec npc = scene.npcs[i];
                if (i > 0) sb.Append("；");
                sb.Append(npc.displayName).Append('[').Append(npc.tag).Append("] ")
                  .Append(Pos(npc.localPosition.x, npc.localPosition.z))
                  .Append("，面朝场景中心");
            }
            sb.Append("。\n");
        }

        private static void AppendProps(StringBuilder sb, SceneSpec scene)
        {
            if (scene.props == null || scene.props.Count == 0) return;
            sb.Append("【可互动道具】");
            for (int i = 0; i < scene.props.Count; i++)
            {
                PropInteraction prop = scene.props[i];
                if (i > 0) sb.Append("；");
                sb.Append(prop.displayName).Append('（').Append(prop.interactKind).Append('）');
            }
            sb.Append("。道具没有独立坐标字段，由灰盒 Agent 依据布局描述就近落在同名地物旁。\n");
        }
    }
}
