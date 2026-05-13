using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EmsToRedis.Acquisition
{
    /// <summary>
    /// EMS Plus 实时库 (EosDapi.dll) 包装。
    /// 与既有项目 GetFileToEmsplus.EmsApi 的对外方法一致：
    ///   GetAiID(tag)                         —— 取 AI 测点 ID
    ///   GetIDAiValue(id)                     —— 按 ID 读单点 AI 值
    ///   GetIDAiValues(baseId, count, ...)    —— 按基址 + 连续 n 个 AI 值（GetAxVM）★ 高吞吐
    ///   GetPointPdstate(tag)                 —— 取点状态（bit3 判定坏点/超时）
    ///   PrewarmIds(tags)                     —— 启动时一次性查 N 个 tag 的 ID 进缓存
    ///
    /// 平台：Windows x86，EosDapi.dll 安装于 C:\Windows\SysWOW64\EosDapi.dll。
    /// 1300 车场景：每车用 GetAxVM 后只需 18 次 P/Invoke（vs 单读 100 次）。
    /// </summary>
    public static class Emsplusapi
    {
        private const string DllPath = @"C:\Windows\SysWOW64\EosDapi.dll";
        private const int DefaultSrvNo = 0;
        private const int GoodStatus = 0x80;

        // —— P/Invoke（签名严格对齐既有 EmsApi.cs）——
        [DllImport(DllPath, EntryPoint = "GetAxIDVS")]
        private static extern int GetAxIDVS(int dwSrvNo, string szTagName);

        [DllImport(DllPath, EntryPoint = "GetAxVS")]
        private static extern int GetAxVS(int dwAxID, ref float pfValue, ref int pdwStatus);

        [DllImport(DllPath, EntryPoint = "GetAxVM")]
        private static extern int GetAxVM(int dwAxID, float[] pfValue, int[] pdwStatus, int n);

        // —— ID 缓存。GetAxIDVS 是按 tag 名查 ID，缓存避免每秒重复查 N 次 ——
        private static readonly Dictionary<string, int> _idCache = new Dictionary<string, int>();
        private static readonly object _lock = new object();

        /// <summary>
        /// 取 AI（模拟量）测点 ID。
        /// </summary>
        public static int GetAiID(string pointName)
        {
            if (string.IsNullOrEmpty(pointName)) return 0;
            lock (_lock)
            {
                int id;
                if (!_idCache.TryGetValue(pointName, out id))
                {
                    id = GetAxIDVS(DefaultSrvNo, pointName);
                    _idCache[pointName] = id;
                }
                return id;
            }
        }

        /// <summary>
        /// 按 ID 读取单点 AI 值。
        /// </summary>
        public static float GetIDAiValue(int id)
        {
            float v = 0;
            int status = GoodStatus;
            GetAxVS(id, ref v, ref status);
            return v;
        }

        /// <summary>
        /// 按基址 + 数量批量读取连续 AI 值（GetAxVM）。
        /// 调用方提供长度 >= count 的复用缓冲，避免每次分配；返回值数量 = count。
        /// </summary>
        /// <param name="baseId">起始 AI ID</param>
        /// <param name="count">连续读取的点数</param>
        /// <param name="valueBuffer">浮点结果缓冲（长度 >= count）</param>
        /// <param name="statusBuffer">状态字缓冲（长度 >= count）</param>
        public static void GetIDAiValues(int baseId, int count, float[] valueBuffer, int[] statusBuffer)
        {
            if (count <= 0) return;
            if (valueBuffer == null || valueBuffer.Length < count)
                throw new ArgumentException("valueBuffer 长度不足", nameof(valueBuffer));
            if (statusBuffer == null || statusBuffer.Length < count)
                throw new ArgumentException("statusBuffer 长度不足", nameof(statusBuffer));
            GetAxVM(baseId, valueBuffer, statusBuffer, count);
        }

        /// <summary>
        /// 读取点状态：true=好点，false=坏点/超时。
        /// 判定规则与既有 EmsApi.GetPointPdstate 一致：pdstate 第 3 位置 1 视为坏点/超时。
        /// </summary>
        public static bool GetPointPdstate(string pointName)
        {
            if (string.IsNullOrEmpty(pointName)) return false;
            float v = 0;
            int pdstate = 0;
            GetAxVS(GetAiID(pointName), ref v, ref pdstate);
            const int badBit = 1 << 3;
            return (pdstate & badBit) != badBit;
        }

        /// <summary>
        /// 预热：一次性把 tags 列表里的 ID 查回缓存。
        /// 用于启动时分摊 1300 车 × 17 tag = 22000+ 次 GetAxIDVS，避免第一轮拖慢。
        /// </summary>
        public static void PrewarmIds(IEnumerable<string> tags)
        {
            if (tags == null) return;
            foreach (var tag in tags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                GetAiID(tag); // 副作用：写入缓存
            }
        }

        /// <summary>当前缓存的 tag → ID 数量（运维查看用）</summary>
        public static int CachedIdCount
        {
            get { lock (_lock) return _idCache.Count; }
        }

        /// <summary>
        /// 清空 ID 缓存。若运行中 EMS 端点位重建，调用一次让下次读重新查 ID。
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock) { _idCache.Clear(); }
        }
    }
}
