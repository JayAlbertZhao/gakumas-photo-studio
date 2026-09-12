# 场景灯光源阴影

`SceneDecalLight.shadow` 为默认关闭的 Spot 阴影。它从光源视角绘制显式登记的遮挡物，再让场景直接光读取该深度；不使用宿主相机深度代替光源可见性，也不把静态烘焙 GI 当作动态阴影。

```csharp
spot.shadow.enabled = true;
spot.shadow.nearPlane = .05f;
spot.shadow.depthBias = .002f;
spot.shadow.filter = SceneShadowFilter.Pcf3x3;
scene.decalLighting.shadows.tileResolution = 256;
scene.decalLighting.shadows.casters = new[] {
    new SceneShadowCaster { renderer = movingObjectRenderer }
};
```

光源和 caster 的变换在每次宿主渲染前读取。接收器仍为 `SceneDeferredCamera.surfaces`；caster 不必是接收器，也不必在宿主相机可见层中。主灯 Directional、Point／Capsule／Area 阴影尚未实现：对这些形状显式请求阴影会拒绝场景帧，不会悄悄退化成无阴影。

## 几何与采样约定

- Spot 局部 +Z 朝前，外锥完整角度作为透视 FOV，宽高比 1；near 为正轴向距离，far 为 light.range。灯光自身仍按径向球截断范围，不改为轴向衰减。
- atlas 为独立 RFloat 颜色深度和 24 位硬件深度附件。硬件裁剪／深度测试选择最近几何；颜色保存 `光源轴向距离 / range`，空白清为 1。颜色深度不随 reversed-Z 改变方向。
- 接收点先沿着色法线移动 `normalBias`，投影后使用 `(轴向距离-depthBias)/range <= 保存深度`。两种 bias 都以世界单位表示；默认 normalBias=0。过大 bias 会使阴影脱离物体。
- 投影 near/far 或 XY 范围之外的采样可见性为 1；near 以内的遮挡物不参与。将 near 设得过大会漏遮挡，不能把此范围外行为当作无限范围阴影。
- Hard 使用一个 point 深度比较；Pcf3x3 使用九个 point 比较的平均，逐 tap 限制在本 tile 的半 texel 内边界，避免读到相邻光源。它是固定分辨率滤波，不是物理面光源软阴影。
- `strength` 在完全可见与比较结果之间线性插值。可见性乘到当前灯的正向漫反射、镜面、可选背向漫反射；基础 GI、其他光源与 emission 不受该灯阴影压暗。GI 乘色只修饰直接光的颜色响应，顺序与原模块一致。

投影使用 [GL.GetGPUProjectionMatrix](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/GL.GetGPUProjectionMatrix.html) 的 RenderTexture 路径；局部投影 UV 按图形 API 翻转一次，tile 行号不再翻转。每次 [SetRenderTarget 后 viewport 恢复为完整目标](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetViewport.html)。平台的硬件 Z 约定见 [Unity 平台差异](https://docs.unity3d.com/cn/2022.2/Manual/SL-PlatformDifferences.html)。实际验收范围为本机 t15 / D3D11，其他 API 仍需实测。

## Caster、所有权与资源

显式 `MeshRenderer`／`SkinnedMeshRenderer` triangle submesh，`materialIndex` 须同时存在于网格和材质槽。模块不读取源 shader 的变形、材质关键字或 Cutout 规则。需要裁切时提供 `alphaMap` 的 alpha、uvST、alpha 和 cutoff；`alphaMap.a * alpha < cutoff` 被裁掉。默认 cutoff=0，alpha=0 的几何也会写深度，需设置正 cutoff 才是空裁切。

`vertexScale` 是蒙皮后的局部顶点缩放，cull 默认 Back，可显式改 Off。禁用、inactive、forceRenderingOff 的 caster 不提交；不改这些借用状态，不创建 Unity Light，不写全局 shader 参数。PropertyBlock、非三角形、缺失 UV、无效子网格、奇异变换或未创建／MSAA alpha 纹理被明确拒绝。蒙皮流由 Unity 更新；离屏角色由调用方保证骨骼更新，例如在自己的角色上设置 `updateWhenOffscreen`。不保证任意原版 GPU 变形／发丝透明材质自动匹配。

资源归宿主相机独占。最多 16 个有效阴影灯，每 tile 32–2048 的 2 次幂，atlas 边长上限为 4096 及设备限制的较小值；最多 1024 个登记 caster。超预算拒绝，不丢灯。每个光源绘制全部启用 caster，没有 caster 空间剔除／静态缓存或时间摊销。

原灯数据保持 144 字节。仅存在可见且非零阴影灯时启用 shader 变体，Instanced 路径额外绑定每灯 112 字节的独立阴影矩阵／参数缓冲；Scalar 绑定等价逐灯参数。无阴影灯、强度为零、关闭或剔除后释放阴影目标／材质／缓冲。后端、尺寸及数量变化会刷新资源。没有阴影的旧路径不增加深度目标与采样。

`Frame.lightShadowAtlas` 是借用的诊断纹理，仅在 `Frame.IsCurrent` 为真时有效；不要缓存、销毁或当作已经完成的体积光 API。`LightShadowMapCount` 和 `LightShadowCasterDrawCalls` 说明提交量，不是 GPU 性能数据。

## 验收与待补范围

资产无关 Player 自检使用不出现在接收 GBuffer 中的移动遮挡片、独立光源到平面的 CPU 射线、相机透视／正交、离轴光、near/far、depth／normal bias、Cutout／UV、PCF、GI／镜面／背光、跨帧单骨蒙皮以及四 tile 与逐灯独立渲染叠加对照。另检查遮挡片倾斜时的透视深度、前后剔除和倒置提交顺序下的最近深度。记录中明确保留排除的栅格边缘窄带。

蒙皮验收范围为均匀缩放根节点、单骨、三组跨帧平移／旋转；与 CPU 变形后的静态网格完整阴影和深度对照，并核对原生 post-VS 顶点。早期非均匀父级缩放用例中，Unity 的实际蒙皮流与直接相乘的骨骼世界矩阵存在可见差异，已通过原生顶点定位并保留失败证据，没有放宽阈值。一般非均匀缩放／剪切、多骨及原版材质 GPU 变形尚未完成独立验收。

原生捕获开关为 `GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOWS=1`；单骨和静态参考对照可用 `GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOW_SKIN=1`。只用于已注入 RenderDoc 的专用自检 Player，不与其他捕获开关混用。

对应 PPT110–112 的场景实时光源遮挡基础及 PDF13–23 的光源阴影调度方向。未完成 PDF 的 MainLight／Actor／ScreenShadow 完整调度、烘焙 ShadowMask 3:3:2 打包、Forward+／透明／角色接收器、其他光源形状、移动 Memoryless/subpass 与成本验收。PPT131 的体积 Spot 积分及体积中的动态 DepthShadow 仍待接入，不随本模块标记完成。
