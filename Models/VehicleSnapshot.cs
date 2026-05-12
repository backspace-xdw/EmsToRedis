using System.Collections.Generic;

namespace EmsToRedis.Models
{
    /// <summary>
    /// 单车完整快照。字段对齐 VFSMP 协议 §4。
    /// 由 VehicleAcquirer 填充后交给 SyncWorker 写入 Redis。
    ///
    /// 注意：
    ///   * 所有字符串字段默认空串（避免 null 出现在 Redis HSET 中）；
    ///   * UpdatedAtUnixMs 由 SyncWorker 在写入前统一覆盖为当前时刻，不需要 Acquirer 填；
    ///   * IsAlarm / RtError 是 bool 类型，由 RedisWriter 序列化为 "1"/"0"。
    /// </summary>
    public class VehicleSnapshot
    {
        /// <summary>设备 ID（必填），与 Redis key 中 {deviceId} 一致</summary>
        public string DeviceId { get; set; } = "";

        /// <summary>车架号（必填）</summary>
        public string Vin { get; set; } = "";

        /// <summary>车牌号</summary>
        public string PlateNumber { get; set; } = "";

        /// <summary>经度（字符串，缺省或 "0" 视为无定位）</summary>
        public string Lng { get; set; } = "0";

        /// <summary>纬度</summary>
        public string Lat { get; set; } = "0";

        /// <summary>海拔</summary>
        public string Altitude { get; set; } = "0";

        /// <summary>定位状态（来源同 9902 RTDB Location tag）</summary>
        public string LocationStatus { get; set; } = "0";

        /// <summary>整车告警状态（覆盖式，仅反映"当前是否告警"）</summary>
        public bool IsAlarm { get; set; }

        /// <summary>RTDB 通信异常标志（true → Redis 字段 rtError="1"）</summary>
        public bool RtError { get; set; }

        /// <summary>灭火器数组（每辆车 N 个）</summary>
        public List<FireExtinguisher> FireExtinguishers { get; set; } = new List<FireExtinguisher>();

        /// <summary>
        /// 数据时间戳（unix ms）。
        /// SyncWorker 在写 Redis 前会统一覆盖为 DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()，
        /// Acquirer 无需填写。
        /// </summary>
        public long UpdatedAtUnixMs { get; set; }
    }
}
