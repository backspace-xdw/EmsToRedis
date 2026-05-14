using System;

namespace EmsToRedis.Logic
{
    /// <summary>
    /// .NET 4.5.2 没有 DateTimeOffset.ToUnixTimeMilliseconds()（4.6 才加入），
    /// 这里手算 unix ms。
    /// </summary>
    internal static class TimeHelper
    {
        private static readonly DateTime UnixEpoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowUnixMs()
        {
            return (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
        }

        /// <summary>Unix ms → 本地时间（日志展示用）。</summary>
        public static DateTime FromUnixMsLocal(long unixMs)
        {
            return UnixEpoch.AddMilliseconds(unixMs).ToLocalTime();
        }
    }
}
