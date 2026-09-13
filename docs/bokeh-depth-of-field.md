# 独立 Bokeh 景深模块

`BokehDepthOfFieldRenderer` 接收线性 HDR 颜色与对应的正眼空间深度，输出带景深的 HDR 纹理。它不搜索角色、不修改相机投影、不借用全局 `_CameraDepthTexture`，也不依赖 Photo Studio UI 或学马资产。

默认关闭。旧 `OriginalStyleRenderPipeline` 的 43 采样摄影路径和 `AdvDepthOfField.shader` 保留；显式启用此模块才使用新的范围／物理镜头控制。新模块的 43 档也采用新的深度、单位、前景处理和 float 缓冲约定，不能把它当作旧画面的逐像素替代。

## 独立应用接入

```csharp
private readonly BokehDepthOfFieldRenderer dof = new BokehDepthOfFieldRenderer();
private readonly BokehDepthOfFieldSettings focus = new BokehDepthOfFieldSettings {
    enabled = true,
    sampleCount = BokehSampleCount.Samples30,
    focusMode = BokehFocusMode.FocusRange,
    focusNear = 2,        // 米；清晰区间的近端
    focusFar = 5,         // 米；清晰区间的远端
    nearTransition = 2,  // 向相机方向过渡到最大散焦所需的距离
    farTransition = 8,   // 向背景方向的独立过渡距离
    nearBlur = 1,
    farBlur = 1,
    maximumRadius = .02f // 图像高度的比例，不是固定像素数
};

// 在本次颜色／深度都已渲染完毕后调用；两者必须对应同一视图。
void RenderDepthOfField(RenderTexture currentHdr, RenderTexture positiveEyeDepth,
                       RenderTexture destination) {
    bool ok = dof.TryRender(currentHdr, positiveEyeDepth, focus, out var frame);
    Graphics.Blit(ok ? frame.color : currentHdr, destination);
    // ok == false 时可读取 dof.UnavailableReason；缺失输入不会复用旧结果。
}

void OnDisable() { dof.Dispose(); }
```

清晰范围内的深度产生零 CoC，脸与伸向镜头的手可以同时清晰；近、远散焦通过各自的 smoothstep 距离控制。前景物体自身的散景仍可能遮挡后面的清晰物体，这是前景合成的一部分，不保证任意遮挡下的所有像素都保持原值。

`focusMode = BokehFocusMode.Physical` 则使用 `focusDistance`（米）、`focalLengthMillimetres`、`fNumber`、`sensorHeightMillimetres` 计算薄透镜的传感器散焦半径，并换算为图像高度比例，再应用最大半径和近／远强度。`EvaluateRadius(depth)` 是对应的 CPU 查询接口，不修改渲染状态。不要把旧摄影参数中的散焦直径、缩放系数直接当作此 API 的半径。

## 输入与资源约定

- 颜色：已创建、线性的 ARGBFloat、ARGBHalf 或 RGB111110Float。最后一种没有存储 alpha，输出 alpha 为其采样值 1。
- 深度：相同尺寸的 RFloat／RHalf，R 存正眼空间距离，单位为世界米；不是原始 D24／D32、反向 Z 或归一化深度。清屏背景应写应用选定的远景距离。
- 两个输入必须是 2D、单采样、非 dynamic-scale、无 mipmap 的独立外部目标。模块会检查尺寸、格式、创建状态与自有纹理别名；它无法鉴定外部纹理是否真来自同一帧／相机。
- 有效输入颜色应有限。不要把包含 NaN 的颜色当作受支持 HDR；零／无效深度按清晰处理。全景深、cutout、透明、位移、MSAA 解析和颜色／深度的 jitter 对齐由宿主负责。
- `Frame` 是借用的结果：下一次调用、禁用、释放、重分配或任一内部目标丢失都会使 `IsCurrent` 变为 false。不要释放或写入模块目标；需要长期保存时复制到自己的纹理。
- 失败会释放本模块资源，不会返回上一帧结果；调用方自行透传原图。模块保存、恢复调用方的有效 `RenderTexture.active`。

## Photo Studio 后处理桥接

```csharp
post.bokehDepthOfField = focus;
post.bokehDepthProvider = (camera, currentPostHdr) => {
    // 这是宿主显式提供的深度。必须覆盖当前 HDR 的所有相关表面，
    // 并匹配 TAA 之后的像素坐标；不能直接返回任意场景预通道。
    return GetCorrespondingEyeDepth(camera, currentPostHdr);
};
```

