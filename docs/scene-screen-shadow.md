# ScreenShadow 与 Capsule AO

`SceneDeferredCamera.screenShadow` 提供默认关闭的屏幕主灯可见性和环境可见性。对应 PDF15／17／19 的光源深度、几何深度法线、ScreenShadow／CapsuleAO 数据流。公式为独立实现；没有引入原版 shader／素材或第三方 AO 插件。胶囊与新增的可选 [GTAO](scene-gtao.md) 分别计算，再合并进 G 通道。

```csharp
scene.mainLightShadow.enabled = true; // 仍需配置光源范围及显式 caster
scene.screenShadow = new SceneScreenShadowSettings {
    enabled = true,
    capsuleSamples = 32,
    capsuleMaxDistance = 1.2f,
    capsuleNormalBias = .002f,
    capsules = new[] {
        new SceneCapsuleOccluder {
            start = anklePosition,
            end = toePosition,
            radius = .08f
        }
    }
};
```

端点和半径使用世界单位；宿主在渲染前更新端点即可跟随脚部、道具或其他物体。不自动查找角色骨骼，不解析游戏专属 rig。胶囊也不必对应实际渲染网格。空胶囊数组可只用主灯 ScreenShadow；主灯阴影关闭时可只用 Capsule AO。

## 实际调度与附件

开启且存在有效主灯阴影、非零 Capsule AO 或非零 GTAO 时，模块按以下顺序执行：

1. 主方向光、Point／Spot 的实际光源深度。
2. 登记场景表面的几何预通道。
3. 主灯阴影、胶囊 AO 与可选 GTAO 的屏幕 resolve。
4. 原场景材质 GBuffer、贴花、附加灯和最终 HDR／深度 resolve。

预通道复用登记表面的 renderer／submesh、vertexScale、cull、albedoMap alpha、uvST、alpha 和 cutoff，具有自己的最近深度附件。只保存平滑网格世界法线和正视空间深度；不采 normalMap、材质贴花、GI 或 Forward 角色。宿主相机深度不作为场景预通道，caster 也不会自动成为接收器。

现有 `SceneDepthData` 组件按宿主 cullingMask 提交、在 OnPostRender 才发布结果；`SceneDeferredCamera` 则要求宿主排除其场景层。因此这里使用同一相机 command buffer 内的显式预通道，避免修改宿主 cullingMask、依赖组件添加顺序或读取上一帧数据。没有自动连接 SSR 的独立 DepthID／层级，也没有因新增预通道就宣称复用了所有几何 draw。

| 附件 | 格式及通道 | 清屏／采样 |
| --- | --- | --- |
| `Frame.screenGeometry` | RGBAFloat：RGB=世界网格法线，A=正视空间深度；另有 24 位请求的硬件深度附件 | 清 0，A≤0 为背景；Point 采样 |
| `Frame.shadowOcclusion` | **R8G8_UNorm**：R=主灯可见性，G=胶囊与可选 GTAO 的环境可见性 | 两通道 1 表示完全可见；背景写 1；Point 采样 |

两张图与宿主目标同分辨率，不做缩放、双线性插值、时域滤波或深度引导上采样。8 位可见性会量化中间强度／PCF 结果；不保证与直接深度比较路径逐字节相同。颜色附件额外占 18 字节/像素，再加预通道硬件深度及驱动开销。没有 Memoryless／subpass 复用，不能把本桌面实现的附件布局当成移动端性能完成。

## 主灯与间接光的消费边界

R 由现有主方向光的真实深度图和 Hard／PCF／strength／bias 产生。法线偏移采用预通道网格法线，因此 normalMap 或材质贴花法线不会使屏幕阴影偏移改变；原默认直接采样路径仍用最终着色法线，不改变其行为。光源范围外主灯可见性仍为 1。

