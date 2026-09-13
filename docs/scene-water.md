# 独立水面与有光照的屏幕透射

`SceneWaterRenderer` 是默认关闭的显式水面阶段。它接受宿主当前不透明 HDR、对应深度、相机及按从后到前排列的表面，输出借用的完整分辨率 HDR。无需摄影 UI、资产清单或原版 shader。

PPT 109 分别列出水面与钻石折射 shader，并明确省略个别实现细节。本模块采用自己约定的薄表面模型，不声称恢复原版公式。正弦波与解析斜率的通用思路可参考 [NVIDIA GPU Gems 第 1 章](https://developer.nvidia.com/gpugems/gpugems/part-i-natural-effects/chapter-1-effective-water-simulation-physical-models)；代码和参数在本包独立实现。

## 宿主接口

```csharp
using GakumasPhotoMode;
using UnityEngine;

var water = new SceneWaterRenderer(); // 宿主持有，结束时 Dispose()
var options = new SceneWaterSettings { enabled = true };
var surface = new SceneWaterSurface();
surface.surface.renderer = waterRenderer;
surface.surface.inputs.albedo = new Vector3(.03f, .05f, .06f);
surface.surface.inputs.mos = new Vector3(0, 1, .9f);
surface.absorption = new Vector3(.3f, .08f, .04f);
surface.reflectionProbe = linearHdrCube;
options.surfaces = new[] { surface };
options.lighting.localLights = authoredLights;

// 水面图层必须从此相机的原生 cullingMask 排除，避免绘制两次。
// opaqueColor / opaqueEyeDepth 必须来自同一次相机视图和尺寸。
camera.Render();
options.seconds = playbackSeconds;
if (water.TryRender(opaqueColor, new FogVolumeDepth(opaqueEyeDepth),
                    camera, options, out var frame))
    Graphics.Blit(frame.color, destination);
```

显式流程为当前不透明颜色和深度、当前 Planar、完整分辨率水面、后续透明和重特效、后处理。此模块不自动插入 `OriginalStyleRenderPipeline`，不改变相机、图层、共享材质或全局 shader 参数。完整深度注册是宿主责任，只有远裁剪值的空深度图无法表示水底／前景遮挡。

## 材质约定

- 仅非金属表面。`surface.inputs` 复用 Forward 的线性 Albedo、RGB MOS、Emissive、alpha、UV 和 GI。点／胶囊／面／Spot 光源、GPU tile bitset、主光与局部阴影使用既有共享光照资源。此处的直接光 BRDF 保留框架的 `.04` 介电反射率约定；`indexOfRefraction` 控制环境反射分配，二者不构成完整物理 BSDF。
- `absorption` 为每世界单位 RGB 衰减系数。厚度为当前像素不透明眼空间深度减表面深度，换算为观察射线长度，再限于 `maximumThickness`。`scatteringRadiance` 为独立常量源颜色，透射为 `background * exp(-absorption * thickness) + scattering * (1 - exp(...))`。
- 环境响应采用 Schlick 近似。显式线性浮点 Cubemap 为回退，不自动猜测 RGBM／Unity HDR 编码。Mip 与 smoothness 线性映射，不宣称 GGX 预过滤。
- `waveA/B` 表示两个 UV 正弦高度场的解析斜率，只改变法线，不移动顶点。`xy` 是角频率向量，`z` 是 UV 单位高度，`w` 是角速度。显式 `seconds` 可倒放／跳转，CPU 将时间相位约简后绑定，不读全局时间。
- `normalMap` 是线性 RGB 切线空间法线，支持显式平移与强度。要求法线、切线和 UV0；不猜测 DXT5nm／导入器打包方式。手动绘制显式传递变换奇偶性，保留当前 GPU 蒙皮属性。
- 折射偏移采用视空间法线差、厚度和 `refractionPixelsPerUnit`。它是屏幕偏移模型，参数单位是像素／世界单位，分辨率改变时外观不自动恒定。四个采样点分别排除画外、非有限颜色和比水面更近的深度，剩余权重归一；全部无效则使用原像素。没有画外追踪、Snell 路径或多次内部反射。
- `shoreFadeDistance` 按厚度软化接触边缘。材质 alpha 是覆盖率，和光学透射分开；颜色只合成一次，alpha 使用 over 规则。多个表面按宿主顺序逐层读取上一层颜色，仍共用不透明深度，不能据此推导相交水体的光学输运。

## Planar 接入

将 `surface.planarReflection` 绑定到同相机当前 `PlanarReflection`。该 producer 的水面 receiver 设置 `allowExcludedLayer = true`，允许接收面位于相机原生排除图层；默认 `false` 保留旧行为。接收面仍须处于反射平面内，真实不透明深度仍遮挡接收面。

完整分辨率 Planar radiance／coverage 按几何与最终法线差偏移，覆盖之外混合 Cubemap。相机、尺寸或渲染时序不匹配时回退到 Cube；没有 Cube 则反射辐射为黑色。不会重复添加已经乘过 BRDF 的间接光，也不会把背景 SSR 输出冒充水面自己的反射。

## 生命周期与范围

输入要求固定、线性、非 MSAA、非 XR 的 2D HDR，最大每轴 4096。深度接受 RFloat／RHalf 线性眼深度，或显式 Device 深度。每次调用使旧 `Frame` 失效，失败／关闭释放所有自有资源；结果不允许作为同实例下一次输入。调用结束恢复活动渲染目标。两张完整尺寸 RGBAFloat 颜色目标合计 `width * height * 32` 字节，灯光 bitset／buffer 和阴影附件另计。

运行时不读回 GPU、不自动发现材质、不 CPU BakeMesh。桌面验收与当前待完成项目记录于 [技术清单](framework-techniques.md)。钻石折射、真实制作水面／完整舞台、位移顶点与时域数据、折射后的雾／体积光、多层相交、SSR 水面接收、移动内存与实机帧时仍是独立缺口。

桌面验收包含透视／正交的独立整图 Schlick／吸收参考、当前局部灯光、正负变换切线奇偶性、三组原生蒙皮／blendshape 对照、真实前景深度保护、Planar／Cube 覆盖合成、GI／两套阴影、显式时间回放及两层顺序合成。另有独立四采样点屏幕透射参考、相反 Y 轴负对照、63×41 的 Float／Half／RGB111110 输入，以及 513×289 的水平水池场景。

透射坐标转换同时考虑 GPU 投影和视口方向，避免 RenderTexture 已翻转后再多翻一次 Y。CPU 验收在 D3D11 上按 8-bit subpixel 网格与 top-left 规则计算边界，不把光栅覆盖差异归咎于材质格式，也不扩大颜色容差；规则见 [Microsoft D3D11 功能规格](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)。这些对照验证本模型及桌面路径，不代表原版画质或移动平台验收完成。
