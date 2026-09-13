# 自主调色与 3D LUT

`ColorGradingProfile`、`ColorGradingLut`、`ColorGradingRenderer` 提供不依赖游戏资产或抓取 LUT 的调色流程。PPT126 列出的颜色调整、白平衡、Lift／Gamma／Gain、颜色曲线和 GT 色调映射在这里采用明确的独立参数与顺序；不声称 Unity Volume／游戏配置数值可原样互换。

## 独立应用使用

```csharp
ColorGradingLut lut;
readonly ColorGradingRenderer grading = new ColorGradingRenderer();

void CreateGrade() {
    var profile = new ColorGradingProfile {
        size = 32,
        maximumInput = 64,
        exposureEV = .6f,
        contrast = 1.08f,
        saturation = .88f,
        colorFilter = new Vector3(1.08f, .97f, .89f)
    };
    lut = ColorGradingLut.Bake(profile); // Unity 主线程；调用方拥有结果
}

void ApplyGrade(RenderTexture hdr, RenderTexture destination) {
    bool ok = grading.TryRender(hdr, lut, out var frame);
    Graphics.Blit(ok ? frame.color : hdr, destination);
    // 失败原因：grading.UnavailableReason。失败不会返回旧画面。
}

void ReleaseGrade() {
    grading.Dispose();
    lut?.Dispose();
    lut = null;
}
```

不要每帧 Bake。LUT 是制作时的快照，修改原 profile 不会改变已生成的 LUT；重新 Bake，再把新引用交给各消费者，最后释放旧 LUT。多个相机可共享只读 LUT，各自持有 renderer。`Texture` 是借用资源，禁止修改／销毁；`CopyValues()` 返回独立 CPU 副本。

Unity 的 Create > Digital Kotone > Color Grading Profile 可创建项目自己的 profile 资产。也可用 `profile.ToJson()` 保存文本，再通过 `ColorGradingProfile.FromJson(json)` 恢复。JSON 必须明确包含 `schemaVersion: 1`，未知版本和无效范围拒绝。这里没有自动文件读写或网络下载，路径／资产管理由应用决定。

## 颜色契约与处理顺序

输入是线性 sRGB 原色／D65 的场景 HDR，输出是同原色的显示线性 RGB `[0,1]`。模块不执行 sRGB OETF／PQ／HLG，不处理广色域 HDR 显示元数据。最后显示编码由宿主处理，避免重复 gamma。Alpha 原样传递，不做预乘或去预乘；有透明合成需求时请在正确的工作空间显式安排顺序。

