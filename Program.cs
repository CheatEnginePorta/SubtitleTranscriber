// SubtitleTranscriber.cs
// 字幕转录者 v1.3
// 功能：给已有视频用 Whisper 本地识别语音，生成 ASS 字幕，用户确认后 ffmpeg 烧录
// 用法：编辑 config.json → dotnet run

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.Ggml;
using OpenCCNET;

// 设置控制台编码为 UTF-8
Console.OutputEncoding = System.Text.Encoding.UTF8;
// 全局设置：所有 Console 输出立即刷新
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
// ★ 强制控制台实时刷新
Console.BufferHeight = 9999;
// ★ 防止控制台程序在后台休眠
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

// ========== 配置加载 ==========
string projectDir = AppDomain.CurrentDomain.BaseDirectory;
string configFile = Path.Combine(projectDir, "config.json");

var config = new Config
{
    FfmpegPath = @"C:\software\ffmpeg\bin\ffmpeg.exe",
    ModelPath = "ggml-base.bin",
    Language = "zh",
    FontName = "Microsoft YaHei",
    FontSize = 48,
    MaxCharsPerLine = 25,
    SubtitleMarginV = 30,
    SubtitleAlignment = 2,
    Debug = new DebugConfig()
};

if (File.Exists(configFile))
{
    try
    {
        string json = File.ReadAllText(configFile, Encoding.UTF8);
        var loaded = JsonSerializer.Deserialize<Config>(json);
        if (loaded != null) config = loaded;
        Console.WriteLine("✅ 已加载配置文件");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ 配置文件读取失败，使用默认配置: {ex.Message}");
    }
}
else
{
    string defaultJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configFile, defaultJson, Encoding.UTF8);
    Console.WriteLine($"📝 已生成默认配置文件: {configFile}");
    Console.WriteLine("   请修改配置后重新运行程序。");
    Console.ReadKey();
    return;
}

// 文件路径
string videoFile = Path.Combine(projectDir, "input.mp4");
string audioFile = Path.Combine(projectDir, "temp_audio.wav");
string cleanedAudio = Path.Combine(projectDir, "temp_audio_cleaned.wav");
string assFile = Path.Combine(projectDir, "subtitle.ass");
string outputFile = Path.Combine(projectDir, "output.mp4");
string debugLogFile = Path.Combine(projectDir, "debug.log");

// ========== 检查输入 ==========
if (!File.Exists(videoFile))
{
    Console.WriteLine($"❌ 找不到视频文件: {videoFile}");
    Console.WriteLine("   请将视频文件命名为 input.mp4 放在程序目录");
    Console.ReadKey();
    return;
}

Console.WriteLine($"🎬 输入视频: {videoFile}");
Console.WriteLine($"📝 语言: {config.Language}");
Console.WriteLine($"🧠 模型: {config.ModelPath}");
Console.WriteLine();

// ★ 提前获取视频时长，用于后续进度和验证
float videoDurationSec = GetMediaDuration(config.FfmpegPath, videoFile);
Console.WriteLine($"   ⏱️ 视频时长: {FormatTime(TimeSpan.FromSeconds(videoDurationSec))}");
Console.WriteLine();

