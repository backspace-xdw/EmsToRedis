# EmsToRedis — VFSMP 9902 同机适配器

按 **VFSMP Redis 共享层协议 v1** 把 9902 RTDB 的车辆数据写入本机 Redis，供 EAP 订阅。

- **平台**：.NET Framework 4.5.2 / x86 / Windows
- **角色**：协议中的"9902 同机适配器"（写入端）
- **不负责**：EAP 端读取、Redis 安装、历史告警入库

---

## 1. 目录结构

```
EmsToRedis/
├── EmsToRedis.csproj
├── App.config
├── appsettings.json          配置（Redis 连接、间隔、版本）
├── NLog.config               日志配置
├── Program.cs                入口
├── README.md
├── Acquisition/
│   └── VehicleAcquirer.cs    ★ 你唯一需要改的文件
├── Configuration/
│   └── AdapterConfig.cs
├── Models/
│   ├── VehicleSnapshot.cs    单车快照对象
│   └── FireExtinguisher.cs   灭火器子结构
├── Logic/
│   ├── AlarmRule.cs          灭火器告警判定（占位，等 EAP 源码替换）
│   └── DiffTracker.cs        内存 diff
├── Redis/
│   ├── VfsmpKeys.cs          协议 §3 Key 常量
│   └── RedisWriter.cs        所有 Redis 写操作（HSET 多字段 / SADD / SREM / 心跳）
└── Workers/
    ├── SyncWorker.cs         1s 主循环
    └── HeartbeatWorker.cs    3s 心跳
```

---

## 2. 编译

需要 **.NET Framework 4.5.2 开发者包** 已装。

Windows 命令行：
```cmd
cd EmsToRedis
dotnet restore
dotnet build -c Release
```

或用 Visual Studio 2017+ 直接打开 `EmsToRedis.csproj` 编译。

产物在 `bin\Release\` 下，包含：
- `EmsToRedis.exe`
- `EmsToRedis.exe.config`
- `appsettings.json`、`NLog.config`
- `StackExchange.Redis.dll`、`Newtonsoft.Json.dll`、`NLog.dll`

---

## 3. 你需要做的事

**只改 `Acquisition/VehicleAcquirer.cs` 一个文件**，把你已有的"从 9902 RTDB 读取车辆"的代码粘贴进去，逐字段映射到 `VehicleSnapshot`，返回 `IList<VehicleSnapshot>`。

模板见 `VehicleAcquirer.cs` 内的注释。

不需要管：
- diff 检测、Redis 写入顺序、HSET 原子性 → `RedisWriter` 已封装
- 心跳 → `HeartbeatWorker` 后台自动跑
- 车辆增删（SADD/SREM `vehicles:active`）→ `SyncWorker` 自动维护
- `schema:version` 写入 → 启动时自动
- `updatedAt` 时间戳 → 写入前自动填
- 冷启动全量重写 → 第一轮自动走全量分支

---

## 4. 配置

`appsettings.json`：

```json
{
  "Redis": {
    "ConnectionString": "127.0.0.1:6379,password=请改我,abortConnect=false"
  },
  "Adapter": {
    "SchemaVersion": "1",
    "PollIntervalMs": 1000,
    "HeartbeatIntervalMs": 3000,
    "HeartbeatTtlSeconds": 10
  }
}
```

- `password=` 改为运维给你的实际密码
- `PollIntervalMs` 建议保持 1000（协议 §8.2）
- `HeartbeatIntervalMs / HeartbeatTtlSeconds` 必须满足 `Ttl > Interval × 3`，避免抖动误判

---

## 5. 运行

```cmd
EmsToRedis.exe
```

指定配置文件路径：
```cmd
EmsToRedis.exe -c D:\config\appsettings.json
```

Ctrl+C 优雅退出（两个 Worker 都会收到 cancel 信号）。

---

## 6. 联调验证清单（协议 §12）

```cmd
# 1. Redis 可达
redis-cli -h 127.0.0.1 -p 6379 -a <密码> PING
# → PONG

# 2. keyspace notification 已开
redis-cli -a <密码> CONFIG GET notify-keyspace-events
# → 应包含 K, h, s

# 3. 启动 EmsToRedis 后

# 3.1 schema 版本
redis-cli -a <密码> GET vfsmp:schema:version
# → "1"

# 3.2 车辆数量
redis-cli -a <密码> SCARD vfsmp:vehicles:active
# → 应等于实际车辆数

# 3.3 单车数据
redis-cli -a <密码> HGETALL vfsmp:vehicle:<某车ID>
# → 协议 §4 全部字段齐全

# 3.4 心跳
redis-cli -a <密码> GET vfsmp:adapter:heartbeat
# → 当前时间戳，10s 内会自动刷新

# 3.5 关掉 EmsToRedis 10s 后
redis-cli -a <密码> GET vfsmp:adapter:heartbeat
# → (nil)   ← EAP 据此报警

# 3.6 重启 EmsToRedis
# → SCARD 立即恢复，无需 EAP 重启
```

---

## 7. 日志

- 控制台：`Info` 及以上
- 文件：`logs\EmsToRedis-yyyyMMdd.log`，保留 30 天，单文件 10 MB 滚动

主要事件：
- 启动 / 退出
- schema:version 写入
- 冷启动全量重写
- 车辆增删（INFO）
- 每轮 diff 写入数（DEBUG，仅文件）
- Redis 异常（WARN/ERROR）
- 数据源异常（ERROR）

---

## 8. 部署成 Windows 服务（可选）

用 [NSSM](https://nssm.cc/) 包成服务：

```cmd
nssm install EmsToRedis "C:\Vfsmp\EmsToRedis.exe"
nssm set EmsToRedis AppDirectory "C:\Vfsmp"
nssm set EmsToRedis Start SERVICE_AUTO_START
nssm set EmsToRedis AppStdout "C:\Vfsmp\logs\stdout.log"
nssm set EmsToRedis AppStderr "C:\Vfsmp\logs\stderr.log"
nssm start EmsToRedis
```

---

## 9. 后续待办

| 项 | 来源 | 状态 |
|---|---|---|
| AlarmRule.IsExtinguisherAlarm 真实逻辑 | EAP 团队给 C# 源码 | 占位中（返回 false） |
| VehicleAcquirer.ReadAllAsync 真实实现 | 你这边粘贴 9902 读取代码 | 占位中（抛 NotImplementedException） |

---

## 10. 协议对照表

| 协议条目 | 实现位置 |
|---|---|
| §2 schema:version | `RedisWriter.EnsureSchemaVersionAsync` |
| §4 HASH 字段 | `VehicleSnapshot` + `RedisWriter.UpsertVehicleAsync` |
| §4.1 fireExtinguishers JSON | `JsonConvert.SerializeObject` |
| §5 vehicles:active SADD/SREM | `RedisWriter.EnsureActiveAsync / RemoveVehicleAsync` |
| §6 心跳 + EX | `RedisWriter.WriteHeartbeatAsync` |
| §7 单车原子 / 顺序 | `HashEntry[]` 单调用 + `RedisWriter` 顺序封装 |
| §8.2 1s 轮询 | `SyncWorker` |
| §8.3 diff | `DiffTracker` |
| §8.4 灭火器告警 | `AlarmRule`（占位） |
| §8.5 冷启动全量 | `SyncWorker._coldStartDone` 分支 |
| §8.6 错误处理 | `SyncWorker.IsHealthy` 联动 `HeartbeatWorker` |
| §8.7 心跳 3s | `HeartbeatWorker` |
| §8.8 车辆增删感知 | `SyncWorker.DoOneCycleAsync` 内 `currentIds` vs `activeNow` |