最终 HDR resolve 仅将主灯直接漫反射、镜面及背光乘 R，不再重复读取主灯源深度。环境漫反射或基础 baked GI 乘原材质 AO 和 G。直接光中的 GI 乘色、Point／Spot 辐射和 emission 不乘 G；其他灯也不乘 R。Forward 角色既不进入这个预通道，也不读取这张图，仍按宿主深度正常遮挡。

## 胶囊环境可见性模型

这部分使用实际射线与显式胶囊的相交结果，不使用屏幕深度伪造胶囊，也不把已烘焙 GI 当成实时遮蔽。

- `capsuleSamples` 为 8／16／32／64。以网格法线建立正交半球，切线取 `normalize(up×normal)`；abs(normal.y)≥0.9 时 up 改为世界 +X，其余使用 +Y。使用 `u=(i+0.5)/N` 和二进制逆序的 v，按余弦分布生成方向。法线长度接近零时不计算 AO，环境可见性为 1。
- 射线起点为世界接收点加 normal×capsuleNormalBias。胶囊为有限圆柱和两个端点球的并集；端点重合退化为球。起点在任意胶囊内部时该方向距离为 0。
- 每个方向取所有启用胶囊的最近非负交点距离 t，最大为 capsuleMaxDistance。环境可见性为各方向 `clamp(t/maxDistance,0,1)` 的平均，再以 capsuleStrength 与 1 插值。重复或重叠胶囊按并集处理，不逐胶囊连乘变黑。
- 这是带距离线性淡出的离散半球积分。没有屏幕空间远处遮挡、方向性环境、真实网格轮廓、弯曲法线、随机种子或时间累积。低采样数量和切线轴切换会带来离散图案，胶囊需由调用方合理拟合足部；不宣称原讲演使用相同公式或采样数。

最多登记 16 个胶囊；每像素成本随启用胶囊数和采样数增长。适用于显式选择的少量近接触遮挡，当前未做 tile 剔除或移动性能验收。

## 所有权、失败与验证范围

纹理和材质归宿主相机独占。Frame 中两张纹理均为借用数据，必须检查 `Frame.IsCurrent`；渲染、尺寸变化、目标丢失和禁用会使旧帧失效。禁止调用方释放借用纹理。关闭后不增加预通道、屏幕 resolve 或附件；无有效主灯阴影且胶囊／GTAO 均无非零效果时同样不分配。启用但非法的配置先拒绝，不靠零贡献掩盖错误；关闭时不校验未使用配置。

对 RG8 的渲染／采样支持进行 [IsFormatSupported](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SystemInfo.IsFormatSupported.html) 检查，不支持或分配后格式改变时拒绝场景帧，不暗中换成更宽格式。当前实际验收设备为本机 t15／D3D11；其他 GPU／API 的拒绝或兼容行为仍需实测。

原生捕获需分别查看底层资源格式和实际视图格式：本机资源显示 `R8G8_TYPELESS`，实际输出视图是 `R8G8_UNORM`。D3D11 的 FLOAT→UNORM 转换允许整数侧 0.6 ULP 误差，不能把理想的半量化步长当成全部硬件必须满足的界限。参见 [D3D11 格式转换规范 §3.2.3.6](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)。

资产无关 Player 自检覆盖独立平面深度／法线、主光射线、双精度胶囊半球相交、四档采样、端点顺序／退化球、并集与重复、裁切／法线贴图、主灯／GI／附加灯／emission 分量求和、蒙皮／静态参考、Forward 排除及多相机／目标生命周期。`GAKUMAS_SELFTEST_CAPTURE_SCREEN_SHADOW=1` 请求原生截帧，检查光源深度→预通道→RG8→材质→灯光的实际顺序和绑定；不要与其他捕获开关混用。

GTAO 可通过 `screenShadow.gtao` 独立启用，采样与合并限制见 [GTAO 契约](scene-gtao.md)。尚未完成烘焙 ShadowMask 打包、Actor 自阴影／完整角色接收、Forward+、低分辨率／时域稳定 AO、与 SSR 共 RenderPass、移动附件和实机成本。详见 [完整技术清单](framework-techniques.md)。