provider 在本台相机的时域 resolve 之后、景深之前调用。成功结果供 bloom、diffusion、最终颜色合成消费。提供者缺失、抛出异常或返回不合法纹理时透传当前 HDR，通过 `BokehDepthOfFieldUnavailableReason` 说明原因；不会再叠加旧 DOF。

`SceneDepthData` 只包含登记且通过层筛选的 opaque／cutout 表面，不能自动视为整个相机的深度。只有在宿主能保证它覆盖全部相关颜色、没有未处理的透明／自定义顶点位移，并且与时域输出对齐时，才可在回调内用它的 `TryGetFrame` 获取 `linearDepth`。本模块不替宿主作这个判断。

## 采样与成本边界

30 档使用中心加 7／9／13 个环采样，43 档使用中心加 7／14／21 个环采样。两档都有完整的径向分布，低档没有截取高档数组的前 29 项；前景覆盖率分别除以 30 和 43。内环七点还供填洞步骤使用。`bladeCount`、`bladeCurvature`、`bladeRotation` 控制自己的多边形孔径模型。

这是独立采样布局，不是讲座未公开的 30 点原始坐标，也不声称复现其引用论文的可分离多边形算法。散景大小使用图像高度比例，横轴按完整输入的宽高比换算，包括奇数尺寸；半分辨率的离散采样误差仍然存在。

| 模式 | 自有目标 | 全屏 draw | 每个 blur gather 的颜色采样预算 |
| --- | ---: | ---: | ---: |
| 仅远景（`nearBlur = 0`） | 6 | 6 | 30 或 43 |
| 含前景 | 11 | 11 | 近、远各 30 或 43 |

这里统计的是 blur gather，未把 CoC、prefilter、膨胀、填洞、后滤波、合成的读取冒充为零。两档会跳过没有对应 CoC 的像素；降低采样预算不等于已经测得帧率提升。

当前实现使用 float32 中间结果保证可检验的 HDR／CoC，不是移动端内存优化版。目标尺寸为全分辨率、向下取整的半／四分之一／八分之一分辨率，最小 1×1；不复用调用方深度或历史缓冲。1920×1080 时仅远景自有目标约 71.19 MiB，包含前景约 95.54 MiB，均不含输入及其他后处理缓冲。多相机应分别持有模块实例。

shader 显式选择 Point／Linear Clamp 的 LOD 0 采样，不继承输入纹理的 wrap／filter／anisotropy 或全局各向异性质量设置。这样 30／43 的预算不会暗中变成导数相关的各向异性采样。

新前景膨胀从 prefilter 的 alpha 读取 CoC，后续标量目标从 R 读取。它不再让物体红色通道决定前景膨胀；旧 shader 因兼容要求未作同样修改。

## 验证边界

公开 Player 自检中的 `VerifyBokeh` 执行实际 GPU 绘制，覆盖手动范围、物理镜头、近远强度、两档 gather、红通道为零的前景、HDR／alpha、清晰主体与背景分离、退化尺寸、资源生命周期和实际后处理回调。源代码测试只验证 API／默认兼容边界，不代替这些 GPU 控制。

数值参考须考虑硬件纹理寻址精度。D3D11 规范要求至少 8 位小数寻址，并允许浮点转定点的 0.6 ULP 误差；测试将其传播为局部采样区间，不删除边缘像素，也不把单一理想取整方式的误差直接判作画面错误。[Microsoft D3D11.3 规范](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm#3.2.4.1%20FLOAT%20-%3E%20Fixed%20Point%20Integer)

本机 RTX 5090／D3D11 原生捕获还通过只读回放中的 `Load`／`SampleLevel` 对照，测得保持行列边际权重的 8 位双线性交叉权重量化。原生整图参考单独传播了这一局部误差；这项实测不属于上述寻址规范的保证，也不代表其他驱动。验收分别记录相对单一取整参考的最大差和超出局部采样区间的残差，不能把后者冒充原始像素误差。

完整动态人物／发丝／透明材质的景深质量、所有场景适配器、移动端／Vulkan／Metal 的画质和计时仍需单独验证。当前文档描述接口与验收范围，不表示整个 P02 或整个技术框架已追平。
