# DragonBoatVR

单人龙舟桌面仿真与后续 VR／力反馈设备接入工程。Unity 负责水动力、船体、三绳分配、六缸平台目标及声画；独立 Python 控制器模拟执行响应，并通过 UDP 返回实际编码器观测，形成可记录的软件闭环。

**当前状态**

本地最近已发布的桌面基线为 `20261031T053308`（2026-10-31）。该版本的默认机构与 CAD 理想限位入口既有软件运行检查均为 14/14，双船补验已完成。三绳反馈保真仍为 **FIDELITY FAIL**；真实传感器、电机、机械限位、平台、PICO 4 Ultra 及人体体验尚未验收。CAD 配置是设计输入，不能解释成实测标定。

首次 Git 导入保存当前工作区，其中包含正式基线之后的头姿、相机及渲染改动。它是开发快照，后续改动的完整验收状态应以对应版本的实际检查为准；本次 Git 导入没有重新执行 Unity 编译或整包运行验收。

**目录**

| 路径 | 内容 |
|---|---|
| [DragonBoatVR](DragonBoatVR) | Unity 工程：Assets、Packages、ProjectSettings 及原工程说明 |
| [tools](tools) | 构建、协议核验、软件检查与诊断工具；fixtures 保留在库中 |
| [10月11日修改](10月11日修改) | 原生 SolidWorks 机构零件和装配 |
| [龙舟摆件.SLDPRT](龙舟摆件.SLDPRT) | 雕饰龙舟原生模型 |
| [数据流全链路规格](资料汇总/数据流全链路规格.md) | 输入、受力、执行与反馈的边界说明 |
| [项目深度审阅](项目深度审阅_2026-10-31.md) | 架构、版本证据及能力缺口 |
| [PICO 接入手册](VR头显接入手册-Pico4Ultra.md) | 后续头显接入步骤 |

**从源码打开**

1. 克隆本仓库，使用 Unity **2022.3.62f3**，并安装对应的 Windows Build Support。
2. 在 Unity Hub 中添加仓库内的 `DragonBoatVR` 目录。Library 会在本机生成；原开发机的目录联结不入库。
3. 安装 Windows CPython，确保 `python.exe` 在 PATH 中。当前已验交付使用 CPython 3.13.14；编辑器内的独立控制器默认调用系统 Python。
4. 打开已有的 `Assets/Scenes/Demo.unity` 或 `Assets/Scenes/DragonBoatOrnament.unity`。当前场景已保存，无须重新运行旧说明中的初始化向导。

控制器和常规构建入口使用 Python 标准库；历史 CAD 配置生成器另需 NumPy，并保留原开发机绝对路径。其四份原始提取输入按原路径纳入版本控制，跨机器重新提取 CAD 时需核对路径与散列，已有场景运行无需重做提取。

**构建与检查**

在仓库根目录打开 PowerShell，将 `UNITY_EDITOR_PATH` 设置为实际编辑器路径。以下示例构建独立候选目录：

```powershell
$env:UNITY_EDITOR_PATH = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe'
python.exe tools/build_closed_loop_player.py --out Builds/DragonBoatVR-Candidate
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR-Candidate/DragonBoatVR.exe --cad-tendon-rig
```

这些命令会实际运行 Unity 或 Player。按改动范围完成对应检查，保持 Unity 工程排他使用；构建成功不等于完整验收通过。构建工具会把本机 CPython 标准库打包到候选目录，不复制用户配置和 site-packages。`tools` 中部分旧 BAT 文件保留原开发机路径，其他机器优先使用 Unity Hub 和上述环境变量。

[普通启动入口](启动龙舟VR.cmd)和[CAD 启动入口](启动CAD机构龙舟VR.cmd)面向本地已验的 `Builds/DragonBoatVR` 正式目录。Git 仓库不包含该构建包，克隆后可先在 Unity 中运行，或生成自己的候选构建。

**版本管理范围**

提交源码、Unity 资源及 `.meta`、包配置、工具、CAD 和项目文档。Library、构建包、运行录屏／日志、历史整包归档、个人工具状态和对话导出留在本地；`.gitignore` 中的 CAD 例外仅保留工具依赖的四份输入。历史文档中指向本地验收目录、Builds 或归档的链接，仅在保有对应资料的完整工作区中有效。

`.gitattributes` 保留文件原始字节和换行，避免换机时自动换行转换破坏既有源文件散列。本次没有超过 GitHub 100 MiB 限制的单文件，不需要 Git LFS 即可取得完整已提交内容。

仓库沿用已有的 [Apache-2.0 许可证](LICENSE)。第三方代码、模型、纹理、音频及参考文献保留各自来源与许可；Assets 中现有 LICENSE／NOTICE 文件随工程提交，根许可证不替代第三方许可。
