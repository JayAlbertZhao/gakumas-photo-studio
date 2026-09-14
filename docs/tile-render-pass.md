# 显式 RenderPass／Subpass 调度

`TileRenderPass` 是可独立调用的 SRP 调度模块。宿主提供附件、明确的绘制列表和当前 `ScriptableRenderContext`；模块记录真实的 `BeginRenderPass`／`BeginSubPass`，支持同像素输入、附件角色复用和明确的 Load／Store。默认 `Plan.enabled=false`、`BackendPolicy.RequireNative`。

它尚未替换 Built-in 的 `SceneDeferredCamera`、角色 Forward 或摄影应用默认管线。现有场景模块包含邻域采样和跨 pass 历史，不能直接把它们的目标改成 memoryless。本模块提供后续整合所需的调度基础；附带自检 shader 使用已知通道方程，不能作为 PBR／角色画质追平的证据。

## 宿主入口

在自己的 `RenderPipeline.Render` 或 `ProcessRenderRequests` 回调中调用：

```csharp
// 作为宿主字段保存，结束宿主生命周期时 Dispose。
readonly TileRenderPass tiles = new TileRenderPass();

// 以下代码位于宿主已获得真实 context 的渲染回调中。
context.SetupCameraProperties(camera);
if (!tiles.TryRecord(context, plan, out var submitted, out var error))
    throw new System.InvalidOperationException(error);
context.Submit();
```

引用 `Gakumas.Toolkit`，使用 `GakumasPhotoMode` 与 `UnityEngine.Rendering` 命名空间。不要构造 `default(ScriptableRenderContext)` 来执行有效计划，也不要在 `Camera.Render()` 的 Built-in 回调中调用此入口。模块不切换 `GraphicsSettings`／`QualitySettings`，不执行宿主的 `Submit()`，不改变相机投影或材质。

`Submission` 仅表示已记录的命令、序号与预算估计。它不表示 GPU 已完成、所有驱动执行成功或已节省带宽；需要宿主的正常同步、日志和平台捕获。

## 附件与复用

下面是调度自检的布局，不是现有 Built-in 场景的材质 ABI：

| 索引 | 格式 | 第一阶段 | 后续角色 | 默认生命周期 |
| --- | --- | --- | --- | --- |
| 0 | `R8G8B8A8_SRGB` | Base／mask | 当前像素输入 | transient，Clear／不 Store |
| 1 | `R8G8B8A8_UNorm` | MOS／mask | 当前像素输入 | transient，Clear／不 Store |
| 2 | `R16G16B16A16_SFloat` | Normal／ID | 最终 guide 输出 | 外部目标，Clear／Store |
| 3 | `B10G11R11_UFloatPack32` | 环境／发光 | 累加光照，随后被读取 | transient，Clear／不 Store |
| 4 | `B10G11R11_UFloatPack32` | GI | 最终颜色输出 | 外部目标，Clear／Store |
| 5 | `D32_SFloat` | 几何深度 | 只读深度 | transient，Clear／不 Store |

三个 subpass 的 `colors`／`inputs` 分别为 `[0,1,2,3,4] / []`、`[3] / [0,1,2,4]`、`[2,4] / [3]`。数组顺序定义 `SV_TargetN` 和 `FRAMEBUFFER_INPUT_FLOAT(N)` 的对应关系，不按附件名字猜测。Shader 应通过当前像素的 `LOAD_FRAMEBUFFER_INPUT` 读取；需要邻域纹理采样的算法应拆成独立 render pass。

`Attachment.target=null` 请求引擎管理的 transient 附件；不能 Load，也不能要求跨 pass Store。有外部目标时显式设置 `initialContents=Load` 才会保留之前的内容，`store=true` 才能供 pass 结束后的消费者使用。`Clear` 覆盖旧内容，`clearDepth=1` 是 Unity 的逻辑远平面；不要再根据 reversed-Z 手动改成 0。宿主不能依赖未 Store 附件在 pass 结束后的值。

