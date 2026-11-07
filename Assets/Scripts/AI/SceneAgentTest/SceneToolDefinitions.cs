using System.Collections.Generic;
using EpicSpark.AI.OpenAI;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 场景Agent工具定义 (ARCHITECT.md §4.2), 以原生 Function Calling 的 JSON Schema 形式下发给模型。
    /// 注意: 参数名遵循文档拼写, 如锚点相关为 auchor(而非 anchor)。
    /// </summary>
    public static class SceneToolDefinitions
    {
        static ToolDefinition Def(string name, string description, string parametersSchema) =>
            new ToolDefinition
            {
                function = new ToolFunctionInfo { name = name, description = description, parameters = parametersSchema },
            };

        static string NodeArraySchema(int len, string itemDesc) =>
            "{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":" + len +
            ",\"maxItems\":" + len + "},\"description\":\"" + itemDesc + "\"}";

        const string SmoothingProp =
            "\"smoothing_factor\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1,\"description\":\"平滑因子, 越小越平滑\"}";

        static string MountainRiverItem(string nodeDesc) =>
            "{\"type\":\"object\",\"properties\":{\"nodes\":" + NodeArraySchema(4, nodeDesc) + "," + SmoothingProp +
            "},\"required\":[\"nodes\"]}";

        public static readonly ToolDefinition[] All =
        {
            Def("init_terrain",
                "初始化地形。场景生成的第一步必须调用。基础地形目前仅支持平原; 山脉/河流节点会被系统平滑化处理。坐标为平面 x, y。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"base\":{\"type\":\"string\",\"enum\":[\"plain\"],\"description\":\"基础地形, 目前只能选择 plain(平原, 无高差)\"}," +
                "\"mountains\":{\"type\":\"array\",\"items\":" + MountainRiverItem("[x, y, height, width] 山脉隆起节点") +
                ",\"description\":\"山脉列表, 可省略\"}," +
                "\"rivers\":{\"type\":\"array\",\"items\":" + MountainRiverItem("[x, y, depth, width] 河流下切节点") +
                ",\"description\":\"河流列表, 可省略\"}" +
                "},\"required\":[\"base\"]}"),

            Def("place_model",
                "放置模型。高度坐标由系统自动计算(以刚好不穿透地形为原则)。模型必须先用 search_model 从缓存模型库中选择。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"model_id\":{\"type\":\"string\",\"description\":\"模型ID, 来自 search_model 返回结果\"}," +
                "\"x\":{\"type\":\"number\",\"description\":\"平面X坐标(0 到 场景width)\"}," +
                "\"y\":{\"type\":\"number\",\"description\":\"平面Y坐标(0 到 场景length)\"}," +
                "\"placed_name\":{\"type\":\"string\",\"description\":\"放置实例名称, 场景内唯一, 后续 set_auchor_point 绑定锚点时使用\"}," +
                "\"size_x\":{\"type\":\"number\",\"exclusiveMinimum\":0,\"description\":\"X轴大小\"}," +
                "\"size_y\":{\"type\":\"number\",\"exclusiveMinimum\":0,\"description\":\"Y轴大小\"}" +
                "},\"required\":[\"model_id\",\"x\",\"y\",\"placed_name\",\"size_x\",\"size_y\"]}"),

            Def("place_bar_shaped_object",
                "放置条状物体(墙/栅栏/道路/战壕)。高度坐标由系统自动计算。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"type\":{\"type\":\"string\",\"enum\":[\"wall\",\"fence\",\"road\",\"trench\"],\"description\":\"wall=墙(3m), fence=栅栏(1.5m), road=道路, trench=战壕(真实下切1.8m深的沟)\"}," +
                "\"nodes\":{\"type\":\"array\",\"minItems\":2,\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3}," +
                "\"description\":\"节点列表 [x, y, width], 至少2个, 场景会平滑化处理\"}" +
                "},\"required\":[\"type\",\"nodes\"]}"),

            Def("set_auchor_point",
                "设置传送锚点, 将剧情Agent提供的传送锚点绑定到一个已放置的模型实例(如马车、床、建筑等)。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"auchor_name\":{\"type\":\"string\",\"description\":\"锚点名称, 必须来自剧情Agent提供的传送锚点列表\"}," +
                "\"placed_name\":{\"type\":\"string\",\"description\":\"锚点绑定的模型实例名, 必须已通过 place_model 放置\"}" +
                "},\"required\":[\"auchor_name\",\"placed_name\"]}"),

            Def("set_start_point",
                "设置玩家起始点。高度坐标由系统自动计算。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"x\":{\"type\":\"number\",\"description\":\"平面X坐标(0 到 场景width)\"}," +
                "\"y\":{\"type\":\"number\",\"description\":\"平面Y坐标(0 到 场景length)\"}" +
                "},\"required\":[\"x\",\"y\"]}"),

            Def("search_model",
                "在缓存模型库中搜索模型。空关键词返回全部模型列表(最多20条)。结果包含模型ID、名称和生成提示词, 供 place_model 使用。",
                "{\"type\":\"object\",\"properties\":{" +
                "\"search_word\":{\"type\":\"string\",\"description\":\"搜索关键词, 可为空(返回全部)\"}" +
                "},\"required\":[\"search_word\"]}"),

            Def("finish_scene",
                "完成场景生成。在所有场景元素放置完成后调用; 若锚点未绑定齐、未设置起始点等问题, 会返回错误, 修正后需重新调用。",
                "{\"type\":\"object\",\"properties\":{}}"),
        };
    }
}
