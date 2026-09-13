# 特效几何的当前 Forward 光照

`LowResolutionFxRenderer` 和 `HeavyFxRenderer` 可以照亮 Full／Half／Quarter 工作目标中的当前透明网格。两级开关默认关闭；未选择光照的特效仍走原有 shader，不分配新光源缓冲或阴影目标。角色的 `ActorToon` Forward 路径不变。

这项独立实现把 [透明 Forward+](scene-forward-plus.md) 的当前材质、GI、光源集合和阴影接到 [低分辨率几何](low-resolution-fx.md)／[联合特效](heavy-fx.md)。PDF 第29–32页给出的全尺寸透明、降采样透明／扭曲、再上采样顺序用来约束组合位置；讲演未公开的剔除和修复策略使用本工具自己的契约。

## 接入

```csharp
var geometry = new LowResolutionFxSettings { enabled = true };
geometry.lighting.enabled = true;
geometry.lighting.localLights.enabled = true;
geometry.lighting.localLights.lights = new[] {
    new SceneDecalLight {
        position = new Vector3(0, 1, -2), range = 4,
        radiance = new Vector3(2, 1, .5f)
    }
};
geometry.surfaces = new[] {
    new LowResolutionFxSurface {
        renderer = effectRenderer, resolution = FxResolution.Half,
        opacity = .7f, linearRadiance = Vector3.one,
        lighting = new FxSurfaceLighting {
            enabled = true,
            inputs = new SceneDeferredCamera.MaterialInputs {
                albedo = new Vector3(.5f, .3f, .2f),
                mos = new Vector3(0, 1, .4f), alpha = .6f
            }
        }
    }
};

// 与旧 API 相同：source 是已画好的线性 HDR，depth 是匹配的当前深度。
using var renderer = new LowResolutionFxRenderer();
if (renderer.TryRender(source, depth, camera, geometry, out var frame)) {
    // 在下一次 TryRender／Dispose 前消费借用的 frame.color。
    Graphics.Blit(frame.color, destination);
}
// 联合版本：new HeavyFxSettings { enabled = true, geometry = geometry, ... }
```

宿主负责数组中的远到近顺序、源资源寿命，以及避免原生相机重复绘制这些特效。独立生成的粒子网格可用 `mesh` + `localToWorld`；不是把 `ParticleSystemRenderer` 隐式转换为网格。`renderer` 使用当前 MeshRenderer／原生 SkinnedMeshRenderer，包括骨骼和 blendshape，不调用 `BakeMesh`。本模块不修改共享材质、PropertyBlock、相机 mask 或现有应用设置。

## 材质与合成

照明使用表面自己的 world position、normal、tangent、UV／UV2，不采样后方不透明 GBuffer 的材质。`lighting.inputs`、`lighting.gi` 和 `receiverGroup` 沿用场景材质／GI／灯组契约。

Renderer 与显式 mesh/matrix 都从本次实际矩阵计算切线基的镜像符号；不会因引擎未填写 `unity_WorldTransformParams.w` 丢失法线图 Y 分量。相关行为修正与独立整图检查见 [共享法线基](forward-normal-basis.md)。

- FX 纹理 RGB、可选顶点 RGB、`linearRadiance` 乘在 PBR 输出辐射上，包含 emission；它们不重新解释为 albedo。
- 材质 alpha = albedoMap alpha × inputs.alpha；再乘原有 opacity、FX纹理／顶点 alpha、径向与软交界淡出。`alphaCutoff` 对材质 alpha 生效。
- 先计算 PBR／emission，再做表面雾或体积传输。Alpha表面的体积散射也乘材质 alpha，包括第二盏及之后的介质灯。Additive表面保留原有只透射、不叠加介质散射的语义。
- RGB 使用预乘合成，最终特效输出保留 **source alpha**。这与相机透明组件更新目标覆盖率 alpha 的语义不同。

透明 Alpha／Additive 可以与未照明特效混用。未照明 Distortion 仍是有序屏障；**开启 lighting 的 Distortion 会被拒绝**，尚无独立折射／透射材质模型。也没有透明自阴影或相交透明自动排序。

## 分块、修复与资源

公共 `SceneForwardLightSettings` 供相机与 FX 配置使用；每个 renderer 有独立的当前灯快照、GPU bitset、主光与局部阴影资源。复用参数不等于共享可变 GPU 状态。原 `SceneForwardLightingSettings` 继承公共字段并保留相机 surface 数组。

Compute 网格覆盖完整 **source 尺寸**，不取 `camera.targetTexture` 的尺寸。降低分辨率后的片元按实际 ceil-divided 目标映射回原生全尺寸像素，再查询光源 tile；边缘重画直接使用全尺寸坐标。光照在真实低分辨率几何绘制中执行，不预烘焙到颜色图，也不悄悄把所有特效改为 Full。

HeavyFx 保留现有共享批次、修复 mask、一次上采样和必要的整批边缘重画。介质的标量 Spot 阴影、表面的局部 Point／Spot 阴影、主方向阴影各用独立绑定与 shader 变体，不能拿其中一张图代替另一张。

`LightingSubmittedLights`、`LightingTileCount`、`LightingBufferCount`、`LightingBufferBytes`、`LightingShadowMapCount`、`LightingBackend`、`LightingFallbackReason` 提供灯资源状态。`TargetCount/TargetBytes` 仍是原有 FX 工作目标统计，不包含这些新增灯缓冲／表面阴影；不要把两类统计混为总显存。阴影分辨率／caster上限沿用各自设置。`maximumGridMiB` 仅限制网格，预算不足可显式选择逐灯回退。

启用光照时要求 SM4.5、有效完整视口相机、无 XR／动态分辨率、source 单轴1–4096。光源上限4096、特效表面上限256。Normal 必需，法线图另需 tangent，纹理／径向淡出另需 UV，顶点色另需 Color；显式网格 GI 不能要求 RendererLightmap／SceneProbe 查找。输入失效、已释放纹理、stale Monitor 或禁用会使本次调用失败并释放当前资源；查看 `UnavailableReason`。不支持的输入不会默默退到未照明输出。

## 验证边界

实际 GPU 验证位于 `ActorRenderingSelfTest.FxForward`：两种 renderer 的完整浮点 RGBA Tiled／BruteForce 对照、全尺寸相机参考、当前原生骨骼／blendshape与独立几何、材质／GI／Monitor、双类阴影、混合批次与介质 alpha、实际相机桥接、4096尾灯、奇数尺寸、预算和资源失效。数值容差分别为分块等价 `2e-5`、独立全尺寸合成 `2e-4`；不裁剪轮廓像素以通过对照。降采样重建自身的近似误差单独统计，不能据此声称与 Full 逐像素相同。

这些契约没有证明生产舞台画质、移动 GPU 带宽／帧时、Metal／Vulkan一致性。透明历史、折射／反射、移动附件／subpass、完整粒子系统仍属于后续工作。
