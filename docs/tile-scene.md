# 显式 Tile 场景材质与光照

`TileSceneRenderer` 是默认关闭、供自有 SRP 调用的场景消费者。输入复用 `SceneDeferredCamera.Surface`、`MaterialInputs`、`SceneGiInput` 和 `SceneBakedShadowInput`；不会切换摄影应用的 Built-in 管线，不修改已有 Deferred GBuffer ABI。

当前接入普通不透明／cutout 表面、线性 RGB 切线法线、UV0 材质、UV2／Probe GI、四通道烘焙可见性及显式方向光 PBR／GI 乘色／背向漫反射。没有自动移植贴花、局部灯、实时阴影、薄叶、反射、Actor 或运动附件；薄叶输入明确拒绝。源讲演只提供技术布局，这些材质、编码和光照方程是本工具包的独立实现。

## 调用与所有权

创建 `TileSceneRenderer.Settings`，显式开启，指定表面及已创建的 `B10G11R11_UFloatPack32` 输出；可选提供 `R16G16B16A16_SFloat` 法线目标。固定单层 2D、完整视口、非 MSAA／mipmap／dynamic scale／random write，Linear 项目。输出不能与任何材质、GI 或烘焙遮罩输入别名。

`TryPrepare(camera, settings, out frame, out error)` 不绘制、不分配输出、不修改 Camera；生成拥有独立材质的单次快照。宿主在有效 SRP context 中完成 Camera 设置后调用 `frame.TryRecord`，随后自行 `context.Submit`。材质参数已快照，网格、Renderer 当前几何、纹理和目标仍借用，必须保持有效且不被并发修改。等 GPU 不再使用本批资源后才 `frame.Dispose()`；提交返回或 `Budget` 均不代表 GPU 完成。快照不能重复记录；关闭、非法输入、能力不足返回具体错误，不偷偷回退默认相机。

当前消费者限定桌面 Vulkan 与显式允许的 D3D11 仿真。方向光 PBR 只需要相机射线，使用有限 far-plane 端点，不读取深度附件；这不提供实际表面世界位置。位置相关局部光／实时阴影需要后续独立深度桥接，Metal／移动端仍待验收。

## 五 MRT 与精度

| 附件 | 格式与内容 | 后续用途 |
| --- | --- | --- |
| G0 | sRGB8 RGB 基色、A 遮罩 R8 | 光照当前像素输入，transient |
| G1 | UNorm8 MOS、A 遮罩 GBA332 | 光照当前像素输入，transient |
| G2 | Half4 世界法线、A 自主身份标记 | 可选保存，不写运动矢量 |
| G3 | packed HDR 自发光，随后加方向／间接光 | 最终 resolve 输入，transient |
| G4 | packed HDR GI | GI 消费后复用为最终输出 |
| 深度 | D32 | 几何遮挡，后续 subpass 保持只读，不作为光照输入 |

G2 A 的 0 表示背景；有几何时为 `1 + receiverGroup + (hasGi ? 256 : 0)`，最大 512 可被 Half 精确表达。此编码不是原版 MaterialID。R8／GBA332 使用现有独立遮罩编码与可选 dither，不更改旧 Deferred 的额外 RG8 附件。

颜色保守预算 256 bits，深度另计 32 bits；物理格式合计 28 bytes/pixel。数值是声明预算，不是实测驻留／带宽。sRGB8、UNorm8 和 packed HDR 的多次格式量化不等于旧 Half4 输出逐位相同；最亮 HDR 最终显式截到 R/G 65024、B 64512，背景与几何的 alpha 均为 1。G3 加法可溢出，最终 resolve 负责有限上限；不是透明合成附件。

## 桌面验证与剩余范围

`--self-test-tile-scene <输出目录>` 在隔离的自有 SRP 中执行 28 组自制材质配置：金属／介电、sRGB／MOS 中点量化、cutout、发光、法线／镜像／非均匀缩放、透视观察射线、UV0／UV2 分离、Lightmap／SH、GI 乘色、AO、四个遮罩通道、背光、HDR 上限、重叠接收面和实际当前单骨蒙皮、奇数尺寸。完整颜色与法线字段逐像素验收，非法输入、别名、单次记录和借用资源另有检查。

可设置 `GAKUMAS_SELFTEST_TILE_SCENE_LEGACY=1` 运行同一材质配置的既有 `SceneDeferredCamera` 消费者；它是独立控制模式，不是自动运行时降级。两种输出分别按对应格式和同一光照语义对照。Point 采样的奇数尺寸配置将输入域避开精确 texel 边界，避免把插值浮点舍入的相邻 texel 选择误判为确定的单一结果；sRGB／UNorm 中点则枚举事先规定的相邻格式值。

D3D11／Vulkan 的 56 份原生捕获检查了五 MRT、独立解码材质、几何深度、四个同像素光照输入、加法混合、G4 复用及真实蒙皮顶点变化。D3D11 捕获使用显式 `-force-gfx-direct`，并与正常线程输出分别核对；该诊断模式仍会出现四条退出时的 bound-color-surface 释放警告，不启用 RenderDoc 时也存在，未定位根因、不推荐作为生产模式。正常线程 D3D11／Vulkan 本模块运行没有这些警告。

上述证据限于自制桌面内容，尚不证明完整角色／场景或所有 GI 导入变体、移动收益及长时间资源行为。局部灯、实时遮挡、贴花、反射、Actor 与运动等 tile 消费者继续保留为开项，未宣布框架全部追平。默认摄影路径保持原样。
