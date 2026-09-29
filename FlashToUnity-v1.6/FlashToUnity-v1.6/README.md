# FlashToUnity

将 Flash `.swf` 动画转换为 Unity 动画资源，并通过换装、造型调整和部件绑定制作自己的角色。

**版本：v1.6 · 协议：MIT · 已验证环境：Unity 2022.3.20f1c1**

## 快速开始

1. 将 **FlashToUnity 文件夹整体复制到 Unity 工程的 Assets 下**，等待编译完成。
2. 打开 **Tools → Flash 动画导入 (SWF → Unity)**，选择 SWF，点击「开始导入」。第一次建议关闭整帧预渲染、选择「不分割」。
3. 将生成的 Prefab 拖入场景播放。
4. 需要换装或新增部件时，打开 **Tools → Flash 角色制作（换装与部件绑定）**，用生成的 Prefab 创建独立角色。

不需要外部 Flash 播放器或额外付费插件。输入为 SWF，不直接读取 FLA。

## 文档

- [使用手册](使用手册.md)：安装、导入、换装、绑定、保存和常见问题。
- [插件详细介绍](插件详细介绍.md)：工具定位、功能、工作原理、扩展方式与能力边界。
- [更新日志](CHANGELOG.md)：版本变更摘要。
- [开源协议](LICENSE)：使用、修改和分发的许可。

## 分享与安装范围

```text
FlashToUnity/          插件源代码；安装时复制整个文件夹
README.md             快速开始
使用手册.md            完整操作步骤
插件详细介绍.md         功能与原理介绍
CHANGELOG.md          更新记录
LICENSE               MIT 协议
.gitignore            公开仓库忽略规则
```

本地若另有 `SampleProject`，它是个人样例工程，不属于发布内容。**压缩分享时排除 SampleProject，其余文件可一起打包。** 接收者只需安装 `FlashToUnity/`，不需要开发工具或样例素材。

工具面向 SWF 显示列表与时间轴，不是完整的 Flash Player。ActionScript、部分图形格式、滤镜和复杂遮罩可能无法还原，具体见详细介绍。

MIT 协议适用于插件源码与随附文档。导入的 SWF、角色图片及生成的相关素材，其使用权限取决于原素材授权。