// ========== 调试初始化 ==========
if (config.Debug.Enable)
{
    if (File.Exists(debugLogFile)) File.Delete(debugLogFile);
    LogDebug(debugLogFile, "=== 字幕转录者 调试日志 ===");
    LogDebug(debugLogFile, $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    LogDebug(debugLogFile, $"视频: {videoFile}");
    LogDebug(debugLogFile, $"模型: {config.ModelPath}");
    LogDebug(debugLogFile, $"语言: {config.Language}");
    LogDebug(debugLogFile, $"跳过识别: {config.Debug.SkipRecognition}");
    LogDebug(debugLogFile, $"跳过导出: {config.Debug.SkipExport}");
    LogDebug(debugLogFile, $"DryRun: {config.Debug.DryRun}");
    LogDebug(debugLogFile, "---");
}

// ========== 第一步：提取音频 ==========
Console.WriteLine("🎵 步骤1/4: 提取音频...");
string extractArgs = $"-i \"{videoFile}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 -y \"{audioFile}\"";
RunFfmpeg(config.FfmpegPath, extractArgs, projectDir, "提取音频", debugLogFile, config.Debug.Enable);

// ★ 验证提取的音频时长
float audioDurationSec = GetMediaDuration(config.FfmpegPath, audioFile);
TimeSpan audioDuration = TimeSpan.FromSeconds(audioDurationSec);
Console.WriteLine($"✅ 音频已提取: {new FileInfo(audioFile).Length / 1024} KB, 时长: {FormatTime(audioDuration)}");

// ★ 如果音频时长明显短于视频时长，给出警告
if (audioDurationSec > 0 && videoDurationSec > 0 && audioDurationSec < videoDurationSec * 0.9)
{
    Console.WriteLine($"   ⚠️ 警告：音频时长 ({FormatTime(audioDuration)}) 明显短于视频时长 ({FormatTime(TimeSpan.FromSeconds(videoDurationSec))})");
}

// ========== 检测静音边界 ==========
Console.WriteLine("🔇 检测静音边界...");
double silenceOffset = GetFirstVoiceStart(config.FfmpegPath, audioFile);
Console.WriteLine($"   🎯 第一个声音开始于: {silenceOffset:F2}s");

// ========== 初始化繁转简 ==========
try
{
    ZhConverter.Initialize();
    Console.WriteLine("   ✅ 繁转简引擎已初始化");
}
catch (Exception ex)
{
    Console.WriteLine($"   ⚠️ 繁转简引擎初始化失败: {ex.Message}");
}

// ========== 第二步：Whisper 语音识别（分段识别，避免内存问题）==========
Console.WriteLine("🎤 步骤2/4: 语音识别...");

var segments = new List<(TimeSpan Start, TimeSpan End, string Text)>();

if (config.Debug.SkipRecognition)
{
    if (File.Exists(assFile))
    {
        Console.WriteLine("   [调试] 跳过识别，使用已有字幕文件");
    }
    else
    {
        Console.WriteLine("   [调试] 跳过识别，生成测试字幕");
        segments.Add((TimeSpan.Zero, TimeSpan.FromSeconds(5), "这是测试字幕第一行，用于调试模式"));
        segments.Add((TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10), "调试模式 - 字幕合成测试"));
        segments.Add((TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(15), "请替换为实际识别结果"));
    }
}
else
{
    // 检查/下载模型
    string modelPath = Path.Combine(projectDir, config.ModelPath);
    if (!File.Exists(modelPath))
    {
        Console.WriteLine($"   ⬇️ 模型文件不存在，正在下载 {config.ModelPath}...");
        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
            using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream);
            Console.WriteLine("   ✅ 模型下载完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ 模型下载失败: {ex.Message}");
            Console.WriteLine("   请手动下载模型放到程序目录");
            Console.WriteLine("   下载地址: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
            Console.ReadKey();
            return;
        }
    }

    // 识别
    Console.WriteLine("   🧠 加载模型中...");
    try
    {
        int segmentDurationSec = 60;
        float totalAudioSec = GetMediaDuration(config.FfmpegPath, audioFile);
        int totalChunks = (int)Math.Ceiling(totalAudioSec / segmentDurationSec);

        Console.WriteLine($"   📝 开始分段识别（共 {totalChunks} 段，每段 {segmentDurationSec} 秒）...");

        var rawSegments = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        int segmentCount = 0;
        TimeSpan lastProcessedTime = TimeSpan.Zero;
        string lastRawText = "";
        int repeatCount = 0;
        const int maxRepeat = 5;

        // ★★★ 模型只加载一次（重）★★★
        using (var whisperFactory = WhisperFactory.FromPath(modelPath))
        {
            for (int chunk = 0; chunk < totalChunks; chunk++)
            {
                float chunkStartSec = chunk * segmentDurationSec;
                float chunkEndSec = Math.Min((chunk + 1) * segmentDurationSec, totalAudioSec);

                Console.WriteLine($"   📦 处理第 {chunk + 1}/{totalChunks} 段 ({FormatTime(TimeSpan.FromSeconds(chunkStartSec))} -> {FormatTime(TimeSpan.FromSeconds(chunkEndSec))})");

                string chunkAudio = Path.Combine(projectDir, $"temp_chunk_{chunk}.wav");
                string chunkArgs = $"-i \"{audioFile}\" -ss {chunkStartSec} -to {chunkEndSec} -c copy -y \"{chunkAudio}\"";
                RunFfmpeg(config.FfmpegPath, chunkArgs, projectDir, $"截取第 {chunk + 1} 段", debugLogFile, config.Debug.Enable);

                // ★★★ 每段独立创建 Processor（轻），确保 Prompt 每次都生效 ★★★
                using (var processor = whisperFactory.CreateBuilder()
                    .WithLanguage(config.Language)
                    .WithPrompt("以下是简体中文的句子。请用简体中文输出。")
                    .WithTemperature(0.0f)
                    .WithMaxSegmentLength(10)
                    .Build())
                using (var chunkStream = File.OpenRead(chunkAudio))
                {
                    await foreach (var result in processor.ProcessAsync(chunkStream))
                    {
                        TimeSpan correctedStart = result.Start.Add(TimeSpan.FromSeconds(chunkStartSec + silenceOffset));
                        TimeSpan correctedEnd = result.End.Add(TimeSpan.FromSeconds(chunkStartSec + silenceOffset));
                        string text = result.Text.Trim();

                        // 重复检测
                        if (text == lastRawText && !string.IsNullOrEmpty(text))
                        {
                            repeatCount++;
                            if (repeatCount >= maxRepeat)
                            {
                                Console.WriteLine($"   ⚠️ 检测到连续重复 {repeatCount} 次，跳过: \"{text}\"");
                                continue;
                            }
                        }
                        else
                        {
                            repeatCount = 0;
                        }
                        lastRawText = text;

                        Console.WriteLine($"   [{FormatTime(correctedStart)} -> {FormatTime(correctedEnd)}] {text}");
                        Console.Out.Flush();

                        rawSegments.Add((correctedStart, correctedEnd, text));
                        segmentCount++;
                        lastProcessedTime = correctedEnd;
                    }
                }

                // 删除临时文件
                try { if (File.Exists(chunkAudio)) File.Delete(chunkAudio); } catch { }

                // ★★★ 每段结束后回收 Processor 资源 ★★★
                GC.Collect();
                GC.WaitForPendingFinalizers();

                double progressPct = chunkEndSec / totalAudioSec * 100;
                Console.WriteLine($"   📊 分段进度: 第 {chunk + 1}/{totalChunks} 段完成 ({progressPct:F1}%)");
            }

            // ★ 检查是否完整识别
            if (segmentCount > 0 && lastProcessedTime.TotalSeconds < videoDurationSec * 0.9)
            {
                Console.WriteLine($"   ⚠️ 警告：识别可能不完整！最后处理到 {FormatTime(lastProcessedTime)}，视频时长 {FormatTime(TimeSpan.FromSeconds(videoDurationSec))}");
                Console.WriteLine($"     共识别 {segmentCount} 条片段，但只覆盖了前 {(lastProcessedTime.TotalSeconds / videoDurationSec * 100):F1}% 的内容");
            }
            else if (segmentCount == 0)
            {
                Console.WriteLine("   ⚠️ 警告：未识别出任何内容！");
            }
            else
            {
                Console.WriteLine($"   ✅ 识别覆盖完整时长");
            }

            // ★ 处理原始识别结果：统一清理、去重、拆分超长字幕
            segments.Clear();
            string lastText = "";
            foreach (var seg in rawSegments)
            {
                string text = seg.Text.Trim();
                double duration = (seg.End - seg.Start).TotalSeconds;

                // ★ 统一文本清理（包含繁转简）
                text = CleanTranscriptText(text);

                // 如果清理后为空，跳过
                if (string.IsNullOrEmpty(text))
                    continue;

                // 去重：和上一条内容相似则跳过
                if (lastText != "" && IsSimilarText(text, lastText))
                    continue;

                // 基本过滤：太短且时间太短则跳过
                if (text.Length < 2 && duration < 0.3)
                    continue;

                // ★★★ 所有字幕都经过拆分，确保每段不超过 MaxCharsPerLine ★★★
                var parts = SplitLongSubtitle(text, seg.Start, seg.End, config.MaxCharsPerLine);
                foreach (var part in parts)
                    segments.Add(part);

                lastText = text;
            }

            // 打印结果
            foreach (var seg in segments)
            {
                string msg = $"   [{FormatTime(seg.Start)} --> {FormatTime(seg.End)}] {seg.Text}";
                Console.WriteLine(msg);
                if (config.Debug.Enable)
                    LogDebug(debugLogFile, msg);
            }

            Console.WriteLine($"   ✅ 识别完成，共 {segments.Count} 条字幕（原始 {rawSegments.Count} 条）");
        } // ★★★ whisperFactory 在此释放 ★★★
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ❌ 识别失败: {ex.Message}");
        Console.WriteLine($"      异常类型: {ex.GetType().FullName}");
        Console.WriteLine($"      堆栈跟踪: {ex.StackTrace}");
        if (config.Debug.Enable) LogDebug(debugLogFile, $"识别错误: {ex}");
        Console.ReadKey();
        return;
    }
}

