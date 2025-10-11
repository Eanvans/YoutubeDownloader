using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gress;
using Gress.Completable;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeDownloader.Core.Tagging;
using YoutubeDownloader.Framework;
using YoutubeDownloader.Services;
using YoutubeDownloader.Utils;
using YoutubeDownloader.Utils.Extensions;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;

namespace YoutubeDownloader.ViewModels.Components;

public partial class DashboardViewModel : ViewModelBase
{
    private const string ContinuationPrefix =
        "https://www.youtube.com/live_chat_replay?continuation=";

    private readonly ViewModelManager _viewModelManager;
    private readonly SnackbarManager _snackbarManager;
    private readonly DialogManager _dialogManager;
    private readonly SettingsService _settingsService;

    private readonly DisposableCollector _eventRoot = new();
    private readonly ResizableSemaphore _downloadSemaphore = new();
    private readonly AutoResetProgressMuxer _progressMuxer;

    public DashboardViewModel(
        ViewModelManager viewModelManager,
        SnackbarManager snackbarManager,
        DialogManager dialogManager,
        SettingsService settingsService
    )
    {
        _viewModelManager = viewModelManager;
        _snackbarManager = snackbarManager;
        _dialogManager = dialogManager;
        _settingsService = settingsService;

        _progressMuxer = Progress.CreateMuxer().WithAutoReset();

        _eventRoot.Add(
            _settingsService.WatchProperty(
                o => o.ParallelLimit,
                () => _downloadSemaphore.MaxCount = _settingsService.ParallelLimit,
                true
            )
        );

        _eventRoot.Add(
            Progress.WatchProperty(
                o => o.Current,
                () => OnPropertyChanged(nameof(IsProgressIndeterminate))
            )
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(ProcessQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowAuthSetupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSettingsCommand))]
    public partial bool IsBusy { get; set; }

    public ProgressContainer<Percentage> Progress { get; } = new();

