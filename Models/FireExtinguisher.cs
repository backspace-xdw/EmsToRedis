using Newtonsoft.Json;

namespace EmsToRedis.Models
{
    /// <summary>
    /// 灭火器实时数据。字段命名严格对齐 VFSMP 协议 §4.1。
    /// 序列化到 Redis 的 fireExtinguishers 字段时使用 JSON 形式。
    /// </summary>
    public class FireExtinguisher
    {
        [JsonProperty("boxNumber")]
        public string BoxNumber { get; set; } = "0";

        [JsonProperty("startType")]
        public string StartType { get; set; } = "0";

        [JsonProperty("warningLevel")]
        public string WarningLevel { get; set; } = "0";

        [JsonProperty("status")]
        public string Status { get; set; } = "0";

        [JsonProperty("command")]
        public string Command { get; set; } = "0";

        [JsonProperty("customFaultCode")]
        public string CustomFaultCode { get; set; } = "0";

        [JsonProperty("fireAlarmLevel")]
        public string FireAlarmLevel { get; set; } = "0";

        /// <summary>
        /// 由 AlarmRule.IsExtinguisherAlarm 计算得出；
        /// 当前 AlarmRule 为占位实现，恒返回 false，等拿到 EAP 源码后替换。
        /// </summary>
        [JsonProperty("isAlarm")]
        public bool IsAlarm { get; set; }
    }
}
