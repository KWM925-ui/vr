# DragonBoatVR — 工程使用说明

> 当前交付为 `20261031T053308`。双击根目录[启动CAD机构龙舟VR.cmd](../启动CAD机构龙舟VR.cmd)使用已有 CAD 设计数据及理想机械限位；[启动龙舟VR.cmd](../启动龙舟VR.cmd)保留原默认示例配置。两个入口均保留船内/船外双视窗及两船选择，保留完整 `Builds/DragonBoatVR` 即可运行，无须另装 Python。**最新范围、验收证据及后续任务只读[执行交接](../tmp/closed_loop_20261025/IMPLEMENTATION_STATUS.md)**。最终实机由人的划桨和机构限位决定桨姿态，电脑握持只用于测试；CAD 配准、导轮/电机映射及具体限位仍有未实测假设。三绳 FIDELITY、实机/PICO 与人体手感未因此通过。下方旧版本均为历史记录。

2026-10-28 05:45 历史交付：[Windows Player](../Builds/DragonBoatVR/DragonBoatVR.exe) 当时为 **054503 构建**，包含手持桨轨迹、Poly Haven 木纹、实录木材吱呀声、同步水声及 Crest 泡沫。本次还修复了平台显示模型首步把船顶高约 10 cm 的碰撞，以及部分浮力点离水时波高遥测被放大的错误；已清理旧桨锁、浮空船舷线、旧音频逻辑和 16 个零引用河景材质。

同包普通连续运行及失焦/350 ms 暂停均为 **14/14 + schema 5 独立核验 PASS**；**575 个源文件、200 个交付文件**哈希一致，两次共 **1,456** 个实际桨姿态无穿船。默认体验由独立 UDP 控制进程的实际回包驱动。普通运行桨叶相对意图偏差均值/峰值为 **0.192/0.455 m**，原始水动力峰值 **128.89 N**；这些是本次模型响应，不是实测手感精度。三绳 FIDELITY 仍 **FAIL（28.14/143.52 N 平均/峰值残差）**，完整受载手感、实物/Pico 与长期实时保证未验收。

[连续画面与声音回看](../tools/closed_loop_checks/20261027T214641.441770Z_0c0be8d2/motion_review.html)、[原始混音](../tools/closed_loop_checks/20261027T214641.441770Z_0c0be8d2/listener_mix.wav)、[验收依据](../软件全链路闭环验收.md)和[清理与恢复记录](../tmp/cleanup_20261028/README.md)已保存。266 张画面和 13.74 秒实际混音完成核验，削波 0；尚未主观试听。保留整个 `Builds/DragonBoatVR` 目录即可运行，无须另装 Python。下方 2026-10-23 表为历史记录。

已经造好的 Unity 工程（含 URP 配置、全部脚本、一键搭场景工具）。**不需要手动摆场景。**

## 零、历史状态（2026-10-23 已验证）

| 项 | 结果 |
|---|---|
| 编辑器 | `F:\Unity\Editor`，2022.3.62f3 |
| 许可证 | ✅ 已激活（Personal） |
| 脚本编译 | ✅ 0 error / 0 warning（Assembly-CSharp.dll 生成成功） |
| 演示场景 | ✅ `Assets/Scenes/Demo.unity`，已加入 Build Settings |
| 双视窗 | ✅ 左侧船内第一人称，右侧船外第三人称龙舟跟拍 |
| 桌面运行检查 | ✅ `run_runtime_check.py` 19/19 |
| 桨位姿/水动力演示 | ✅ `run_paddle_demo.py` 14/14，含入水、出水、水花和尾流 |
| Windows Player | ✅ `Builds/DragonBoatVR/DragonBoatVR.exe` 已生成并以 Direct3D 11 启动 |

**两个自检都能随时复跑**（改完参数必跑）：

