# 场景贴花灯与 Monitor 照明

`SceneDecalLightSettings` 给可选 `SceneDeferredCamera` 增加实际直接光照。它读取贴花处理后的 albedo、世界法线、MOS 和深度，照亮登记的场景表面；不把照明写进材质 emission，也不把 HDR Monitor 的自发光网格当作周围表面已经受光。

参考范围：PDF 59–62 的点、线段、面形灯与 Monitor 采样、实例化，以及 PPT 119 的舞台示意。原文没有提供完整数学公式。本模块采用独立的线性距离衰减、最近线段点和梯形投影模型；不声称复制 Unity Light 的数值、物理面光源积分或原版 shader。原始资产、字节码和图示不随包提供。

## 接入

先按 [场景后端](scene-deferred.md) 设置显式场景层、PBR 表面与 HDR 目标。再设置：

```csharp
scene.decalLighting = new SceneDecalLightSettings {
    enabled = true,
    backend = SceneDecalLightBackend.Auto,
    batchSize = 256,
    monitor = hdrMonitor, // 宿主须先更新并发布 Monitor；也可改用 atlas 纹理
    lights = new[] {
        new SceneDecalLight {
            shape = SceneDecalLightShape.Point,
            position = new Vector3(0, 1, -1),
            range = 3,
            radiance = new Vector3(2, 2, 2),
            monitorUV = new Vector4(0, 0, .25f, .5f)
        },
        new SceneDecalLight {
            shape = SceneDecalLightShape.Capsule,
            position = new Vector3(0, .5f, 0),
            rotation = Quaternion.identity,
            halfLength = 1,
            range = 2,
            monitorUV = new Vector4(.5f, 0, .1f, .8f)
        }
    }
};
// 每帧先发布内容，再渲染场景。模块不隐式执行其他相机或视频解码器。
hdrMonitor.TryUpdate(seconds, contentVersion, out _);
camera.Render();
```

没有 `monitor` 时使用 `atlas`；两者都空则取白色。已有 `HdrMonitor` 的当前发布帧优先，失效时拒绝此帧场景提交并释放额外灯光资源，不继续使用旧图或静默回退另一图。引用是借用，灯光模块不会修改、释放 atlas 或驱动 Monitor。纹理 RGB 参与照明，alpha 不再次相乘。数值辐射使用线性纹理；普通 sRGB 纹理仍按 Unity 纹理设置解码。

## 形状、UV 与响应

| 形状 | 作用体积与表面朝光方向 | Monitor 采样 |
| --- | --- | --- |
| Point | `position` 为光源，距光源超过 `range` 的位置无贡献 | 固定 `monitorUV.zw`，同一光源只取 atlas 一点 |
| Capsule | 局部 X 轴上 `[-halfLength,+halfLength]` 线段；选接收位置的最近线段点，按离该点的径向距离衰减 | 最近点参数 `t` 从 0 到 1，UV 为 `zw+t*xy` |
| Area | 局部 XY 发光面、朝局部 +Z 单向照明；深度 z 的半宽为 `halfSize+areaSpread*z`，体积呈矩形棱台；选与该位置投影对应的面上点 | 投影归一化到面内 0–1，再用 `zw+planeUV*xy` |

`position`、`range`、`halfLength`、`halfSize` 使用世界单位，`rotation` 会归一化；不从任意 GameObject 继承缩放。宿主需要在自己的动画采样后更新这些字段。Area 的 `areaSpread` 是每单位深度增加的半宽，零值产生直棱柱，背面、超出深度或横向边界时无贡献。它是投影式灯光，未进行面光源面积积分、LTC 或软阴影。

衰减为 `pow(saturate(1-distance/range), falloffExponent)`。Point/Capsule 使用到点／线段的距离，Area 使用沿 +Z 的深度。最终直接光为共享的 GGX/Smith/Schlick 与 Lambert 响应乘 atlas RGB、`radiance`、衰减。`diffuseScale` 和 `specularScale` 可独立调节；默认背向法线不受光，可用 `backlightScale` 添加反向漫反射。`giWeight` 控制各灯乘上预计算响应，两个新值均默认零，完整公式及边界见 [场景 GI](scene-gi.md)。光源正好位于接收点时方向取零并输出零，避免除零。AO 不重复乘到直接光。

