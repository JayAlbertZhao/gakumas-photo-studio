# Reproduction playground

本仓库当前按“复现 playground”维护，不把 Photo Studio、AR 或其他应用作为主目标。应用目录和已有工具仍保留，作为宿主、回归和渲染证据；新的工作优先回答“能否独立复现一个技术行为”，而不是增加应用功能。

## Reference → hook → fork

- `references/forks/` 保存带许可证和上游 commit 的公开实现快照。当前的 `open-swing` 来自 [unity-hair-skirt-dynamics](https://github.com/Arin3000/unity-hair-skirt-dynamics)，仅包含代码和合成示例。
- `references/hooks/` 描述可选接入边界。hook 默认关闭，输入由调用方提供的合成骨架和动画姿态组成；不会自动扫描、下载或绑定原版资产。
- `packages/` 和 `unity/` 中的实现仍是独立复现，不因为存在参考 fork 就宣称等价于原版实现。

## 当前验收边界

一个复现阶段只有在以下证据齐全后才算完成：

1. 能在无原版资产的情况下运行合成样例。
2. 能明确记录固定步长、姿态采样、重置和碰撞输入顺序。
3. 能把参考实现、我们的实现和关闭 hook 的基线分开回放。
4. 能说明差异、已知缺口和许可证边界。

原版游戏源码、解包参数、模型、贴图、动画、语音和剧情文本不进入公开仓库。
