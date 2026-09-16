# FSR1 空间重建

默认关闭的空间重建模块。以下验收范围为桌面 D3D11 自制场景；不声明完整制作场景或移动性能达标。

`FsrRenderer` 通过已安装 UPM Core 的 AMD FSR1 头文件执行 EASU 和可选 RCAS。
本模块提供输入编码、裁剪、资源管理和相机适配，不包含原游戏 shader。算法来源和分发声明见
[第三方声明](../packages/com.digital-kotone.toolkit/ThirdPartyNotices.md)。

## 独立图像输入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var settings = new FsrSettings {
    enabled = true,
    quality = FsrQuality.Quality,
    backend = FsrBackend.Auto,
    encoding = FsrInputEncoding.LinearLdr,
    sharpen = true,
    sharpnessStops = .2f
};
var upscaler = new FsrRenderer(); // 由宿主持有，跨帧复用。
var outputSize = new Vector2Int(1920, 1080);
settings.TryGetRenderSize(outputSize, out var renderSize);
// 宿主应真正以 renderSize 绘制场景，并先完成抗锯齿、调色/色调映射。
var viewport = new RectInt(0, 0, renderSize.x, renderSize.y);
if (upscaler.TryRender(antialiasedColor, viewport, outputSize, settings, out var frame)) {
    Graphics.Blit(frame.color, presentationTarget);
    // 最后由宿主绘制全分辨率 UI、噪声/颗粒等。
} else {
    Debug.LogWarning(upscaler.UnavailableReason);
}
// 宿主关闭时 upscaler.Dispose()。
```

四档输出／输入每轴比例为 1.3、1.5、1.7、2。输入尺寸向上取整；例如
1920×1080 的 Quality 输入为 1280×720。低层接口允许每轴 1–2 倍的显式尺寸，
不根据 `quality` 再缩放一次。`TryGetRenderSize` 只计算尺寸，不替宿主降低渲染分辨率。

输入是已创建、单层、无 MSAA／mip／动态缩放的 2D 纹理；元数据必须为线性。
支持 RGBAFloat、RGBAHalf、RGBA8 UNorm 和无符号 RGB111110Float。
`viewport` 是整数 texel 范围，先复制到独立附件，再进行重建，所以边界采样不会读到
外围未使用区域。不会改变源纹理的过滤／寻址设置。输出尺寸每轴限制为 1–4096。

`encoding` 描述数值含义，不能靠纹理格式猜测：

| 编码 | 进入 EASU／RCAS | 输出 |
| --- | --- | --- |
| `LinearLdr` | 非负线性 RGB 限制到 0–1，再开平方 | 重建值平方，返回线性 LDR |
| `PerceptualGamma2Ldr` | 已经为 gamma2 的 0–1 RGB | gamma2；不是自动 sRGB 解码 |
| `LinearHdr` | 显式可逆压缩 `rgb/(1+max(rgb))`，再开平方 | 平方后逆变换，返回 HDR |

HDR 模式用于色调映射之前的兼容接入；与推荐的色调映射后 FSR 部署有区别，
高亮精度与锐化仍需单独验收。`accurateRcasNormalization=true` 只把 RCAS 归一化的近似倒数
改为准确除法，避免小幅常量偏差在 HDR 逆变换中放大；滤波器及限幅仍执行外部 AMD 实现。
关闭此项保留上游近似算术作对照，不建议用于 HDR 逆变换。有限输入上限为 65504；负数裁到 0，NaN／无穷值归零。
alpha 单独以四次 clamped Load 和 FP32 双线性权重重采样，并限制到 0–1；避免硬件采样器的子像素权重量化影响细密 alpha。FSR 不重建透明覆盖率，建议先完成场景合成。

### 可选近等亮度稳定化

`stabilizeLumaGradients` 默认 `false`，保留原有 AMD EASU 变体。
设为 `true` 会选择一个独立 shader keyword 变体：对 EASU 的近零亮度梯度
归一化分母设置 `2/4096` 下限。这里的亮度是经过压缩／gamma2 准备后的
`b*.5 + (r*.5 + g)`，范围 0–2；该下限是显式的 12-bit 感知梯度噪声尺度，
并非原版游戏参数或 AMD 默认算法的一部分。该尺度相当于亮度满量程的
`1/4096`，只限制梯度归一化，不将输入 RGB 量化成 12-bit。

用途是避免除法／平方根的 Float32 舍入，在近等亮度、不同色度的区域被
梯度比值放大成强边缘权重。它保留 EASU 的采样、重建核、去振铃及 RCAS，
但会改变极弱亮度梯度的边缘强度，不能声称与未经修改的 FSR 逐像素相同。
正常梯度及其他 EASU 倒数输入超过下限时不受该分母限制影响。
该扩展独立开关，不替代 TAA，也不解决极亮 HDR 的逆变换精度限制。

该变体已在 [显式整帧宿主](desktop-host.md#可选整帧-fsr) 的自制 D3D11／Vulkan
四档 HDR 链路中验证。未开启时仍执行原有变体，原摄影路径和独立模块默认不变。

## 实际低分辨率相机

`FsrCameraRenderer` 是显式的手动渲染宿主，不会自行挂组件或替换应用的默认 presenter。
相机必须使用 Built-in 管线，且 `enabled=false`、`allowMSAA=false`、非 XR、完整 viewport。宿主调用一次就实际
执行一次低分辨率 `Camera.Render()`，随后恢复相机 target 和 aspect；输出是调用者的完整尺寸附件。

```csharp
var cameraRenderer = new FsrCameraRenderer();
camera.enabled = false;
camera.allowMSAA = false;
if (!cameraRenderer.TryRender(camera, presentationTarget, settings, out var result,
        temporalSource: sceneDeferredCamera)) {
    Debug.LogWarning(cameraRenderer.UnavailableReason);
}
```

`temporalSource` 必须属于同一相机。启用场景 TAA 时，它消耗本次低分辨率几何、motion
与当前颜色；jitter 和统一时钟仍由宿主管理。FSR 不生成 TAA，也不把抗锯齿不足的输入
自动变成干净图像。切换质量／尺寸会让已有场景 TAA 依据实际附件尺寸重建历史。

存在启用的 `OriginalStyleRenderPipeline` 时，手动宿主要求 `LinearHdr`，并使用该 pipeline
自身的 TAA 绑定，不能再同时传另一套 temporal 参数。可选路径为：

`低分辨率场景 → TAA → DOF → Motion Blur → Bloom 合成 → FSR → Diffusion → 最终调色 → 全尺寸输出`

Bloom 在重建前合成一次，最终合成不再重复加 Bloom。这个可选顺序不会改动没有 FSR
请求时的摄影路径或既有超采样。已有超采样 presenter 占用相机时会拒绝请求；不要让两个
宿主争抢同一 target。UI 由调用者在上述输出之后绘制。

## 后端、所有权和预算

Compute 执行三次 dispatch：准备／EASU／RCAS 解码。Raster 执行相同算法的三次全屏 draw，
也要求 SM4.5。可显式选择 Raster；Compute 不可用时，只有允许回退才使用 Raster。
能力不足会返回失败原因，不会默默换成双线性。

三个附件均为 RGBAFloat，裸颜色预算为 `16×(输入像素数+2×输出像素数)` 字节。
手动相机另有低分辨率 HDR／深度；前置 Bloom 桥接另有低分辨率 HDR。`memoryBudgetMiB`
在分配前检查这些模块拥有的附件，不包括场景/TAA/驱动分配。相机深度预算按每像素 4 字节
分配为 D24S8 并验证实际格式；不支持该格式时明确失败。此初始精度配置不能据此声称适合移动端。

`Frame` 是借用，不要释放其附件，也不要把它们作为同一个 renderer 的下一次输入。
下一次调用（包括关闭／失败）、释放或附件丢失都会让旧 `IsCurrent` 失效。关闭／失败释放
模块拥有的附件，不释放外部输入或目标。调用者必须检查返回值，不能把旧输出当成成功的新帧。

## 已验证范围与限制

实际 Player 检查覆盖 96 组裁剪／奇数／极小尺寸／四档／编码／锐化组合，144 组黑白、脉冲、阶梯、
非有限输入与 HDR 图案，以及 30 组常量信号。全图逐通道比较独立标量实现与 Compute／Raster，
另测四种支持的实际输入 RT 格式、释放／重用／预算／相机重入与失败恢复。标量参考包含本次 D3D11
原生 float32 坐标舍入；其他后端需要自己的数值实测。

四档各执行 48 帧真实低分辨率相机抖动，以另一台零历史相机和实际 4 倍每轴渲染的全图 box 参考作对照。
最后 16 帧的 TAA＋FSR 整图 RGB 误差总量为去抖动但不积累历史的 FSR 对照的 0.731 倍；
逐帧变化为 0.099 倍。细密纹理仍与高分辨率参考有差异，不据此宣称角色／舞台画质追平。

实际完整后处理链检查 TAA／完整注册深度／DOF／Motion Blur／Bloom／FSR／Diffusion／自主调色；
常量 Bloom 守恒、非恒定整图独立合成和错误次序对照均执行。原生 UGUI 的 33×17 个一像素棋盘格
在 FSR 后绘制，与独立原生 UI 的全图合成完全一致；错误地先缩小 UI 再 FSR 会丢失细节。

RenderDoc 实际捕获确认三次 Compute dispatch 的生产／消费资源、FP32 视图、低尺寸 TAA 输入、
Bloom 前后关系、重建后的三次 Diffusion draw 和完整尺寸调色。另测 1281×721 与 1920×1080 的四档。
下表为 RTX 5090／Tuanjie 2022.3.62t15／D3D11 的 **三次重放 GPU 事件计时中位数**，仅包含准备、
EASU 和 RCAS／解码之和；同步读回、3D、TAA、后处理、UI 和呈现不计入。捕获边界有诊断专用的一像素
同步读回，运行模块没有读回。该结果不能换算为应用 FPS 或手机收益。

| 1080p 档位 | 实际 3D 输入 | 三次 FSR dispatch 合计 | 相机＋FSR 自有附件 |
| --- | --- | --- | --- |
| UltraQuality | 1477×831 | 0.073 ms | 105.42 MiB |
| Quality | 1280×720 | 0.068 ms | 94.92 MiB |
| Balanced | 1130×636 | 0.065 ms | 87.96 MiB |
| Performance | 960×540 | 0.066 ms | 81.08 MiB |

内存列不含外部输出、场景、TAA、驱动或前置 Bloom 合成附件；后三者及其他后处理另有开销。
当前 FP32 保真版本尚未做移动内存优化。极亮 HDR 的逆变换会放大有限精度：64000 常量的本次最大相对误差
约 1.6%，即使准确 RCAS 归一化也不等于无损 HDR。复杂动态角色／透明层、制作场景、Vulkan／Metal、
移动功耗、Raster GPU 成本和完整应用净收益仍未完成验收。
