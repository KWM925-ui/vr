# DragonBoatVR 工程说明

接真实船桨请先读 [嵌入式交接手册](../hardware/paddle/README.md)。本文说明桌面演示与 Unity 开发入口；模拟控制器、旧 UDP 姿态示例和新的 DBPF 硬件接口有不同用途。

## 打开与运行

Unity Hub 添加本目录，使用 **Unity 2022.3.62f3**、URP 14。安装 Windows CPython，使 `python.exe` 在 PATH；编辑器中的独立软件控制器默认调用它。等待资源导入和脚本编译完成，打开 `Assets/Scenes/Demo.unity` 或 `Assets/Scenes/DragonBoatOrnament.unity` 后 Play。已有场景沿用当前保存配置，不用重新运行旧初始化向导。

默认 `PaddleDriver.Mode.VirtualPaddler` 会自动启动独立 UDP 模拟控制器。实际模拟回包进入水动力和船体，再产生设备目标及下一次响应。无需另起 mock；回放只作内部诊断。

完整本地发布包存在时，根目录 [启动龙舟VR.cmd](../启动龙舟VR.cmd) 以 `-presentation` 开启中文首页；[CAD 机构入口](../启动CAD机构龙舟VR.cmd) 另加 `-cad-tendon-rig`，仍是未实测的 CAD 设计配置。Git 不包含 `Builds`，新克隆须用编辑器或自行构建候选。

## 展示操作

| 操作 | 行为 |
|---|---|
| 首页／设置 | 选经典木船或雕饰龙舟、两种河景、水况；切船重新建立体验 |
| 左侧船内窗 | 右键转头，WASD／方向键移动，R 回划桨位；视点受船体边界约束，不推动船 |
| 右侧船外窗 | 左键环绕，滚轮调整距离；跟随同一船体 |
| F2 | 河景与体验设置；舒适模式默认关闭 |
| F8 | 暂停／恢复模拟划桨意图，船和波浪仍继续运动 |
| F10／F11 | 结束／重新定位并开始体验 |
| F1／F9 | 航行读数／完整诊断 |
| H／操作说明 | 移动方法、VR 追踪说明和运行数据 |
| 演示导览 | 七步展示常用功能，按钮委托现有域组件执行 |

产品只有船内、船外两视窗，三绳和平台装置不在产品画面显示。桌面演示失焦时只停止相机输入，仿真继续；**真实电机适配器必须另外实现失焦等生命周期停机**，不能照搬此模拟行为。

完整界面功能与 C# 调用入口见 [展示与功能接口](../展示与功能接口.md)。本地最近一次发布为 `20261101T195259`；其历史软件检查见 [软件验收说明](../软件全链路闭环验收.md)。真实 PICO、传感器、电机、平台和人体体验未验收，三绳 FIDELITY 仍 FAIL。

## 代码入口

| 文件／目录 | 责任 |
|---|---|
| `Assets/Scripts/SimulationSession.cs` | 采样、物理、设备和记录的编排，体验生命周期 |
| `Assets/Scripts/SimulationContracts.cs` | PaddleSample、力反馈请求、设备反馈与适配器接口 |
| `Assets/Scripts/PaddleDriver.cs` | 桨输入模式、有效性与真实 External 入口 |
| `Assets/Scripts/PaddleHydrodynamics.cs` | 桨的水动力及握把力矩 |
| `Assets/Scripts/UdpClosedLoopClient.cs` | 独立软件控制器的启动、协议与时效检查；拒绝实测配置 |
| `Assets/StreamingAssets/closed_loop_controller.py` | 软件控制器的执行与观测模型 |
| `Assets/Scripts/DemoExperienceController.cs` | 展示界面共用的功能命令 |
| `Assets/StreamingAssets` | 当前软件几何和装置配置；measured=false 的内容不是实物标定 |

旧 48 字节姿态协议仍可用于明确选择外部 UDP 模式时的诊断，发送示例在 `tools/legacy/mock_hardware.py`。它没有三电机命令与本机保护合同，不用于硬件力控；新的固件和电脑接入按 [DBPF](../hardware/paddle/PROTOCOL.md) 实现。

## 构建与验证

在**仓库根目录**执行。先关闭占用同一工程的 Unity／Player，设置实际编辑器路径：

```powershell
$env:UNITY_EDITOR_PATH = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe'
python.exe tools/build_closed_loop_player.py --out Builds/DragonBoatVR-Candidate
```

构建会将所调用的本机 CPython 标准库随包部署，完成后核对版本。候选包自带控制器运行时；运行时应保留整个输出目录。

按改动范围选择检查，不把下面全部命令当每次文档修改都要执行的流程：

```powershell
python.exe tools/run_all_checks.py
python.exe tools/run_runtime_check.py
python.exe tools/run_paddle_demo.py
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe --cad-tendon-rig
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe --background-check --host-stall-check
```

前三条是编辑器范围的对应检查；后三条检查指定候选包、CAD 配置和失焦／主机暂停恢复。检查结果写入 `tools` 下相应的本地结果目录。保持原身份、时效、力／行程和退出门槛，不以改阈值取得 PASS。参数与修改约束见 [三方会话统一约定](../三方会话统一约定.md)，后续用户决定与当前硬件接入范围优先。

## 故障定位

| 现象 | 先查 |
|---|---|
| 默认模拟桨不动 | 控制器是否启动、实际 Python 路径、配置读取和反馈是否过期 |
| 外部 UDP 显示 Alive=False | 是否明确选择该模式、发送器是否存在、端口及包格式是否一致 |
| 构建包控制器启动失败 | 随包 `controller_runtime` 是否完整，查看 Player 日志里的原始原因 |
| 画面粉色 | URP 包、Graphics／Quality 管线资产和场景引用 |
| 船持续异常抖动、沉底或飞出 | 源码／配置是否匹配、浮力采样和非预期碰撞；不要先重调锁定波谱 |
| 真设备接不上 | 先按硬件手册检查 DBPF；当前演示包未实现该串口适配器 |
