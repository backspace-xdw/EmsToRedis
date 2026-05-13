using System.Collections.Generic;

namespace EmsToRedis.Models
{
    /// <summary>
    /// 单车完整快照。字段对齐 VFSMP 协议 §4 实际启用的 HSET 字段：
    ///   deviceId / lng / lat / altitude / locationStatus / isAlarm / fireExtinguishers / updatedAt
    ///
    /// 注意：
    ///   * 所有字符串字段默认 "0"，避免 null 出现在 Redis HSET 中；
    ///   * UpdatedAtUnixMs 由 SyncWorker 在写入前统一覆盖为当前时刻，不需要 Acquirer 填；
    ///   * IsAlarm 由 SyncWorker 按"任一灭火器告警 ⇒ 整车告警"派生，覆盖 Acquirer 传入值；
    ///   * RtError 是**内部标志**，不写 Redis：单车读失败时 Acquirer 置 true，
    ///     SyncWorker 看到后跳过本轮该车的 upsert，保留上轮 Redis 数据不动。
    /// </summary>
    public class VehicleSnapshot
    {
        /// <summary>设备 ID（必填），与 Redis key 中 {deviceId} 一致</summary>
        public string DeviceId { get; set; } = "";

        /// <summary>经度（字符串，缺省或 "0" 视为无定位）</summary>
        public string Lng { get; set; } = "0";

        /// <summary>纬度</summary>
        public string Lat { get; set; } = "0";

        /// <summary>海拔</summary>
        public string Altitude { get; set; } = "0";

        /// <summary>定位状态（来源 9902 RTDB Location tag），"1"=好点 "0"=坏点</summary>
        public string LocationStatus { get; set; } = "0";

        /// <summary>整车告警状态（由 SyncWorker 基于各灭火器 IsAlarm 派生）</summary>
        public bool IsAlarm { get; set; }

        /// <summary>灭火器数组（每辆车 N 个）</summary>
        public List<FireExtinguisher> FireExtinguishers { get; set; } = new List<FireExtinguisher>();

        /// <summary>
        /// 数据时间戳（unix ms）。
        /// SyncWorker 在写 Redis 前会统一覆盖为 DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()。
        /// </summary>
        public long UpdatedAtUnixMs { get; set; }

        /// <summary>
        /// 内部标志：单车采集异常。不写 Redis；SyncWorker 据此跳过该车的 upsert，
        /// 让上轮 Redis 数据保持不变，避免把坏数据覆盖好数据。
        /// </summary>
        public bool RtError { get; set; }
    }
}
