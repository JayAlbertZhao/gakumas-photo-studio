# 显式桌面整帧宿主

`DesktopFrameRenderer` 把现有模块按显式输入串联起来，供摄影、沙盒或其他应用复用。
默认 Photo Studio 不会切换到这条路径；构造或关闭模块均不修改全局 shader 参数。

## 无资产示例

在仓库支持的桌面工程中使用 Linear 色彩空间，新建空场景、空 GameObject，添加
**Character Toolkit → Examples → Desktop Host**，进入 Play。
也可以从 Package Manager 导入 **Asset-free Desktop Host** 的说明。

示例实现位于包内 `Examples/DesktopHost`，有单独的 `Gakumas.Toolkit.Examples`
程序集，实际参与工程编译。所有几何、材质输入、Ramp、Probe 和 LUT 均现场生成；
不需要原版模型、AB、私有 LUT 或资产 ZIP。示例会临时选择自己的 SRP，只绘制自己的
显式输入；不要放入已有应用场景后误以为它会自动纳入该场景的其他相机或 renderer。

组件禁用时释放自己创建的资源，并恢复调用方原来的 Graphics／Quality pipeline；
如果其他宿主已经改选了 pipeline，不会覆盖其选择。只允许一个示例实例持有该选择。
`RenderOffscreen(seconds)` 使用同一实现和显式时间输出 `Display`，不呈现到窗口。
`ResetHistory()` 是显式的跳转／镜头切换边界：示例等待自己已排队的 GPU 工作完成，
然后一起清除反射、几何对应与颜色历史。单独改变 `seconds` 不会自动判定为跳转。

`HasCompletedFrame` 表示最近一次渲染尝试产生了完整输出。失败时它会清零，`LastError` 保留原因，
`Display` 中上次成功的像素不会冒充新帧，也不会继续呈现。修正配置后可再次调用；关闭后可以重新初始化。
参数／pipeline 前置校验抛出异常时尚未开始新帧，不改变上次完成帧的状态；调用方仍须处理异常，不能把旧输出当成被拒绝请求的结果。
示例明确给生成角色填写非金属 Definition 输入；shader 的常量回退值不是通用漫反射材质预设。