```bash
# 物理：浮力/吃水/姿态
"F:/Unity/Editor/Unity.exe" -batchmode -nographics -quit ^
  -projectPath "F:\vr视觉\DragonBoatVR" ^
  -executeMethod DragonBoatVR.EditorTools.PhysicsSelfTest.Run -logFile -

# 通信：C# 侧收包与二进制解析
"F:/Unity/Editor/Unity.exe" -batchmode -nographics -quit ^
  -projectPath "F:\vr视觉\DragonBoatVR" ^
  -executeMethod DragonBoatVR.EditorTools.LinkSelfTest.Run -logFile -
```

日志里搜 `RESULT=PASS` / `RESULT=FAIL`。编辑器里也有菜单：`DragonBoatVR → ② 物理自检 / ③ UDP 链路自检`。

> **盘位说明**：F 盘写入小文件很慢（实测 18ms/个），Unity 解包 UPM 包会超时报 EPERM。
> 因此 `Library` 用目录联结指向 `D:\UnityLib\DragonBoatVR`，UPM 缓存用 `D:\UnityCache`。
> 工程源码、场景、脚本仍在 F 盘。**删工程库不影响源码**，重新打开会自动重建。

## 一、打开工程

**方式 A（不需要 Hub）**：双击 `F:\vr视觉\tools\打开工程.bat`，或命令行

```bash
"F:/Unity/Editor/Unity.exe" -projectPath "F:\vr视觉\DragonBoatVR"
```

**方式 B（装了 Unity Hub 之后）**：Hub → `Add` → `Add project from disk` → 选 `F:\vr视觉\DragonBoatVR`。

编辑器版本：**Unity 2022.3.62f3**（本机已装，工程 `ProjectVersion.txt` 已对上）。
首次打开会导入 URP 14 包并编译脚本，等进度条走完、Console 无红色报错。

## 二、一键初始化（重要）

菜单栏 → **DragonBoatVR → ① 一键初始化（URP + 演示场景）**

它会自动完成：
- 设 Color Space = Linear；
- 创建并指定 URP 管线资产（`Assets/Settings/DragonBoatVR-URP.asset`）；
- 创建水面材质（`Assets/Settings/Water.mat`，用同源水面 shader）；
- 生成演示场景 `Assets/Scenes/Demo.unity`：方向光 + WaveField + 水面 + 样机龙舟 + 桨（水动力 + 脚本驱动）+ 通信桥 + 三绳/六缸软件模型 + 第一人称/第三人称双视窗，并自动写入 Build Settings。

> 若日志提示"自动补 Renderer Data 失败"且画面发粉：按提示手动创建 URP Asset（with Universal Renderer），拖进 Project Settings → Graphics 的 Default Render Pipeline，再重跑一次本菜单。

## 三、运行桌面仿真

1. 展示时双击根目录的[启动龙舟VR.cmd](../启动龙舟VR.cmd)，它固定打开[正式程序](../Builds/DragonBoatVR/DragonBoatVR.exe)。在编辑器检查时打开 `Assets/Scenes/Demo.unity`，按 **Play**；
2. 默认使用 `PaddleDriver.Mode.VirtualPaddler`，自动启动独立 UDP 控制进程，无须手动启动 mock；
3. 只保留左侧船内第一人称和右侧龙舟第三人称跟拍；无水装置视窗与 `F3` 入口已移除。
4. 鼠标置于左窗：按住**右键转头**，使用 **WASD / 方向键**沿当前朝向在船舱内移动，**R** 回到原划桨位。可前后查看船舱；镜头在船舷、首尾及实体前停止，不会推动船体。右键释放、Esc 或失焦会释放鼠标。
5. 鼠标置于右窗：**左键拖动环绕**，**滚轮调整远近**。左窗操作不会同时拖动右窗；右窗保持水面以上的船外视角。
6. **F2** 打开/收起河景与体验菜单，可选“经典木船”或“雕饰龙舟”、“湘江·橘子洲”或“顺德·逢简水乡”，以及平静/有风水况。切船会重新开始当前体验，保留河景、水况和舒适选项；“舒适模式”默认关闭，可自行开启。
7. **F10** 结束体验，随后 **F11** 重新定位并开始，主视角回到划桨位。菜单或 F9 诊断面板打开时暂停相机操作。窗口失焦时仿真和 UDP 闭环继续运行，相机停止接收输入。
8. **F1** 显示/隐藏速度和距离，**F9** 显示/隐藏诊断详情。**F8** 暂停/恢复划桨意图；波浪和船体物理继续运行，船可惯性滑行，这与 F10 结束体验不同。