// ========== 后处理：过滤垃圾字幕 ==========
Console.WriteLine("   🧹 后处理过滤...");
segments = PostProcessSegments(segments);
Console.WriteLine($"   ✅ 过滤后剩余 {segments.Count} 条字幕");

// ========== 生成 ASS 字幕 ==========
Console.WriteLine("   📄 生成 ASS 字幕...");
string assContent = GenerateAss(segments, config);
File.WriteAllText(assFile, assContent, Encoding.UTF8);
Console.WriteLine($"   ✅ 字幕已保存: {assFile}");

// ========== 第三步：用户确认 ==========
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════");
Console.WriteLine("  ✏️ 步骤3/4: 请检查并编辑字幕");
Console.WriteLine($"  字幕文件: {assFile}");
Console.WriteLine("═══════════════════════════════════");

// 打开字幕文件
try
{
    Process.Start(new ProcessStartInfo { FileName = assFile, UseShellExecute = true });
    Console.WriteLine("   📂 已打开字幕文件，请编辑后保存");
}
catch
{
    Console.WriteLine("   ⚠️ 无法自动打开，请手动编辑: " + assFile);
}

Console.WriteLine();
Console.WriteLine("编辑完成后选择：");
Console.WriteLine("  [Y] 确认导出视频");
Console.WriteLine("  [N] 取消退出");
Console.WriteLine("  [R] 重新识别（重新运行 Whisper）");
Console.WriteLine("  [D] 查看调试日志");
Console.Write("> ");

