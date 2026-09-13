# 场景透明 Forward+

`SceneForwardLightingCamera` 是默认关闭、可单独接入的全分辨率透明几何路径。它在 Built-in Forward 相机的不透明阶段之后，用当前 GPU 分块光源集合照亮显式提交的透明网格。无需修改 `ActorToon`、共享材质或摄影应用默认配置。

PDF 第13页区分不透明 Deferred、透明 Forward+、角色 Forward；第69页说明角色最终改用限制灯数的 Forward。本模块遵循这个分工，不把角色改成多灯 PBR。讲演没有公开透明光源剔除代码，下述二维保守分块与 bitset 是独立实现。

## 接入

```csharp
var pass = camera.gameObject.AddComponent<SceneForwardLightingCamera>();
pass.surfaceLayers = 1 << transparentLayer;
// 宿主主动排除这些层，模块本身不会修改 cullingMask。
camera.cullingMask &= ~pass.surfaceLayers.value;
pass.settings.enabled = true;
pass.settings.localLights.enabled = true;
pass.settings.localLights.lights = new[] {
    new SceneDecalLight {
        position = new Vector3(0, 1, -1), range = 3,
        radiance = new Vector3(2, 1, .5f)
    }
};
pass.settings.surfaces = new[] {
    new SceneForwardSurface {
        renderer = transparentRenderer, submesh = 0,
        inputs = new SceneDeferredCamera.MaterialInputs {
            albedo = new Vector3(.5f, .3f, .2f), alpha = .4f,
            mos = new Vector3(0, 1, .4f)
        }
    }
};
```

相机须为 Built-in Forward，使用已创建的线性 `ARGBHalf`／`ARGBFloat` 二维目标、至少16位深度、完整视口和颜色／深度清除，无 MSAA、XR、动态尺寸，单轴不超过4096。宿主负责目标和输入资源的存活。`UnavailableReason` 给出拒绝原因；错误会清空本组件的命令并释放私有资源，不替用户改设置或接管其他物体。

表面按数组顺序绘制，宿主应提供从远到近的透明顺序。每个 Renderer／submesh 只能提交一次，必须属于 `surfaceLayers`，且相机不能自行再次画这些层。当前支持带 normal 的 MeshRenderer／SkinnedMeshRenderer 三角形子网格；法线贴图另需 tangent，纹理另需 UV。原生蒙皮／blendshape 顶点由实际 DrawRenderer 使用，不烘焙静态替身。自定义顶点位移、PropertyBlock、ParticleSystemRenderer 不隐式适配。

## 数据流与光源集合

1. 复用场景灯的验证／保守投影 bounds，取得当前 Point／Spot／Capsule／Area、接收组、颜色、衰减和 Monitor 采样参数；不创建 Deferred 光照累加目标。
2. GPU Compute 为每个8／16／32像素方块生成全部光源的 bitset。每32盏灯占一个 `uint`，所有字都在本次 dispatch 覆盖，包括尾部零位。
3. 当前透明几何片元通过原生像素坐标取 tile，按输入索引递增遍历置位灯，使用片元自己的位置、法线、视向和材质计算光照；不拿后方 GBuffer 的材质／深度代替透明表面。
4. 全部灯相加后一次限制到 Half HDR范围，乘 alpha，以预乘 over 合成；`additive` 则只加 RGB，保留目标 alpha。均 `ZTest LEqual`、`ZWrite Off`，所以遵守已有不透明／角色深度。

二维集合覆盖全部深度，不能拿不透明表面的最小／最大深度来剔除其前面的透明灯。投影矩形增加一像素保守边界，允许多算灯，不允许漏灯。每灯还在片元中执行真实距离／形状范围检查。相机穿过灯体时允许保守全屏投影。

上限4096盏输入灯、256个表面；不会在第32／64盏处丢弃重叠灯。网格占用为 `ceil(width/tileSize) × ceil(height/tileSize) × ceil(visibleLights/32) × 4` 字节，另有144字节／灯的源数据、按需阴影图。零灯／BruteForce 仅绑定一个已初始化的网格字，不 dispatch。`maximumGridMiB` 是网格预算，非所有资源总预算。

