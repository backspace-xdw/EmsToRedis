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

## 3. 数据源接入说明

`VehicleAcquirer.ReadAllAsync` 已按既有 EMS 采集程序的"基址 + emsid 递增"模式实现，直接复用 `Emsplusapi`（P/Invoke `C:\Windows\System32\EosDapi.dll`）。

### 3.1 车辆列表
`DeviceToEmspoint.ini`（与 `EmsToRedis.exe` 同目录，编码 GB2312/GBK），每行一辆车 2 列：

```ini
Emspoint,DeviceID
Device001,4G2501080001
Device002,4G2501080002
```

**Redis HSET 字段**（精简到 8 项，协议中 `vin / plateNumber / rtError` 在本环境不启用）：

| 字段 | 来源 |
| --- | --- |
| `deviceId` | ini 第 2 列 |
| `lng` / `lat` / `altitude` | `_Location` 基址 +1/+2/+3 |
| `locationStatus` | `GetPointPdstate("_Location")` ⇒ "1"/"0" |
| `isAlarm` | SyncWorker 派生：任一灭火器 `isAlarm=true` 即整车告警 |
| `fireExtinguishers` | 16 个灭火器 JSON 数组，每个 `isAlarm` 由 `AlarmRule` 算 |
| `updatedAt` | 写入 Redis 的时刻（unix ms） |

**单车采集失败**：Acquirer 标记内部 `RtError=true`，SyncWorker 跳过本轮 upsert，**保留 Redis 上轮数据**（不写零值覆盖好数据，也不把该车从 active 移除）。

### 3.2 测点命名约定
| Redis 字段 | 来源 |
| --- | --- |
| `locationStatus` | `GetPointPdstate("{Emspoint}_Location")` → 1/0 |
| `lng` | `GetIDAiValue(GetAiID("{Emspoint}_Location") + 1)` |
| `lat` | `GetIDAiValue(GetAiID("{Emspoint}_Location") + 2)` |
| `altitude` | `GetIDAiValue(GetAiID("{Emspoint}_Location") + 3)` |
| 灭火器 `i` × 6 字段 | `GetAiID("{Emspoint}_Number_{i}_Start") + 0..5` 顺序读 |

每辆车固定 16 个灭火器，6 个字段：startType / warningLevel / status / command / customFaultCode / fireAlarmLevel。

### 3.3 EosDapi.dll
`Acquisition/Emsplusapi.cs` 与既有 `GetFileToEmsplus.EmsApi` 对齐：
- 路径 `C:\Windows\SysWOW64\EosDapi.dll`（x86 进程在 64 位 Windows 上的标准位置）
- `GetAxIDVS(srvNo, tagName)` —— 取 AI 测点 ID
- `GetAxVS(axId, ref value, ref status)` —— 读 AI 值 + 状态
- `GetPointPdstate(tag)` —— **C# 包装**：调 `GetAxVS`，检查 `status` 第 3 位（`1<<3` 为坏点/超时位）

若 EosDapi.dll 导出名或路径不同，**只改 `Emsplusapi.cs` 一个文件**，上层 `VehicleAcquirer` 不必动。

### 3.4 SyncWorker 接手以下事项
- diff 检测、Redis 写入顺序、HSET 原子性 → `RedisWriter`
- 心跳 → `HeartbeatWorker`
- 车辆增删（SADD/SREM `vehicles:active`）→ `SyncWorker`
- `schema:version`、`updatedAt`、冷启动全量重写 → 自动
- 整车 `IsAlarm` 派生（任一灭火器告警即整车告警）→ `SyncWorker`

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
- `HeartbeatIntervalMs / HeartbeatTtlSeconds` 必须满足 `Ttl > Interval × 3`，避免抖动误判

#### `PollIntervalMs` 按车数选档

| 车辆规模 | 推荐 `PollIntervalMs` | 单轮预估耗时 | 备注 |
| --- | --- | --- | --- |
| ≤ 200 车 | 1000 | < 50 ms | 协议 §8.2 推荐值 |
| ≤ 1300 车 | 1000 ~ 2000 | ~ 300 ms | 默认配置覆盖（GetAxVM 批量读） |
| ≥ 3000 车 | 2000 ~ 3000 | ~ 700 ms | 需考虑分片或并行采集 |

下限 100 ms（`AdapterConfig.Validate` 强制）。运行时观察 SyncWorker 输出：
```
cycle: 车 1300 | 读 234ms 计 28ms 写 31ms | upsert 8 ...
```
若 `(读+计+写) > PollIntervalMs` 持续出现 `WARN 本轮耗时 N ms 超过周期`，把 `PollIntervalMs` 上调即可，**不需要改代码**。

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
| AlarmRule.IsExtinguisherAlarm | EAP 给口径 | ✅ 已落地：`startType ∈ [2..7] ∪ {255}` / `warningLevel ∈ [1..4]` / `status ∈ [1..3]` / `customFaultCode ∈ {1,2}` / `fireAlarmLevel > 0` 任一命中即告警 |
| EosDapi.dll 路径与导出名 | 现场机器 | 已对齐 `GetFileToEmsplus.EmsApi`：`SysWOW64\EosDapi.dll`，导出 `GetAxIDVS / GetAxVS` |
| `vin / plateNumber / rtError` 字段 | 现场决定 | ✅ 不写 HSET（DeviceID 为唯一码；RtError 仅作内部跳过 upsert 的开关） |

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