string? choice;
do
{
    choice = Console.ReadLine()?.Trim().ToUpper();
    switch (choice)
    {
        case "Y":
            // 重新读取用户编辑后的字幕
            assContent = File.ReadAllText(assFile, Encoding.UTF8);
            Console.WriteLine("   ✅ 已读取编辑后的字幕");
            break;
        case "N":
            Console.WriteLine("   🚫 用户取消");
            Cleanup(audioFile, cleanedAudio, debugLogFile, config.Debug.Enable);
            return;
        case "R":
            Console.WriteLine("   🔄 重新识别...");
            segments.Clear();
            string modelPath2 = Path.Combine(projectDir, config.ModelPath);
            using (var whisperFactory = WhisperFactory.FromPath(modelPath2))
            using (var processor = whisperFactory.CreateBuilder()
                .WithLanguage(config.Language)
                .WithPrompt("以下是简体中文的句子。请用简体中文输出。")
                .Build())
            using (var audioStream = File.OpenRead(cleanedAudio))
            {
                await foreach (var result in processor.ProcessAsync(audioStream))
                {
                    TimeSpan correctedStart = result.Start.Add(TimeSpan.FromSeconds(silenceOffset));
                    TimeSpan correctedEnd = result.End.Add(TimeSpan.FromSeconds(silenceOffset));
                    string text = CleanTranscriptText(result.Text.Trim());
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add((correctedStart, correctedEnd, text));
                        Console.WriteLine($"   [{FormatTime(correctedStart)} --> {FormatTime(correctedEnd)}] {text}");
                    }
                }
            }
            assContent = GenerateAss(segments, config);
            File.WriteAllText(assFile, assContent, Encoding.UTF8);
            try { Process.Start(new ProcessStartInfo { FileName = assFile, UseShellExecute = true }); } catch { }
            Console.Write("编辑完成后 [Y]确认 [N]取消 [R]重试 > ");
            break;
        case "D":
            if (File.Exists(debugLogFile))
            {
                try { Process.Start(new ProcessStartInfo { FileName = debugLogFile, UseShellExecute = true }); } catch { }
            }
            else
                Console.WriteLine("   📭 调试日志不存在（需在配置中启用 Debug.Enable）");
            Console.Write("> ");
            break;
        default:
            Console.Write("请输入 Y / N / R / D: ");
            break;
    }
} while (choice != "Y" && choice != "N");

// ========== 第四步：合成视频 ==========
Console.WriteLine();
Console.WriteLine("🎬 步骤4/4: 烧录字幕到视频...");

