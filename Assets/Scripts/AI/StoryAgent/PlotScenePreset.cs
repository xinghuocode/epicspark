using System.Collections.Generic;

namespace EpicSpark.AI.StoryAgent
{
    /// <summary>预置传送锚点(结构对应 ARCHITECT §4.1 transmission_auchor_points 元素)。</summary>
    public class PlotAnchorPreset
    {
        public string name;
        public string description;
        public string requirement;
        public string spendTime;
    }

    /// <summary>
    /// 预置场景(ARCHITECT §4 剧情Agent 的"可选项目录")。
    /// 剧情Agent 在未接入 generate_scene 工具前, 只能从该目录中选择场景,
    /// 并为其补充当前历史事件的描述/要求/锚点选择, 输出 generate_scene 的完整参数。
    /// </summary>
    public class PlotScenePreset
    {
        public string name;
        public string description;
        /// <summary>该预置场景支持的传送锚点(剧情Agent可全选或选子集)。</summary>
        public List<PlotAnchorPreset> anchors = new List<PlotAnchorPreset>();
        public int defaultWidth = 100;
        public int defaultLength = 100;
        /// <summary>场景要求模板(剧情Agent应结合具体事件改写)。</summary>
        public string requirementHints;
    }

    /// <summary>内置预置场景目录(与缓存模型库 43 个 GLB 的能力匹配)。</summary>
    public static class PlotScenePresets
    {
        public static readonly List<PlotScenePreset> All = new List<PlotScenePreset>
        {
            new PlotScenePreset
            {
                name = "中世纪村庄广场",
                description = "欧洲中世纪风格的村庄开放广场。有酒馆、民居、水井、手推车、木栅栏、碎石道路," +
                              "适合表现古代市井、集会和冲突前的人群聚集。",
                anchors = new List<PlotAnchorPreset>
                {
                    new PlotAnchorPreset { name = "酒馆内部", description = "进入酒馆内部", requirement = "必须是酒馆建筑, 能实现室内外切换", spendTime = "0min" },
                    new PlotAnchorPreset { name = "乘马车离开", description = "乘坐马车前往远方", requirement = "必须是马车", spendTime = "30min" },
                },
                requirementHints = "中世纪欧洲村庄; 需要酒馆、民居3-5栋、水井、手推车、木栅栏、街灯; 碎石道路; 氛围符合事件背景。",
            },
            new PlotScenePreset
            {
                name = "王国要塞城门",
                description = "中世纪城堡要塞的城门外景。有城堡主楼、瞭望塔、城墙、拱门、旗帜," +
                              "适合表现攻城、守卫、谈判等冲突场景。",
                anchors = new List<PlotAnchorPreset>
                {
                    new PlotAnchorPreset { name = "进入城堡", description = "穿过拱门进入城堡", requirement = "必须是拱门或城门建筑", spendTime = "5min" },
                    new PlotAnchorPreset { name = "撤离要塞", description = "乘马车撤离", requirement = "必须是马车", spendTime = "20min" },
                },
                defaultWidth = 120,
                defaultLength = 120,
                requirementHints = "中世纪要塞; 需要城堡主楼、瞭望塔、城墙段、拱门、旗帜; 地面为土石; 气势威严。",
            },
            new PlotScenePreset
            {
                name = "野外营地",
                description = "郊野中的临时营地。有营火、马车、木桶、推车、岩石和树木点缀," +
                              "适合表现行军、密谋、暴风雨前的宁静。",
                anchors = new List<PlotAnchorPreset>
                {
                    new PlotAnchorPreset { name = "启程出发", description = "乘马车启程", requirement = "必须是马车", spendTime = "1h" },
                    new PlotAnchorPreset { name = "就地休整", description = "在营地休整(时间跳转)", requirement = "营火", spendTime = "8h" },
                },
                requirementHints = "野外营地; 需要营火、马车、木桶、推车; 夜间用火把/蜡烛照明; 氛围紧张或肃杀。",
            },
            new PlotScenePreset
            {
                name = "集市街道",
                description = "古代城镇的热闹集市街道。有招牌、木桌、木桶、麻袋、街灯、价格告示," +
                              "适合表现民生、物价、流言传播等社会背景场景。",
                anchors = new List<PlotAnchorPreset>
                {
                    new PlotAnchorPreset { name = "酒馆内部", description = "进入酒馆内部", requirement = "必须是酒馆建筑", spendTime = "0min" },
                    new PlotAnchorPreset { name = "出城", description = "乘马车出城", requirement = "必须是马车", spendTime = "15min" },
                },
                requirementHints = "古代城镇集市; 需要招牌、木桌、木桶、麻袋、街灯、价格告示; 道路宽敞; 人群熙攘感。",
            },
        };

        /// <summary>生成系统提示词中的预置场景目录文本。</summary>
        public static string BuildCatalogText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("你可使用的预置场景目录(只能从中选择一个):");
            foreach (var p in All)
            {
                sb.AppendLine($"- 名称: {p.name}");
                sb.AppendLine($"  简介: {p.description}");
                sb.AppendLine($"  建议尺寸: {p.defaultWidth} x {p.defaultLength}");
                sb.AppendLine($"  场景要求参考: {p.requirementHints}");
                sb.AppendLine("  支持的传送锚点:");
                foreach (var a in p.anchors)
                    sb.AppendLine($"    * {a.name} | {a.description} | 要求: {a.requirement} | 时间: {a.spendTime}");
            }
            return sb.ToString();
        }

        public static PlotScenePreset Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in All)
                if (p.name == name.Trim()) return p;
            return null;
        }
    }
}