`LastRenderedTimeSeconds` 记录实际完成帧的输入时间，包含合法的零时刻。示例使用自有
`ScriptableObject` 承载离屏请求：支持的引擎在 native render-loop 桥接中把请求视为
`UnityEngine.Object`，不能依赖普通托管类的内存布局，否则零时间可能被错误分派到普通实时渲染。
对应桥接可见 [Unity 2022.3 C# reference](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Runtime/Export/RenderPipeline/RenderPipelineManager.cs)；本机另有实际参数类型、零时间／姿态和原生矩阵的检查。

示例每帧在提交、后处理和呈现之后做一次单像素同步 readback，随后才能改动姿态或
释放资源。这是便于阅读的生命周期示范，有明确的 CPU／GPU 同步开销；生产宿主可换成
自己的完成通知和资源调度。不要把该示例帧时当作框架或移动端性能结论。

## 核心输入与调用顺序

`Settings` 中分别填写 Scene surfaces、Actor renderers、独立自阴影 caster、背景主光
caster、Planar draws、反射配置、透明特效、DOF 和调用方持有的不可变调色 LUT。
Scene 列表不得包含 Actor，以免把角色写进场景反射历史。Planar receiver group 必须
与接收面一致；Planar 开启时也必须开启统一反射消费者。

场景输出使用 packed HDR，存储的硬件深度是独立 D32S8；反射需要明确存储的 Half4
法线／身份、SRGB8 base 和 UNorm8 MOS。沿用各模块的格式、尺寸和工作量限制，
不自动修复不兼容输入或猜测原版 MaterialID。

`Settings.actorStorage` 默认 `SeparateHalf`，保留原有独立 Actor 输出。显式选择
`ReuseScenePacked` 可在反射历史完成后原位消费场景 GBuffer4 和 D32S8；后处理仍使用
当前 Actor 深度。该选择有 packed HDR 的精度／无 alpha 限制，不能继续把被覆盖的纹理
当作场景输入。`SeparatePacked` 是同格式独立输出对照；所有权、失效规则和名义内存
预算见 [Actor 存储策略](srp-actor-forward.md#optional-packed-scene-attachment-reuse)。

宿主在有效 SRP context 内完成以下顺序：

1. `TryRecord(context, sequence, sceneRevision, out opaque, out error)`：场景准备、
   当前角色自阴影、场景、可选 Planar、SSR／Probe、完整 Actor。
2. 宿主 `context.Submit()`。失败的尝试也可能已经录制了部分命令，必须处理这些命令。
3. `TryFinishAfterSubmission(opaque, seconds, out frame, out error)`：联合透明特效、
   可选整帧 TAA、可选 DOF、可选调色。特效与 DOF 使用当前 Actor 合成后的 eye depth。
4. 使用 `frame.color`，完成需要的复制／输出／呈现。
5. 确认 GPU 已经不再使用本帧资源，再调用 `RetireAfterGpuCompletion()`。

`sequence` 为单调递增的正数；场景拓扑／内容突变时更新 `sceneRevision`。
`IsCurrent` 只检查票据与资源状态，**不是 GPU fence，也不代表 Submit 已发生**。
录制至 GPU 完成期间，调用方必须保留相机、geometry、骨骼、材质输入、纹理内容和
借用的 render targets。不要在尚未完成的帧中修改配置或改写这些输入。

记录／后处理失败后，保留资源直到已排队 GPU 工作完成，再退休该尝试。失败退休会
丢弃反射、运动与 TAA 历史；不要把上次成功帧的票据当作本次失败后的有效输出。外部自阴影票据
与内部自阴影 producer 二选一。关闭后处理时输出可直接指向 Actor 的颜色附件，
因此不要假定输出归调用方所有，或把它作为下一帧的外部输入。

核心不会调用 Submit、选择 SRP、自动搜索场景、读取资产目录、写 shader globals，
也不会等待 GPU。窗口 Blit 放在 SRP 的 `endContextRendering` 回调；具体要求见
[Unity Graphics.Blit 文档](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Graphics.Blit.html)。

## 覆盖边界

目前是桌面显式离屏协调器。完整 Actor 运动、可选场景运动与方差裁剪 TAA 已有显式接入，
已可选串联 Motion Blur；Bloom／FSR／Diffusion 尚未串联。GBuffer2／GBuffer4／硬件深度原位复用均为可选路径，不改变默认存储。
各模块单独存在，不意味着已进入这条整帧路径。

示例的 primitive 角色只能说明模块如何接入；不证明真实头发／面部贴花／复杂透明
排序、所有服装、原版画质、移动附件驻留或帧时已经追平。移动 Android Vulkan／
iOS Metal 仍须独立实机验证。`docs/framework-techniques.md` 保留完整剩余范围。

集成验证入口为 Player 的 `--self-test-desktop-host <output-directory>`；它使用
生成内容、完整图像与独立数值控制。LUT 比较分别记录理想插值误差和实际硬件八权重
重建误差，不能把硬件滤波取整混为特效混合错误。
Vulkan 的合成三角形运动数值对照另需环境变量 `GAKUMAS_SELFTEST_RASTER_SUBPIXEL_BITS`，
填写目标 GPU 通过 `vulkaninfo` 查询出的 `subPixelPrecisionBits`；不得从输出图像拟合。
这是测试参考计算的光栅精度输入，不是渲染器配置，也不改变验收误差阈值。
其含义见 [Vulkan 设备限制](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceLimits.html)。

自备兼容角色时，可在现有 `--photo-mode --validate-srp-actor-character <output-directory>`
后增加 `--validate-actor-shadows --validate-desktop-character`。先执行原有普通 Forward／显式 SRP
整图与深度对照，再把同一角色接入整帧示例；每个视角都保留只排除主视图角色的负对照，
避免只有地面投影或反射的图像被误判为角色可见。该入口属于应用诊断，不是工具包的资产依赖。

## 可选整帧运动与 TAA

此扩展按桌面可选预览提供，默认关闭。运动、形变、历史拒绝和存储复用已有独立
控制；Vulkan 真实角色仍有已复现的单个 Half ULP 颜色差异，严格颜色检查保留失败，
不保证启用运动后与旧 shader 逐位相同。需要原路径颜色完全一致时保持运动关闭。
精度控制及适用范围见 [Actor 验证说明](srp-actor-forward.md#optional-motion-and-temporal-resolve)。

在填写正常场景与角色输入后，显式设置：

```csharp
settings.actorMotion.enabled = true;
settings.includeSceneMotion = true;
settings.temporal.enabled = true;
// 两项独立的存储优化；不要求同时启用。
settings.reuseSceneMotionStorage = true;
settings.actorStorage = SrpActorForward.Storage.ReuseScenePacked;
```

顺序为场景与反射 → 完整 Actor → 透明特效 → TAA → DOF → 调色。运动来自实际 GPU
顶点流，覆盖角色蒙皮／形变与描边；不使用 CPU BakeMesh 替代。TAA 使用同帧 Half4
运动／深度／身份、额外 R32 预期上一帧深度、HDR 方差裁剪与亮度加权历史。
身份与标志是本框架的独立约定，详见 [Actor 运动附件](srp-actor-forward.md#optional-motion-and-temporal-resolve)。

默认要求可读三角形网格。自备不可读角色网格时，只有宿主能够保证 GPU 拓扑不变，
才启用 `allowImmutableUnreadableMotionMeshes`；原位拓扑改动必须更新 `actorMotionRevision`。
此选项不放宽场景运动的可读性要求，也不修改／重新导入用户资产。

宿主拥有投影 jitter 和时间控制；模块不会修改相机投影。已应用 jitter 时填写纹理 UV
单位的 `temporal.jitterUv`。跳转／镜头切换在 GPU 完成后调用核心
`ResetHistoryAfterGpuCompletion()`；颜色内容不连续时更新 `temporal.contentRevision`。
`Frame.temporal` 是可选的当前输出票据，不是长期保存历史的纹理所有权。

`actorMotionMaximumMiB` 限制角色自有运动附件与保留的顶点快照；
`sceneMotionMaximumMiB` 限制场景顶点快照；`temporalMaximumMiB` 限制 TAA 历史。
复用 GBuffer2 少分配一张全尺寸 Half4（每像素 8 字节），TAA 自有两套颜色与元数据
Float4（每像素共 64 字节）。这些是名义纹理预算，不是实测 VRAM／带宽／移动帧时。

透明头发、显式 `ExcludeTaa` 与被当前特效改变的像素保守拒绝颜色历史；未跟踪背景
直接使用当前颜色。复杂多层透明的运动归属、完整动态画质和移动设备仍须独立验证。

## 可选整帧 Motion Blur

`settings.motionBlur.enabled = true` 需要同时启用 `actorMotion.enabled`。
完整背景运动还需 `includeSceneMotion = true`。顺序为 FX → TAA → DOF →
Motion Blur → 调色；复用已有 `MotionBlurRenderer` 的滤波算法，不另造一套。
自制移动几何已执行 D3D11／Vulkan 控制，包括 CPU 解包 guide、上传身份检查、
整图滤波与 TAA／DOF 顺序对照、时钟／跳转、独立排除项和 GBuffer 复用。
Vulkan 原生捕获确认实际 Half4 → guide／保护层 → tile → neighborhood → 当前 HDR
消费链，guide 与最终输出和运行时原始数据完全一致。此范围不能计为完整角色
动态画质或移动性能通过。

`FrameMotionBlur` 把 Half4 的当前位移、当前眼深度与有效历史位转换为滤波器的
Float4 guide；不直接把打包身份当作 alpha。独立 R8 保护层拒绝 NoJitter、
已分类的混合角色层及被当前特效改变的像素。`ExcludeTaa` 本身仍允许运动模糊。
调用方可另设 `actors.excludeMotionBlur(renderer, submesh)` 或场景表面的
`excludeMotionBlur`；这些设置与 TAA 分类独立，也不改变已有 Half4 布局。

时间来自 `TryFinishAfterSubmission` 的显式 `timeSeconds`。首次、暂停、时间倒退、
长间隔、帧序跳跃、运动失效、关闭再启用或宿主重置均不曝光。启用 TAA 时采用
`temporal.jitterUv`；否则通过 `motionBlurJitterUv` 提供实际已施加的投影 jitter。
模块不会移动相机。跳转仍需宿主在 GPU 完成后显式重置整个历史链。

成功帧的 `Frame.motionBlur` 提供借用的颜色、guide、保护层与实际采样间隔；
不能释放或在宿主退役后继续使用。`motionBlurMaximumMiB` 控制本模块名义附件预算：
输入为 W×H、半径上限 R 时，共 `33WH + 32·ceil(W/R)·ceil(H/R)` 字节，
包括 Float4 guide、R8 保护层、Float4 输出及两张 tile 纹理。运动生产器、TAA、
DOF 和驱动开销另算；零曝光目前仍执行过滤路径，不代表零成本。
关闭后清空曝光历史，但保留已分配附件供再次启用；宿主释放时统一销毁。

DOF 已混合的颜色仍由当前可见深度／运动引导；复杂近远遮挡、透明重叠与完整
曝光积分没有因此恢复，需要单独进行动态画质评估。
