# VR 头显接入手册(Pico 4 Ultra)

> 用户已选定 **Pico 4 Ultra**。本轮三绳船桨接入另见 [嵌入式交接手册](hardware/paddle/README.md)。
> 这是后续实机接入清单，不是已完成的 VR 验收。当前先完成桌面软件；
> 头显到手后再确认 runtime、双目、追踪、串流和运动补偿，不预估固定验收时长。

## 0. 硬件与连接

1. 核对头显 PICO OS、PC 驱动及所选串流软件的兼容版本；仅在实际调试/部署步骤需要时开启开发者模式。
2. PC 端使用 **PICO Connect** 等经过实机核验的 PCVR 路线。官方将 PICO Connect 描述为 SteamVR/桌面**串流**，不能将其写成 DP 直显工具。
3. 有线方案使用符合实际串流要求的 USB 数据连接；USB-C 接口形状不能证明支持 DP Alt Mode。分别记录有线/无线的实际帧呈现、迟延和稳定性，运动平台所用方案由实测决定，当前没有“禁止无线”的用户要求。
4. 确认当前实际生效的 SteamVR/OpenXR runtime 及其 PICO 兼容路径；Unity 使用 OpenXR 的方案须以真实双目与头动测试通过为准。

## 1. Unity 工程(DragonBoatVR)改动

2026-10-31 已补齐软件准备：`UnityXrHeadTrackingProvider`读取有效中心眼姿态，`HeadPoseAttachment.SubmitIndependentTrackingSample`分别保留头部与平台观测时间；该版历史 GPU 夹具为 6/6、配准与时钟反例为 14/14，范围见 [软件验收说明](软件全链路闭环验收.md)。原始 GPU 记录保留在本地 `tmp/software_followthrough_20261031/xr_visual_review.json`。这些检查未使用 PICO，当前工程未安装 XR 插件，也未把新追踪组件接入两个场景；以下步骤仍需在实机接入时执行。

场景接线时，将 `UnityXrHeadTrackingProvider`与`HeadPoseAttachment`放在输出主相机上，指定当前船体的`virtualBoat`和已完成实测配准的`SpatialCalibrator`。平台适配器须在 Unity 主线程调用`SubmitPlatformObservation(stageFromPlatform, observedLocalSeconds, HeadTrackingSource.ExternalTracking, tracked)`，提供同一追踪空间中的实际平台观测及其原始本地单调时间；重复头部读取不能刷新旧平台数据。设备时间若来自另一个时钟，须先确认时钟映射，不能直接填入或以平台目标代替测量。当前 Unity 节点 API 仅提供本地观察时间，未提供设备采集时间。

完成上述接线和有效配准后再开启`useTrackedPose`；`stageRegistrationMeasured`由有效配准结果计算，不能手动设为真跳过检查。输出相机仅由此链路写姿态，勿同时挂接`TrackedPoseDriver`；启用追踪后，包括丢失期间，桌面 WASD 不再改写相机位置。追踪原点重置或头显身份变化会清除旧平台观测与配准，需要重新建立。`SplitViewController`已将主相机设置为双眼输出，第三人称相机禁用 XR 输出；原生左右眼、桌面监视和真实平台补偿仍须按第 2 节验证。

1. Package Manager 安装:
   - `XR Plugin Management`
   - `OpenXR Plugin`（选择支持本项目 Unity 2022.3/URP 14 的版本并固定依赖）
2. Project Settings → XR Plug-in Management:
   - PC 平台勾选 **OpenXR**;
   - 用户拿的是真实船桨，桨输入继续使用专用适配器；不把 VR 手柄 profile 当成桨位姿接口。若头显菜单需要手柄，单独核验 runtime 支持的交互 profile。
3. 分别核对 URP 深度纹理与 OpenXR 的深度提交能力；它们不是同一设置，不因水面依赖深度纹理而假设 runtime 支持深度提交。
4. 在实际 OpenXR 配置中选择受支持的单通道立体渲染模式，再检查左右眼水面/反射；必要时比较 Multi-pass，记录帧时间和差异。
5. 两个船型场景（`Demo.unity` / `DragonBoatOrnament.unity`）：
   - 新建 **XR Origin**，以各船型当前座位锚点和实物坐姿标定定位，不沿用旧文档写死的视点数值；
   - 通过统一的虚拟船/实际平台/追踪空间变换呈现头部。使用实际平台测量姿态，明确由应用或外部补偿层中的一个承担补偿，防止重复抵消或重复叠加；保留桌面相机作回退；
   - 桌面 WASD 舱内边界与第三人称避障不能直接钳制 HMD 的实测头动；真实头动必须保持对应关系，活动边界另按实体布局处理；
   - 双视窗策略:VR 模式下主眼画面即第一人称,右视窗第三人称保留在桌面监视器。
6. 着色器自查：`DragonBoatVR/WaveFieldWater`、泡沫与 `PlanarWaterReflection` 逐眼验证纹理、采样矩阵和实例索引；URP HLSL 或桌面渲染通过不能证明立体兼容。可选舒适模式也须逐眼验证。

## 2. 验收记录(每项留证据,桌面截图不算数)

- [ ] 头部追踪:前后左右上下 6 自由度跟随,无漂移(录屏或帧日志);
- [ ] 坐姿高度：实际座椅、头部和船桨工作空间对齐，两船分别核验；
- [ ] 视野内可见:船内景、桨、水面、两岸;桌面保留第三人称监视窗，不提供平台装置视角;
- [ ] 帧呈现：PICO 4 Ultra 官方刷新率为 **90 Hz**，单刷新周期约 **11.11 ms**；核对实际运行模式、CPU/GPU 帧时间、编码传输解码、重投影与掉帧，不能用桌面 fps 或 72 fps 代替；
- [ ] 划桨交互不依赖头部姿态；真实桨追踪与力反馈分别验证，不将头显手柄震动当作自由桨阻力；
- [ ] 实际平台动而头相对座舱不动、头独立移动、二者同时移动三种条件下，补偿均正确；
- [ ] 舒适性：可选周边视野减弱默认关闭，记录用户反馈与停止情况；不单独修改视觉船摇摆来掩盖平台不同步。若调整运动强度，通过同一船体模型和受限平台映射核验。

## 3. 回归口径

接入后重新运行桌面门禁：`run_all_checks.py`（13/13）、`run_runtime_check.py`、`run_paddle_demo.py` 及实际 Player 闭环，确认桌面路径未退化。
VR 专属验收项单独记录,不并入桌面门禁。

---
2026-10-24 由 ZCode 会话 6b21485d 依据交接文档阶段 G 预置。

2026-10-30 Codex 按已完成的研究纠正串流/DP、90 Hz、真桨与补偿边界。官方依据：[PICO 4 Ultra 规格](https://www.picoxr.com/global/products/pico4-ultra/specs)、[PICO Connect](https://www.picoxr.com/global/software/pico-link)。本次没有安装 XR 依赖或宣称实机已通过。