if (config.Debug.SkipExport)
{
    Console.WriteLine("   [调试] 跳过导出步骤");
    Console.WriteLine($"   ✅ 字幕文件保留在: {assFile}");
}
else if (config.Debug.DryRun)
{
    Console.WriteLine($"   [DryRun] ffmpeg -i \"{videoFile}\" -vf \"ass={assFile}\" -c:v libx264 -c:a aac -y \"{outputFile}\"");
    Console.WriteLine("   ✅ [DryRun] 模拟完成");
}
else
{
    Console.WriteLine($"   ⏱️ 视频时长: {FormatTime(TimeSpan.FromSeconds(videoDurationSec))}");

    // ASS 路径转义（ffmpeg 滤镜对路径中的冒号和反斜杠敏感）
    string assForFilter = "subtitle.ass";
    string burnArgs = $"-i \"{videoFile}\" -vf \"ass={assForFilter}\" -c:v libx264 -c:a aac -y \"{outputFile}\"";

    Console.WriteLine();
    Console.WriteLine("⏳ 合成进度:");

    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = burnArgs,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = projectDir
        }
    };

    int lastProgress = -1;
    var progressRegex = new Regex(@"time=(\d+):(\d+):(\d+\.\d+)");
    var ffmpegOutput = new StringBuilder();

    process.ErrorDataReceived += (sender, e) =>
    {
        if (e.Data == null) return;

        ffmpegOutput.AppendLine(e.Data);

        if (config.Debug.Enable)
            LogDebug(debugLogFile, $"[FFmpeg] {e.Data}");

        var match = progressRegex.Match(e.Data);
        if (match.Success)
        {
            int h = int.Parse(match.Groups[1].Value);
            int m = int.Parse(match.Groups[2].Value);
            float s = float.Parse(match.Groups[3].Value);
            float currentSec = h * 3600 + m * 60 + s;

            int progress = videoDurationSec > 0 ? (int)(currentSec / videoDurationSec * 100) : 0;
            if (progress > lastProgress)
            {
                lastProgress = progress;
                int barLen = 30;
                int filled = progress * barLen / 100;
                string bar = new string('█', filled) + new string('░', barLen - filled);
                TimeSpan current = TimeSpan.FromSeconds(currentSec);
                Console.Error.Write($"\r   [{bar}] {progress}%  ({FormatTime(current)} / {FormatTime(TimeSpan.FromSeconds(videoDurationSec))})");
                Console.Out.Flush();
            }
        }
    };

    process.Start();
    process.BeginErrorReadLine();
    process.WaitForExit();

    Console.WriteLine();
    Console.WriteLine();

    if (process.ExitCode == 0 && File.Exists(outputFile))
    {
        long fileSize = new FileInfo(outputFile).Length;
        Console.WriteLine($"✅ 视频合成完成！");
        Console.WriteLine($"   输出文件: {outputFile}");
        Console.WriteLine($"   文件大小: {fileSize / 1024 / 1024} MB");
    }
    else
    {
        Console.WriteLine($"❌ 合成失败 (退出码: {process.ExitCode})");
        Console.WriteLine();
        Console.WriteLine("══════ FFmpeg 完整输出 ══════");
        Console.WriteLine(ffmpegOutput.ToString());
        Console.WriteLine("═══════════════════════════════");
        if (config.Debug.Enable)
            Console.WriteLine("   详细日志已保存到 debug.log");
    }
}

// ========== 清理 ==========
Console.WriteLine();
Console.Write("🧹 清理临时文件...");
Cleanup(audioFile, cleanedAudio, debugLogFile, config.Debug.Enable);
Console.WriteLine(" 完成");

Console.WriteLine();
Console.WriteLine("按任意键退出...");
Console.ReadKey();

// =====================================================================
// 辅助方法
// =====================================================================

// 格式化 TimeSpan -> SRT 时间格式 (hh:mm:ss,fff)
static string FormatTime(TimeSpan ts) =>
    $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

// 格式化 TimeSpan -> ASS 时间格式 (h:mm:ss.xx)
static string FormatAssTime(TimeSpan ts)
{
    int h = ts.Hours;
    int m = ts.Minutes;
    float s = ts.Seconds + ts.Milliseconds / 1000f;
    return $"{h}:{m:D2}:{s:F2}";
}

static string GenerateAss(List<(TimeSpan Start, TimeSpan End, string Text)> segments, Config cfg)
{
    var sb = new StringBuilder();
    sb.AppendLine("[Script Info]");
    sb.AppendLine("ScriptType: v4.00+");
    sb.AppendLine("PlayResX: 1920");
    sb.AppendLine("PlayResY: 1080");
    sb.AppendLine("ScaledBorderAndShadow: yes");
    sb.AppendLine();
    sb.AppendLine("[V4+ Styles]");
    sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
    sb.AppendLine($"Style: Default,{cfg.FontName},{cfg.FontSize},&H00FFFFFF,&H00000000,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,{cfg.SubtitleAlignment},{60},{60},{cfg.SubtitleMarginV},1");
    sb.AppendLine();
    sb.AppendLine("[Events]");
    sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

    foreach (var seg in segments)
    {
        string startTime = FormatAssTime(seg.Start);
        string endTime = FormatAssTime(seg.End);
        string wrappedText = WrapText(seg.Text, cfg.MaxCharsPerLine);
        sb.AppendLine($"Dialogue: 0,{startTime},{endTime},Default,,0,0,0,,{wrappedText}");
    }

    return sb.ToString();
}

