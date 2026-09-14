# Tile 几何法线与材质贴花

`TileSceneRenderer` 可显式生成未叠加法线贴图的几何法线／SSR 资格，并用当前深度把材质贴花写入场景 GBuffer。复用 `SceneDeferredCamera.Decal` 参数；默认关闭，不修改摄影应用或既有 Built-in Deferred 的行为。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

// sceneSettings 已提供显式 surfaces 和已创建的 packed HDR output。
sceneSettings.geometryDepthId = true; // 只需要几何数据时也可单独开启
sceneSettings.reflectionSmoothnessThreshold = 0.5f;
sceneSettings.reflectionExcludedLayers = actorLayers;
sceneSettings.decals = new[] {
    new SceneDeferredCamera.Decal {
        receiverGroup = 7, // 接收面的 Surface.receiverGroup 同样设为 7
        localToWorld = Matrix4x4.TRS(position, rotation, size),
        inputs = new SceneDeferredCamera.MaterialInputs {
            albedoMap = colorAndCoverage,
            normalMap = linearRgbNormal,
            mosMap = metallicOcclusionSmoothness,
            albedo = Vector3.one,
            mos = Vector3.one
        },
        albedoWeight = 1,
        normalWeight = 0.6f,
        mosWeight = new Vector3(0.2f, 0.4f, 0.8f)
    }
};
```

继续使用 [TileSceneRenderer 的 prepare／record／Submit 与生命周期约定](tile-scene.md)。有效贴花会自动要求几何预处理，无需同时开启 `positionLighting`；局部灯与实时阴影仍须显式开启位置光照。所有项目相关对象、纹理和参数由宿主提供，不扫描场景或自动收集角色。

`localToWorld` 将单位盒 `[-0.5,0.5]` 变换到世界空间；局部 XY 为贴图平面，局部 Z 为高度轴。支持旋转、非均匀缩放和 `inputs.uvST`。`receiverGroup` 是精确的 1–255；表面组 0 不接受贴花。同深度几何按显式绘制顺序决定可见组，遮挡与 cutout 使用当前几何的相同配置。

## 数据与混合规则

`frame.EyeDepth` 是独立的 `R32_SFloat` 正 eye depth。`frame.GeometryDepthId` 是 `R8G8B8A8_UNorm`：RGB = `worldGeometricNormal * 0.5 + 0.5`；A 为 0 或 1，表示原表面 MOS smoothness 达到阈值且所在层未被排除。法线不使用 normal map，SSR mask 不随贴花修改。这是自主编码，不解析原版 MaterialID。

两个输出均属于 frame。调用者在记录／提交及必要的 GPU 同步之后才能读取；不得释放、覆盖或在 frame 释放后继续使用。读取法线需要解码再归一化；UNorm8 的中点量化会使平坦面的切线分量略偏离零。

每张贴花由三个有序绘制组成，与主几何位于**同一个五 MRT subpass**。它们只普通采样已完成的预处理纹理，通过固定功能混合读写当前颜色附件，不在 shader 中反馈采样 GBuffer，不分配 DBuffer 或全屏 ping-pong。三个绘制分别处理 metallic、occlusion、smoothness 的独立权重；albedo、normal、emission 只在第一个绘制中修改。

| 字段 | 混合与保留 |
| --- | --- |
| G0 RGB | 线性空间 albedo 插值，再编码为 sRGB8；A 的烘焙遮罩不变 |
| G1 RGB | 三个 MOS 分量分别插值；A 的 GBA332 遮罩不变 |
| G2 RGB | 投影切线基相对于几何法线构造，法线方向线性插值，光照消费者再归一化；A 的组／GI 标记不变 |
| G3 RGB | HDR emission 插值，后续光照仍加到该附件 |
| G4 | 三次绘制均关闭写入，保留 GI 直到原光照／resolve 消费 |

覆盖率为 `albedoMap.a * inputs.alpha`，再乘对应通道权重。`heightOcclusion` 只调整 AO 插值覆盖，使用本工具包的独立高度密度模型，不宣称还原未公开的原版公式。投影朝向用几何法线与 `minimumFacing` 比较，不随接收面的法线贴图摆动。退化切线基跳过法线修改。

多张贴花按数组顺序混合，结果不满足交换律。Half／UNorm8／packed HDR 的每步精度会影响结果；Tile 法线方向混合与既有 Built-in 的逐层归一化模型不保证逐位相同。Half 附件的固定混合中间运算也可能采用 Half 精度，不能只在最终结果处量化一次。参见 [Vulkan 混合精度](https://docs.vulkan.org/spec/latest/chapters/framebuffer.html) 与 [D3D 输出合并阶段](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-output-merger-stage)。

实际 UI／HDR Monitor 可作为 `inputs.emissionMap`：先在引擎进入 SRP request 前调用 `SrpHdrMonitor.TryPrepare`；在有效 context 中记录 Monitor，取得当前发布纹理，再绑定贴花并准备／记录 TileScene。宿主仍负责恢复接收相机的 context 状态、Submit 与借用纹理生命周期。不能把过期 Frame 当作当前内容；详见 [Monitor](hdr-monitor.md)。

## 成本、拒绝与验证边界

无有效贴花、`geometryDepthId=false` 时不增加贴花绘制、法线目标或 stencil：普通路径仍为主附件 28 bytes/pixel；已有位置路径仍另加 8 bytes/pixel。几何法线预处理包含 R32、RGBA8 与 D32，共 12 bytes/pixel。启用贴花时主深度改为 D32S8，主附件名义 32 bytes/pixel，加预处理合计 44 bytes/pixel；单独开启几何法线合计 40 bytes/pixel。两部分共用 `maximumAttachmentMiB` 检查，主颜色预算仍为保守 256 bits。以上不是实测带宽、驻留或驱动总分配。

最多 256 个显式贴花条目。关闭或所有权重精确为零的条目不执行。有效条目要求有限可逆仿射变换、合法权重／接收组、有效 2D 非 MSAA 纹理。只开放桌面 Vulkan 与显式允许的 D3D11 仿真，要求独立 MRT 混合、各颜色格式的 Blend 支持以及 D32S8；能力不足明确返回错误，不换用不同效果的隐式后端。

`--self-test-tile-decal <输出目录>` 使用无原版资产的隔离 SRP，覆盖 34 组当前深度／几何法线／mask、各材质通道、独立权重、顺序、非均匀投影、移动相机、同平面组、遮挡／cutout、GI／遮罩、位置光照与真实 UGUI Monitor 更新。保存整图颜色、映射法线、几何法线／mask 和深度；另有非法输入、零权重和生命周期检查。

34 组配置分别在本机 D3D11／Vulkan 执行，各有 296 项运行条件与 136 张字段预览；68 份原生捕获逐绘制验证 MRT 资源、写掩码、stencil 和完整通道字段，再检查贴花后的 PBR。几何按独立射线／平面求交；格式边界先限定运算与各级量化，不剔除边缘像素。D3D11 Direct 仅用于捕获诊断，已知退出时目标绑定释放警告仍保留，不能据此推荐生产使用该模式。

贴花采用同 subpass 有序混合，避免引入新的跨 subpass 固定混合依赖。既有方向光／resolve 仍通过引擎生成 subpass 依赖；本次原生布局与数值对照不替代它们在其他驱动上的同步验证。桌面 Vulkan 成功不能推导所有 tile GPU 的缓存行为。

当前范围不包括完整制作场景、动态水面、反射消费桥接、一般蒙皮贴花组合、Metal／移动实机及性能收益。原表面 SSR 资格与几何数据已提供，不表示 SSR／Planar 已接入这一 Tile 生产者。默认摄影管线保持原样。
