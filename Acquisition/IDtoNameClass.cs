namespace EmsToRedis.Acquisition
{
    /// <summary>
    /// 单车在 9902 端的标识映射，对应 DeviceToEmspoint.ini 一行。
    ///   Emspoint  ── 9902 RTDB 测点前缀（如 "Device001"），用于拼 "_Location" / "_Number_N_Start"
    ///   DeviceID  ── 协议 §4 Redis HASH key 中的 {deviceId}，全平台唯一码（如 4G 模块号）
    /// 协议 §4 的 vin / plateNumber 字段在本环境不接入，统一写空串。
    /// </summary>
    public class IDtoNameClass
    {
        public string Emspoint { get; set; } = "";
        public string DeviceID { get; set; } = "";
    }
}