static string WrapText(string text, int maxCharsPerLine)
{
    if (string.IsNullOrEmpty(text)) return text;

    var lines = new List<string>();
    int start = 0;
    while (start < text.Length)
    {
        int len = Math.Min(maxCharsPerLine, text.Length - start);
        if (start + len < text.Length)
        {
            int lastSpace = text.LastIndexOf(' ', start + len - 1, len);
            if (lastSpace > start)
                len = lastSpace - start;
        }
        lines.Add(text.Substring(start, len));
        start += len;
    }
    return string.Join("\\N", lines);
}

static float GetMediaDuration(string ffmpeg, string mediaFile)
{
    var proc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-i \"{mediaFile}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    proc.Start();
    string output = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    var match = Regex.Match(output, @"Duration: (\d+):(\d+):(\d+\.\d+)");
    if (match.Success)
    {
        int h = int.Parse(match.Groups[1].Value);
        int m = int.Parse(match.Groups[2].Value);
        float s = float.Parse(match.Groups[3].Value);
        return h * 3600 + m * 60 + s;
    }
    return 0;
}

static void RunFfmpeg(string ffmpeg, string args, string workDir, string label, string logFile, bool debug)
{
    Console.WriteLine($"   ⏳ {label}...");

    var proc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        }
    };

    var outputBuilder = new StringBuilder();
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            outputBuilder.AppendLine(e.Data);
            if (debug) LogDebug(logFile, $"[FFmpeg-{label}] {e.Data}");
        }
    };

    proc.Start();
    proc.BeginErrorReadLine();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
    {
        Console.WriteLine($"   ⚠️ {label} 返回非零退出码: {proc.ExitCode}");
        Console.WriteLine("   FFmpeg 输出:");
        Console.WriteLine(outputBuilder.ToString());
        if (debug) Console.WriteLine("   请查看 debug.log 获取详细信息");
    }
}

