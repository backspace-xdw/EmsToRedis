using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmsToRedis.Models;

namespace EmsToRedis.Acquisition
{
    /// <summary>
    /// ╔══════════════════════════════════════════════════════════════════╗
    /// ║   ★★★  你只需要改这个文件  ★★★                                  ║
    /// ╚══════════════════════════════════════════════════════════════════╝
    ///
    /// 把你已有的"从 9902 RTDB 读取车辆数据"代码粘贴到 ReadAllAsync 方法里，
    /// 然后逐一映射到 VehicleSnapshot 对象、返回一个 List。
    ///
    /// 其余的事情（diff 检测、Redis 写入、心跳、车辆增删感知、错误处理）
    /// 都由 SyncWorker / HeartbeatWorker / RedisWriter 自动完成。
    ///
    /// ────────────────────────────────────────────────────────────────────
    /// 实现注意：
    /// ────────────────────────────────────────────────────────────────────
    ///   1. 本方法每秒被主循环调用一次（间隔由 appsettings.json 的
    ///      Adapter.PollIntervalMs 控制）。返回应当快速（秒级以内），
    ///      避免阻塞主循环。
    ///
    ///   2. 单车读失败 ≠ 全局失败：
    ///        - 单车读失败 → 把该车 VehicleSnapshot.RtError = true，
    ///          其他字段保留上次的最近值（或填 "0"），照常加入返回列表；
    ///        - 全局读失败（RTDB 整个连不上）→ 直接 throw 异常，
    ///          SyncWorker 会捕获并跳过本轮 + HeartbeatWorker 自动停发心跳，
    ///          让 EAP 通过心跳超期察觉。
    ///
    ///   3. UpdatedAtUnixMs 不需要你填——SyncWorker 在写入 Redis 前
    ///      会统一覆盖为当前时刻。
    ///
    ///   4. FireExtinguisher.IsAlarm 不需要你计算——SyncWorker 会调用
    ///      AlarmRule.IsExtinguisherAlarm 统一覆盖。
    ///      （AlarmRule 当前是占位实现，等拿到 EAP 源码后替换）
    /// </summary>
    public static class VehicleAcquirer
    {
        /// <summary>
        /// 读取当前全部车辆快照。
        /// </summary>
        public static Task<IList<VehicleSnapshot>> ReadAllAsync(CancellationToken ct)
        {
            // ════════════════════════════════════════════════════════════
            //  TODO: 把你的车辆数据读取代码粘贴到这里
            // ════════════════════════════════════════════════════════════
            //
            //  填充模板示例（删掉本段后照葫芦画瓢）：
            //
            //      var result = new List<VehicleSnapshot>();
            //      var rawList = MyExisting9902Reader.ReadAll();   // ← 你的读取
            //
            //      foreach (var raw in rawList)
            //      {
            //          var v = new VehicleSnapshot
            //          {
            //              DeviceId = raw.DeviceId,
            //              Vin = raw.Vin,
            //              PlateNumber = raw.PlateNumber,
            //              Lng = raw.Longitude.ToString("F6"),
            //              Lat = raw.Latitude.ToString("F6"),
            //              Altitude = raw.Altitude.ToString(),
            //              LocationStatus = raw.LocationStatus,
            //              IsAlarm = raw.OverallAlarm,
            //              RtError = false,
            //              FireExtinguishers = raw.Boxes.Select(b => new FireExtinguisher
            //              {
            //                  BoxNumber = b.Index.ToString(),
            //                  StartType = b.Start,
            //                  WarningLevel = b.Fault,
            //                  Status = b.State,
            //                  Command = b.Cmd,
            //                  CustomFaultCode = b.CusFault,
            //                  FireAlarmLevel = b.FireAlarmLevel
            //                  // IsAlarm 不用填,SyncWorker 会统一计算
            //              }).ToList()
            //          };
            //          result.Add(v);
            //      }
            //
            //      return Task.FromResult<IList<VehicleSnapshot>>(result);
            //
            // ════════════════════════════════════════════════════════════

            // 当前未实现，启动后第一轮就会抛异常，提醒填代码
            throw new System.NotImplementedException(
                "请在 EmsToRedis/Acquisition/VehicleAcquirer.cs 中实现 ReadAllAsync");
        }
    }
}
