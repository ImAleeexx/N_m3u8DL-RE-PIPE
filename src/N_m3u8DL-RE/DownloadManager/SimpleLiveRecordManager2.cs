﻿using Mp4SubtitleParser;
using N_m3u8DL_RE.Column;
using N_m3u8DL_RE.Common.Entity;
using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Common.Log;
using N_m3u8DL_RE.Common.Resource;
using N_m3u8DL_RE.Common.Util;
using N_m3u8DL_RE.Config;
using N_m3u8DL_RE.Downloader;
using N_m3u8DL_RE.Entity;
using N_m3u8DL_RE.Parser;
using N_m3u8DL_RE.Util;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks.Dataflow;
using System.Xml.Linq;

namespace N_m3u8DL_RE.DownloadManager
{
    internal class SimpleLiveRecordManager2
    {
        IDownloader Downloader;
        DownloaderConfig DownloaderConfig;
        StreamExtractor StreamExtractor;
        List<StreamSpec> SelectedSteams;
        List<OutputFile> OutputFiles = new();
        DateTime NowDateTime;
        DateTime? PublishDateTime;
        bool STOP_FLAG = false;
        int WAIT_SEC = 0; //刷新间隔
        ConcurrentDictionary<int, int> RecordingDurDic = new(); //已录制时长
        ConcurrentDictionary<string, string> LastFileNameDic = new(); //上次下载的文件名
        ConcurrentDictionary<string, long> DateTimeDic = new(); //上次下载的dateTime
        CancellationTokenSource CancellationTokenSource = new(); //取消Wait

        public SimpleLiveRecordManager2(DownloaderConfig downloaderConfig, List<StreamSpec> selectedSteams, StreamExtractor streamExtractor)
        {
            this.DownloaderConfig = downloaderConfig;
            Downloader = new SimpleDownloader(DownloaderConfig);
            NowDateTime = DateTime.Now;
            PublishDateTime = selectedSteams.FirstOrDefault()?.PublishTime;
            StreamExtractor = streamExtractor;
            SelectedSteams = selectedSteams;
        }

        private string? ReadInit(byte[] data)
        {
            var info = MP4InitUtil.ReadInit(data);
            if (info.Scheme != null) Logger.WarnMarkUp($"[grey]Type: {info.Scheme}[/]");
            if (info.PSSH != null) Logger.WarnMarkUp($"[grey]PSSH(WV): {info.PSSH}[/]");
            if (info.KID != null) Logger.WarnMarkUp($"[grey]KID: {info.KID}[/]");
            return info.KID;
        }

        private string? ReadInit(string output)
        {
            using (var fs = File.OpenRead(output))
            {
                var header = new byte[4096]; //4KB
                fs.Read(header);
                return ReadInit(header);
            }
        }

        //从文件读取KEY
        private async Task SearchKeyAsync(string? currentKID)
        {
            var _key = await MP4DecryptUtil.SearchKeyFromFile(DownloaderConfig.MyOptions.KeyTextFile, currentKID);
            if (_key != null)
            {
                if (DownloaderConfig.MyOptions.Keys == null)
                    DownloaderConfig.MyOptions.Keys = new string[] { _key };
                else
                    DownloaderConfig.MyOptions.Keys = DownloaderConfig.MyOptions.Keys.Concat(new string[] { _key }).ToArray();
            }
        }

        /// <summary>
        /// 获取时间戳
        /// </summary>
        /// <param name="dateTime"></param>
        /// <returns></returns>
        private long GetUnixTimestamp(DateTime dateTime)
        {
            return new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeSeconds();
        }

        /// <summary>
        /// 获取分段文件夹
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="allHasDatetime"></param>
        /// <returns></returns>
        private string GetSegmentName(MediaSegment segment, bool allHasDatetime)
        {
            bool hls = StreamExtractor.ExtractorType == ExtractorType.HLS;

            string name = OtherUtil.GetFileNameFromInput(segment.Url, false);
            if (hls && allHasDatetime)
            {
                name = GetUnixTimestamp(segment.DateTime!.Value).ToString();
            }
            else if (hls && segment.Index > 10)
            {
                name = segment.Index.ToString();
            }

            return name;
        }

