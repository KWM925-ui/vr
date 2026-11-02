# DragonBoatVR

单人龙舟桌面仿真与 VR／力反馈设备接入工程。当前开始接三绳船桨装置，**嵌入式人员先看 [船桨力反馈交接手册](hardware/paddle/README.md)**：从板卡接线、只读采集、标定、单电机台架到三绳联调，包含分工、操作步骤和交付要求。

## 当前能做什么

Unity 已有船体、水动力、三绳目标分配、平台目标和声画；独立 Python 控制器提供模拟执行响应，形成桌面软件闭环。展示界面包含中文首页、双船选择、双视窗、导览、设置和运行数据。

本地最近一次正式桌面发布为 `20261101T195259`。历史验收范围见 [软件闭环验收说明](软件全链路闭环验收.md)；Git 仓库提供源码和资源，**不包含 `Builds/DragonBoatVR` 运行包**。

真实传感器、电机、平台、PICO 4 Ultra 和人体体验尚未验收，三绳反馈保真仍为 **FIDELITY FAIL**。最终体验要求自由持桨、仅边界限位；CAD 是设计依据，不能代替实际绳路、传感器标定或驱动限值。新的 DBPF 协议已有说明、样包和离线检查器，真实固件与 Unity 硬件适配器按交接手册实现。

## 项目入口

| 入口 | 用途 |
|---|---|
| [hardware/paddle](hardware/paddle/README.md) | 本轮嵌入式交接、DBPF 协议、原理图、AD7190 手册、配置模板及样包 |
| [DragonBoatVR](DragonBoatVR/README.md) | Unity 工程，含 Assets、Packages、ProjectSettings 和开发运行说明 |
| [10月11日修改](10月11日修改) | 船桨机构原生 SolidWorks 装配与零件，保留原名及引用关系 |
| [展示与功能接口](展示与功能接口.md) | 演示操作、统一 C# 功能接口和设备接入边界 |
| [数据流规格](资料汇总/数据流全链路规格.md) | 软件采样、水动力、目标、执行和反馈的现有实现 |
| [PICO 接入手册](VR头显接入手册-Pico4Ultra.md) | 后续头显、追踪与平台补偿接入 |
| [tools](tools) | 当前构建、诊断、协议和几何分析工具；旧 48 字节 UDP 示例在 `tools/legacy` |
| [docs/references](docs/references) | 已有龙舟／划船相关参考论文；[龙舟摆件.SLDPRT](龙舟摆件.SLDPRT) 为另一船型原件 |

## 嵌入式先对接口

在仓库根目录运行，仅需 Python 3.11+ 标准库：

```powershell
python.exe -m unittest discover -s tools -p test_paddle_hardware.py
python.exe -m unittest discover -s tools -p test_adc_replay.py
python.exe tools/paddle_hardware.py check hardware/paddle/examples/telemetry.bin --allow-simulated
```

这些命令不打开串口或驱动电机。后续接线和实物记录按照 [交接手册](hardware/paddle/README.md) 执行，不把模拟样包或结构检查通过当作硬件验收。

## 从源码运行桌面演示

1. 克隆仓库，在 Unity Hub 添加 `DragonBoatVR` 目录，使用 **Unity 2022.3.62f3**、URP 14；构建 Windows 时需要对应 Windows Build Support。
2. 安装 Windows CPython，确保 `python.exe` 在 PATH。独立控制器使用标准库；本地历史发布使用 CPython 3.13.14，换版本后应验证自己的候选包。
3. 打开已保存的 `Assets/Scenes/Demo.unity` 或 `Assets/Scenes/DragonBoatOrnament.unity`，等待导入完成后 Play。现有场景不需要重新执行初始化向导。

编辑器使用模拟输入，不需要另起 mock。中文展示首页在 Player 的 `-presentation` 模式开启，详细操作见 [工程说明](DragonBoatVR/README.md)。

已有完整本地发布包时，双击 [启动龙舟VR.cmd](启动龙舟VR.cmd) 或 [CAD 机构入口](启动CAD机构龙舟VR.cmd)。对外提供运行包需复制整个 `Builds/DragonBoatVR`，不能只复制 EXE；克隆仓库后没有该包，应从源码运行或构建候选。

## 构建与检查

在仓库根目录设置实际 Unity 路径，构建到独立候选目录：

```powershell
$env:UNITY_EDITOR_PATH = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe'
python.exe tools/build_closed_loop_player.py --out Builds/DragonBoatVR-Candidate
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe --cad-tendon-rig
```

这些命令会实际运行 Unity／Player，工程须排他使用。构建工具把调用它的 CPython 标准库随包部署；构建成功和某一项检查通过不等于完整运行或硬件验收。按 [软件验收说明](软件全链路闭环验收.md) 选择与改动对应的检查。

可选的 CAD／记录分析使用 [requirements-analysis.txt](requirements-analysis.txt)；交接对包不需要这些依赖。CAD 原始提取数据已收拢到 `hardware/paddle/cad`，工具按当前克隆目录解析原生文件，不依赖原开发机 F 盘。分析输出选择新的 `_local` 子目录，不能覆盖已确认的实物配置。

## 版本管理

提交源码、Unity 资源和 `.meta`、包配置、必要工具、原生 CAD、硬件资料与文档。Unity 缓存、运行包、录制／日志、历史恢复副本和个人工具状态保留在本地，由 `.gitignore` 排除；`tmp` 仅提交目录占位文件。

旧试算和过时入口已退出当前交付，本地恢复副本在 `_local/handoff_cleanup_20261102`。原始失败证据、历史正式包继续保留在原本的本地目录，不作为新电脑接手的依赖。

`.gitattributes` 保留原始字节和换行；不要自动转换 CAD、资源或已绑定来源散列的输入。仓库沿用 [Apache-2.0](LICENSE)，第三方代码、模型、纹理、音频和文献保留各自来源与许可，根许可证不替代它们。
