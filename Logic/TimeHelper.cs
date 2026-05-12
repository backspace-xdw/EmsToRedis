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
    }
}