颜色名义存储为每像素 192 bit，加 D32 共 28 byte。默认把两个 packed HDR 各按 64 个 tile bit 计费，得到颜色预算 256 bit，深度另计。`chargePackedHdrAs64Bits=false` 是宿主明确选择的 192-bit 估计，不是硬件自动检测。`maximumColorTileBits`、`maximumAttachmentMiB`、`maximumDraws` 分别限制颜色 tile 估计、整套名义附件字节和绘制数。`loadedBytes`／`storedBytes` 按声明目标面积计算，不是 GPU 总线测量；真实分配还可能包含对齐、驱动临时纹理和其他成本。

## 绘制、所有权与拒绝条件

每个 `Draw` 恰好提供一个 `Mesh` 或 `Renderer`、有效三角形 submesh、材质和明确的 shader pass。Mesh 路径支持 `localToWorld` 与显式 `MaterialPropertyBlock`；Renderer 路径使用当前对象矩阵，拒绝隐式 property block。自定义 shader 必须正确消费对象变换；深度只读 subpass 中的 shader 必须关闭深度／stencil 写入，模块不反编译 shader 来推断这些状态。

模块只拥有自己的 `CommandBuffer`。网格、Renderer、材质、property block 和外部 RenderTexture 均借用；`Dispose()` 不释放这些对象。宿主负责其 GPU 生命周期，并且不能在一次同步记录过程中修改计划或被借用对象。调用应在 Unity 主线程及有效的 SRP 渲染作用域中执行。

所有描述符先验证、后记录命令：尺寸 1–4096、最多八个颜色附件加一个深度附件、1–16 个 subpass、设备 MRT／格式支持、预算和绘制参数。外部目标必须已创建、格式和尺寸严格匹配、固定单层 2D、无 MSAA／mip／RandomWrite／动态缩放／memoryless。拒绝重复目标、重复索引、未使用附件、同一 subpass 的颜色输入／输出反馈，以及材质已声明纹理属性中对附件的普通采样别名。

任意 shader 全局纹理、未声明的隐藏资源、shader 自身的状态或宿主 context 有效性不能由这些检查完整证明。宿主仍须保证不存在其他读写冲突。Metal 不支持此接口的深度 input attachment；应由宿主选择颜色深度输出或独立 pass。此处没有自动的深度复制／平台降级。

## 验证范围

无资产 Player 入口 `--self-test-tile-render-pass <output-directory>` 使用隔离的 request-only SRP host，退出时恢复两级管线设置和当前 RT。完整 `--self-test-actor-rendering` 在旧用例之后执行相同检查。自制内容覆盖五 MRT、奇数尺寸、当前像素输入、对象矩阵、深度遮挡／cutout、绘制次序、Renderer／Mesh、跨 subpass 保留、G2／G4 复用、外部 Store→下一 pass Load、Clear 清除旧值、预算拒绝与借用生命周期。错误深度 clear 和交换输入顺序作为单变量反例保留。

精确可表示的用例要求完整图像满足固定数值；非整数 sRGB／UNorm／packed HDR 用例按格式逐级枚举相邻量化值，并要求全图均匀，不从实测误差拟合阈值。特别是 0.5 写入 UNorm8，以及混合前后写入 R11G11B10，不能假定总是采用最近偶数舍入。相关范围依据 [Vulkan 数值与格式转换规则](https://docs.vulkan.org/spec/latest/chapters/fundamentals.html)；不同 API／驱动仍需独立验证。

Windows D3D11 是明确允许的 emulation 路径；Vulkan／Metal 属于 Unity 的 native API 分类。桌面 Vulkan 原生 RenderPass、附件 load/store 和 transient usage 可以通过 GPU 捕获检查；这仍不证明 tile 驻留、懒分配内存或移动端净收益。Android／iOS 实机、整场生产内容、长时间稳定性及原有 Deferred／Actor／反射／运动消费者的组合接入仍是后续工作。API 行为见 [Unity 2022.3 BeginRenderPass](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.ScriptableRenderContext.BeginRenderPass.html)。

已知开项：本机 D3D11 `Direct` 单线程模式退出时仍出现三条目标绑定释放警告，未注入捕获工具时也能复现；显式解除目标和延后到下一帧销毁尚未消除它们。该模式目前仅用于诊断，不推荐作为生产配置。正常多线程 D3D11 与本机 Vulkan 的此夹具未出现这些新增警告；数值与原生捕获验收不代表 Direct 生命周期问题已解决。