只有单独诊断外部 UDP 输入模式时，才需要另开 mock 发送器；它不是默认动态闭环的控制器：

```powershell
Set-Location 'F:\vr视觉'
python.exe .\mock_hardware.py
```

3. **验收现象**：
   - 左侧第一人称能看到船头、水面和桨；
   - 右侧第三人称能看到整条龙舟、船体起伏和桨运动；
   - 水面有起伏的浪；
   - 船体随桨和波浪前进、起伏和横摇。

## 四、故障排查

| 现象 | 排查方向 | 处理 |
|---|---|---|
| 默认动态闭环无法启动 | 控制器配置或随包运行时缺失 | 保留完整交付目录，查看 Player 日志中的闭环启动状态与失败原因 |
| 外部 UDP 输入模式 Alive=False | 发送器未运行或端口配置不一致 | 核对当前输入模式、发送器与收包端口；默认动态模式不依赖 mock |
| 画面全粉 | URP 管线资产没挂上 | 见上文"渲染发粉"提示 |
| 水面是纯色平面，没浪 | 水面 plane 没挂 WaterSurface，或材质没用 DragonBoatVR/WaveFieldWater | 检查 Water 对象的材质与组件 |
| 船持续异常抖动 | 受力、碰撞、输入时序或配置变化 | 先与已确认正常的源/包比较，再查实际受力和碰撞记录；`BoatBuoyancy` 当前使用 `verticalWaterDamping`、`maxSubmergenceDepth`、`totalBuoyancyMultiplier`，没有旧说明中的 `stiffness/damping` 字段，不凭现象重调锁定参数 |
| 船沉底或飞出 | 波浪绑定、浮力采样位置、初始姿态或非预期碰撞 | 核对实际场景、六点浸深及碰撞来源；每点浮力上限由质量和 `totalBuoyancyMultiplier` 等计算，没有 `maxForcePerPoint` 配置项，不套用旧文档的固定初始 Y 值 |
| 桨不动但 HUD 正常 | 当前输入未收到有效的新反馈 | 默认模式检查独立控制器是否启动及反馈是否过期；外部输入模式再检查 bridge 接线 |

## 五、验证命令

从工作区 `F:\vr视觉` 按改动范围选用。Unity/Player 检查须排他运行；同包验收要求待测 Player 与当前源码一致，正式包与候选不同时先查[执行交接](../tmp/closed_loop_20261025/IMPLEMENTATION_STATUS.md)。已经通过且未受改动影响的检查直接复用。

```powershell
Set-Location 'F:\vr视觉'
python.exe tools/run_runtime_check.py
python.exe tools/run_paddle_demo.py
python.exe tools/run_closed_loop_check.py
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR/DragonBoatVR.exe
python.exe tools/run_closed_loop_check.py --player Builds/DragonBoatVR/DragonBoatVR.exe --background-check --host-stall-check
```

各命令分别在 `tools/runtime_checks`、`tools/paddle_demo`、`tools/closed_loop_checks` 保存日志、截图和 JSON。前两项采用给定运动输入的解析 fixture，后三条闭环命令分别检查编辑器、指定 Player 和该 Player 的失焦/主机暂停恢复；它们验证默认动态链路，不能由前两项替代。回放只用于内部诊断。