        private void ChangeSpecInfo(StreamSpec streamSpec, List<Mediainfo> mediainfos, ref bool useAACFilter)
        {
            if (!DownloaderConfig.MyOptions.BinaryMerge && mediainfos.Any(m => m.DolbyVison == true))
            {
                DownloaderConfig.MyOptions.BinaryMerge = true;
                Logger.WarnMarkUp($"[darkorange3_1]{ResString.autoBinaryMerge2}[/]");
            }

            if (DownloaderConfig.MyOptions.MuxAfterDone && mediainfos.Any(m => m.DolbyVison == true))
            {
                DownloaderConfig.MyOptions.MuxAfterDone = false;
                Logger.WarnMarkUp($"[darkorange3_1]{ResString.autoBinaryMerge5}[/]");
            }

            if (mediainfos.Where(m => m.Type == "Audio").All(m => m.BaseInfo!.Contains("aac")))
            {
                useAACFilter = true;
            }

            if (mediainfos.All(m => m.Type == "Audio") && streamSpec.MediaType != MediaType.AUDIO)
            {
                var lastKey = streamSpec.ToShortString();
                streamSpec.MediaType = MediaType.AUDIO;
                var newKey = streamSpec.ToShortString();
                //需要同步修改Dictionary中的Key
                if (LastFileNameDic.Remove(lastKey, out var lastValue1))
                    LastFileNameDic[newKey] = lastValue1!;
                if (DateTimeDic.Remove(lastKey, out var lastValue2))
                    DateTimeDic[newKey] = lastValue2;
            }
            else if (mediainfos.All(m => m.Type == "Subtitle") && streamSpec.MediaType != MediaType.SUBTITLES)
            {
                var lastKey = streamSpec.ToShortString();
                streamSpec.MediaType = MediaType.SUBTITLES;
                var newKey = streamSpec.ToShortString();

                //需要同步修改Dictionary中的Key
                if (LastFileNameDic.Remove(lastKey, out var lastValue1))
                    LastFileNameDic[newKey] = lastValue1!;
                if (DateTimeDic.Remove(lastKey, out var lastValue2))
                    DateTimeDic[newKey] = lastValue2;

                if (streamSpec.Extension == null || streamSpec.Extension == "ts")
                    streamSpec.Extension = "vtt";
            }
        }

        private async Task<bool> RecordStreamAsync(StreamSpec streamSpec, ProgressTask task, SpeedContainer speedContainer, BufferBlock<List<MediaSegment>> source)
        {
            var baseTimestamp = PublishDateTime == null ? 0L : (long)(PublishDateTime.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, 0)).TotalMilliseconds;
            //mp4decrypt
            var mp4decrypt = DownloaderConfig.MyOptions.DecryptionBinaryPath!;
            var mp4InitFile = "";
            var currentKID = "";
            var readInfo = false; //是否读取过
            bool useAACFilter = false; //ffmpeg合并flag
            bool initDownloaded = false; //是否下载过init文件
            ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();
            List<Mediainfo> mediaInfos = new();
            FileStream? fileOutputStream = null;
            WebVttSub currentVtt = new(); //字幕流始终维护一个实例
            bool firstSub = true;
            task.MaxValue = 0;
            task.StartTask();

            var name = streamSpec.ToShortString();
            var type = streamSpec.MediaType ?? Common.Enum.MediaType.VIDEO;
            var dirName = $"{DownloaderConfig.MyOptions.SaveName ?? NowDateTime.ToString("yyyy-MM-dd_HH-mm-ss")}_{task.Id}_{streamSpec.GroupId}_{streamSpec.Codecs}_{streamSpec.Bandwidth}_{streamSpec.Language}";
            var tmpDir = Path.Combine(DownloaderConfig.MyOptions.TmpDir ?? Environment.CurrentDirectory, dirName);
            var saveDir = DownloaderConfig.MyOptions.SaveDir ?? Environment.CurrentDirectory;
            var saveName = DownloaderConfig.MyOptions.SaveName != null ? $"{DownloaderConfig.MyOptions.SaveName}.{streamSpec.Language}".TrimEnd('.') : dirName;
            var headers = DownloaderConfig.Headers;

            Logger.Debug($"dirName: {dirName}; tmpDir: {tmpDir}; saveDir: {saveDir}; saveName: {saveName}");

