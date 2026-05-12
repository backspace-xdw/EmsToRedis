namespace EmsToRedis.Redis
{
    /// <summary>
    /// VFSMP 协议 §3 Key 命名常量。
    /// 修改任何 key 前先确认 EAP 同步升级，避免互不兼容。
    /// </summary>
    internal static class VfsmpKeys
    {
        public const string SchemaVersion = "vfsmp:schema:version";
        public const string VehiclesActive = "vfsmp:vehicles:active";
        public const string Heartbeat = "vfsmp:adapter:heartbeat";

        public static string Vehicle(string deviceId) => "vfsmp:vehicle:" + deviceId;
    }
}
