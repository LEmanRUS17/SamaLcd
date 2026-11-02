# SAMA LCD

[English](README.md) | **简体中文** | [日本語](README.ja.md) | [Русский](README.ru.md)

使用 C# / .NET 8 编写的开源 **SAMA WaterCooling**（`Sama.exe`）替代程序，用于先马水冷泵上的 2.1 英寸、480×480 圆形屏幕。

## 开始使用

运行环境：Windows、.NET 8 Desktop Runtime、Microsoft Edge WebView2 Runtime。部分硬件传感器还需要管理员权限和单独安装的 [PawnIO 驱动](https://pawnio.eu)。

1. 完全退出原厂 SAMA 软件并关闭其开机自启。一个 COM 端口同一时间只能由一个程序占用。
2. 按下方说明构建、发布项目，再运行 `publish/SamaLcd.exe`。程序会为硬件传感器请求管理员权限。
3. **图像**管理背景、裁剪、旋转和播放；**数据**选择主题和编辑布局；**传感器**查看读数与键名；**程序**设置语言、自启动、存储和诊断。

关闭窗口会缩到托盘，从托盘菜单才能完全退出。开机自启时在托盘运行，不打开设置窗口。

设置：`%APPDATA%/SamaLcd/settings.json`。自定义布局：`%APPDATA%/SamaLcd/custom-layout.json`。

## 界面语言

默认语言为**英文**，没有语言字段的旧设置也使用英文。通过 **App → Language** 切换英文、简体中文、日文或俄文，立即生效。`language` 字段保存为 `en`、`zh-Hans`、`ja` 或 `ru`。

设置页面、编辑器标签、传感器别名、托盘菜单、对话框和状态信息均提供翻译。自定义文字、文件名、传感器 ID 和主题内容保留原文。

## 背景与应用设置

- **图库**读取程序旁 `backgrounds` 文件夹中的图片、GIF 和视频。加入文件后点击**刷新**。第一个“主题背景”选项恢复主题自带背景。
- **颜色**提供调色板和自定义颜色。
- **本地文件**支持图片、动画 GIF 和 MP4、MKV、WEBM、MOV、AVI 等视频。缩放方式为**填满**、**适应**、**拉伸**。

选择文件或图库，修改颜色、缩放、裁剪、旋转和播放方式，都只更新本地草稿预览。**尚未应用**表示存在待提交修改。实体屏幕继续使用上次应用的设置；调整期间不会转换或上传新视频。

点击**应用到屏幕**后，统一保存并发送图像设置，已暂停时恢复播放。**放弃修改**恢复已应用的设置。没有修改时不能重复应用。切换语言或亮度不会保存图像草稿；完全退出程序后，未应用的修改会丢弃。亮度和暂停仍立即生效。

长文件名最多显示两行，超出部分省略，悬停可查看完整名称。**放大／裁剪…**与缩放按钮位于同一行并对齐。

### 正方形裁剪与视频预览

选择文件或图库项目后点击**放大／裁剪…**。拖动画面调整位置，用缩放滑块或鼠标滚轮去除黑边。裁剪区域为正方形，圆形遮罩表示实体屏幕可见范围。

视频可拖动进度条选择有内容的画面，即使开头是黑屏也能调整。预览帧由 FFmpeg 提取，也支持浏览器无法直接解码的格式。切换时间位置会保留缩放和裁剪位置。**采用裁剪**只更新草稿；**取消**不修改设置；**取消裁剪**恢复缩放方式。最后点击**应用到屏幕**才发送。裁剪应用到整段视频，不覆盖原文件。

图片和通过 USB 传输的 GIF 使用选定的源区域；视频和屏幕端播放的 GIF 在缩放、旋转前通过 FFmpeg 应用同一区域。修改裁剪会使旧转码缓存失效。

### FFmpeg 与播放

启动时检查程序目录、PATH、工具缓存及已知 SAMA 安装目录中的 `ffmpeg.exe`。找不到时，从 [ffmpeg.org](https://ffmpeg.org/download.html) 链接的 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 下载 essentials ZIP 与 SHA-256，校验后安装可执行文件和许可证。程序目录不可写时使用 `%LOCALAPPDATA%/SamaLcd/tools`。通过**程序 → 检查 FFmpeg**可重试；安装失败时仍可使用静态图片。

**在屏幕端播放视频和 GIF**默认开启。应用设置后，转为 480×480、24 fps、H.264/yuv420p 无音频文件，上传后本地循环播放；主题数据作为透明叠加层传输。缓存位于 `%LOCALAPPDATA%/SamaLcd/video`，上传文件名为 `samalcd_*.mp4`。

关闭屏幕端播放，或本地播放失败后回退到 USB 传输时，会逐帧发送。实测设备吞吐约 2.7 MB/s，整帧变化时约 4 fps，因此屏幕本地播放更流畅。

## 容量检查与 SD 文件

转码前读取容量，转码后按实际大小再次检查。优先按空闲空间编码；无法维持可用码率时，按总容量编码，完成后再询问是否清理。编码使用两遍码率控制，预留 512 KiB，另留 10% 封装余量，目标码率最高 2400 kbit/s。不会截短视频来压缩体积。

空间足够就上传；不足则列出 SD 视频、大小和可释放空间，等待确认。只删除确认清单中且大小未改变的文件。取消或 Esc 会保留文件并暂停操作。切换源文件或退出程序会使请求失效。没有确认处理器的 CLI 会话不能自动删除文件。

**程序 → SD 卡文件**管理 `/mnt/SDCARD/video/`。进入页面或点击**刷新**可读取名称、大小、文件合计占用和容量。每行**删除**只确认一个文件；**清空视频…**确认整个显示列表。删除后暂停播放，防止立即重新上传，再刷新列表及空闲空间。列表变化或设备重连后，旧确认失效。不会清空或格式化其他 SD 目录及内置存储。

## 传感器与自定义布局

无法读取的数值显示为 `--`。`gpu.*` 别名优先使用独立显卡；`ram.*` 表示物理内存。CPU 温度、频率、功耗及部分主板、风扇、水泵读数需要 PawnIO。CPU 读数不可用时，界面会提示。

**数据 → 自定义布局**提供 4×4 至 16×16 网格。拖动元素移动位置，拖动右下角改变大小；方向键移动选中元素，Delete 删除。支持传感器数值、进度条、文字、时间和日期，可设置颜色、大小、对齐和标题。布局修改立即生效。

## 主题

主题位于程序旁 `themes/<名称>/theme.json`。内置 `Dashboard`、`Clock`、`Animation`、`Orbit`、`Gauges`、`Minimal`、`Neon`、`Terminal`。主题目录可包含图片和 `.ttf`/`.otf` 字体。

```json
{
  "name": "我的主题",
  "intervalMs": 1000,
  "background": {
    "color": "#000000",
    "image": "bg.gif",
    "fit": "cover"
  },
  "widgets": [
    {
      "type": "text",
      "x": 240,
      "y": 200,
      "text": "{cpu.temp:0}°",
      "size": 96,
      "bold": true,
      "color": "#FFFFFF"
    },
    {
      "type": "arc",
      "x": 240,
      "y": 240,
      "radius": 218,
      "thickness": 18,
      "value": "cpu.load",
      "startAngle": 135,
      "sweepAngle": 270,
      "color": "#00D1FF",
      "colorEnd": "#FF3B5C"
    },
    {
      "type": "bar",
      "x": 140,
      "y": 300,
      "width": 200,
      "height": 10,
      "value": "gpu.load",
      "max": 100
    },
    {
      "type": "image",
      "x": 0,
      "y": 0,
      "path": "logo.png",
      "width": 100,
      "height": 100
    },
    {
      "type": "circle",
      "x": 240,
      "y": 400,
      "radius": 58,
      "color": "#99000000"
    }
  ]
}
```

文字替换格式为 `{键名:格式}`，例如 `{gpu.temp:0.0}`、`{time:HH:mm:ss}`、`{date:dd.MM}`。常用键名：`cpu.temp`、`cpu.load`、`cpu.clock`、`cpu.power`、`gpu.temp`、`gpu.hotspot`、`gpu.load`、`gpu.clock`、`gpu.power`、`gpu.mem.load`、`gpu.fan`、`ram.load`、`ram.used`、`ram.free`、`board.temp`、`pump.rpm`、`fan.rpm`。也支持 LibreHardwareMonitor ID，可在**传感器**页面或通过 `samalcd-cli sensors` 查看。

## 诊断日志

**程序 → 诊断日志**提供持久化的 **Debug 日志**开关、**打开日志文件夹**和**导出诊断日志**。基本操作和错误始终记录；Debug 额外记录界面操作、裁剪手势、FFmpeg 参数、USB 文本命令及回复、内存和传输统计。默认关闭，不会应用待提交的图像修改。

日志位于 `%LOCALAPPDATA%/SamaLcd/logs`，包含 UTC 时间、会话与操作 ID、线程 ID 和异常堆栈。每条写入后刷新；单文件达到 2 MiB 时轮转，最多保留 10 个。导出 ZIP 包含日志、运行环境／设备／设置快照，以及限定数量、只读查询的近期 SAMA LCD Windows 崩溃记录。不包含媒体文件、原始画面或完整用户布局，不会自动上传。

排查时先开启 Debug，再复现操作；必要时重启后导出 ZIP。未正常结束的会话会在下次启动时标记。原生组件崩溃可能绕过托管异常处理，Windows 记录可提供额外线索。文件对话框在 WebView2 回调返回后排队打开，遵循[微软线程模型说明](https://learn.microsoft.com/microsoft-edge/webview2/concepts/threading-model#reentrancy)；解码读取线程异常会被捕获并记录。

## 构建与开发

```powershell
dotnet build
dotnet test
.\publish.ps1
```

发布后，托盘程序位于 `publish/`，CLI 位于 `publish/cli/`。请保留可执行文件旁的依赖和资源目录。

- [SamaLcd.Core](src/SamaLcd.Core)：设备协议、渲染、传感器、显示循环和诊断。
- [SamaLcd](src/SamaLcd)：Windows 托盘程序与使用本地 daisyUI/Tailwind 资源的 WebView2 界面。
- [SamaLcd.Cli](src/SamaLcd.Cli)：命令行工具。
- [测试](tests/SamaLcd.Tests)：协议参考对比、渲染、多语言、清理确认和诊断。

普通浏览器打开 `src/SamaLcd/wwwroot/index.html` 可使用模拟数据预览。新增组件类型时，修改 `Rendering/Theme.cs` 和 `ThemeRenderer` 的绘制逻辑。

原生和网页界面共享 `src/SamaLcd.Core/Localization/*.json`。修改翻译后运行 `node scripts/generate-locales.mjs`，再运行 `dotnet test`。提交的浏览器语言包支持独立预览。说明功能变化时，请同步四种语言的 README。

## 命令行工具

运行 `publish/cli/samalcd-cli.exe`：

| 命令 | 用途 |
|---|---|
| `ports`, `info`, `sensors` | 列出端口、识别屏幕、查看传感器 |
| `brightness 50`, `color "#FF0000"`, `image pic.png` | 设置亮度、颜色或图片 |
| `theme <folder> [seconds] [--bg <color-or-file>]` | 运行主题 |
| `render <folder> out.png` | 不访问设备，渲染到文件 |
| `bench [frames]` | 测量传输速度 |
| `ls [folder]` | 列出文件及存储信息 |
| `upload <file> <remote-path>`, `play <remote-path>` | 上传或播放设备文件 |
| `fit-video <source> <budget-bytes> <output>` | 按字节预算编码 |
| `raw <code> [text]` | 发送实验性命令 |

可加 `--port COM3` 指定端口。访问设备前，请退出其他屏幕控制程序。

## 设备协议

实测屏幕工作模式为 `VID_1D6B&PID_0121`，启动模式为 `VID_1A86&PID_CA21` / `CT21INCH`，打开启动模式端口可唤醒。实测握手为 `chs_5inch.dev1_rom1.88`。协议由 `Sama.exe` 逆向分析而来，与 Turing Smart Screen rev C 有关；兼容性取决于硬件与固件。

| 命令 | 编号 | 数据／回复 |
|---|---|---|
| 握手 | 1 | `C5 D3` → `chs_…rom<version>` |
| 存储 | 100 | 内置总量／已用／空闲，SD 总量／已用／空闲，单位 KiB |
| 列目录 | 101 | 路径 → `result:dir:…file:a.mp4/b.mp4/` |
| 删除文件 | 102 | 文件路径 |
| 文件大小 | 110 | 路径 → 字节数；不存在为 `0` |
| 创建／上传文件 | 111 | 路径 + LE32 大小 → `create_success`、文件字节、`file_rev_done` |
| 播放视频 | 120 | 路径，标志 1 → `play_video_success` |
| 停止视频／媒体 | 121 / 150 | 无文本载荷 |
| 亮度 | 123 | 一个字节；原厂程序将 0–255 乘以 0.76 |
| 全帧 | 200 | W×H×4 字节 BGRA |
| 叠加层 | 202 + 208 | BGRA + 掩码 `[index:24][length:16]…EF 69` |
| 差分帧 | 204 | BE32 序号、可选 BE32 掩码长度、片段和 `EF 69` |
| 状态 | 207 | `needReSend:X\|renderCnt:N` |

命令包长 250 字节：`[code][EF 69][length BE32][flag][0 0][从偏移 10 开始的载荷]`。数据包每包含 249 个有效字节。上传前需要停止播放。实测内置存储约 2.6 MiB，SD 容量依设备而异。像素／片段编码见 [FrameCodec.cs](src/SamaLcd.Core/Device/FrameCodec.cs)，测试包含与原厂输出的逐字节对比。