1. 输入每通道夹到 `[0, maximumInput]`；NaN 置零，正／负无穷分别夹到上／下界。LUT 域可选 Linear 或 `log(1+x)/log(1+maximumInput)`，默认后者以保留暗部节点密度。
2. 依据 sourceWhite／targetWhite 的 CIE xy，做 Bradford 色适应；默认两者均为 D65 `(0.3127,0.3290)`。然后裁掉负 RGB，乘 `2^exposureEV` 和线性 `colorFilter`。
3. 对比度以 `0.18` 为枢轴：`0.18 * pow(x/0.18, contrast)`。Lift／Gain／Gamma 为每通道 `pow(max(x*gain+lift,0), 1/gamma)`。这些是直接数学量，不是 Unity 色轮的编码值。
4. 可选 GT 曲线，或 Clip 直接限制到 `[0,1]`。GT 固定 SDR 峰值为 1，提供线性起点、长度、斜率、toe 幂和 pedestal；公式依据 [Uchimura 2017 原始数学图](https://www.desmos.com/calculator/mbkwnuihbd)，不是旧私有 LUT 的拟合。
5. 在映射后 RGB 的 HSV 色相上应用 hueDegrees 和 Hue-vs-Hue。其余三个 versus 曲线共同调制饱和度，沿当前亮度灰轴缩放 RGB 与灰色的差；亮度系数为 `(0.2126,0.7152,0.0722)`，超出显示域则夹断。
6. 对每通道依次应用 master、对应 R／G／B 曲线。曲线均为显式分段线性节点，未实现 Unity 的自动切线／色轮／Volume 混合语义。

白平衡的矩阵由 sRGB 原色与白点推导，再做 cone-domain 比例适应，原理与 [W3C 颜色转换参考](https://www.w3.org/TR/css-color-4/#color-conversion-code) 中的 Bradford 流程一致。本 API 使用明确 xy 白点，不把未标定的温度滑块当作 Kelvin。移除偏色时 sourceWhite 填入输入照明白点，targetWhite 通常为 D65；艺术性偏色可反向设置。

| 曲线 | x 轴 | y 轴与中性值 |
| --- | --- | --- |
| master、red、green、blue | `[0,1]` 通道强度 | 输出强度；中性 `y=x` |
| hueVsHue | 一圈归一化色相 | 以圈为单位的偏移 `[-.5,.5]`；中性 0 |
| hueVsSaturation | 输入色相 | 饱和度倍率 `[0,2]`；中性 1 |
| saturationVsSaturation | 输入 HSV 饱和度 | 倍率 `[0,2]`；中性 1 |
| luminanceVsSaturation | 输入亮度 | 倍率 `[0,2]`；中性 1 |

每条曲线含 2–256 个严格递增 x 节点，首尾必须是 0／1。两个 hue 曲线的首尾 y 必须相同，以维持环形边界；非法曲线不会静默排序或修复。

## GPU 与资源

LUT 支持 16³／32³／64³，R 索引最快，依次为 G、B；存储线性 RGBAFloat、无 mipmap，alpha 固定为 1。32³ 的 CPU 节点与 GPU 纹理各约 0.5 MiB，64³ 各约 4 MiB。GPU 每像素一次源颜色采样和一次 3D 线性滤波采样；实际滤波精度仍受驱动影响，不等于逐点执行完整解析调色公式。选择范围／分辨率时要比较量化、插值和视觉误差，不能只检查 LUT 是否成功生成。

renderer 接收已创建、同帧的线性 ARGBFloat／ARGBHalf／RGB111110Float 2D 目标，单采样、无 mipmap、非 dynamic-scale。RGB111110Float 采样 alpha 为 1。内部拥有一张同尺寸 RGBAFloat 输出，1080p 约 31.64 MiB，不包含输入、LUT 或其他后处理目标。显式 LOD 0 的 Point／Linear Clamp 不继承调用方各向异性及 wrap 设置。

`Frame` 只借用结果，下一次调用／失败／释放／输出目标丢失会使它失效。不可把自己的输出再作为源输入，也不能释放或改写模块目标。`RenderTexture.active` 的有效原值会还原。共享 LUT 的寿命由调用方负责，释放一个 renderer 不会释放 LUT。

## Photo Studio 桥接

```csharp
post.authoredColorLut = lut;
post.useAuthoredColorGrading = true;
```

默认 false，旧 shader／旧默认调色路径保留。启用后，在既有时域／景深、bloom、Paraffin 和 diffusion 之后，单独合成未调色 HDR，再调用新 LUT，最后呈现；不在旧 LUT 或 ACES 输出上叠加第二次调色。profile 的曝光只在此新 LUT 中执行一次，不叠加旧故事曝光。

从一开始就启用此路径的相机不尝试读取私有抓取 LUT。LUT 缺失／失效时透传未调色的当前 HDR，并报告 `AuthoredColorUnavailableReason`，不会偷偷选回私有 LUT。旧故事设置仍控制 bloom／diffusion 等合成效果；自主 profile 单独控制颜色，不自动把旧故事 ColorAdjustments／Tonemapping 值解释为本模块参数。

## 已测精度与使用边界

129×73 的自制 HDR 色域控制使用上文非中性设置，另加入白点变换、LGG、master 与 Hue-vs-Saturation 曲线。GPU LUT 输出相对逐像素解析调色的 RGB 误差如下；这是固定输入／profile 的插值质量控制，不是原版画面对比。

| LUT | 平均绝对误差 | 最大单通道误差 |
| --- | --- | --- |
| 16³ | 0.00742531 | 0.279000 |
| 32³ | 0.00210182 | 0.130581 |
| 64³ | 0.000624219 | 0.0472349 |

64³ 的平均误差约为 16³ 的 8.41%，但高非线性、色域裁剪处仍有明显局部误差。不要仅凭平均值选默认档；按自己的曝光范围、曲线与实际角色检查局部误差，必要时缩小 maximumInput 或增加节点密度。本阶段不把这些最大误差称为像素一致。

还分别验证了 CPU／GPU 全节点、八个硬件滤波权重、全幅 LUT 插值和实际 HDR→LUT→半精度消费者。桌面驱动的地址吸附、交叉权重取整使理想双／三线性计算不能直接要求逐位相同；测试保留所有像素并分别记录理想差值和局部采样区间残差。局部权重模型有本机探针门槛，不能泛化为 D3D API 保证或其他 GPU 的结果。消费者 float32→float16 按 [D3D11.3 浮点转换规则](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm#3.2.2%20Floating%20Point%20Conversion) 向零舍入，与 NumPy 默认 nearest-even 不同。

本接口不表示完整 P03 已追平。完整舞台／角色的调色匹配、Unity Volume 参数与曲线语义的逐项适配、显示 HDR／广色域、其他平台的纹理精度及帧时仍需独立验收。实际相机桥接的原生控制使用自制不透明面板，未覆盖角色分类／发丝透明组合，也未验收可见窗口的最终呈现帧。