`receiverGroup=0` 照亮所有登记场景表面，包括不接受材质贴花的 group 0；1–255 仅匹配相同组。它不照亮未登记的 Forward 角色或透明物体。主 Forward 前景遮挡和背景深度仍保留，但**没有光源视角阴影**：即使接收面在相机中可见，灯和它之间的另一块墙也不会自动挡住此光。

## 实例化、目标与失败行为

`Auto` 在 shader／平台支持时选择 `Instanced`，否则选择 `Scalar` 并报告 `LightFallbackReason`。`Instanced` 使用实际 procedural GPU instancing：每实例六个顶点，在 VS 读取自己的结构化灯数据，绘制该体积的保守屏幕矩形。剔除使用实际投影矩阵的齐次 XY 平面，不用可能与自定义投影不一致的 nearClipPlane 属性；跨过观察原点时保守覆盖全屏，片元仍做真实形状测试。这会多提交部分深度范围外的灯，不会把它们错误视为已经着色的表面。`Scalar` 使用同一 HLSL 数学、一个普通 quad draw 对应一个灯。显式 Instanced 请求可用 `allowInstancingFallback=false` 严格拒绝不支持的平台。

同一个设置集共用一个 atlas，因此兼容光源可一起提交。最多 4096 灯，batchSize 1–1024；默认 256 时 110 灯一个 instanced draw。使用独立结构化 buffer，容量向上取 2 的幂。该路径不是 Unity Light 对象、CPU 逐灯 draw loop 或 Unity 自动 Static Batching。

只在有可见且非零灯时分配一个相机私有 ARGBFloat 累加目标。灯光在 float32 累加，原场景 resolve 最后统一合并直接光／间接光／emission 并限制到 half HDR 范围，避免多个高亮灯在 half 加法目标中溢出。缓冲、材质与目标均按相机持有，不使用全局 shader 写入。空灯集、关闭、零辐射、全部被视锥剔除时没有额外灯光 draw、结构化 buffer 或累加目标。

修改数量、后端、尺寸会重新配置相应资源；目标丢失重建，禁用释放。参数无效、旧 Monitor 帧、不支持的严格后端、GPU 准备失败时通过 `UnavailableReason` 拒绝场景提交。模块不销毁宿主相机目标或源材质。

通过 `SubmittedLights`、`CulledLights`、`LightDrawCalls`、`LightBufferCapacity`、`LightTargetCount`、`ActiveLightBackend` 查询实际提交状态。数字只说明工作量和选择结果，不等于测得 GPU 帧时。

## 验证与边界

自制 GPU 场景检查比较 Scalar／Instanced 的完整图像以及 CPU BRDF／衰减／叠加，覆盖三个形状和 atlas 维度、实例偏移、110 灯、HDR、Monitor 内容更新、材质贴花后光照、前景保护和资源生命周期。独立 RenderDoc 离屏截帧检查实际 D3D11 DrawInstanced、SV_InstanceID 和累加目标；完整执行记录见 [渲染验收](rendering.md)。

可在已由 RenderDoc 注入的专用自检 Player 中显式设置 `GAKUMAS_SELFTEST_CAPTURE_DECAL_LIGHTS=1`，只截取 110 灯的离屏帧。普通运行不加载或依赖 RenderDoc。该诊断使用公开 C API，失败即记录检查失败，不把“发起请求”当作取得原生 draw 证据。Unity Player 的 `-batchmode` 可用于无窗口 D3D11 检查，不能同时加 `-nographics`。参见 [Player 参数](https://docs.unity3d.com/cn/2022.3/Manual/PlayerCommandLineArguments.html) 和 [DrawProcedural 实例数参数](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.DrawProcedural.html)。

尚未包含原版完整舞台参数、所有角色/透明接收器、遮挡阴影、自动 GI、Forward+ 光列表，以及移动 Memoryless/subpass、带宽与 Vulkan/Metal 实测。不把减少 draw 数量直接写成移动端提速或完整管线已追平。
