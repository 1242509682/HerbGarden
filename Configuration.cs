using Newtonsoft.Json;
using static Plugin.HerbGarden;

namespace Plugin;

internal class Configuration
{
    #region 配置项成员
    [JsonProperty("插件开关", Order = 1)]
    public bool Enabled { get; set; } = true;
    [JsonProperty("生长概率分母", Order = 2)]
    public int RandomRate { get; set; } = 3;
    [JsonProperty("草药进箱范围", Order = 3)]
    public int HopperRange { get; set; } = 32;
    [JsonProperty("生长最小冷却", Order = 4)]
    public int MinCd { get; set; } = 300;
    [JsonProperty("生长最大冷却", Order = 5)]
    public int MaxCd { get; set; } = 900;
    [JsonProperty("收割延迟帧数", Order = 6)]
    public int DelayFrames { get; set; } = 60; // 默认1秒
    [JsonProperty("插件使用说明", Order = 7)]
    public string Text { get; set; } = "第一次种植需手动放种子到种植盆,给箱子连上电线与计时器,即可实现自动化种植";
    #endregion

    #region 预设参数方法
    public void SetDefault()
    {

    }
    #endregion

    #region 读取与创建配置文件方法
    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(Paths, json);
    }
    public static Configuration Read()
    {
        if (!File.Exists(Paths))
        {
            var NewConfig = new Configuration();
            NewConfig.SetDefault();
            NewConfig.Write();
            return NewConfig;
        }
        else
        {
            string jsonContent = File.ReadAllText(Paths);
            var config = JsonConvert.DeserializeObject<Configuration>(jsonContent)!;
            return config;
        }
    }
    #endregion
}