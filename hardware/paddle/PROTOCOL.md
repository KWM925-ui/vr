# 船桨硬件接口 v1：DBPF

本文件定义本次交接后要共同实现的 **MCU／设备网关 ↔ 电脑硬件适配器** 接口。随仓库提供离线编解码器、样包和检查程序；**现有 Unity 展示包尚不消费 DBPF，也没有随仓库交付真实电机固件**。电脑端的接入任务见 [交接主文档](README.md#电脑端适配必须完成的工作)。

这不是 `DBCL` 模拟控制器协议，不是 52 字节 `DBEP` 模拟编码器包，也不是 UDP 9870／9871 的旧 48 字节诊断协议。协议、原始设备厂商协议和电脑内部数据类型各有边界。

## 1. 通信与字节格式

第一阶段使用 ESP32-S3 **原生 USB CDC**，由板上 USB Hub 接出。协议流与调试文字分开：原生 CDC 只传 DBPF，UART0／CH340K 留作下载与调试。若已有可用固件或上位机，保留其底层设备协议，由设备网关转换成 DBPF；不要为统一外壳协议重写已经可靠工作的电机驱动。

USB CDC 是字节流。一次 `read()` 可能只有半个包，也可能包含多个包；不得假设 USB 传输块就是协议帧。CDC 的波特率设置不等于原生 USB 实际吞吐率。

包头 **24 字节**，小端序；后接 UTF-8 JSON 对象及 4 字节 CRC32：

| 偏移 | 长度 | 类型 | 含义 |
|---:|---:|---|---|
| 0 | 4 | 原始字节 | ASCII `DBPF`，即 `44 42 50 46` |
| 4 | 2 | u16 LE | 版本，目前为 `1` |
| 6 | 2 | u16 LE | `1`＝telemetry，`2`＝command |
| 8 | 4 | u32 LE | JSON 的**字节数**，1～16384；不含头和 CRC |
| 12 | 4 | u32 LE | 此方向的帧序号 `sequence` |
| 16 | 8 | u64 LE | 设备本次启动的 `bootId`，非零，每次重启重新生成 |
| 24 | N | UTF-8 | JSON 对象；无 BOM，无额外换行要求 |
| 24+N | 4 | u32 LE | CRC32，覆盖包头和 JSON 的原始字节 |

CRC 使用 CRC-32/ISO-HDLC：反射多项式 `0xEDB88320`，初值和最终异或均为 `0xFFFFFFFF`，与 `zlib.crc32(data)` 一致。标准串 `123456789` 的结果为 `0xCBF43926`。不要把 CRC16、STM32 默认未反射 CRC 或 JSON 对象重新序列化后的校验值代入。

发送端可自行安排 JSON 字段顺序和空白，CRC 按**实际发送字节**计算。接收端先校验长度和 CRC，再解析 JSON。JSON 禁止重复键、NaN、Infinity；v1 不接受未知字段。以后扩展字段时双方同时升级版本，不能偷偷改变单位或数组顺序。

推荐接收状态机：搜索 4 字节帧头 → 收满包头 → 校验版本／类型／长度 → 等待完整负载及 CRC → 校验并消费 → 继续处理缓冲剩余字节。长度越界、CRC 失败或收包中断时丢弃该帧并重新同步；不能刷新命令有效期。缓冲区设固定上限。仓库 `check` 命令检查的是保存后的完整原始流，会直接报告截断，不替代固件的实时重同步状态机。

序号按 u32 回绕：`0 < uint32(new-old) < 0x80000000` 才是更新。设备重启用新 `bootId`；电脑重启控制会话用新 `hostSessionId`。跨启动身份的旧命令不可接受。

## 2. telemetry：设备上行

完整 JSON 示例在 [examples/telemetry.json](examples/telemetry.json)。示例明确为模拟、未标定、未使能；它用于对字节，不能当实机记录。

### 帧级字段

| 字段 | 类型／单位 | 必须表达的真实含义 |
|---|---|---|
| `deviceId` | 1～64 个 ASCII 字母、数字、`_-.` | 固定设备身份，与现场配置一致 |
| `deviceTimeUs` | u64，μs | 设备单调时钟的组帧时刻；同一启动持续增加 |
| `simulated` | bool | 真设备采集为 false；测试替身为 true |
| `state` | 字符串 | `BOOT / DISARMED / ARMED / FAULT / ESTOP` |
| `faults` | 字符串数组 | 活动故障码，无故障为 `[]`；如 `ADC_U7_TIMEOUT`、`DRIVE_M2_FAULT` |
| `configurationId` | SHA-256 小写十六进制或 null | 当前核准配置的身份；未完成配置时 null，禁止使能 |
| `appliedHostSessionId` | 16 位小写十六进制或 null | 最近实际接受命令所属的电脑控制会话 |
| `appliedCommandSequence` | u32 或 null | 最近实际接受命令的包头序号；与上项同时有值或同时 null |
| `motorEnabled` | 3×bool | M1、M2、M3 的实际使能状态，不能回显“希望使能” |
| `targetTensionN` | 3×数值，N | MCU 当前有效的张力设定值；与实际测得张力分开 |
| `adcs` | 2 个对象 | U7、U8 的实际 MODE／CONFIG 寄存器读回 |
| `forces` | 4 个对象 | 固定顺序 CH1、CH2、CH3、CH4；未接通道也保留槽位 |
| `encoders` | 3 个对象 | 固定顺序 M1、M2、M3；这里是交接逻辑编号，不是 CAD 实例尾号 |
| `imu` | 1 个对象 | 姿态传感器的一次真实输出及其采样时间 |

非 ARMED 状态必须撤销三路目标和使能。实际绳张力可能因机械储能、制动或摩擦没有立即归零，仍按实际测量上报；不得把测量清零来表示停止。ARMED 必须有有效配置、无活动故障。紧急停止的机械制动策略由驱动和机构共同确认，详见主文档。

每个采样对象都有以下字段：

| 字段 | 规则 |
|---|---|
| `sampleSequence` | 新的实际采样完成后才递增；尚未采到为 null |
| `sampleTimeUs` | 该采样发生的设备单调时刻；与 sampleSequence 同时存在或同时 null，不能晚于 deviceTimeUs |
| `valid` | 此次采样及设备状态是否有效。有效样本必须有真实序号／采样时间 |

同一旧样本重复装入多个上行帧时，必须保留原序号、原时间和原始值。不得每次发包给旧样本盖新时间。`valid=true` 也不等于新鲜；消费者还要按 sampleTimeUs 判断年龄。传感器掉线或新采样失败时，可以保留最后一次采样身份及原始值，把 valid 从 true 撤销为 false，并将 forceN／payoutM／driveCurrentA 等可用值撤销为 null；同时报告故障。恢复为 valid=true 必须有新的实际采样，不能复活旧样本。同一 bootId 内已经采到过的通道不能重新清空采样身份为 null，以此隐藏传感器复位。

### `adcs` 与 `forces`

`adcs` 中每个对象恰好有 `adcId`（U7/U8）、`mode`（u24 或 null）、`config`（u24 或 null）。两个寄存器同时有值或同时 null。它们是读回值，不是源代码里想写入的值。重配 ADC 后，在新转换完成前使旧转换失效，避免用新增益解释旧 DATA。

`forces` 对象恰好有：

| 字段 | 类型／含义 |
|---|---|
| `channelId` | CH1～CH4 |
| `sampleSequence / sampleTimeUs / valid` | 同上 |
| `saturated` | bool，ADC 到码轨或传感器过载不能作为有效力值 |
| `rawCode` | 0～16777215 的整数或 null；AD7190 DATA 的 24 位无符号码，**不是补码** |
| `status` | u8 或 null；必须与 DATA 来自同一次转换，按 DAT_STA 读取 |
| `forceN` | 有标定时为带符号力值 N；未标定、失效或饱和为 null |
| `calibrationId` | 对应标定文件的 SHA-256 或 null |

CH1/CH2 来自 U7 的 AIN1-AIN2 / AIN3-AIN4，CH3/CH4 来自 U8 的同两组输入。STATUS 的通道码必须匹配；不能只按收包行号奇偶猜通道。检查器会利用随仓库的 AD7190 寄存器解释验证这些条件。

有 DATA 但无力标定时，上传 rawCode、status 和有效的采样身份，forceN 留 null。不要把 ADC 码、毫伏、电机电流或请求张力放进 forceN。CH4 尚未分配功能，不得默认加进三绳驱动或当中心测量绳的驱动力。

### `encoders`

每个对象恰好有 `channelId`、`sampleSequence`、`sampleTimeUs`、`valid`、`rawCount`、`payoutM`、`calibrationId`、`driveCurrentA`。

`rawCount` 是**带符号 int64 的十进制字符串**，例如 `"-123456"`。这样可避免 C JSON 库或 JavaScript 把大整数转成浮点后丢位。禁止 `"01"`、`"-0"`、小数及越界值。尚未取得为 null。

`payoutM` 是校准零点后的累计放绳量，单位 m，**不是末级导轮到桨上拴点的自由直线段长度**；允许相对零点为负，实际行程由本机已核准限值限制。未完成计数比例、正方向和卷绕校准时为 null。`driveCurrentA` 仅填驱动器实际回读电流，不可填电流目标；没有回读为 null。

### `imu`

恰好有 `sampleSequence`、`sampleTimeUs`、`valid`、`quaternionXyzw`、`gyroRadS`、`accelerometerMps2`、`frameId`。

- 四元数顺序固定 xyzw；有效姿态时必须存在且范数误差不超过 0.001。它表示传感器原生参考系中的姿态，安装变换由标定文件说明。
- `gyroRadS` 为传感器坐标中的角速度 rad/s；不能直接填 deg/s。
- `accelerometerMps2` 为传感器原始加速度计量 m/s²，通常包含重力对应的比力分量；不把它直接当桨叶平动加速度。
- 没有对应输出时填 null。`frameId` 固定标识传感器原生坐标定义，轴方向、左右手系和零姿态必须另附实测说明。

CAD 中外壳的轴向不是电子敏感轴标定。若 IMU 由另一个 MCU 采样，先建立两设备时钟映射，并保留原时间及映射依据；不得把串口转发时刻冒充 IMU 采样时刻。需要扩展原时间字段时升级协议，而不是塞进无定义字段。

## 3. command：电脑下行

完整 stop 样例见 [examples/stop.json](examples/stop.json)。包头 bootId 必须是设备当前启动身份；包头 sequence 在同一 hostSessionId 内递增。

| 字段 | 类型／规则 |
|---|---|
| `deviceId` | 指定目标设备，必须精确一致 |
| `hostSessionId` | 非空的 16 位小写十六进制；电脑每次控制会话重新生成 |
| `action` | `stop / arm / tension` |
| `configurationId` | 当前核准配置的 SHA-256；arm/tension 必须与 MCU 本地一致 |
| `sourceSequence` | 本命令计算所用 telemetry **包头**序号；stop 为 null |
| `sourceDeviceTimeUs` | 对应 telemetry 的 deviceTimeUs 原值；stop 为 null |
| `ttlUs` | arm/tension 为 1～100000 μs；stop 固定 0 |
| `tensionsN` | M1～M3 目标张力 N；stop/arm 必须全 0；tension 必须非负且服从本机限值 |

MCU 不接受电脑通过数据包提高电流、张力、速度、行程或变化率上限。本机限值来自已核准配置；v1 命令里故意没有这些修改字段。`5 N / 300 N` 是现有软件仿真边界，不是这台实物获准的上下限。

### 必须在 MCU 实现的使能和时效规则

下面是**固件实现要求**。离线编解码通过，只证明字节和数据结构，不能代替这些执行检查。

1. 上电／复位进入 BOOT，输出关闭；自检后进入 DISARMED。重新枚举 USB、电脑重启或收到新会话不能自动使能。
2. arm 只在 DISARMED、实物通道和标定完整、驱动就绪、无急停／故障、现场人工使能成立时接受。记住 hostSessionId，确认有效配置；arm 本身不施加预紧。
3. tension 仅对已使能的同一会话接受。校验 CRC、版本、目标设备、bootId、配置身份、序号、来源 telemetry 和所有相关采样的新鲜度，然后按本机限值限制输出。三路编号不得交换。
4. MCU 保留最近至少覆盖 100 ms 的**已发送 telemetry 身份记录**。收到命令时 sourceSequence 和 sourceDeviceTimeUs 必须精确匹配其中一条；来源缺失、未来时间或超龄就拒绝。
5. 全部时效判断使用 MCU 的单调墙钟。设接收时刻为 `rx_us`，来源帧组帧时刻为 `source_us`，包中寿命为 `ttl_us`：命令到期时刻不晚于 `min(rx_us + ttl_us, source_us + ttl_us)`。另外逐一检查控制实际使用的 force／encoder／IMU 采样年龄；来源帧刚发出但内部数据已陈旧，同样不能加力。
6. 重复包、乱序包、CRC 错包、未知来源或无效采样**不能续命**。接收线程之外的周期任务检查到期，主机停顿时也必须撤销目标和使能，并退出 ARMED，按原因进入 DISARMED 或 FAULT。恢复通信后需重新现场确认并 arm，不能自动恢复旧张力。检查任务目标周期 1 ms，实际延迟必须测量；100 ms 是命令寿命上限，不是传感器和各链路可以分别耗尽的多份预算。
7. stop 不依赖仍然存在的采样来源，在设备身份和包校验通过后立即进入停止流程；重复 stop 应保持停止。退出 ARMED、故障或急停后需现场确认、重新 arm，不能因来了一包非零张力自动恢复。
8. 撤销目标后仍回报实际张力、实际驱动状态及故障。继电器、制动器和 STO（若有）的动作按驱动器手册与机构承载条件实现；不能仅把反馈数组清零。

`appliedCommandSequence` 只回报真正通过检查并由控制任务接受的命令，不能回报每个收到的包。被拒绝包计数和具体原因写调试日志；活动故障在 faults 上报。

## 4. 采样、坐标和 Unity 的边界

MCU 首先负责可靠的真实采样、单电机张力控制和本地失效保护。水面、水动力、船体、三绳目标分配由电脑负责。电脑使用已标定的绳路、桨上拴点、姿态安装及原始计数求真实桨的位置和运动，再生成 Unity `PaddleSample`。

Unity 输入要求见 `SimulationContracts.cs`：桨叶参考点的**世界坐标 m**、xyzw 四元数、世界线速度 m/s 和角速度 rad/s。`timeSeconds` 是本地仿真时间；`deviceTimeUs` 保留设备来源。导数应先由设备采样时间算好，不能用 Unity 补帧的间隔重复差分。

三条已校准自由直线段长度加已知姿态可能仍有两个位置解，参见 `tools/cable_pose.py`。原始电机计数、累计放绳量、自由段长度和六维位姿不是同一个量。一只拉线盒加 IMU 也不能凭空提供完整三维位置。真实布局、初始化分支和必要的独立位置观测由联调共同确定。

## 5. 在电脑上对字节和记录

在仓库根目录运行；仅需 Python 3.11+ 标准库：

```powershell
python.exe -m unittest discover -s tools -p test_paddle_hardware.py
python.exe tools/paddle_hardware.py check hardware/paddle/examples/telemetry.bin --allow-simulated
python.exe tools/paddle_hardware.py inspect hardware/paddle/examples/stop.bin
```

固件工程师用 [golden_vectors.json](examples/golden_vectors.json) 的包头、JSON 和完整十六进制帧对照自己的 C/C++ 编解码器。样例中的 bootId、deviceId、配置和 ADC 数据全部只是字节测试值。

串口终端或已有采集程序把原生 CDC 收到的二进制原流保存为 `telemetry.bin` 后：

```powershell
python.exe tools/paddle_hardware.py check telemetry.bin
python.exe tools/paddle_hardware.py inspect telemetry.bin --output telemetry.decoded.jsonl
```

真机记录不加 `--allow-simulated`。检查会拒绝包截断、CRC 错误、重复／乱序帧、同一启动中的时间倒退、旧采样被重打时间戳、ADC 通道错配、无标定的 forceN 等。输出 `hardware_acceptance=false` 是刻意保留的：时钟误差、实际采样频率、延迟、控制响应、急停电路和人体体验要按主文档在实物上验证。

USB 抓取程序还须记录电脑 `monotonic_ns` 接收时刻、发送原始命令、串口连接／断开事件。DBPF 流本身没有电脑接收时间，不能仅凭包内设备时间计算端到端延迟。
