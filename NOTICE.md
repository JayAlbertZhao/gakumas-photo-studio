# 来源与授权边界

这是第三方研究工程，与游戏发行方/开发方无隶属关系。游戏名称、人物、模型、音乐、台本、动画、图片及其他资产归相应权利人。这里的兼容类型名和资料引用用于技术说明。

本项目包含基于运行观察、序列化元数据和反编译分析重建的实现；不能宣称所有代码经过独立洁净室开发，也不能因为去掉资产就自动认定全部代码具有可再许可权。

发布候选没有为全部文件擅自套用 MIT 等许可证。权利人应在公开发布前确认实现来源和适用许可；未授予的权利保留。

## 依赖与参考

- Unity / Tuanjie、URP 与 UGUI：由使用者的编辑器/包管理器提供，适用各自许可，仓库不复制编辑器或包源码。
- [gakumas-VRify](https://github.com/KagaminTheMirror/gakumas-VRify)：研究参考项目，GPL-3.0。当前独立 Photo Studio 发布清单不包含其源码、派生探针补丁或二进制。未来若合入派生代码，须单独履行适用许可，不能沿用此处“仅参考”的分类。
- [Gakumas_Launcher](https://github.com/a4nqi3n/Gakumas_Launcher)、[gakuen-imas-localify](https://github.com/chinosk6/gakuen-imas-localify)：资料索引；没有打包 release、翻译台本或插件二进制。
- RenderDoc：运行时可选本地 API 连接；不捆绑 RenderDoc 二进制。
- Gradle Wrapper：`apps/ar-photo-android/gradle/wrapper/gradle-wrapper.jar` 是 Gradle 9.2.1 官方构建引导程序，按公开 SHA-256 固定并由源码审计校验；构建时另行下载 Gradle 发行包，不包含游戏内容。适用 Gradle 的 Apache-2.0 许可。
- FSR1：通过使用者安装的 UPM Core 引用 AMD EASU／RCAS 实现，不内置算法头文件。AMD 算法不是本项目原创；适配与标量校验见 [第三方声明](packages/com.digital-kotone.toolkit/ThirdPartyNotices.md)，含编译产物分发所需的 AMD MIT 声明。

原始游戏 DLL、反汇编/反编译文本、shader DXBC/SPIR-V、抓帧和内存转储、解密配置、登录凭据、个人绝对路径均留在私人参考目录。
