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

## 角色整链诊断

`--validate-srp-actor-character <output> --validate-desktop-character` 在调用方
已合法提供资产的验证环境中使用相同的 `DesktopHostExample`，不内置游戏资产。
新增诊断把实际几何降至 Quality 342²／Performance 256²，再输出 512²；覆盖前、侧、
后视图及 TAA／DOF／Motion Blur／Bloom／FSR／Diffusion／调色串联。
采样时间为 `.70,.72,.74,.76` 秒，正对照每帧平移相机 4% 角色高度；无相机移动的
idle 另作观察，不要求低于半像素曝光门限的运动也必须产生模糊。

通过排除角色绘制构造角色相关区域，并以投影头部区域单独记录误差；该区域不是语义
分割真值，会含受角色影响的后处理邻域。`character-post-diagnostics.json` 记录完整
分辨率参考误差、每个效果关闭后的响应、idle 全图差异及显式配置。参考 MAE 只作
观察，不用“有限数值”宣称画质达标；当前压力配置有可见模糊与边缘误差。

诊断显式选择 `colorGradeSampling = ColorLutSampling.ExplicitFloatTrilinear`；
原有示例及默认摄影仍使用原配置。硬件 LUT 过滤可把上游很小的浮点差异放大为阶跃；
见 [调色采样选项](authored-color-grading.md#可选显式浮点三线性插值)。整个序列仍使用
原 `1e-4` 后端最大误差门限，冷帧／暂停曝光与重播保持精确比较。
这些离屏检查不验证呈现帧率、移动端收益、真实投影 jitter 收敛或原版画面一致性。

## 显式投影抖动

`TemporalProjectionJitter` 为需要 TAA 的宿主生成离屏投影及对应纹理 UV 修正，
不修改 Camera、全局状态、时钟或历史。默认应用不调用它。

```csharp
// 在分辨率／FOV／镜头改变后取得新的未抖动投影；不能累加到上一帧抖动矩阵。
Matrix4x4 baseProjection = camera.projectionMatrix;
Vector2Int rasterSize = new Vector2Int(sceneColor.width, sceneColor.height);
if (TemporalProjectionJitter.TryCreate(baseProjection, rasterSize,
        TemporalProjectionJitter.Offset(phase), out var jitter)) {
    camera.projectionMatrix = jitter.projection;
    settings.temporal.jitterUv = jitter.correctionUv;
    settings.motionBlurJitterUv = jitter.correctionUv;
    // 记录／提交／完成当前帧。phase 由宿主管理；切镜时一起重置 phase 和历史。
}
// 退出此渲染路径时恢复 baseProjection，并清零两个 UV 修正。
```

八相序列为中心化的 Halton(2,3)，不是讲演未公开的原版采样序列。输入 offset
以相机投影像素轴表示，范围每轴 `[-.5,.5]`；尺寸为实际几何栅格，FSR 前的低尺寸，
不能使用最终显示尺寸。纯 clip 平移对 x/y 加上 w 倍偏移，兼容透视、正交及偏轴投影。
`GL.GetGPUProjectionMatrix(..., true)` 和平台纹理轴用于计算实际离屏位移；
`rasterOffsetUv` 是位移，`correctionUv` 是它的相反数。TAA 在
`uv - correctionUv` 采样当前帧。直接把实际位移作为修正会朝反方向采样。

必须在 Unity 主线程调用，按 bool 处理非有限矩阵／offset、奇异投影和非法尺寸。
当前接口只描述 render-into-texture，不能直接用于 backbuffer 坐标。
UI 应在独立的未抖动阶段绘制；给已经抖动的几何标记 `NoJitter` 不会撤销顶点位移。

`--validate-desktop-jitter` 与角色验证参数一起使用，替代整链压力诊断，专门检查
固定姿态的头部近景。对照包含原始单采样、独立 4×4 空间积分、每帧清历史的
双线性修正、连续累积，以及错误符号／漏传修正负控。角色可见性差分在相同未抖动
投影下建立，再扩张一像素；不是语义分割。误差在 `max(HDR,0)/(1+max(HDR,0))`
映射后统计 RGB MSE，第二周期另记静态相位方差。保留所有输入与原始 HDR 输出，
不把一次模糊或更低时间方差单独记为画质改进。

调用示例（资产仍由使用者自行提供）：

```text
KotonePhotoStudio.exe --photo-mode --validate-srp-actor-character <output> --validate-actor-shadows --validate-desktop-character --validate-desktop-jitter
```

本机 D3D11／Vulkan、两套服装、前／侧／后视图，以及原生 512²／实际 342²／256²
几何共 36 组静态近景：连续历史相对原始无抖动输出的上述参考 MSE 降低约 35–55%，
相对每帧清历史的双线性修正降低约 11–25%，静态相位方差降低约 22–40%。
独立离线计算从保存的 HDR RAW 重新积分参考、重建区域并复算全部指标；正确修正
在每组均优于错误符号和漏传修正。该结果只证明这组静态空间收敛，不能推导动画无拖影。

D3D11 原生捕获确认实际 342² TAA 双 Float4 输出接入 FSR，再到 512² 零强度
Diffusion；最终像素与运行时 RAW 完全相同。角色有效运动像素上的静态重投影残差
最大约 `0.000405` 像素，有 27,455 个角色像素实际使用历史。投影 helper 另有透视／
正交／偏轴及四种尺寸的世界点对齐控制，不修改旧 TAA shader 或其默认调用路径。

保留失败：其中一套服装的 Vulkan 整体报告仍有抖动诊断之前的
`temporal-view-180-motion-mrt-retains-real-color` 严格颜色检查失败，最大差异
`3.814697265625e-6`；新增抖动检查通过不代表该完整报告通过，也不关闭已有运动
MRT 颜色差异问题。动态发丝、复杂透明／遮挡、联合曝光与移动端成本仍待验收。

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
已可选串联 Motion Blur、自主 Bloom 和 FSR；Diffusion 尚未串联。GBuffer2／GBuffer4／硬件深度原位复用均为可选路径，不改变默认存储。
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

## 可选自主 Bloom

`settings.bloom.enabled = true` 在 Motion Blur 后、调色前执行一次发光合成；
独立宿主也可使用 `BloomRenderer.TryRender(source, settings, out frame)`。
不读取 Story／私有 LUT／捕获材质，也不改变默认摄影路径。

输入为已创建的线性 Float4／Half4／R11G11B10 HDR、单层 Tex2D，无 MSAA、mip、
动态尺寸或 Memoryless。源颜色应有限；Bloom 分支将负值及非有限分量归零、正值
限制到 65504，最后只添加发光 RGB，保留原始 alpha。输出与全部金字塔使用 Float4。
调用方的 sampler／wrap／anisotropy 不会被修改，采样由整数 Load 和显式双线性权重完成。

参数由使用者制作：`threshold`、`softKnee`、`intensity`、`scatter` 和
`maximumLevels`，不声称恢复讲演未公开的 Bloom 公式或参数。首层每轴向上取整
减半，以四个偏移半源 texel 的双线性样本平均后做 max-channel 软阈值；之后继续
四点减半，到 1×1 或层数上限停止。回升时以 `scatter` 在当前层与放大后的低层之间
做归一化混合，因此常量发光不随金字塔深度叠加放大。最后一次性乘 `intensity`
加到原 HDR。该独立模型与旧摄影 shader 的累加金字塔分别保留。

`maximumMiB` 在分配前验证名义纹理预算：Float4 输出、各下降层和除最小层外
各回升层的总像素数乘 16 字节。有效 L 层执行 `2L` 次绘制。零强度当前仍构建
金字塔，关闭后保留已有资源供复用；宿主释放时统一销毁。

`Frame.bloom` 是借用票据，宿主退役、下一次渲染、失败或任一依赖附件丢失后无效。
复杂制作场景的眩光外观、完整动态后处理、其他设备与移动帧时仍需单独验证。

已执行自制桌面 D3D11／Vulkan 全图 double 参考：单像素、单行／列、奇数尺寸、
三种 HDR 输入格式、阈值／knee／scatter／层数、零强度、极亮 HDR、alpha、预算与
双实例所有权控制，以及实际 Motion Blur 后的整帧消费。最大归一化分量误差
约 `1.76e-6`（误差定义为 `abs(actual-reference)/(1+abs(reference))`）。
原生 Vulkan 捕获确认五层下降、四层回升和一次最终合成，读取实际 Motion Blur
输出；最终 Float4 与运行时原始数据一致。该结果限定于当前设备与自制输入。

## 可选整帧 FSR

复用 `FsrRenderer`，位于 Bloom 后／最终调色前；默认关闭。使用者指定最终尺寸，
根据档位计算真实的场景渲染尺寸，并以该尺寸分配全部借用的场景附件：

```csharp
settings.fsr.enabled = true;
settings.fsr.quality = FsrQuality.Quality;
settings.fsr.stabilizeLumaGradients = true; // 显式自主稳定化；false 保留原始 EASU 变体。
settings.fsrOutputSize = new Vector2Int(1920, 1080);
settings.fsr.TryGetRenderSize(settings.fsrOutputSize, out var renderSize);
// 使用 renderSize 分配 scene.output、depthStencil、normalIdentity 等附件。
// 相机投影 aspect 对应最终画面比例；其 targetTexture 指向低尺寸 scene.output。
// 后续仍按 TryRecord -> 宿主提交 -> TryFinishAfterSubmission -> GPU完成 -> Retire 调用。
```

宿主验证低尺寸与所选档位吻合，不会将一张全尺寸图先缩小来假装降低几何开销，
也不会擅自重新分配调用者的附件。质量／尺寸切换须在完成并退役前一帧之后进行。
TAA、DOF、运动模糊和 Bloom 均在原生渲染尺寸执行；最终调色在 FSR 输出尺寸执行。
UI 仍由调用者最后绘制，本模块不生成 UI 或替换呈现器。

`Frame.color`／`OutputSize` 是最终尺寸；`eyeDepth`／`RenderSize` 和 `opaque`、
`temporal`、`motionBlur`、`bloom` 仍对应几何渲染尺寸。消费深度的后续模块必须显式处理
尺寸映射，不可将低尺寸深度当成全尺寸深度直接逐 texel 读取。

这个 HDR 插槽只接受 `LinearHdr` 编码。压缩／逆变换、高亮上限、alpha 重采样与
Compute／Raster 回退策略均沿用 [FSR 模块契约](fsr.md)，不保证无损 HDR。
`FsrEstimatedTargetBytes` 是三个 Float4 附件的分配估算，不包括前置渲染、调色和驱动。
开启时在记录场景前检查预算；关闭后保留自有附件供复用，但退役会立即让 FSR 子票据失效。
默认关闭时输出仍为原生尺寸，不改变旧调用者行为。

四档 Compute／Raster 整帧检查发现，原始 EASU 在近等亮度区域会将 HDR 准备的
Float32 舍入放大。为此增加了上述**显式、默认关闭**的自主稳定化变体；它对亮度
梯度归一化分母设 `2/4096` 下限，不声称复原游戏参数或与原始 AMD 变体逐像素相同。

启用该变体后，自制桌面 D3D11／Vulkan 的 FX／TAA／DOF／Motion Blur／Bloom／
FSR／调色全链，四档冷帧及移动帧通过源图出发的独立标量数值比较；另有相同输入
跨后端、输入一 ULP 扰动、独立色度／近等亮度信号和 keyword 切换恢复控制。
实际中间附件只用于定位问题，未取代端到端参考，原失败报告及原始变体保持可检查。
该验收不覆盖完整制作场景动态画质、HDR 极亮部、移动帧时或其他设备。

## 可选自主 Diffusion

`settings.diffusion.enabled = true` 将 `DiffusionRenderer` 接入 FSR 后、调色前。
未启用 FSR 时直接消费当前渲染尺寸。默认关闭，不替换摄影应用的旧后处理。
独立宿主可调用 `TryRender(source, settings, out frame)`，无需相机或私有 profile。

PPT 126 只列出内制 Diffusion，PDF 15 给出 Bloom → FSR → Diffusion → UI 顺序。
讲演没有公开此处所需的核与参数。本模块使用独立制作的模型：按 `downsample`
（1 至 4，逐轴向上取整）以四个双线性样本缩小，再执行水平／垂直五点二项式核
`[1,4,6,4,1]/16`，最后向源 RGB 添加 `intensity * max(blur-max(source,0),0)`。
缩小采样偏移为四分之一目标 texel；倍率 1 直接复制，不添加预滤波。

`radiusPixels` 是相邻核样本在**完整输出画面**中的像素间距，上限 64；每轴核支持域
为两倍间距。它不会因选择低尺寸 blur 附件而放大。缩小／回升也会产生滤波，故仅
`downsample=1, radiusPixels=0` 或 `intensity=0` 表示精确无效果。参数与旧摄影
shader 的截取配置无关，也不宣称恢复游戏外观。强度为 0 至 1，默认 0.2。

输入契约与自主 Bloom 相同：有限线性 HDR、Float4／Half4／R11G11B10、单层
Tex2D，无 MSAA、mip、动态尺寸或 Memoryless。blur 分支清除非有限／负分量并限制
到 65504；源图本身不截断，负 RGB 和原 alpha 保留。内部及输出使用 Float4，
整数 Load 配合显式双线性，不修改调用方 sampler。无需低尺寸深度或运动附件。

四次绘制使用一张完整输出和三张缩小附件。`maximumMiB` 在分配前限制
`16 * (W*H + 3*ceil(W/downsample)*ceil(H/downsample))` 字节；宿主在记录几何前
按最终尺寸检查预算。此值不含调色、驱动开销或实测带宽，零强度目前仍执行四次绘制。
关闭保留已分配资源；`Dispose` 终止实例并释放其所有资源。

`Frame.diffusion` 提供借用的完整颜色与缩小 blur。宿主退役、下次调用、失败或任一
内部附件丢失都会使票据无效。调用方应先保证 GPU 完成，再退役／释放，不可将借用
纹理反向传入同一个生产器。

自制桌面 D3D11／Vulkan 各执行 611 项新增数值、格式、参数、所有权与整帧接入
检查：三种 HDR 格式，单像素／单行列／奇数尺寸，零效果精确相等，极亮 HDR，
实际低尺寸几何经 Compute／Raster FSR 后的冷帧与移动帧，以及错误低尺寸半径
负对照。全图独立 double 参考最大归一化分量误差约 `2.32e-6`，门限 `2e-5`。
Vulkan 原生捕获确认 FSR → 三张 blur 附件 → 一次合成 → 调色的实际资源读取，
四份关键输出与运行时原始数据完全一致。完整角色／透明发丝动态外观、联合曝光／
DOF 遮挡、其他设备和移动内存／帧时仍未由这些自制输入证明。
