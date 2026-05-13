using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EmsToRedis.Models;
using NLog;

namespace EmsToRedis.Acquisition
{
    /// <summary>
    /// 按既有 EMS 数据采集模式（"基址 + 连续偏移"）读取每辆车的实时数据，
    /// 映射到 VehicleSnapshot，交给 SyncWorker 写入 Redis。
    ///
    /// 高吞吐：使用 GetAxVM 一次批量读 N 个连续 AI，单车 P/Invoke 次数：
    ///   1 次 PdState + 1 次 GetAxVM(n=3, Location) + 16 次 GetAxVM(n=6, Box) = 18 次
    /// （相比单点读的 100 次，5.5× 提速；1300 车单线程 ~300ms 完成一轮）
    ///
    /// 字段对照：
    ///   Location 块（4 字段）：
    ///     locationStatus = GetPointPdstate("{Emspoint}_Location") → "1"/"0"
    ///     baseLoc = GetAiID("{Emspoint}_Location")
    ///     [lng, lat, altitude] = GetAxVM(baseLoc + 1, n=3)
    ///   灭火器块（i ∈ [0,16)，每个 6 字段）：
    ///     baseBox = GetAiID("{Emspoint}_Number_{i}_Start")
    ///     [startType, warningLevel, status, command, customFault, fireAlarmLevel]
    ///         = GetAxVM(baseBox, n=6)
    ///
    /// 单车异常 → VehicleSnapshot.RtError = true（内部标志，不写 Redis），
    /// SyncWorker 跳过本轮该车 upsert，保留 Redis 上轮数据。
    /// 整批异常（车辆列表未加载等）→ 直接抛出，由 SyncWorker / HeartbeatWorker
    /// 联动停发心跳，让 EAP 通过 §6 心跳超期察觉。
    /// </summary>
    public static class VehicleAcquirer
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // 车辆列表（启动时由 Program.cs 调用 SetVehicleList 注入；线程安全：只读快照）
        private static volatile List<IDtoNameClass> _vehicles = new List<IDtoNameClass>();

        public const int BoxCount = 16;
        public const int FieldsPerBox = 6;
        public const int LocationAiCount = 3; // baseLoc+1..+3 = lng/lat/alt

        // 单线程批量读：复用缓冲，避免每车每箱分配 2 个数组
        [ThreadStatic] private static float[] _locValues;
        [ThreadStatic] private static int[] _locStatuses;
        [ThreadStatic] private static float[] _boxValues;
        [ThreadStatic] private static int[] _boxStatuses;

        public static void SetVehicleList(List<IDtoNameClass> vehicles)
        {
            _vehicles = vehicles ?? new List<IDtoNameClass>();
        }

        public static int VehicleCount => _vehicles.Count;

        /// <summary>
        /// 启动时调用一次：把所有车辆所需 tag 的 EMS ID 全部查回缓存，
        /// 第一轮采集即可直接走 GetAxVM 不再触发 GetAxIDVS。
        /// 1300 车 × (1 Location + 16 Box) = 22100 个 tag。
        /// </summary>
        public static void PrewarmIds()
        {
            var snapshot = _vehicles;
            if (snapshot.Count == 0) return;

            var tags = new List<string>(snapshot.Count * (1 + BoxCount));
            foreach (var v in snapshot)
            {
                if (v == null || string.IsNullOrEmpty(v.Emspoint)) continue;
                tags.Add(v.Emspoint + "_Location");
                for (int i = 0; i < BoxCount; i++)
                    tags.Add(v.Emspoint + "_Number_" + i + "_Start");
            }
            Emsplusapi.PrewarmIds(tags);
            Log.Info("ID 缓存预热完成：{0} 个 tag → {1} 项在缓存", tags.Count, Emsplusapi.CachedIdCount);
        }

        public static Task<IList<VehicleSnapshot>> ReadAllAsync(CancellationToken ct)
        {
            var snapshot = _vehicles;
            var result = new List<VehicleSnapshot>(snapshot.Count);

            // 复用本线程缓冲（首次调用时分配，之后零分配）
            if (_locValues == null) { _locValues = new float[LocationAiCount]; _locStatuses = new int[LocationAiCount]; }
            if (_boxValues == null) { _boxValues = new float[FieldsPerBox]; _boxStatuses = new int[FieldsPerBox]; }

            for (int k = 0; k < snapshot.Count; k++)
            {
                if (ct.IsCancellationRequested) break;

                var v = snapshot[k];
                if (v == null || string.IsNullOrEmpty(v.Emspoint) || string.IsNullOrEmpty(v.DeviceID))
                    continue;

                VehicleSnapshot vs;
                try
                {
                    vs = ReadOne(v);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "读取单车失败 deviceId={0} emspoint={1}", v.DeviceID, v.Emspoint);
                    vs = new VehicleSnapshot
                    {
                        DeviceId = v.DeviceID,
                        RtError = true,
                    };
                }
                result.Add(vs);
            }

            return Task.FromResult<IList<VehicleSnapshot>>(result);
        }

        private static VehicleSnapshot ReadOne(IDtoNameClass v)
        {
            // —— Location 块（1 次 PdState + 1 次 GetAxVM(n=3)）——
            int baseLoc = Emsplusapi.GetAiID(v.Emspoint + "_Location");
            bool locOk = Emsplusapi.GetPointPdstate(v.Emspoint + "_Location");
            Emsplusapi.GetIDAiValues(baseLoc + 1, LocationAiCount, _locValues, _locStatuses);
            float lng = _locValues[0];
            float lat = _locValues[1];
            float alt = _locValues[2];

            // —— 灭火器块（每箱 1 次 GetAxVM(n=6)）——
            var boxes = new List<FireExtinguisher>(BoxCount);
            for (int i = 0; i < BoxCount; i++)
            {
                int baseBox = Emsplusapi.GetAiID(v.Emspoint + "_Number_" + i + "_Start");
                Emsplusapi.GetIDAiValues(baseBox, FieldsPerBox, _boxValues, _boxStatuses);
                boxes.Add(new FireExtinguisher
                {
                    BoxNumber = i.ToString(CultureInfo.InvariantCulture),
                    StartType = FloatToStr(_boxValues[0]),
                    WarningLevel = FloatToStr(_boxValues[1]),
                    Status = FloatToStr(_boxValues[2]),
                    Command = FloatToStr(_boxValues[3]),
                    CustomFaultCode = FloatToStr(_boxValues[4]),
                    FireAlarmLevel = FloatToStr(_boxValues[5]),
                    // IsAlarm 不在此处计算，SyncWorker 会用 AlarmRule 统一覆盖
                });
            }

            return new VehicleSnapshot
            {
                DeviceId = v.DeviceID,
                Lng = lng.ToString("F6", CultureInfo.InvariantCulture),
                Lat = lat.ToString("F6", CultureInfo.InvariantCulture),
                Altitude = FloatToStr(alt),
                LocationStatus = locOk ? "1" : "0",
                IsAlarm = false, // 整车 IsAlarm 由 SyncWorker 基于各 box.IsAlarm 派生
                FireExtinguishers = boxes,
            };
        }

        private static string FloatToStr(float v)
        {
            // 整数（含 0）输出无小数点；其余保留 6 位有效数字
            if (Math.Abs(v - (int)v) < 1e-6f)
                return ((int)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("G6", CultureInfo.InvariantCulture);
        }
    }
}
