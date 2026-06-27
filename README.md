# 🎬 SubtitleTranscriber · 字幕转录者

> **让视频一键拥有智能字幕，Whisper 本地识别 + ASS 字幕生成 + ffmpeg 烧录，全自动流水线！**

<p align="center">
  <img src="https://img.shields.io/badge/version-1.3-blue.svg" alt="v1.3">
  <img src="https://img.shields.io/badge/.NET-8.0-purple.svg" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/license-MIT-green.svg" alt="MIT License">
  <img src="https://img.shields.io/badge/Whisper-本地识别-brightgreen.svg" alt="Whisper">
</p>

---

## 🌟 这是什么？

**SubtitleTranscriber（字幕转录者）** 是一款纯本地的视频字幕生成工具。你只需要把视频丢进去，它就会：

1. 🎵 **提取音频** — 从视频中分离出高质量音频
2. 🧠 **Whisper 智能识别** — 本地运行 OpenAI Whisper 模型，精准语音转文字
3. 📝 **生成 ASS 字幕** — 自动排版、繁转简、过滤垃圾字幕
4. 🎬 **烧录到视频** — 用 ffmpeg 把字幕永久嵌入视频

**全程离线运行，不依赖任何在线 API，保护你的隐私！**

---

## ✨ 功能亮点

| 功能 | 说明 |
|------|------|
| 🎯 **纯本地运行** | Whisper 模型本地加载，无需联网，数据不出门 |
| 🧹 **智能清洗** | 自动繁转简、去说话人标签、过滤垃圾字幕（音乐/掌声/语气词全干掉） |
| 🔁 **重复检测** | 智能去重，防止 Whisper 疯狂复读同一句话 |
| 📐 **自动换行** | 超长字幕按字符数智能拆分，时间轴自动按比例分配 |
| 🔇 **静音偏移校正** | 自动检测视频开头静音，精准对齐字幕时间轴 |
| 📊 **分段识别** | 大视频分段处理，告别内存爆炸 |
| 🛠 **调试模式** | DryRun / 跳过识别 / 跳过导出，开发调试超方便 |
| 📂 **字幕可编辑** | 生成后自动打开 ASS 文件，确认后再合成，给你修改的机会 |

---

## 📥 下载 & 安装

### 方式一：Release 下载（推荐 🚀）

👉 **[前往 Releases 页面下载](https://github.com/CheatEnginePorta/SubtitleTranscriber/releases)**

下载后解压即用，无需安装任何运行时！

### 方式二：自行编译

```bash
git clone https://github.com/CheatEnginePorta/SubtitleTranscriber.git
cd SubtitleTranscriber
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## 📖 使用方法

### 第一步：准备文件

把以下文件放到程序目录下：

```
你的文件夹/
├── Program.exe          ← 主程序
├── input.mp4            ← 你的视频文件（必须叫这个名字）
└── config.json          ← 配置文件（首次运行自动生成）
```

> **首次运行会自动生成 `config.json`，记得先修改配置再重新运行哦！**

### 第二步：修改配置

打开 `config.json`，主要配置项：

```json
{
  "FfmpegPath": "C:\\software\\ffmpeg\\bin\\ffmpeg.exe",  // ffmpeg 路径
  "ModelPath": "ggml-base.bin",                           // Whisper 模型文件
  "Language": "zh",                                       // 语言
  "FontName": "Microsoft YaHei",                          // 字幕字体
  "FontSize": 48,                                         // 字号
  "MaxCharsPerLine": 25,                                  // 每行最大字符数
  "SubtitleMarginV": 30,                                  // 垂直边距
  "SubtitleAlignment": 2                                  // 字幕对齐方式
}
```

> 💡 **需要 ffmpeg？** 去 [ffmpeg.org](https://ffmpeg.org/download.html) 下载，或者直接放到 `C:\software\ffmpeg\bin\` 下。

### 第三步：运行！

双击运行 `Program.exe`，然后：

1. 🎵 程序自动提取音频
2. 🔇 检测静音边界
3. 🧠 第一次运行会自动下载 Whisper 模型（约 140MB）
4. 🎤 开始语音识别（耐心等待，视频越长越久~）
5. 📄 自动打开生成的字幕文件，**你可以手动编辑修正**
6. ✅ 确认后自动烧录字幕到视频，输出 `output.mp4`

**全程交互超简单，只需要最后按个 Y 确认！**

---

## 🎮 交互菜单

生成字幕文件后，程序会等你确认：

```
[Y] 确认导出视频
[N] 取消退出
[R] 重新识别（重新跑 Whisper）
[D] 查看调试日志
```

---

## 🔧 调试模式

在 `config.json` 中开启调试功能：

```json
"Debug": {
  "Enable": true,          // 开启调试日志
  "SkipRecognition": true, // 跳过识别，用测试字幕
  "SkipExport": true,      // 跳过合成
  "DryRun": true           // 只打印 ffmpeg 命令不执行
}
```

适合快速测试字幕样式或排查问题。

---

## 📂 输出文件

| 文件 | 说明 |
|------|------|
| `output.mp4` | 🎉 最终成品！带字幕的视频 |
| `subtitle.ass` | 📝 ASS 字幕文件，可单独使用 |
| `debug.log` | 📋 调试日志（需开启 Debug） |

---

## ⚠️ 注意事项

- **首次运行会自动下载 Whisper 模型**（ggml-base.bin，约 140MB），请确保网络畅通
- 视频文件必须命名为 **`input.mp4`** 放在程序目录
- 识别时长取决于视频长度和电脑性能，长视频请耐心等待
- 如果识别结果不理想，可以按 `R` 重新识别，或手动编辑字幕文件

---

## 🧪 技术栈

- **.NET 8.0** — 主框架
- **Whisper.net** — 本地语音识别
- **OpenCCNET** — 繁简转换
- **FFmpeg** — 音频提取 + 视频合成
- **ASS 字幕格式** — 高兼容性字幕

---

## 🤝 贡献 & 反馈

项目地址：[https://github.com/CheatEnginePorta/SubtitleTranscriber](https://github.com/CheatEnginePorta/SubtitleTranscriber)

- ⭐ 觉得好用？给个 Star 吧！
- 🐛 遇到 Bug？提个 Issue！
- 💡 有想法？欢迎 PR！

---

## 📜 许可证

MIT License © 2026 CheatEnginePorta

---

<p align="center">
  <b>🎬 视频加字幕，从未如此简单！</b>
  <br>
  <sub>Made with ❤️ by CheatEnginePorta</sub>
</p>


---

