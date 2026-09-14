# SRP Tile 平面反射

`SrpTilePlanarReflection` 在关闭当前 Tile 场景 pass 后，显式绘制镜像角色／发光网格，再把 RGB 辐射与 A 覆盖投影到当前可见接收面。它对应管线 PDF 45–46 页的廉价独立捕获和 SSR 排除区域，以及 PPT 114 页的镜像用途；不加载或公开原版实现。默认关闭，不改变 Photo Studio 的 Built-in 摄影路径。

## 接入顺序

宿主持有一个相机对应的 producer、SSR resolver 和角色材质适配器。下面省略创建相机、场景表面和输出目标的代码；`scene` 是同一相机由 `TileSceneRenderer.TryPrepare` 创建的当前票据。

```csharp
var settings = new SrpTilePlanarReflection.Settings {
    enabled = true,
    planePoint = mirror.position,
    planeNormal = mirror.forward, // 指向主相机所在的一侧
    receiverGroup = 7,
};
var planar = new SrpTilePlanarReflection(camera, settings);
var actors = new ActorPlanarCaptureSet();
var reflections = new SrpTileReflection(camera, new SrpTileReflection.Settings {
    enabled = true,
    sceneOnlyInput = true,
});

// 动画完成后，在宿主当前 ScriptableRenderContext 内：
var reflectedView = camera.worldToCameraMatrix *
    PlanarReflection.ReflectionMatrix(settings.planePoint, settings.planeNormal);
if (!actors.TryRefresh(actorRenderers, reflectedView, captureLighting, out var error))
    throw new System.InvalidOperationException(error);
settings.draws = actors.Draws;
if (!scene.TryRecord(context, out _, out error) ||
    !planar.TryRecord(context, scene, sequence, out var mirrorFrame, out error) ||
    !reflections.TryRecord(context, scene, sequence, sceneRevision, mirrorFrame, out var result, out error))
    throw new System.InvalidOperationException(error);
// result.color 是合成后的 HDR。宿主负责后续 pass、呈现和 context.Submit()。
```

Tile 需导出 `normalIdentity`（Half4）、`materialMos`（UNorm8）和几何预处理深度；SSR resolver 还需要 `materialBase`（sRGB8）及几何法线。`sceneOnlyInput` 声明主输入尚未包含角色与间接镜面项，不会自动替宿主删除已有光照。原有不带 Planar 参数的 resolver 重载保留。

## 绘制与覆盖

- producer 使用自己的禁用相机、镜像 view 和 oblique near plane，只绘制 `draws` 的显式顺序，不依赖主相机可见性，不调用 `Camera.Render()` 或 `Submit()`。画外蒙皮更新由宿主负责。
- `context.SetupCameraProperties` 设置镜像相机所需全局变量，捕获后恢复源相机；不使用标为不适用于 SRP 的 `SetViewProjectionMatrices`。`hostInvertCulling` 是宿主明确提供的进入状态，命令内反转后恢复，不写 `GL.invertCulling`。
- Draw 材质必须适合独立的廉价 Forward 捕获，光照输入显式提供。没有自定义 coverage 的普通 opaque／cutout Draw，其顶点缩放、剔除、纹理 alpha、UV 和 cutoff 必须与 `SceneDepthData.Surface` 一致。透明度、stencil 或复杂位移须提供对应 coverage pass，不能从任意原材质猜测。
- 颜色绘制结束后仅清 A，再清 depth／stencil、按原顺序重放 coverage。源材质 A=0 的不透明发光面仍可有 A=1 的反射覆盖。`ActorPlanarCaptureSet.Draws` 提供角色的匹配颜色与 coverage pass，包含独立材质快照与眼部 stencil；生产者不会改共享主材质。
- 当前 Tile 深度重建可见世界位置，以接收组和距平面容差筛选。前景物体即使与镜面同组，只要离开平面也不会贴上镜像。`maximumRoughnessMip` 使用贴花后 smoothness 的 `(1-s)^2`；颜色和 coverage 共用 mip，读取后 RGB 除以 coverage。

提供有效的当前 Planar 票据时，SSR trace 和粗糙度过滤跳过其覆盖区域；统一 resolve 在相同的高分辨率法线扰动像素及接收组内执行 Planar → SSR → Probe。Planar alpha 控制覆盖混合，不包含 Fresnel；最终材质响应仍来自当前 Tile 材质。空 Draw 数组会写出透明捕获，避免沿用上帧角色。

## 生命周期与范围

票据绑定同一 Tile 对象、相机和正递增 `sequence`，不接受调用者随意塞入一张反射纹理。生产者下一次成功记录、资源失效或 Dispose 会使旧票据失效；消费者票据也依赖生产者票据。记录成功不等于 GPU 已完成。宿主须保留源内容、renderer、材质快照和纹理，等待 GPU 完成后才能刷新、调整尺寸、复用或释放。

每个 producer 当前只处理一个平面、一个接收组。资源是半精度 RGBA 捕获完整 mip 链、显式 D32S8 深度／stencil、主分辨率 Half4 投影。名义纹理预算计入深度每像素 8 字节，不以 `RenderTexture.depth=24` 推断实际分配；不包括驱动对齐、材质、CPU 内存或 GPU 驻留开销。

当前范围是桌面 D3D11／Vulkan、Linear、完整固定尺寸视口、非 MSAA／XR。box mip 不等于 GGX 预过滤。多平面调度、完整应用角色排序、移动／Metal 性能和原版全角色画质仍未由本模块完成。

诊断入口为 `--self-test-tile-planar <output-directory>`；有自备合法角色资产时，还可用 `--photo-mode --validate-srp-planar-character <output-directory>` 检查真实角色四方向、蒙皮跳转、材质细节负对照和反射优先区域。该入口不发布游戏资产，也不会默认运行。

## 已执行的桌面验收

D3D11／Vulkan 各执行 42 组配置、606 条具名运行条件。包含画外发光面、遮挡／裁剪、倾斜平移平面、正交相机、半分辨率、roughness mip 与贴花、九类简化角色材质、cutout／MPB／眼部 stencil、相机及剔除恢复、预算和票据失效。相同地板在关闭 Planar 时产生 898 个真实 SSR 命中，其中 889 个在 Planar 覆盖启用后从 trace 排除；部分覆盖仍保留未覆盖区域 SSR，并验证 Probe 和大幅法线扰动回退。

投影检查从独立 CPU 相机射线与世界平面反射推导 UV／LOD，不借用 producer 的捕获矩阵或投影输出作为预期值。测试专用 shader 只在这些查询上采样真实捕获纹理；CPU 再计算接收资格、RGB／A 与强度。保留错误 UV／LOD 负对照。硬件纹理过滤具有有限精度，不能用理想 CPU 三线性插值冒充逐位参考；该检查隔离了引擎硬件采样原语，没有复现或拟合厂商内部过滤权重。依据见 [Direct3D 过滤精度规范 §7.18.16](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)。

84 份原生捕获检查实际颜色／A 重放、D32S8、完整 mip、当前生产者字节、Draw／Dispatch、全部投影／反射字段及独立材质／resolve／合成算术。所有像素参与投影与合成误差检查；解析矩形捕获检查仅排除预先定义的图元边界带。真实自备角色另有每 API 23 条条件；范围限于该角色／服装与简化捕获，不代表原版全角色画质。

相关接口：[SetupCameraProperties](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.ScriptableRenderContext.SetupCameraProperties.html)、[SetInvertCulling](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetInvertCulling.html)、[SetViewProjectionMatrices 的管线限制](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetViewProjectionMatrices.html)。其余框架开项见[技术清单](framework-techniques.md)。