`Auto` 优先 Tiled；Compute 能力或网格预算不足时，`allowBruteForceFallback` 控制是否退到同顺序逐灯片元循环。`BruteForce` 可用于数值对照，仍要求 shader model4.5／structured buffer，不是旧 GPU 的无条件兼容后端。能力不足且不允许回退时拒绝绘制。CPU 提交计数与 `GridBytes` 不代表 GPU 帧时或移动内存流量。

选择本模块后端用 `settings.backend`。借用的 `localLights.backend`／`batchSize` 仍按场景灯格式验证，但只控制 Deferred贴花灯，不决定这里的调度。

## 材质、GI、阴影与组合

材质复用 [场景材质](scene-deferred.md) 的线性 RGB normal map、albedo／MOS／emission／UV／cutoff／vertexScale 输入。透明 alpha 来自 albedo纹理 alpha × 输入 alpha。MOS 为 metallic、occlusion、smoothness，不猜读原版 Def。

法线贴图的副切线方向由实际物体矩阵、原 tangent.w 和显式 vertexScale 决定，不依赖手工 DrawRenderer 可能缺失的引擎镜像标志。非均匀／镜像变换、独立参考及这次修正的画面范围见 [共享法线基](forward-normal-basis.md)。

各表面可用 [SceneGiInput](scene-gi.md) 显式光照贴图／Unity绑定贴图／SH Probe。主光与附加灯分别控制 GI乘色、漫反射／镜面／背向漫反射，基础 GI 也可缩放。Monitor／atlas 复用 [贴花灯](scene-decal-lights.md) 的采样规则；指定 Monitor 就必须有当前发布帧，没有时不退到白图。

显式主方向光及 Spot／Point 六面阴影分别借助 [已有阴影生产器](scene-light-shadows.md)，主光与局部光使用独立图和参数。透明接收面不自动成为半透明 caster；caster 列表仍使用已有 opaque／cutout 契约。胶囊／面光源阴影请求仍被拒绝。

组件命令位于 `BeforeForwardAlpha`：在 `SceneDeferredCamera` 与宿主 Forward opaque 之后、原生透明之前。它不会与宿主原生透明物体自动交错排序；同一需交错的透明集合应由同一宿主管理。默认摄影路径不添加本组件。LowResolutionFx／HeavyFx 已有独立的 [当前分块光照接入](fx-forward-lighting.md)，包含逐表面雾／介质输运和工作尺寸映射；该特效 API 保留 source alpha。相机透明组件自身不增加雾／介质绘制，TAA 的复杂透明历史及 SSR／Planar 的透明反射仍待整合。

## 证据边界

新增 `ActorRenderingSelfTest.ForwardPlus` 在 t15／D3D11 实际 Player 中完成203项命名控制：45组完整浮点 RGBA 分块／逐灯对照精确，三组原生蒙皮／blendshape 与独立当前网格的整图最大差 `2.98e-8`；所有原始输入灯的独立 CPU PBR／双层透明合成，完整143×103图最大差 `2.064e-6`。没有排除轮廓像素。13组实际 GPU bitset 的全部字与独立 CPU 区间计算一致，十组明确发生逐 tile剔除；4096盏重叠灯及最后一盏的可见贡献均覆盖。

同一套检查覆盖当前 HDR Monitor／GI、Point六面与独立方向阴影、已有 Deferred深度、原生 Forward不透明遮挡、双相机隔离、奇数尺寸、镜头穿灯、预算回退、缩容／零灯、alpha cutoff、失效纹理／目标及释放。GPU回读仅存在于自检，不在运行时模块中。旧7706条完整检查记录和1418张 PNG 保持不变；这些旧项包含外置的真实 GI及 DCC导入数据，不随仓库分发。

桌面等价、容量与资源生命周期验收不能证明全舞台画质、移动带宽／帧时、Metal／Vulkan驱动一致性。相交透明排序、折射、透射、半透明阴影、移动附件架构仍需后续实现与实测。

算法背景参见 [AMD Forward+ 概述](https://github.com/GPUOpen-LibrariesAndSDKs/ForwardPlus11)：GPU分块光源集合供 Forward片元读取。命令调度使用 [Unity2022.3 DispatchCompute](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.DispatchCompute.html)。本仓库没有复制该示例代码或资产。
