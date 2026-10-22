# PCL Download Unlocker

> 第三方独立作品，非 PCL 官方提供。PCL 原作者：龙腾猫跃（LTCat）。  
> [PCL 官方仓库](https://github.com/Meloong-Git/PCL) · [赞助原作者](https://meloong.com/afd/a/LTCat)

这是针对 **Plain Craft Launcher 2（PCL）** 的外置下载功能补丁。它不锁定具体版本号，而是按程序集结构判断兼容性，可用于结构兼容的正式版与快照版。

它不会覆盖、改写或删除原始 PCL EXE。禁用前的代码载体已经嵌入补丁程序，运行过程中完全离线，不会访问 GitHub 或其他网络地址。补丁会在同一目录生成：

`原文件名.DownloadUnlocked.exe`

## 使用

1. 完整解压压缩包，不要直接在压缩包预览中运行，也不要只复制其中的 EXE。
2. 将解压目录里的全部补丁文件放到 PCL EXE 旁边。
3. 关闭正在运行的 PCL。
4. 双击 `PCL.DownloadUnlocker.exe`。
5. 以后使用生成的 `原文件名.DownloadUnlocked.exe`。

如果同一目录有多个正式版或快照版，请把要处理的 PCL EXE 拖到补丁 EXE 上。默认情况下，补丁完成后会自动启动生成的副本。

命令行：

```text
PCL.DownloadUnlocker.exe [PCL_EXE路径] [--no-launch]
```

## 恢复的功能

- Minecraft 原版/组件安装页
- 整合包导入与安装
- 已安装版本修改页中的 Minecraft 版本选择
- 社区资源详情页中的整合包安装按钮
- 下载栏“原版游戏”一项显示“程序经修改”。

补丁依据官方 `2.14.0.0 → 2.99.0.0` 提交差异，只恢复下载相关的 5 个方法。

## 安全边界

- 内置代码载体固定为官方 PCL 2.14.0.0，并在内存中校验：`5582952DED3B443503F66C23B810802BD20F1A3E847B54D9E09653A825859A36`。
- 输出前后都会校验结构，并再次确认原始 EXE 哈希未变化。
- 输出会保留目标 PCL 自己的程序集名称和版本号；来源校验失败时会直接中止。

兼容性说明：只要正式版或快照版仍保留上述类型与方法签名，补丁即可尝试生成副本。

## 构建

需要 .NET 9 SDK：

```powershell
dotnet build -c Release
```

生成兼容 32 位与 64 位 Windows 的 x86 自包含目录版本：

```powershell
dotnet restore -r win-x86
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=false
```

源码另行提供，其中包含 `dnlib 4.5.0` 和构建所需的本地代码载体。dnlib 许可证见 `LICENSE-dnlib.txt`，PCL 的许可与合理使用指南见 `LICENCE-PCL.txt`。

## 技术来源

- 下载限制引入提交：<https://github.com/Meloong-Git/PCL/commit/e4b0489a22cfd4bc57a18fc1e2ac66d5c18e7fc1>
- 禁用前版本提交：<https://github.com/Meloong-Git/PCL/commit/5ef81e490ae46b1100d61e33d0183094c7a80ca8>
- PCL 2.99.0.0 发布说明：<https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0>

本项目按照 PCL 存储库合理使用指南明确标注为第三方独立二次创作，并提供完整补丁源码。