    public bool IsProgressIndeterminate => IsBusy && Progress.Current.Fraction is <= 0 or >= 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProcessQueryCommand))]
    public partial string? Query { get; set; }

    public ObservableCollection<DownloadViewModel> Downloads { get; } = [];

    private bool CanShowAuthSetup() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanShowAuthSetup))]
    private async Task ShowAuthSetupAsync() =>
        await _dialogManager.ShowDialogAsync(_viewModelManager.CreateAuthSetupViewModel());

    private bool CanShowSettings() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanShowSettings))]
    private async Task ShowSettingsAsync() =>
        await _dialogManager.ShowDialogAsync(_viewModelManager.CreateSettingsViewModel());

    private async void EnqueueDownload(DownloadViewModel download, int position = 0)
    {
        Downloads.Insert(position, download);
        var progress = _progressMuxer.CreateInput();

        try
        {
            var downloader = new VideoDownloader(_settingsService.LastAuthCookies);
            var tagInjector = new MediaTagInjector();

            using var access = await _downloadSemaphore.AcquireAsync(download.CancellationToken);

            download.Status = DownloadStatus.Started;

            var downloadOption =
                download.DownloadOption
                ?? await downloader.GetBestDownloadOptionAsync(
                    download.Video!.Id,
                    download.DownloadPreference!,
                    _settingsService.ShouldInjectLanguageSpecificAudioStreams,
                    download.CancellationToken
                );

            await downloader.DownloadVideoAsync(
                download.FilePath!,
                download.Video!,
                downloadOption,
                _settingsService.ShouldInjectSubtitles,
                download.Progress.Merge(progress),
                download.CancellationToken
            );

            if (_settingsService.ShouldInjectTags)
            {
                try
                {
                    await tagInjector.InjectTagsAsync(
                        download.FilePath!,
                        download.Video!,
                        download.CancellationToken
                    );
                }
                catch
                {
                    // Media tagging is not critical
                }
            }

            download.Status = DownloadStatus.Completed;
        }
        catch (Exception ex)
        {
            try
            {
                // Delete the incompletely downloaded file
                if (!string.IsNullOrWhiteSpace(download.FilePath))
                    File.Delete(download.FilePath);
            }
            catch
            {
                // Ignore
            }

            download.Status =
                ex is OperationCanceledException ? DownloadStatus.Canceled : DownloadStatus.Failed;

            // Short error message for YouTube-related errors, full for others
            download.ErrorMessage = ex is YoutubeExplodeException ? ex.Message : ex.ToString();
        }
        finally
        {
            progress.ReportCompletion();
            download.Dispose();
        }
    }

    private bool CanProcessQuery() => !IsBusy && !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanProcessQuery))]
    private async Task ProcessQueryAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
            return;

        IsBusy = true;

        // Small weight so as to not offset any existing download operations
        var progress = _progressMuxer.CreateInput(0.01);

        try
        {
            var resolver = new QueryResolver(_settingsService.LastAuthCookies);
            var downloader = new VideoDownloader(_settingsService.LastAuthCookies);

            // Split queries by newlines
            var queries = Query.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

            // Process individual queries
            var queryResults = new List<QueryResult>();
            foreach (var (i, query) in queries.Index())
            {
                try
                {
                    queryResults.Add(await resolver.ResolveAsync(query));
                }
                // If it's not the only query in the list, don't interrupt the process
                // and report the error via an async notification instead of a sync dialog.
                // https://github.com/Tyrrrz/YoutubeDownloader/issues/563
                catch (YoutubeExplodeException ex)
                    when (ex is VideoUnavailableException or PlaylistUnavailableException
                        && queries.Length > 1
                    )
                {
                    _snackbarManager.Notify(ex.Message);
                }

                progress.Report(Percentage.FromFraction((i + 1.0) / queries.Length));
            }

            // Aggregate results
            var queryResult = QueryResult.Aggregate(queryResults);

            // Single video result
            if (queryResult.Videos.Count == 1)
            {
                var video = queryResult.Videos.Single();

                var downloadOptions = await downloader.GetDownloadOptionsAsync(
                    video.Id,
                    _settingsService.ShouldInjectLanguageSpecificAudioStreams
                );

                // 下载Chats
                var rst = await DownloadChatsData(video.Id);

                var download = await _dialogManager.ShowDialogAsync(
                    _viewModelManager.CreateDownloadSingleSetupViewModel(
                        video,
                        downloadOptions,
                        rst
                    )
                );

                if (download is null)
                    return;

                EnqueueDownload(download);

                Query = "";
            }
            // Multiple videos
            else if (queryResult.Videos.Count > 1)
            {
                var downloads = await _dialogManager.ShowDialogAsync(
                    _viewModelManager.CreateDownloadMultipleSetupViewModel(
                        queryResult.Title,
                        queryResult.Videos,
                        // Pre-select videos if they come from a single query and not from search
                        queryResult.Kind
                            is not QueryResultKind.Search
                                and not QueryResultKind.Aggregate
                    )
                );

                if (downloads is null)
                    return;

                foreach (var download in downloads)
                    EnqueueDownload(download);

                Query = "";
            }
            // No videos found
            else
            {
                await _dialogManager.ShowDialogAsync(
                    _viewModelManager.CreateMessageBoxViewModel(
                        "Nothing found",
                        "Couldn't find any videos based on the query or URL you provided"
                    )
                );
            }
        }
        catch (Exception ex)
        {
            await _dialogManager.ShowDialogAsync(
                _viewModelManager.CreateMessageBoxViewModel(
                    "Error",
                    // Short error message for YouTube-related errors, full for others
                    ex is YoutubeExplodeException
                        ? ex.Message
                        : ex.ToString()
                )
            );
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private void RemoveDownload(DownloadViewModel download)
    {
        Downloads.Remove(download);
        download.CancelCommand.Execute(null);
        download.Dispose();
    }

    [RelayCommand]
    private void RemoveSuccessfulDownloads()
    {
        foreach (var download in Downloads.ToArray())
        {
            if (download.Status == DownloadStatus.Completed)
                RemoveDownload(download);
        }
    }

    [RelayCommand]
    private void RemoveInactiveDownloads()
    {
        foreach (var download in Downloads.ToArray())
        {
            if (
                download.Status
                is DownloadStatus.Completed
                    or DownloadStatus.Failed
                    or DownloadStatus.Canceled
            )
                RemoveDownload(download);
        }
    }

    [RelayCommand]
    private void RestartDownload(DownloadViewModel download)
    {
        var position = Math.Max(0, Downloads.IndexOf(download));
        RemoveDownload(download);

        var newDownload = download.DownloadOption is not null
            ? _viewModelManager.CreateDownloadViewModel(
                download.Video!,
                download.DownloadOption,
                download.FilePath!
            )
            : _viewModelManager.CreateDownloadViewModel(
                download.Video!,
                download.DownloadPreference!,
                download.FilePath!
            );

        EnqueueDownload(newDownload, position);
    }

    [RelayCommand]
    private void RestartFailedDownloads()
    {
        foreach (var download in Downloads.ToArray())
        {
            if (download.Status == DownloadStatus.Failed)
                RestartDownload(download);
        }
    }

    [RelayCommand]
    private void CancelAllDownloads()
    {
        foreach (var download in Downloads)
            download.CancelCommand.Execute(null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelAllDownloads();

            _eventRoot.Dispose();
            _downloadSemaphore.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<List<VodCommentData>> DownloadChatsData(VideoId video)
    {
        var youtube = new YoutubeClient();
        string url = $"https://www.youtube.com/watch?v={video.Value}";

        // 创建HttpClient实例
        using (HttpClient client = new HttpClient())
        {
            // 设置请求头（可选）
            client.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.4103.116 Safari/537.36"
            );

            // 发送Get请求
            HttpResponseMessage response = await client.GetAsync(url);

            // 检查响应状态码
            if (response.IsSuccessStatusCode)
            {
                // 读取响应内容
                string responseBody = await response.Content.ReadAsStringAsync();

                var d = GetYtInitialData(responseBody);
                var contiuation = GetContinueUrl(d as JObject);

                // 获取Chats
                var (lst, _) = await GetChatReplayFromContinuation(video.Value, contiuation);
                if (lst != null)
                {
                    List<VodCommentData> rst =
                        DataAnalyzeService.FindHotCommentsIntervalSlidingFilter(lst);

                    return rst;
                }
            }
            else
            {
                Console.WriteLine($"Error: {response.StatusCode}");
                Console.WriteLine($"Reason: {response.ReasonPhrase}");
            }
        }

        return new();
    }

    public class RestrictedFromYoutubeException : Exception { }

    public static object? GetYtInitialData(string htmlContent)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        // 检查是否被限制
        if (
            htmlContent.Contains(
                "Sorry for the interruption. We have been receiving a large volume of requests from your network."
            )
        )
        {
            throw new RestrictedFromYoutubeException();
        }

        var scriptNodes = doc.DocumentNode.SelectNodes("//script");

        if (scriptNodes == null)
            return null;

        foreach (var script in scriptNodes)
        {
            var scriptText = script.InnerText;

            if (scriptText.Contains("ytInitialData"))
            {
                // 尝试匹配 'var ytInitialData = ...'
                var varMatch = Regex.Match(
                    scriptText,
                    @"var ytInitialData\s*=\s*(\{.*?\});",
                    RegexOptions.Singleline
                );
                if (varMatch.Success)
                {
                    string jsonData = varMatch.Groups[1].Value;
                    try
                    {
                        return JsonConvert.DeserializeObject(jsonData);
                    }
                    catch
                    {
                        // 可选：处理反序列化错误
                    }
                }

                // 尝试匹配 'window["ytInitialData"] = ...'
                //var windowMatch = Regex.Match(scriptText, @"window[$"ytInitialData"$]\s*=\s*(\{.*?\});", RegexOptions.Singleline);
                var windowMatch = Regex.Match(
                    scriptText,
                    @"window[$""]ytInitialData[$""]\s*=\s*(\{.*?\});",
                    RegexOptions.Singleline
                );
                if (windowMatch.Success)
                {
                    string jsonData = windowMatch.Groups[1].Value;
                    try
                    {
                        return JsonConvert.DeserializeObject(jsonData);
                    }
                    catch
                    {
                        // 可选：处理反序列化错误
                    }
                }
            }
        }

        return null;
    }

    public class ContinuationURLNotFound : Exception
    {
        public ContinuationURLNotFound()
            : base("Continuation URL not found") { }
    }

    public static string GetContinueUrl(JObject? ytInitialData)
    {
        if (ytInitialData == null)
            return "";

        var continueDict = new Dictionary<string, string>();

        try
        {
            var continuations = ytInitialData["contents"]
                ?["twoColumnWatchNextResults"]
                ?["conversationBar"]
                ?["liveChatRenderer"]
                ?["header"]
                ?["liveChatHeaderRenderer"]
                ?["viewSelector"]
                ?["sortFilterSubMenuRenderer"]
                ?["subMenuItems"];

            if (continuations != null)
            {
                foreach (JToken continuation in continuations)
                {
                    var titleToken = continuation.SelectToken("title")?.ToString();
                    var continuationToken = continuation
                        .SelectToken("continuation.reloadContinuationData.continuation")
                        ?.ToString();

                    if (
                        !string.IsNullOrEmpty(titleToken)
                        && !string.IsNullOrEmpty(continuationToken)
                    )
                    {
                        continueDict[titleToken] = continuationToken;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error parsing continuations: " + ex.Message);
        }

        string? continueUrl = null;

        // 尝试匹配日文键名
        if (continueDict.TryGetValue("上位のチャットのリプレイ", out var topJa))
        {
            continueUrl = topJa;
        }
        else if (continueDict.TryGetValue("Top chat replay", out var topEn))
        {
            continueUrl = topEn;
        }

        // 如果没找到，尝试普通聊天回放
        if (string.IsNullOrEmpty(continueUrl))
        {
            if (continueDict.TryGetValue("チャットのリプレイ", out var chatJa))
            {
                continueUrl = chatJa;
            }
            else if (continueDict.TryGetValue("Live chat replay", out var chatEn))
            {
                continueUrl = chatEn;
            }
        }

        // 最后尝试 fallback 到默认路径
        if (string.IsNullOrEmpty(continueUrl))
        {
            var defaultContinuation = ytInitialData["contents"]
                ?["twoColumnWatchNextResults"]?["conversationBar"]?["liveChatRenderer"]?[
                    "continuations"
                ]?[0]?["reloadContinuationData"]?["continuation"]?.ToString();
            continueUrl = defaultContinuation;
        }

        if (string.IsNullOrEmpty(continueUrl))
        {
            throw new ContinuationURLNotFound();
        }

        return continueUrl;
    }

    public class ChatLog
    {
        public string? Author { get; set; }
        public string? Message { get; set; }
        public string? Timestamp { get; set; }
        public string? Video_id { get; set; }
        public string? Chat_No { get; set; }
    }

    public static async Task<(
        List<ChatLog>? result,
        string? continuation
    )> GetChatReplayFromContinuation(
        string videoId,
        string? continuation,
        int pageCountLimit = 9999,
        bool isLocallyRun = false
    )
    {
        var result = new List<ChatLog>();
        int count = 1;
        int pageCount = 1;
        HttpClient client = new HttpClient();

        while (pageCount < pageCountLimit)
        {
            if (string.IsNullOrEmpty(continuation))
            {
                Console.WriteLine("continuation is null. Maybe hit the last chat segment.");
                break;
            }

            try
            {
                string url = ContinuationPrefix + continuation;

                var ytTemp = await GetYtInitialDataAsync(url);
                JObject? ytInitialData = ytTemp as JObject;
                if (ytInitialData == null)
                {
                    Console.WriteLine($"video_id: {videoId}, continuation: {continuation}");
                    continuation = null;
                    break;
                }

                var liveChatCont = ytInitialData["continuationContents"]?["liveChatContinuation"];
                if (liveChatCont == null || liveChatCont["actions"] == null)
                {
                    continuation = null;
                    break;
                }

                JArray? actions = (JArray?)liveChatCont["actions"];
                if (actions == null)
                    break;

                foreach (var action in actions)
                {
                    var replayAction = action["replayChatItemAction"];
                    var tmp = replayAction?["actions"] as JArray;
                    if (replayAction == null || replayAction["actions"] == null || tmp?.Count == 0)
                        continue;

                    var item = replayAction?["actions"]?[0]?["addChatItemAction"]?["item"];
                    if (item == null)
                        continue;

                    ChatLog? chatlog = null;

                    if (item["liveChatTextMessageRenderer"] != null)
                    {
                        chatlog = ConvertChatReplay(item["liveChatTextMessageRenderer"] ?? "");
                    }
                    else if (item["liveChatPaidMessageRenderer"] != null)
                    {
                        chatlog = ConvertChatReplay(item["liveChatPaidMessageRenderer"] ?? "");
                    }

                    if (chatlog != null)
                    {
                        chatlog.Video_id = videoId;
                        chatlog.Chat_No = count.ToString("D5");
                        result.Add(chatlog);
                        count++;
                    }
                }

                continuation = GetContinuation(ytInitialData);

                if (isLocallyRun)
                {
                    Console.Write($"\rPage {pageCount} ");
                }

                pageCount++;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP Error: {ex.Message}");
                continue;
            }
            catch (RestrictedFromYoutubeException)
            {
                Console.WriteLine("Restricted from Youtube (Rate limit)");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex.GetType().Name}");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
                break;
            }
        }

        Console.WriteLine($"{videoId} found {pageCount:D3} pages");
        return (result, continuation);
    }

    public static string? GetContinuation(JObject ytInitialData)
    {
        var continuation = ytInitialData["continuationContents"]
            ?["liveChatContinuation"]?["continuations"]?[0]?["liveChatReplayContinuationData"]?[
                "continuation"
            ]?.ToString();

        return continuation;
    }

    public static async Task<JObject?> GetYtInitialDataAsync(string targetUrl)
    {
        try
        {
            // 设置请求头
            var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.4103.116 Safari/537.36"
            );

            HttpClient client = new HttpClient();
            // 发送请求
            var response = await client.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();

            // 使用 HtmlAgilityPack 解析 HTML
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // 遍历所有 <script> 标签
            var nodes = htmlDoc.DocumentNode.SelectNodes("//script");
            if (nodes != null)
            {
                foreach (var scriptNode in nodes)
                {
                    string scriptText = scriptNode.InnerHtml;

                    if (scriptText.Contains("ytInitialData"))
                    {
                        // 尝试匹配：var ytInitialData = ...
                        int startIndex = scriptText.IndexOf(
                            "var ytInitialData =",
                            StringComparison.Ordinal
                        );
                        if (startIndex >= 0)
                        {
                            try
                            {
                                string jsonStr = scriptText.Substring(
                                    startIndex,
                                    scriptText.Length - startIndex - 10
                                );
                                return JObject.Parse(jsonStr);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("JSON parse error (var): " + ex.Message);
                            }
                        }

                        // 尝试匹配：window["ytInitialData"] = ...
                        startIndex = scriptText.IndexOf(
                            "window[\"ytInitialData\"] = ",
                            StringComparison.Ordinal
                        );
                        if (startIndex >= 0)
                        {
                            try
                            {
                                // 去掉结尾的分号和 </script>
                                string jsonStr = scriptText
                                    .Substring(startIndex + "window[\"ytInitialData\"] = ".Length)
                                    .TrimEnd(';')
                                    .Trim();
                                return JObject.Parse(jsonStr);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("JSON parse error (window): " + ex.Message);
                            }
                        }
                    }
                }
            }

            // 检查是否被限制访问
            if (
                html.Contains(
                    "Sorry for the interruption. We have been receiving a large volume of requests from your network."
                )
            )
            {
                Console.WriteLine("Restricted from Youtube (Rate limit)");
                throw new RestrictedFromYoutubeException();
            }

            Console.WriteLine("Cannot get ytInitialData");
            return null;
        }
        catch (Newtonsoft.Json.JsonException je)
        {
            Console.WriteLine("JSON parse error: " + je.Message);
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error fetching/parsing ytInitialData: " + ex.Message);
            return null;
        }
    }

    public static ChatLog ConvertChatReplay(JToken renderer)
    {
        var chatlog = new ChatLog();

        // 作者名：authorName.simpleText
        chatlog.Author = renderer.SelectToken("authorName.simpleText")?.ToString() ?? "";

        // 消息内容：message.simpleText 或 message.runs.text/emoji
        chatlog.Message = ExtractMessage(renderer["message"]);

        // 时间戳：timestampText.simpleText
        chatlog.Timestamp = renderer.SelectToken("timestampText.simpleText")?.ToString() ?? "";

        // Video_id 和 Chat_No 暂时无法从 renderer 获取，设为空或传入参数补充
        chatlog.Video_id = ""; // 需要外部提供
        chatlog.Chat_No = ""; // 需要外部提供或生成唯一 ID

        return chatlog;
    }

    private static string ExtractMessage(JToken? messageToken)
    {
        if (messageToken == null)
            return "";

        // 简单文本直接提取
        if (messageToken["simpleText"] != null)
        {
            var s = messageToken["simpleText"];
            if (s != null)
                return s.ToString();
            return "";
        }

        // runs 分段提取
        if (messageToken["runs"] is JArray runs)
        {
            var content = "";
            foreach (var run in runs)
            {
                // 文本部分
                if (run["text"] != null)
                {
                    var s = run["text"] ?? "";
                    content += s.ToString();
                }

                // 表情符号部分S
                if (run["emoji"] is JObject emoji)
                {
                    bool isCustomEmoji = (bool?)emoji["isCustomEmoji"] ?? false;

                    if (isCustomEmoji)
                    {
                        var shortcuts = emoji["shortcuts"] as JArray;
                        if (shortcuts != null && shortcuts.Count > 0)
                        {
                            content += shortcuts[0].ToString(); // 取第一个快捷方式
                        }
                    }
                    else
                    {
                        content += emoji["emojiId"]?.ToString() ?? "";
                    }
                }
            }

            return content;
        }

        return "";
    }
}