static void LogDebug(string logFile, string message)
{
    try
    {
        File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
    catch { }
}

static void Cleanup(string? audio, string? cleaned, string? debugLog, bool keepDebug)
{
    try
    {
        if (audio != null && File.Exists(audio)) File.Delete(audio);
        if (cleaned != null && File.Exists(cleaned)) File.Delete(cleaned);
        if (!keepDebug && debugLog != null && File.Exists(debugLog)) File.Delete(debugLog);
    }
    catch { }
}

// ========== 检测音频中第一个声音的位置 ==========
static double GetFirstVoiceStart(string ffmpeg, string audioFile)
{
    try
    {
        // 用 ffmpeg 的 silencedetect 滤镜检测静音
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{audioFile}\" -af silencedetect=noise=-30dB:d=0.3 -f null -",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        string output = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // 解析 silencedetect 输出，找第一个 silence_end
        // 格式: [silencedetect @ 0000] silence_end: 1.234 | silence_duration: 1.234
        var matches = Regex.Matches(output, @"silence_end:\s*([\d.]+)");
        if (matches.Count > 0)
        {
            // 第一个 silence_end 就是第一个声音开始的位置
            double start = double.Parse(matches[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return start;
        }

        // 没检测到静音，说明开头就是声音
        return 0.0;
    }
    catch
    {
        // 出错时返回0，不裁剪
        return 0.0;
    }
}

// ========== 繁转简 ==========
static string ToSimplifiedChinese(string text)
{
    if (string.IsNullOrEmpty(text)) return text;

    try
    {
        // 使用 TWToHans 进行台湾繁体到简体的转换，并开启地区词汇转换
        return ZhConverter.TWToHans(text, true);
    }
    catch (Exception ex)
    {
        // 转换失败时保留原文，避免丢失内容
        Console.WriteLine($"   ⚠️ 繁转简失败: {ex.Message}");
        return text;
    }
}

// ========== 统一文本清理 ==========
static string CleanTranscriptText(string text)
{
    if (string.IsNullOrEmpty(text)) return text;

    // ★ 繁体转简体（使用 OpenCCNET）
    text = ToSimplifiedChinese(text);

    // 去掉说话人标签 "XXX:" 前缀
    text = RemoveSpeakerLabel(text);

    // 去掉 \NA \N 标记
    text = text.Replace("\\NA", "").Replace("\\N", "");

    // ★ 检测中英文比例，如果英文远多于中文则丢弃
    int chineseCount = Regex.Matches(text, @"[\u4e00-\u9fff]").Count;
    int englishCount = Regex.Matches(text, @"[A-Za-z]").Count;
    if (englishCount > 0 && chineseCount == 0)
        return "";  // 纯英文，丢弃
    if (englishCount > chineseCount * 2 && chineseCount > 0)
        return "";  // 英文远多于中文，丢弃

    // 去掉孤立的单个字母（如 "A"）
    text = Regex.Replace(text, @"\b[A-Za-z]\b", "").Trim();

    // 去掉中文字后面的字母（如 "可以A" -> "可以"）
    text = Regex.Replace(text, @"([\u4e00-\u9fff])[A-Za-z]+", "$1");

    // 去掉多余的逗号空格
    text = Regex.Replace(text, @"[,，]{2,}", "，");
    text = text.Trim().Trim(',').Trim('，');

    // 去掉多余的空格
    text = Regex.Replace(text, @"\s+", " ").Trim();

    return text;
}

// ========== 去掉说话人标签 ==========
static string RemoveSpeakerLabel(string text)
{
    var match = Regex.Match(text, @"^[\u4e00-\u9fffA-Za-z]+[:：]");
    if (match.Success)
    {
        string rest = text.Substring(match.Length).Trim();
        if (!string.IsNullOrEmpty(rest))
            return rest;
    }
    return text;
}

// ========== 拆分超长字幕 ==========
static List<(TimeSpan Start, TimeSpan End, string Text)> SplitLongSubtitle(
    string text, TimeSpan start, TimeSpan end, int maxCharsPerLine)
{
    var result = new List<(TimeSpan Start, TimeSpan End, string Text)>();
    double totalDuration = (end - start).TotalSeconds;

    // ★★★ 如果时长 <= 5 秒 且 长度 <= maxCharsPerLine，不拆分 ★★★
    if (totalDuration <= 5.0 && text.Length <= maxCharsPerLine)
    {
        result.Add((start, end, text));
        return result;
    }

    // ★ 强制拆分：每段不超过 maxCharsPerLine 个字符
    var finalParts = new List<string>();
    for (int i = 0; i < text.Length; i += maxCharsPerLine)
    {
        int len = Math.Min(maxCharsPerLine, text.Length - i);
        string part = text.Substring(i, len).Trim();
        if (!string.IsNullOrEmpty(part))
            finalParts.Add(part);
    }

    // ★★★ 如果按字符拆出来只有 1 段，但时长 > 5 秒，按每 3 秒一段强制拆分 ★★★
    if (finalParts.Count <= 1 && totalDuration > 5.0)
    {
        int numParts = Math.Max(2, (int)Math.Ceiling(totalDuration / 3.0));
        double partDuration = totalDuration / numParts;
        for (int i = 0; i < numParts; i++)
        {
            TimeSpan partStart = start.Add(TimeSpan.FromSeconds(i * partDuration));
            TimeSpan partEnd = start.Add(TimeSpan.FromSeconds(Math.Min((i + 1) * partDuration, totalDuration)));
            // 文本平均分配
            int startIdx = i * text.Length / numParts;
            int endIdx = Math.Min((i + 1) * text.Length / numParts, text.Length);
            string partText = text.Substring(startIdx, endIdx - startIdx).Trim();
            if (!string.IsNullOrEmpty(partText))
                result.Add((partStart, partEnd, partText));
        }
        return result;
    }

    if (finalParts.Count <= 1)
    {
        result.Add((start, end, text));
        return result;
    }

    // ★ 按字符数比例分配时间
    int totalChars = finalParts.Sum(p => p.Length);
    double currentOffset = 0;

    for (int i = 0; i < finalParts.Count; i++)
    {
        double ratio = (double)finalParts[i].Length / totalChars;
        double duration = Math.Max(totalDuration * ratio, 0.5); // 每段至少 0.5 秒
        TimeSpan partStart = start.Add(TimeSpan.FromSeconds(currentOffset));
        TimeSpan partEnd = start.Add(TimeSpan.FromSeconds(Math.Min(currentOffset + duration, totalDuration)));
        result.Add((partStart, partEnd, finalParts[i]));
        currentOffset += duration;
    }

    // 确保最后一段不超出
    if (result.Count > 0 && result.Last().End > end)
    {
        var last = result[result.Count - 1];
        result[result.Count - 1] = (last.Start, end, last.Text);
    }

    return result;
}

// ========== 后处理：过滤垃圾字幕 ==========
static List<(TimeSpan Start, TimeSpan End, string Text)> PostProcessSegments(
    List<(TimeSpan Start, TimeSpan End, string Text)> segments)
{
    if (segments.Count == 0) return segments;

    var result = new List<(TimeSpan Start, TimeSpan End, string Text)>();

    // ★ 垃圾关键词黑名单（完全匹配）
    var garbageSet = new HashSet<string>
    {
        "(设计)", "(笑)", "(我)", "(你)", "(音乐)", "(掌声)",
        "Yeah", "Yeah~", "嘿嘿", "呵呵", "哈哈", "哈囉", "咦", "欸"
    };

    // ★ 垃圾正则
    var garbageRegexes = new[]
    {
        new Regex(@"^\(设计\)$", RegexOptions.Compiled),
        new Regex(@"^\(笑\)$", RegexOptions.Compiled),
        new Regex(@"^\(我\)$", RegexOptions.Compiled),
        new Regex(@"^\(你\)$", RegexOptions.Compiled),
        new Regex(@"^\(有人在旁邊\)$", RegexOptions.Compiled),
        new Regex(@"^Yeah~?$", RegexOptions.Compiled),
        new Regex(@"^嘿嘿+$", RegexOptions.Compiled),
        new Regex(@"^呵呵+$", RegexOptions.Compiled),
        new Regex(@"^哈哈+$", RegexOptions.Compiled),
        new Regex(@"^[A-Za-z\s]+$", RegexOptions.Compiled),  // 纯英文
    };

    foreach (var seg in segments)
    {
        string text = seg.Text.Trim();

        if (string.IsNullOrWhiteSpace(text)) continue;
        if (text.Length < 1) continue;

        // ★ 黑名单过滤
        if (garbageSet.Contains(text)) continue;

        bool isGarbage = false;
        foreach (var regex in garbageRegexes)
        {
            if (regex.IsMatch(text)) { isGarbage = true; break; }
        }
        if (isGarbage) continue;

        // 纯标点
        if (Regex.IsMatch(text, @"^[\p{P}\p{S}\s]+$")) continue;

        // 单字语气词
        if (text.Length <= 1 && Regex.IsMatch(text, @"^[啊呃嗯哦噢哈嗨呀]$")) continue;

        // 重复单字
        if (text.Length > 1 && text.Distinct().Count() == 1) continue;

        result.Add((seg.Start, seg.End, text));
    }

    return result;
}

// ========== 文本相似度检测（用于去重）==========
static bool IsSimilarText(string a, string b)
{
    if (a == b) return true;
    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

    // 去掉标点符号和空格后比较
    string cleanA = Regex.Replace(a, @"[\p{P}\p{S}\s]", "");
    string cleanB = Regex.Replace(b, @"[\p{P}\p{S}\s]", "");

    if (cleanA == cleanB) return true;

    // 如果其中一个包含另一个的大部分内容，也算相似
    string shorter = cleanA.Length <= cleanB.Length ? cleanA : cleanB;
    string longer = cleanA.Length > cleanB.Length ? cleanA : cleanB;

    // 短文本长度超过长文本的70%且是子串，视为重复
    if (shorter.Length > longer.Length * 0.7 && longer.Contains(shorter))
        return true;

    return false;
}

// ========== 配置类 ==========
public class Config
{
    public string FfmpegPath { get; set; } = @"C:\software\ffmpeg\bin\ffmpeg.exe";
    public string ModelPath { get; set; } = "ggml-base.bin";
    public string Language { get; set; } = "zh";
    public string FontName { get; set; } = "Microsoft YaHei";
    public int FontSize { get; set; } = 48;
    public int MaxCharsPerLine { get; set; } = 25;
    public int SubtitleMarginV { get; set; } = 30;
    public int SubtitleAlignment { get; set; } = 2;
    public DebugConfig Debug { get; set; } = new();
}

public class DebugConfig
{
    public bool Enable { get; set; } = false;
    public bool SkipRecognition { get; set; } = false;
    public bool SkipExport { get; set; } = false;
    public bool DryRun { get; set; } = false;
}