            //创建文件夹
            if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            while (!STOP_FLAG && await source.OutputAvailableAsync())
            {
                //接收新片段 且总是拿全部未处理的片段
                //有时每次只有很少的片段，但是之前的片段下载慢，导致后面还没下载的片段都失效了
                //TryReceiveAll可以稍微缓解一下
                source.TryReceiveAll(out IList<List<MediaSegment>>? segmentsList);
                var segments = segmentsList!.SelectMany(s => s);
                Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false))));

                //下载init
                if (!initDownloaded && streamSpec.Playlist?.MediaInit != null) 
                {
                    task.MaxValue += 1;
                    //对于fMP4，自动开启二进制合并
                    if (!DownloaderConfig.MyOptions.BinaryMerge && streamSpec.MediaType != MediaType.SUBTITLES)
                    {
                        DownloaderConfig.MyOptions.BinaryMerge = true;
                        Logger.WarnMarkUp($"[darkorange3_1]{ResString.autoBinaryMerge}[/]");
                    }

                    var path = Path.Combine(tmpDir, "_init.mp4.tmp");
                    var result = await Downloader.DownloadSegmentAsync(streamSpec.Playlist.MediaInit, path, speedContainer, headers);
                    FileDic[streamSpec.Playlist.MediaInit] = result;
                    if (result == null || !result.Success)
                    {
                        throw new Exception("Download init file failed!");
                    }
                    mp4InitFile = result.ActualFilePath;
                    task.Increment(1);

                    //读取mp4信息
                    if (result != null && result.Success)
                    {
                        var data = File.ReadAllBytes(result.ActualFilePath);
                        currentKID = ReadInit(data);
                        //从文件读取KEY
                        await SearchKeyAsync(currentKID);
                        //实时解密
                        if (DownloaderConfig.MyOptions.MP4RealTimeDecryption && !string.IsNullOrEmpty(currentKID))
                        {
                            var enc = result.ActualFilePath;
                            var dec = Path.Combine(Path.GetDirectoryName(enc)!, Path.GetFileNameWithoutExtension(enc) + "_dec" + Path.GetExtension(enc));
                            var dResult = await MP4DecryptUtil.DecryptAsync(DownloaderConfig.MyOptions.UseShakaPackager, mp4decrypt, DownloaderConfig.MyOptions.Keys, enc, dec, currentKID);
                            if (dResult)
                            {
                                FileDic[streamSpec.Playlist.MediaInit]!.ActualFilePath = dec;
                            }
                        }
                        //ffmpeg读取信息
                        if (!readInfo)
                        {
                            Logger.WarnMarkUp(ResString.readingInfo);
                            mediaInfos = await MediainfoUtil.ReadInfoAsync(DownloaderConfig.MyOptions.FFmpegBinaryPath!, result.ActualFilePath);
                            mediaInfos.ForEach(info => Logger.InfoMarkUp(info.ToStringMarkUp()));
                            ChangeSpecInfo(streamSpec, mediaInfos, ref useAACFilter);
                            readInfo = true;
                        }
                        initDownloaded = true;
                    }
                }

                var allHasDatetime = segments.All(s => s.DateTime != null);

                //下载第一个分片
                if (!readInfo)
                {
                    var seg = segments.First();
                    segments = segments.Skip(1);
                    //获取文件名
                    var filename = GetSegmentName(seg, allHasDatetime);
                    var index = seg.Index;
                    var path = Path.Combine(tmpDir, filename + $".{streamSpec.Extension ?? "clip"}.tmp");
                    var result = await Downloader.DownloadSegmentAsync(seg, path, speedContainer, headers);
                    FileDic[seg] = result;
                    if (result == null || !result.Success)
                    {
                        throw new Exception("Download first segment failed!");
                    }
                    task.Increment(1);
                    if (result != null && result.Success)
                    {
                        //读取init信息
                        if (string.IsNullOrEmpty(currentKID))
                        {
                            currentKID = ReadInit(result.ActualFilePath);
                        }
                        //从文件读取KEY
                        await SearchKeyAsync(currentKID);
                        //实时解密
                        if (DownloaderConfig.MyOptions.MP4RealTimeDecryption && !string.IsNullOrEmpty(currentKID))
                        {
                            var enc = result.ActualFilePath;
                            var dec = Path.Combine(Path.GetDirectoryName(enc)!, Path.GetFileNameWithoutExtension(enc) + "_dec" + Path.GetExtension(enc));
                            var dResult = await MP4DecryptUtil.DecryptAsync(DownloaderConfig.MyOptions.UseShakaPackager, mp4decrypt, DownloaderConfig.MyOptions.Keys, enc, dec, currentKID, mp4InitFile);
                            if (dResult)
                            {
                                File.Delete(enc);
                                result.ActualFilePath = dec;
                            }
                        }
                        //ffmpeg读取信息
                        Logger.WarnMarkUp(ResString.readingInfo);
                        mediaInfos = await MediainfoUtil.ReadInfoAsync(DownloaderConfig.MyOptions.FFmpegBinaryPath!, result!.ActualFilePath);
                        mediaInfos.ForEach(info => Logger.InfoMarkUp(info.ToStringMarkUp()));
                        ChangeSpecInfo(streamSpec, mediaInfos, ref useAACFilter);
                        readInfo = true;
                    }
                }

                //开始下载
                var options = new ParallelOptions()
                {
                    MaxDegreeOfParallelism = DownloaderConfig.MyOptions.ThreadCount
                };
                await Parallel.ForEachAsync(segments, options, async (seg, _) =>
                {
                    //获取文件名
                    var filename = GetSegmentName(seg, allHasDatetime);
                    var index = seg.Index;
                    var path = Path.Combine(tmpDir, filename + $".{streamSpec.Extension ?? "clip"}.tmp");
                    var result = await Downloader.DownloadSegmentAsync(seg, path, speedContainer, headers);
                    FileDic[seg] = result;
                    task.Increment(1);
                    //实时解密
                    if (DownloaderConfig.MyOptions.MP4RealTimeDecryption && result != null && result.Success && !string.IsNullOrEmpty(currentKID))
                    {
                        var enc = result.ActualFilePath;
                        var dec = Path.Combine(Path.GetDirectoryName(enc)!, Path.GetFileNameWithoutExtension(enc) + "_dec" + Path.GetExtension(enc));
                        var dResult = await MP4DecryptUtil.DecryptAsync(DownloaderConfig.MyOptions.UseShakaPackager, mp4decrypt, DownloaderConfig.MyOptions.Keys, enc, dec, currentKID, mp4InitFile);
                        if (dResult)
                        {
                            File.Delete(enc);
                            result.ActualFilePath = dec;
                        }
                    }
                });

                RecordingDurDic[task.Id] += (int)segments.Sum(s => s.Duration);

                //自动修复VTT raw字幕
                if (DownloaderConfig.MyOptions.AutoSubtitleFix && streamSpec.MediaType == Common.Enum.MediaType.SUBTITLES
                    && streamSpec.Extension != null && streamSpec.Extension.Contains("vtt"))
                {
                    //排序字幕并修正时间戳
                    var keys = FileDic.Keys.OrderBy(k => k.Index);
                    foreach (var seg in keys)
                    {
                        var vttContent = File.ReadAllText(FileDic[seg]!.ActualFilePath);
                        var vtt = WebVttSub.Parse(vttContent);
                        //手动计算MPEGTS
                        if (currentVtt.MpegtsTimestamp == 0 && vtt.MpegtsTimestamp == 0)
                        {
                            vtt.MpegtsTimestamp = 90000 * (long)keys.Where(s => s.Index < seg.Index).Sum(s => s.Duration);
                        }
                        if (firstSub)
                        {
                            currentVtt = vtt;
                            firstSub = false;
                        }
                        else
                        {
                            currentVtt.AddCuesFromOne(vtt);
                        }
                    }
                }

                //自动修复VTT mp4字幕
                if (DownloaderConfig.MyOptions.AutoSubtitleFix && streamSpec.MediaType == Common.Enum.MediaType.SUBTITLES
                    && streamSpec.Codecs != "stpp" && streamSpec.Extension != null && streamSpec.Extension.Contains("m4s"))
                {
                    var initFile = FileDic.Values.Where(v => Path.GetFileName(v!.ActualFilePath).StartsWith("_init")).FirstOrDefault();
                    var iniFileBytes = File.ReadAllBytes(initFile!.ActualFilePath);
                    var (sawVtt, timescale) = MP4VttUtil.CheckInit(iniFileBytes);
                    if (sawVtt)
                    {
                        var mp4s = FileDic.Values.Select(v => v!.ActualFilePath).Where(p => p.EndsWith(".m4s")).OrderBy(s => s).ToArray();
                        if (firstSub)
                        {
                            currentVtt = MP4VttUtil.ExtractSub(mp4s, timescale);
                            firstSub = false;
                        }
                        else
                        {
                            var finalVtt = MP4VttUtil.ExtractSub(mp4s, timescale);
                            currentVtt.AddCuesFromOne(finalVtt);
                        }
                    }
                }

                //自动修复TTML raw字幕
                if (DownloaderConfig.MyOptions.AutoSubtitleFix && streamSpec.MediaType == Common.Enum.MediaType.SUBTITLES
                    && streamSpec.Extension != null && streamSpec.Extension.Contains("ttml"))
                {
                    var mp4s = FileDic.Values.Select(v => v!.ActualFilePath).Where(p => p.EndsWith(".ttml")).OrderBy(s => s).ToArray();
                    if (firstSub)
                    {
                        if (baseTimestamp != 0)
                        {
                            var total = segments.Sum(s => s.Duration);
                            baseTimestamp -= (long)TimeSpan.FromSeconds(total).TotalMilliseconds;
                        }
                        currentVtt = MP4TtmlUtil.ExtractFromTTMLs(mp4s, 0, baseTimestamp);
                        firstSub = false;
                    }
                    else
                    {
                        var finalVtt = MP4TtmlUtil.ExtractFromTTMLs(mp4s, 0, baseTimestamp);
                        currentVtt.AddCuesFromOne(finalVtt);
                    }
                }

                //自动修复TTML mp4字幕
                if (DownloaderConfig.MyOptions.AutoSubtitleFix && streamSpec.MediaType == Common.Enum.MediaType.SUBTITLES
                    && streamSpec.Extension != null && streamSpec.Extension.Contains("m4s")
                    && streamSpec.Codecs != null && streamSpec.Codecs.Contains("stpp"))
                {
                    //sawTtml暂时不判断
                    //var initFile = FileDic.Values.Where(v => Path.GetFileName(v!.ActualFilePath).StartsWith("_init")).FirstOrDefault();
                    //var iniFileBytes = File.ReadAllBytes(initFile!.ActualFilePath);
                    //var sawTtml = MP4TtmlUtil.CheckInit(iniFileBytes);
                    var mp4s = FileDic.Values.Select(v => v!.ActualFilePath).Where(p => p.EndsWith(".m4s")).OrderBy(s => s).ToArray();
                    if (firstSub)
                    {
                        if (baseTimestamp != 0)
                        {
                            var total = segments.Sum(s => s.Duration);
                            baseTimestamp -= (long)TimeSpan.FromSeconds(total).TotalMilliseconds;
                        }
                        currentVtt = MP4TtmlUtil.ExtractFromMp4s(mp4s, 0, baseTimestamp);
                        firstSub = false;
                    }
                    else
                    {
                        var finalVtt = MP4TtmlUtil.ExtractFromMp4s(mp4s, 0, baseTimestamp);
                        currentVtt.AddCuesFromOne(finalVtt);
                    }
                }

                //合并逻辑
                if (DownloaderConfig.MyOptions.LiveRealTimeMerge)
                {
                    //合并
                    var outputExt = "." + streamSpec.Extension;
                    if (streamSpec.Extension == null) outputExt = ".ts";
                    else if (streamSpec.MediaType == MediaType.AUDIO && streamSpec.Extension == "m4s") outputExt = ".m4a";
                    else if (streamSpec.MediaType != MediaType.SUBTITLES && streamSpec.Extension == "m4s") outputExt = ".mp4";
                    else if (streamSpec.MediaType == MediaType.SUBTITLES)
                    {
                        if (DownloaderConfig.MyOptions.SubtitleFormat == Enum.SubtitleFormat.SRT) outputExt = ".srt";
                        else outputExt = ".vtt";
                    }

                    var output = Path.Combine(saveDir, saveName + outputExt);

                    //移除无效片段
                    var badKeys = FileDic.Where(i => i.Value == null).Select(i => i.Key);
                    foreach (var badKey in badKeys)
                    {
                        FileDic!.Remove(badKey, out _);
                    }

                    //检测目标文件是否存在
                    while (!readInfo && File.Exists(output))
                    {
                        Logger.WarnMarkUp($"{Path.GetFileName(output)} => {Path.GetFileName(output = Path.ChangeExtension(output, $"copy" + Path.GetExtension(output)))}");
                    }

                    //设置输出流
                    if (fileOutputStream == null)
                    {
                        fileOutputStream = new FileStream(output, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    }

                    if (streamSpec.MediaType != MediaType.SUBTITLES)
                    {
                        var initResult = streamSpec.Playlist!.MediaInit != null ? FileDic[streamSpec.Playlist!.MediaInit!]! : null;
                        var files = FileDic.Where(f => f.Key != streamSpec.Playlist!.MediaInit).Select(f => f.Value).Select(v => v!.ActualFilePath).OrderBy(s => s).ToArray();
                        if (initResult != null && mp4InitFile != "")
                        {
                            //shaka实时解密不需要init文件用于合并，mp4decrpyt需要
                            if (!DownloaderConfig.MyOptions.UseShakaPackager)
                            {
                                files = new string[] { initResult.ActualFilePath }.Concat(files).ToArray();
                            }
                        }
                        foreach (var inputFilePath in files)
                        {
                            using (var inputStream = File.OpenRead(inputFilePath))
                            {
                                inputStream.CopyTo(fileOutputStream);
                            }
                            if (!DownloaderConfig.MyOptions.LiveKeepSegments && !Path.GetFileName(inputFilePath).StartsWith("_init"))
                            {
                                File.Delete(inputFilePath);
                            }
                        }
                        FileDic.Clear();
                        if (initResult != null)
                        {
                            FileDic[streamSpec.Playlist!.MediaInit!] = initResult;
                        }
                    }
                    else
                    {
                        var initResult = streamSpec.Playlist!.MediaInit != null ? FileDic[streamSpec.Playlist!.MediaInit!]! : null;
                        var files = FileDic.Select(f => f.Value).Select(v => v!.ActualFilePath).OrderBy(s => s).ToArray();
                        foreach (var inputFilePath in files)
                        {
                            if (!DownloaderConfig.MyOptions.LiveKeepSegments && !Path.GetFileName(inputFilePath).StartsWith("_init"))
                            {
                                File.Delete(inputFilePath);
                            }
                        }
                        var subText = currentVtt.ToStringWithHeader();
                        if (outputExt == ".srt")
                        {
                            subText = OtherUtil.WebVtt2Other(currentVtt, Enum.SubtitleFormat.SRT);
                        }
                        var subBytes = Encoding.UTF8.GetBytes(subText);
                        fileOutputStream.Position = 0;
                        fileOutputStream.Write(subBytes);
                        FileDic.Clear();
                        if (initResult != null)
                        {
                            FileDic[streamSpec.Playlist!.MediaInit!] = initResult;
                        }
                    }

                    //刷新buffer
                    if (fileOutputStream != null)
                    {
                        fileOutputStream.Flush();
                    }
                }

                //检测时长限制
                if (!STOP_FLAG && RecordingDurDic.All(d => d.Value >= DownloaderConfig.MyOptions.LiveRecordLimit?.TotalSeconds))
                {
                    Logger.WarnMarkUp($"[darkorange3_1]{ResString.liveLimitReached}[/]");
                    STOP_FLAG = true;
                    CancellationTokenSource.Cancel();
                }
            }

            if (fileOutputStream != null)
            {
                //记录所有文件信息
                OutputFiles.Add(new OutputFile()
                {
                    Index = task.Id,
                    FilePath = fileOutputStream.Name,
                    LangCode = streamSpec.Language,
                    Description = streamSpec.Name,
                    Mediainfos = mediaInfos
                });
                fileOutputStream.Close();
                fileOutputStream.Dispose();
            }

            return true;
        }

        private async Task PlayListProduceAsync(StreamSpec streamSpec, ProgressTask task, BufferBlock<List<MediaSegment>> target)
        {
            while (!STOP_FLAG)
            {
                if (WAIT_SEC != 0)
                {
                    var allHasDatetime = streamSpec.Playlist!.MediaParts[0].MediaSegments.All(s => s.DateTime != null);
                    //过滤不需要下载的片段
                    FilterMediaSegments(streamSpec, allHasDatetime);
                    var newList = streamSpec.Playlist!.MediaParts[0].MediaSegments;
                    if (newList.Count > 0)
                    {
                        //推送给消费者
                        await target.SendAsync(newList);
                        //更新最新链接
                        LastFileNameDic[streamSpec.ToShortString()] = GetSegmentName(newList.Last(), allHasDatetime);
                        //尝试更新时间戳
                        var dt = newList.Last().DateTime;
                        DateTimeDic[streamSpec.ToShortString()] = dt != null ? GetUnixTimestamp(dt.Value) : 0L;
                        task.MaxValue += newList.Count;
                    }
                    try
                    {
                        //Logger.WarnMarkUp($"wait {waitSec}s");
                        if (!STOP_FLAG) await Task.Delay(WAIT_SEC * 1000, CancellationTokenSource.Token);
                        //刷新列表
                        if (!STOP_FLAG) await StreamExtractor.RefreshPlayListAsync(new List<StreamSpec>() { streamSpec });
                    }
                    catch (OperationCanceledException oce) when (oce.CancellationToken == CancellationTokenSource.Token)
                    {
                        //不需要做事
                    }
                }
            }

            target.Complete();
        }

        private void FilterMediaSegments(StreamSpec streamSpec, bool allHasDatetime)
        {
            if (string.IsNullOrEmpty(LastFileNameDic[streamSpec.ToShortString()]) && DateTimeDic[streamSpec.ToShortString()] == 0) return;

            var index = -1;
            var dateTime = DateTimeDic[streamSpec.ToShortString()];
            var lastName = LastFileNameDic[streamSpec.ToShortString()];

            //优先使用dateTime判断
            if (dateTime != 0 && streamSpec.Playlist!.MediaParts[0].MediaSegments.All(s => s.DateTime != null)) 
            {
                index = streamSpec.Playlist!.MediaParts[0].MediaSegments.FindIndex(s => GetUnixTimestamp(s.DateTime!.Value) == dateTime);
            }
            else
            {
                index = streamSpec.Playlist!.MediaParts[0].MediaSegments.FindIndex(s => GetSegmentName(s, allHasDatetime) == lastName);
            }

            if (index > -1)
            {
                streamSpec.Playlist!.MediaParts[0].MediaSegments = streamSpec.Playlist!.MediaParts[0].MediaSegments.Skip(index + 1).ToList();
            }
        }

        public async Task<bool> StartRecordAsync()
        {
            var takeLastCount = 15;
            ConcurrentDictionary<int, SpeedContainer> SpeedContainerDic = new(); //速度计算
            ConcurrentDictionary<StreamSpec, bool?> Results = new();
            //取最后15个分片
            var maxIndex = SelectedSteams.Min(s => s.Playlist!.MediaParts[0].MediaSegments.Max(s => s.Index));
            foreach (var item in SelectedSteams)
            {
                foreach (var part in item.Playlist!.MediaParts)
                {
                    part.MediaSegments = part.MediaSegments.Where(s => s.Index <= maxIndex).TakeLast(takeLastCount).ToList();
                }
            }
            //初始化dic
            foreach (var item in SelectedSteams)
            {
                LastFileNameDic[item.ToShortString()] = "";
                DateTimeDic[item.ToShortString()] = 0L;
            }
            //设置等待时间
            if (WAIT_SEC == 0)
            {
                WAIT_SEC = (int)(SelectedSteams.Min(s => s.Playlist!.MediaParts[0].MediaSegments.Sum(s => s.Duration)) / 2);
                WAIT_SEC -= 2; //再提前两秒吧 留出冗余
                if (DownloaderConfig.MyOptions.LiveWaitTime != null)
                    WAIT_SEC = DownloaderConfig.MyOptions.LiveWaitTime.Value;
                if (WAIT_SEC <= 0) WAIT_SEC = 1;
                Logger.WarnMarkUp($"set refresh interval to {WAIT_SEC} seconds");
            }

            var progress = AnsiConsole.Progress().AutoClear(true);

            //进度条的列定义
            progress.Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn() { Alignment = Justify.Left },
                new RecordingDurationColumn(RecordingDurDic), //时长显示
                new RecordingStatusColumn(),
                new PercentageColumn(),
                new DownloadSpeedColumn(SpeedContainerDic), //速度计算
                new SpinnerColumn(),
            });

            await progress.StartAsync(async ctx =>
            {
                //创建任务
                var dic = SelectedSteams.Select(item =>
                {
                    var task = ctx.AddTask(item.ToShortString(), autoStart: false);
                    SpeedContainerDic[task.Id] = new SpeedContainer(); //速度计算
                    RecordingDurDic[task.Id] = 0;
                    return (item, task);
                }).ToDictionary(item => item.item, item => item.task);

                DownloaderConfig.MyOptions.ConcurrentDownload = true;
                DownloaderConfig.MyOptions.MP4RealTimeDecryption = true;
                DownloaderConfig.MyOptions.LiveRecordLimit = DownloaderConfig.MyOptions.LiveRecordLimit ?? TimeSpan.MaxValue;
                if (DownloaderConfig.MyOptions.MP4RealTimeDecryption && !DownloaderConfig.MyOptions.UseShakaPackager
                    && DownloaderConfig.MyOptions.Keys != null && DownloaderConfig.MyOptions.Keys.Length > 0)
                    Logger.WarnMarkUp($"[darkorange3_1]{ResString.realTimeDecMessage}[/]");
                var limit = DownloaderConfig.MyOptions.LiveRecordLimit;
                if (limit != TimeSpan.MaxValue)
                    Logger.WarnMarkUp($"[darkorange3_1]{ResString.liveLimit}{GlobalUtil.FormatTime((int)limit.Value.TotalSeconds)}[/]");
                //录制直播时，用户选了几个流就并发录几个
                var options = new ParallelOptions()
                {
                    MaxDegreeOfParallelism = SelectedSteams.Count
                };
                //并发下载
                await Parallel.ForEachAsync(dic, options, async (kp, _) =>
                {
                    var task = kp.Value;
                    var list = new BufferBlock<List<MediaSegment>>();
                    var consumerTask = RecordStreamAsync(kp.Key, task, SpeedContainerDic[task.Id], list);
                    await PlayListProduceAsync(kp.Key, task, list);
                    Results[kp.Key] = await consumerTask;
                });
            });

            var success = Results.Values.All(v => v == true);

            //混流
            if (success && DownloaderConfig.MyOptions.MuxAfterDone && OutputFiles.Count > 0)
            {
                OutputFiles = OutputFiles.OrderBy(o => o.Index).ToList();
                if (DownloaderConfig.MyOptions.MuxImports != null)
                {
                    OutputFiles.AddRange(DownloaderConfig.MyOptions.MuxImports);
                }
                OutputFiles.ForEach(f => Logger.WarnMarkUp($"[grey]{Path.GetFileName(f.FilePath).EscapeMarkup()}[/]"));
                var saveDir = DownloaderConfig.MyOptions.SaveDir ?? Environment.CurrentDirectory;
                var ext = DownloaderConfig.MyOptions.MuxToMp4 ? ".mp4" : ".mkv";
                var outName = $"{DownloaderConfig.MyOptions.SaveName ?? NowDateTime.ToString("yyyy-MM-dd_HH-mm-ss")}.MUX";
                var outPath = Path.Combine(saveDir, outName);
                Logger.WarnMarkUp($"Muxing to [grey]{outName.EscapeMarkup()}{ext}[/]");
                var result = false;
                if (DownloaderConfig.MyOptions.UseMkvmerge) result = MergeUtil.MuxInputsByMkvmerge(DownloaderConfig.MyOptions.MkvmergeBinaryPath!, OutputFiles.ToArray(), outPath);
                else result = MergeUtil.MuxInputsByFFmpeg(DownloaderConfig.MyOptions.FFmpegBinaryPath!, OutputFiles.ToArray(), outPath, DownloaderConfig.MyOptions.MuxToMp4, !DownloaderConfig.MyOptions.NoDateInfo);
                //完成后删除各轨道文件
                if (result && !DownloaderConfig.MyOptions.MuxKeepFiles)
                {
                    Logger.WarnMarkUp("[grey]Cleaning files...[/]");
                    OutputFiles.ForEach(f => File.Delete(f.FilePath));
                    var tmpDir = DownloaderConfig.MyOptions.TmpDir ?? Environment.CurrentDirectory;
                    OtherUtil.SafeDeleteDir(tmpDir);
                }
                else Logger.ErrorMarkUp($"Mux failed");
                //判断是否要改名
                var newPath = Path.ChangeExtension(outPath, ext);
                if (result && !File.Exists(newPath))
                {
                    Logger.WarnMarkUp($"Rename to [grey]{Path.GetFileName(newPath).EscapeMarkup()}[/]");
                    File.Move(outPath + ext, newPath);
                }
            }

            return success;
        }
    }
}
