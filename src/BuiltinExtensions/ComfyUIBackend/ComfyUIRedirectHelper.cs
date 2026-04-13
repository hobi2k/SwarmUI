using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Web;
using SwarmUI.WebAPI;

namespace SwarmUI.Builtin_ComfyUIBackend;

/// <summary>Helper class for network redirections for the '/ComfyBackendDirect' url path.</summary>
public class ComfyUIRedirectHelper
{
    public class SwarmTaskResult
    {
        public string Url { get; set; }
        public string PreviewUrl { get; set; }
        public string Filename { get; set; }
        public string Collection { get; set; }
    }

    public class SwarmTaskRecord
    {
        public string PromptId { get; set; }
        public string UserId { get; set; }
        public string Status { get; set; } = "queued";
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public double Progress { get; set; }
        public int Value { get; set; }
        public int Max { get; set; }
        public string NodeId { get; set; }
        public string Error { get; set; }
        public string WorkflowApi { get; set; }
        public List<SwarmTaskResult> Results { get; set; } = [];
    }

    /// <summary>이 SwarmUI 인스턴스의 고유 식별자. 시작 시 생성되며 prompt_id prefix로 사용한다.</summary>
    public static readonly string InstanceId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Map of all currently connected users.</summary>
    public static ConcurrentDictionary<string, ComfyUser> Users = new();

    /// <summary>Map of browser session keys to active embedded Comfy users.</summary>
    public static ConcurrentDictionary<string, ComfyUser> BrowserUsers = new();

    /// <summary>Set of backend IDs that have recently been assigned to a user (to try to spread new users onto different backends where possible).</summary>
    public static ConcurrentDictionary<int, int> RecentlyClaimedBackends = new();

    /// <summary>Per-user comfy task state tracked by Swarm for isolated UI rendering.</summary>
    public static ConcurrentDictionary<string, ConcurrentDictionary<string, SwarmTaskRecord>> SwarmTasksByUser = new();

    /// <summary>Map of themes to theme file injection content.</summary>
    public static ConcurrentDictionary<string, string> ComfyThemeData = new();

    /// <summary>Backup for <see cref="ObjectInfoReadCacher"/>.</summary>
    public static volatile JObject LastObjectInfo;

    /// <summary>Event-action fired when a new comfy user is connected.</summary>
    public static Action<ComfyUser> NewUserEvent;

    /// <summary>Cache handler to prevent "object_info" reads from spamming and killing the comfy backend (which handles them sequentially, and rather slowly per call).</summary>
    public static SingleValueExpiringCacheAsync<JObject> ObjectInfoReadCacher = new(() =>
    {
        ComfyUIBackendExtension.ComfyBackendData backend = ComfyUIBackendExtension.ComfyBackendsDirect().First();
        JObject result = null;
        try
        {
            using CancellationTokenSource cancel = Utilities.TimedCancel(TimeSpan.FromMinutes(1));
            result = backend.Client.GetAsync($"{backend.APIAddress}/object_info", cancel.Token).Result.Content.ReadAsStringAsync().Result.ParseToJson();
        }
        catch (Exception ex)
        {
            Logs.Error($"object_info read failure: {ex.ReadableString()}");
            if (LastObjectInfo is null)
            {
                throw;
            }
        }
        foreach (ComfyUIBackendExtension.ComfyBackendData trackedBackend in ComfyUIBackendExtension.ComfyBackendsDirect())
        {
            if (trackedBackend.Backend is ComfyUIAPIAbstractBackend comfy && comfy.RawObjectInfo is not null)
            {
                foreach (JProperty property in comfy.RawObjectInfo.Properties())
                {
                    if (!result.ContainsKey(property.Name))
                    {
                        result[property.Name] = property.Value;
                    }
                }
            }
        }
        if (result is not null)
        {
            LastObjectInfo = result;
        }
        return LastObjectInfo;
    }, TimeSpan.FromMinutes(10));

    /// <summary>현재 Swarm 사용자와 연결된 Comfy 사용자 목록을 반환한다.</summary>
    /// <param name="swarmUser">확인할 Swarm 사용자다.</param>
    /// <returns>같은 Swarm 사용자를 가리키는 Comfy 사용자 목록이다.</returns>
    public static List<ComfyUser> GetComfyUsersForSwarmUser(User swarmUser)
    {
        if (swarmUser is null)
        {
            return [];
        }
        return [.. Users.Values.Where(u => u.SwarmUser?.UserID == swarmUser.UserID).Distinct()];
    }

    public static string GetBrowserSessionKey(HttpContext context)
    {
        if (context?.Request?.Cookies is null)
        {
            return null;
        }
        return context.Request.Cookies.TryGetValue("comfy_session_key", out string key) && !string.IsNullOrWhiteSpace(key) ? key : null;
    }

    public static ComfyUser GetRequestComfyUser(HttpContext context, User swarmUser = null)
    {
        string browserSessionKey = GetBrowserSessionKey(context);
        if (!string.IsNullOrWhiteSpace(browserSessionKey) && BrowserUsers.TryGetValue(browserSessionKey, out ComfyUser byBrowser))
        {
            return byBrowser;
        }
        if (swarmUser is not null)
        {
            return GetComfyUsersForSwarmUser(swarmUser).FirstOrDefault();
        }
        return null;
    }

    public static HashSet<string> GetOwnedPromptIdsForContext(HttpContext context, User swarmUser)
    {
        ComfyUser comfyUser = GetRequestComfyUser(context, swarmUser);
        if (comfyUser is not null)
        {
            return [.. comfyUser.OwnedPromptIds.Keys];
        }
        return GetOwnedPromptIds(swarmUser);
    }

    /// <summary>현재 Swarm 사용자 소유 prompt ID 집합을 반환한다.</summary>
    /// <param name="swarmUser">확인할 Swarm 사용자다.</param>
    /// <returns>현재 사용자 소유 prompt ID 집합이다.</returns>
    public static HashSet<string> GetOwnedPromptIds(User swarmUser)
    {
        return [.. GetComfyUsersForSwarmUser(swarmUser).SelectMany(u => u.OwnedPromptIds.Keys)];
    }

    public static SwarmTaskRecord GetOrCreateTask(User swarmUser, string promptId)
    {
        if (swarmUser is null || string.IsNullOrWhiteSpace(promptId))
        {
            return null;
        }
        ConcurrentDictionary<string, SwarmTaskRecord> userTasks = SwarmTasksByUser.GetOrAdd(swarmUser.UserID, _ => new());
        return userTasks.GetOrAdd(promptId, pid => new SwarmTaskRecord()
        {
            PromptId = pid,
            UserId = swarmUser.UserID
        });
    }

    public static void SetTaskQueued(User swarmUser, string promptId, JObject workflowApi = null)
    {
        SwarmTaskRecord task = GetOrCreateTask(swarmUser, promptId);
        if (task is null)
        {
            return;
        }
        task.Status = "queued";
        task.Error = null;
        if (workflowApi is not null)
        {
            task.WorkflowApi = workflowApi.ToString(Newtonsoft.Json.Formatting.None);
        }
        task.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static void UpdateTaskFromEvent(User swarmUser, JObject parsed)
    {
        if (swarmUser is null || parsed?["data"] is not JObject dataObj || !dataObj.TryGetValue("prompt_id", out JToken promptIdTok))
        {
            return;
        }
        string promptId = promptIdTok.ToString();
        SwarmTaskRecord task = GetOrCreateTask(swarmUser, promptId);
        if (task is null)
        {
            return;
        }
        string type = parsed["type"]?.ToString();
        task.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (type)
        {
            case "execution_start":
            case "execution_cached":
                task.Status = "queued";
                task.Error = null;
                break;
            case "executing":
                JToken nodeTok = dataObj["node"];
                if (nodeTok is null || nodeTok.Type == JTokenType.Null || string.IsNullOrWhiteSpace(nodeTok.ToString()))
                {
                    task.Status = task.Results.Count > 0 ? "completed" : "queued";
                    task.NodeId = null;
                    task.Progress = task.Max > 0 ? 1 : task.Progress;
                }
                else
                {
                    task.Status = "running";
                    task.NodeId = nodeTok.ToString();
                    task.Error = null;
                }
                break;
            case "progress":
                task.Status = "running";
                task.Value = dataObj["value"]?.Value<int>() ?? task.Value;
                task.Max = dataObj["max"]?.Value<int>() ?? task.Max;
                task.Progress = task.Max > 0 ? Math.Clamp((double)task.Value / task.Max, 0, 1) : task.Progress;
                break;
            case "executed":
                task.Status = "running";
                task.NodeId = dataObj["node"]?.ToString() ?? task.NodeId;
                break;
            case "execution_success":
                task.Status = task.Results.Count > 0 ? "completed" : "running";
                task.Progress = 1;
                task.Error = null;
                break;
            case "execution_error":
                task.Status = "failed";
                task.Error = dataObj["exception_message"]?.ToString() ?? dataObj["error"]?.ToString() ?? "Execution failed";
                break;
            case "execution_interrupted":
                task.Status = "interrupted";
                task.Error = "Interrupted";
                break;
        }
    }

    public static void AddTaskResult(User swarmUser, string promptId, string savedUrl, string filename, string collectionName)
    {
        SwarmTaskRecord task = GetOrCreateTask(swarmUser, promptId);
        if (task is null || string.IsNullOrWhiteSpace(savedUrl))
        {
            return;
        }
        string previewUrl = savedUrl.Contains('?') ? $"{savedUrl}&preview=true" : $"{savedUrl}?preview=true";
        if (task.Results.Any(r => r.Url == savedUrl))
        {
            task.Status = "completed";
            task.Progress = 1;
            task.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return;
        }
        task.Results.Add(new SwarmTaskResult()
        {
            Url = savedUrl,
            PreviewUrl = previewUrl,
            Filename = filename,
            Collection = collectionName
        });
        task.Status = "completed";
        task.Progress = 1;
        task.Error = null;
        task.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static JArray GetTasksJsonForUser(User swarmUser)
    {
        if (swarmUser is null || !SwarmTasksByUser.TryGetValue(swarmUser.UserID, out ConcurrentDictionary<string, SwarmTaskRecord> tasks))
        {
            return [];
        }
        return new JArray(tasks.Values
            .OrderByDescending(t => t.UpdatedAt)
            .Take(100)
            .Select(t => new JObject()
            {
                ["prompt_id"] = t.PromptId,
                ["status"] = t.Status,
                ["created_at"] = t.CreatedAt,
                ["updated_at"] = t.UpdatedAt,
                ["progress"] = t.Progress,
                ["value"] = t.Value,
                ["max"] = t.Max,
                ["node_id"] = t.NodeId,
                ["error"] = t.Error,
                ["workflow_api"] = t.WorkflowApi,
                ["results"] = new JArray(t.Results.Select(r => new JObject()
                {
                    ["url"] = r.Url,
                    ["preview_url"] = r.PreviewUrl,
                    ["filename"] = r.Filename,
                    ["collection"] = r.Collection
                }))
            }));
    }

    /// <summary>대상 prompt ID가 현재 사용자 소유인지 반환한다.</summary>
    /// <param name="swarmUser">확인할 Swarm 사용자다.</param>
    /// <param name="promptId">검사할 prompt ID다.</param>
    /// <returns>현재 사용자 소유 prompt ID면 true를 반환한다.</returns>
    public static bool IsOwnedPromptId(User swarmUser, string promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId)) return false;
        if (promptId.StartsWith($"swarm-{InstanceId}-")) return true;
        return GetOwnedPromptIds(swarmUser).Contains(promptId);
    }

    public static bool IsOwnedPromptIdForContext(HttpContext context, User swarmUser, string promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return false;
        }
        return GetOwnedPromptIdsForContext(context, swarmUser).Contains(promptId);
    }

    /// <summary>Comfy websocket JSON 이벤트를 현재 ComfyUser 소유 prompt 기준으로 필터링할지 반환한다.</summary>
    /// <param name="comfyUser">현재 브라우저 WebSocket 연결에 해당하는 ComfyUser다.</param>
    /// <param name="parsed">검사할 websocket 메시지다.</param>
    /// <returns>메시지를 현재 사용자에게 보여줘도 되면 true를 반환한다.</returns>
    public static bool ShouldForwardComfyEvent(ComfyUser comfyUser, JObject parsed)
    {
        if (parsed?["data"] is not JObject dataObj || !dataObj.TryGetValue("prompt_id", out JToken promptIdTok))
        {
            return true;
        }
        string promptId = promptIdTok.ToString();
        bool result = promptId.StartsWith($"swarm-{InstanceId}-") || comfyUser.OwnsPromptId(promptId);
        Logs.Debug($"[QFilter] type={parsed["type"]} pid={promptId} masterSID={comfyUser.MasterSID} result={result}");
        return result;
    }

    /// <summary>여러 backend의 queue 응답을 하나로 합친다.</summary>
    public static JObject MergeQueueResponses(IEnumerable<JObject> queues)
    {
        JArray running = [];
        JArray pending = [];
        foreach (JObject queue in queues.Where(q => q is not null))
        {
            if (queue["queue_running"] is JArray queueRunning)
            {
                foreach (JToken item in queueRunning)
                {
                    running.Add(item);
                }
            }
            if (queue["queue_pending"] is JArray queuePending)
            {
                foreach (JToken item in queuePending)
                {
                    pending.Add(item);
                }
            }
        }
        return new JObject()
        {
            ["queue_running"] = running,
            ["queue_pending"] = pending
        };
    }

    /// <summary>여러 backend의 history 응답을 하나로 합친다.</summary>
    public static JObject MergeHistoryResponses(IEnumerable<JObject> histories)
    {
        JObject merged = new();
        foreach (JObject history in histories.Where(h => h is not null))
        {
            foreach (JProperty property in history.Properties())
            {
                merged[property.Name] = property.Value;
            }
        }
        return merged;
    }

    /// <summary>Comfy queue 응답을 현재 사용자 prompt만 남기도록 필터링한다.</summary>
    /// <param name="swarmUser">현재 Swarm 사용자다.</param>
    /// <param name="queue">원본 queue 응답이다.</param>
    /// <returns>필터링된 queue 응답이다.</returns>
    public static JObject FilterQueueResponse(User swarmUser, JObject queue, HttpContext context = null)
    {
        bool keepItem(JToken item)
        {
            if (item is JArray arr && arr.Count > 1)
            {
                return context is null ? IsOwnedPromptId(swarmUser, arr[1]?.ToString()) : IsOwnedPromptIdForContext(context, swarmUser, arr[1]?.ToString());
            }
            if (item is JObject obj)
            {
                return context is null ? IsOwnedPromptId(swarmUser, obj["prompt_id"]?.ToString()) : IsOwnedPromptIdForContext(context, swarmUser, obj["prompt_id"]?.ToString());
            }
            return false;
        }
        JObject result = queue.DeepClone() as JObject ?? new JObject();
        int rawRunning = (result["queue_running"] as JArray)?.Count ?? 0;
        int rawPending = (result["queue_pending"] as JArray)?.Count ?? 0;
        if (result["queue_running"] is JArray running)
        {
            result["queue_running"] = new JArray(running.Where(keepItem));
        }
        if (result["queue_pending"] is JArray pending)
        {
            result["queue_pending"] = new JArray(pending.Where(keepItem));
        }
        int filtRunning = (result["queue_running"] as JArray)?.Count ?? 0;
        int filtPending = (result["queue_pending"] as JArray)?.Count ?? 0;
        Logs.Debug($"[QFilter] FilterQueueResponse: raw=({rawRunning}+{rawPending}) filtered=({filtRunning}+{filtPending}) instanceId={InstanceId}");
        return result;
    }

    /// <summary>Comfy history 응답을 현재 사용자 prompt만 남기도록 필터링한다.</summary>
    /// <param name="swarmUser">현재 Swarm 사용자다.</param>
    /// <param name="history">원본 history 응답이다.</param>
    /// <returns>필터링된 history 응답이다.</returns>
    public static JObject FilterHistoryResponse(User swarmUser, JObject history, HttpContext context = null)
    {
        JObject result = new();
        foreach (JProperty property in history.Properties())
        {
            if (context is null ? IsOwnedPromptId(swarmUser, property.Name) : IsOwnedPromptIdForContext(context, swarmUser, property.Name))
            {
                result[property.Name] = property.Value;
            }
        }
        Logs.Debug($"[QFilter] FilterHistoryResponse: raw={history.Count} filtered={result.Count} instanceId={InstanceId}");
        return result;
    }

    /// <summary>Comfy history 결과를 현재 사용자 Gallery 인덱스에 동기화한다.</summary>
    /// <param name="swarmUser">현재 Swarm 사용자다.</param>
    /// <param name="webClient">현재 backend HTTP 클라이언트다.</param>
    /// <param name="webAddress">현재 backend 웹 주소다.</param>
    /// <param name="history">현재 사용자 기준으로 필터링된 history JSON이다.</param>
    public static async Task SyncHistoryOutputsToUserGallery(User swarmUser, HttpClient webClient, string webAddress, JObject history)
    {
        if (swarmUser is null || webClient is null || string.IsNullOrWhiteSpace(webAddress) || history is null || !history.HasValues)
        {
            Logs.Debug($"[ComfyGallerySync] skip-empty user={(swarmUser?.UserID ?? "null")} history_count={(history?.Count ?? 0)}");
            return;
        }
        Logs.Debug($"[ComfyGallerySync] begin user={swarmUser.UserID} history_count={history.Count}");
        Session session = swarmUser.GetGenericSession();
        T2IParamInput saveInput = new(session);
        saveInput.Set(T2IParamTypes.OverrideOutpathFormat, "comfy-workflow-[year]-[month]-[day]-[hour][minute][request_time_inc]-[number]");
        foreach (JProperty historyEntry in history.Properties())
        {
            string promptId = historyEntry.Name;
            if (historyEntry.Value is not JObject historyObj || historyObj["outputs"] is not JObject outputs)
            {
                Logs.Debug($"[ComfyGallerySync] prompt={promptId} skip-no-outputs");
                continue;
            }
            JToken finalOutput = outputs.Properties().LastOrDefault(p => p.Value is not null)?.Value;
            if (finalOutput is null)
            {
                Logs.Debug($"[ComfyGallerySync] prompt={promptId} skip-no-final-output");
                continue;
            }
            int batchIndex = 0;
            foreach (JToken outData in new[] { finalOutput })
            {
                if (outData is null)
                {
                    Logs.Debug($"[ComfyGallerySync] prompt={promptId} skip-null-outdata");
                    continue;
                }
                async Task syncSingleMedia(JObject outImage, string collectionName)
                {
                    if (outImage is null || outImage["filename"] is null)
                    {
                        Logs.Debug($"[ComfyGallerySync] prompt={promptId} collection={collectionName} skip-null-media");
                        return;
                    }
                    string filename = outImage["filename"]?.ToString();
                    string subfolder = outImage["subfolder"]?.ToString() ?? "";
                    string requestId = $"comfyhistory:{promptId}:{collectionName}:{subfolder}/{filename}";
                    if (UserOutputHistoryIndex.HasRequest(swarmUser, requestId))
                    {
                        Logs.Debug($"[ComfyGallerySync] prompt={promptId} collection={collectionName} file={filename} skip-existing");
                        batchIndex++;
                        return;
                    }
                    string viewUrl = $"filename={HttpUtility.UrlEncode(filename)}&type={(($"{outImage["type"]}" == "temp") ? "temp" : "output")}";
                    if (!string.IsNullOrWhiteSpace(subfolder))
                    {
                        viewUrl += $"&subfolder={HttpUtility.UrlEncode(subfolder)}";
                    }
                    byte[] raw = await (await webClient.GetAsync($"{webAddress}/view?{viewUrl}")).Content.ReadAsByteArrayAsync();
                    if (raw is null || raw.Length == 0)
                    {
                        Logs.Debug($"[ComfyGallerySync] prompt={promptId} collection={collectionName} file={filename} skip-empty-raw");
                        batchIndex++;
                        return;
                    }
                    string ext = filename.AfterLast('.');
                    string format = outImage.TryGetValue("format", out JToken formatTok) ? formatTok.ToString() : null;
                    MediaType type = MediaType.GetByExtension(ext) ?? MediaType.TypesByMimeType.GetValueOrDefault(format ?? "") ?? MediaType.ImagePng;
                    MediaFile file = type.MetaType.CreateNew(raw, type);
                    string metadata = new JObject()
                    {
                        ["source"] = "comfy_workflow",
                        ["prompt_id"] = promptId,
                        ["filename"] = filename,
                        ["subfolder"] = subfolder,
                        ["collection"] = collectionName
                    }.ToString(Newtonsoft.Json.Formatting.None);
                    T2IEngine.ImageOutput imageOutput = new() { File = file, IsReal = true };
                    (string savedUrl, string localPath) = session.SaveImage(imageOutput, batchIndex, saveInput, metadata);
                    if (savedUrl != "ERROR" && !string.IsNullOrWhiteSpace(localPath))
                    {
                        UserOutputHistoryIndex.RecordOutput(session, savedUrl, localPath, metadata, requestId, batchIndex);
                        AddTaskResult(swarmUser, promptId, savedUrl, filename, collectionName);
                        Logs.Debug($"[ComfyGallerySync] prompt={promptId} collection={collectionName} file={filename} saved={savedUrl}");
                    }
                    else
                    {
                        Logs.Debug($"[ComfyGallerySync] prompt={promptId} collection={collectionName} file={filename} save-failed");
                    }
                    batchIndex++;
                }
                JArray images = outData["images"] as JArray;
                JArray gifs = outData["gifs"] as JArray;
                JArray audio = outData["audio"] as JArray;
                if (images?.LastOrDefault() is JObject lastImage)
                {
                    Logs.Debug($"[ComfyGallerySync] prompt={promptId} select=images count={images.Count}");
                    await syncSingleMedia(lastImage, "images");
                }
                else if (gifs?.LastOrDefault() is JObject lastGif)
                {
                    Logs.Debug($"[ComfyGallerySync] prompt={promptId} select=gifs count={gifs.Count}");
                    await syncSingleMedia(lastGif, "gifs");
                }
                else if (audio?.LastOrDefault() is JObject lastAudio)
                {
                    Logs.Debug($"[ComfyGallerySync] prompt={promptId} select=audio count={audio.Count}");
                    await syncSingleMedia(lastAudio, "audio");
                }
                else
                {
                    Logs.Debug($"[ComfyGallerySync] prompt={promptId} skip-no-media-in-final-output");
                }
            }
        }
    }

    /// <summary>Comfy queue/history 삭제 요청 본문을 현재 사용자 소유 prompt 기준으로 정리한다.</summary>
    /// <param name="swarmUser">현재 Swarm 사용자다.</param>
    /// <param name="requestBody">원본 요청 본문 JSON이다.</param>
    /// <returns>필터링된 요청 본문 JSON이다.</returns>
    public static JObject FilterQueueMutationRequest(User swarmUser, JObject requestBody)
    {
        HashSet<string> ownedPromptIds = GetOwnedPromptIds(swarmUser);
        JObject filtered = new();
        if (requestBody.TryGetValue("delete", out JToken deleteTok) && deleteTok is JArray deleteArr)
        {
            JArray allowedDeletes = new(deleteArr.Where(id => ownedPromptIds.Contains(id?.ToString())));
            if (allowedDeletes.Count > 0)
            {
                filtered["delete"] = allowedDeletes;
            }
        }
        if (requestBody.TryGetValue("clear", out JToken clearTok) && clearTok.Type == JTokenType.Boolean && clearTok.Value<bool>())
        {
            if (ownedPromptIds.Count > 0)
            {
                filtered["delete"] = new JArray(ownedPromptIds);
            }
        }
        return filtered;
    }

    /// <summary>Main comfy redirection handler - the core handler for the '/ComfyBackendDirect' route.</summary>
    public static async Task ComfyBackendDirectHandler(HttpContext context)
    {
        if (context.Response.StatusCode == 404)
        {
            return;
        }
        User swarmUser = WebServer.GetUserFor(context);
        if (swarmUser is null || !swarmUser.HasPermission(ComfyUIBackendExtension.PermDirectCalls))
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("<!DOCTYPE html>\n<html>\n<head>\n<style>body{background-color:#101010;color:#eeeeee;}</style>\n</head>\n<body>\n<span class=\"comfy-failed-to-load\">Permission denied.</span>\n</body>\n</html>");
            await context.Response.CompleteAsync();
            return;
        }
        List<ComfyUIBackendExtension.ComfyBackendData> allBackends = [.. ComfyUIBackendExtension.ComfyBackendsDirect()];
        if (context.Request.Headers.TryGetValue("X-Swarm-Backend-ID", out StringValues backendId) && int.TryParse(backendId, out int backendIdInt))
        {
            allBackends = [.. allBackends.Where(b => b.Backend.BackendData.ID == backendIdInt)];
        }
        (HttpClient webClient, string apiAddress, string webAddress, AbstractT2IBackend backend) = allBackends.FirstOrDefault();
        if (webClient is null)
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("<!DOCTYPE html>\n<html>\n<head>\n<style>body{background-color:#101010;color:#eeeeee;}</style>\n</head>\n<body>\n<span class=\"comfy-failed-to-load\">No ComfyUI backend available, loading failed.</span>\n</body>\n</html>");
            await context.Response.CompleteAsync();
            return;
        }
        if (!context.Request.Cookies.TryGetValue("comfy_domulti", out string doMultiStr))
        {
            doMultiStr = "false";
        }
        bool wantsMulti = doMultiStr == "true" || doMultiStr == "queue";
        bool shouldReserve = doMultiStr == "reserve";
        if (!shouldReserve && !wantsMulti)
        {
            allBackends = [new(webClient, apiAddress, webAddress, backend)];
        }
        string path = context.Request.Path.Value;
        path = path.After("/ComfyBackendDirect");
        if (path.StartsWith('/'))
        {
            path = path[1..];
        }
        if (!string.IsNullOrWhiteSpace(context.Request.QueryString.Value))
        {
            path = $"{path}{context.Request.QueryString.Value}";
        }
        if (context.Request.Method == "GET" && path == "swarm/session_jobs")
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JObject()
            {
                ["tasks"] = GetTasksJsonForUser(swarmUser)
            }.ToString(Newtonsoft.Json.Formatting.None));
            await context.Response.CompleteAsync();
            return;
        }
        if (context.WebSockets.IsWebSocketRequest)
        {
            Logs.Debug($"Comfy backend direct websocket request to {path}, have {allBackends.Count} backends available");
            WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            List<Task> tasks = [];
            ComfyUser user = new() { Socket = socket, SwarmUser = swarmUser, WantsAllBackends = wantsMulti, WantsQueuing = doMultiStr == "queue", WantsReserve = doMultiStr == "reserve", BrowserSessionKey = GetBrowserSessionKey(context) };
            if (!string.IsNullOrWhiteSpace(user.BrowserSessionKey))
            {
                BrowserUsers[user.BrowserSessionKey] = user;
            }
            user.RunSendTask();
            user.RunClientReceiveTask();
            NewUserEvent?.Invoke(user);
            // Order all evens then all odds - eg 0, 2, 4, 6, 1, 3, 5, 7 (to reduce chance of overlap when sharing)
            int[] vals = [.. Enumerable.Range(0, allBackends.Count)];
            vals = [.. vals.Where(v => v % 2 == 0), .. vals.Where(v => v % 2 == 1)];
            bool found = false;
            void tryFindBackend()
            {
                foreach (int option in vals)
                {
                    if (!Users.Values.Any(u => u.BackendOffset == option) && RecentlyClaimedBackends.TryAdd(option, option))
                    {
                        Logs.Debug($"Comfy backend direct offset for new user is {option}");
                        user.BackendOffset = option;
                        found = true;
                        break;
                    }
                }
            }
            tryFindBackend(); // First try: find one never claimed
            if (!found) // second chance: clear claims and find one at least not taken by existing user
            {
                RecentlyClaimedBackends.Clear();
                tryFindBackend();
            }
            // (All else fails, default to 0)
            foreach (ComfyUIBackendExtension.ComfyBackendData localback in allBackends)
            {
                string scheme = localback.WebAddress.BeforeAndAfter("://", out string addr);
                scheme = scheme == "http" ? "ws" : "wss";
                ClientWebSocket outSocket = new();
                outSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await outSocket.ConnectAsync(new Uri($"{scheme}://{addr}/{path}"), Program.GlobalProgramCancel);
                ComfyClientData client = new() { Address = localback.WebAddress, Backend = localback.Backend, Socket = outSocket };
                await user.AddClient(client);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] recvBuf = new byte[20 * 1024 * 1024];
                        while (true)
                        {
                            WebSocketReceiveResult received = await outSocket.ReceiveAsync(recvBuf, Program.GlobalProgramCancel);
                            if (received.MessageType != WebSocketMessageType.Close)
                            {
                                Memory<byte> toSend = recvBuf.AsMemory(0, received.Count);
                                await user.Lock.WaitAsync();
                                try
                                {
                                    bool isJson = received.MessageType == WebSocketMessageType.Text && received.EndOfMessage && received.Count < 8192 * 10 && recvBuf[0] == '{';
                                    if (isJson)
                                    {
                                        string rawText = null;
                                        try
                                        {
                                            rawText = StringConversionHelper.UTF8Encoding.GetString(recvBuf[0..received.Count]);
                                            JObject parsed = rawText.ParseToJson();
                                            JToken dataTok = parsed["data"];
                                            if (dataTok is JObject dataObj)
                                            {
                                                if (dataObj.TryGetValue("sid", out JToken sidTok))
                                                {
                                                    if (client.SID is not null)
                                                    {
                                                        Users.TryRemove(client.SID, out _);
                                                    }
                                                    client.SID = sidTok.ToString();
                                                    Users.TryAdd(client.SID, user);
                                                    if (user.MasterSID is null)
                                                    {
                                                        user.MasterSID = client.SID;
                                                    }
                                                    else
                                                    {
                                                        dataObj["sid"] = user.MasterSID;
                                                    }
                                                }
                                                if (dataObj.TryGetValue("node", out JToken nodeTok))
                                                {
                                                    client.LastNode = nodeTok.ToString();
                                                }
                                                if (!ShouldForwardComfyEvent(user, parsed))
                                                {
                                                    continue;
                                                }
                                                UpdateTaskFromEvent(user.SwarmUser, parsed);
                                                // 소유권 검사 통과한 이벤트만 LastExecuting/LastProgress에 저장
                                                string type = parsed["type"]?.ToString();
                                                if (type == "status")
                                                {
                                                    if (dataObj["status"]?["exec_info"]?["queue_remaining"] is not null)
                                                    {
                                                        dataObj["status"]["exec_info"]["queue_remaining"] = user.TotalQueue;
                                                    }
                                                }
                                                else if (type == "executing")
                                                {
                                                    // node=null이면 이 prompt 실행 완료 → LastExecuting 초기화
                                                    JToken nodeTokExec = parsed["data"]?["node"];
                                                    bool execDone = nodeTokExec is null || nodeTokExec.Type == JTokenType.Null || string.IsNullOrEmpty(nodeTokExec.ToString());
                                                    if (execDone)
                                                    {
                                                        client.LastExecuting = null;
                                                        user.LastExecuting = null;
                                                    }
                                                    else
                                                    {
                                                        client.LastExecuting = parsed;
                                                        user.LastExecuting = parsed;
                                                    }
                                                }
                                                else if (type == "progress")
                                                {
                                                    client.LastProgress = parsed;
                                                    user.LastProgress = parsed;
                                                }
                                                toSend = Encoding.UTF8.GetBytes(parsed.ToString());
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logs.Error($"Failed to parse ComfyUI message \"{rawText.Replace('\n', ' ')}\": {ex.ReadableString()}");
                                        }
                                    }
                                    else
                                    {
                                        // 이진(binary) 메시지는 생성 중 미리보기 이미지다.
                                        // 소유 작업이 현재 실행 중인 경우에만 전달한다.
                                        if (received.MessageType == WebSocketMessageType.Binary && client.LastExecuting is null)
                                        {
                                            continue;
                                        }
                                        // LastExecuting/LastProgress는 이미 소유권 검사 통과한 것만 저장되므로 그대로 재전송
                                        if (client.LastExecuting is not null && (client.LastExecuting != user.LastExecuting || client.LastProgress != user.LastProgress))
                                        {
                                            user.LastExecuting = client.LastExecuting;
                                            user.NewMessageToClient(StringConversionHelper.UTF8Encoding.GetBytes(client.LastExecuting.ToString()), WebSocketMessageType.Text, true);
                                        }
                                        if (client.LastProgress is not null && (client.LastExecuting != user.LastExecuting || client.LastProgress != user.LastProgress))
                                        {
                                            user.LastProgress = client.LastProgress;
                                            user.NewMessageToClient(StringConversionHelper.UTF8Encoding.GetBytes(client.LastProgress.ToString()), WebSocketMessageType.Text, true);
                                        }
                                    }
                                    user.NewMessageToClient(toSend, received.MessageType, received.EndOfMessage);
                                }
                                finally
                                {
                                    user.Lock.Release();
                                }
                            }
                            if (socket.CloseStatus.HasValue)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException)
                        {
                            return;
                        }
                        Logs.Debug($"ComfyUI redirection failed (outsocket, user {swarmUser.UserID} with {user.Clients.Count} active sockets): {ex.ReadableString()}");
                    }
                    finally
                    {
                        user.Clients.TryRemove(client, out _);
                        if (client.SID == user.Reserved?.SID)
                        {
                            user.Unreserve();
                        }
                        outSocket.Dispose();
                        if (user.Clients.IsEmpty())
                        {
                            if (!string.IsNullOrWhiteSpace(user.BrowserSessionKey))
                            {
                                BrowserUsers.TryRemove(user.BrowserSessionKey, out _);
                            }
                            Users.TryRemove(client.SID, out _);
                            _ = Utilities.RunCheckedTask(() =>
                            {
                                if (socket.State == WebSocketState.Open)
                                {
                                    socket.CloseAsync(WebSocketCloseStatus.InternalServerError, null, Utilities.TimedCancel(TimeSpan.FromSeconds(2)).Token);
                                }
                                socket.Dispose();
                            });
                        }
                    }
                }));
            }
            tasks.Add(Task.WhenAny(Task.Delay(Timeout.Infinite, Program.GlobalProgramCancel), Task.Delay(Timeout.Infinite, user.ClientIsClosed.Token)));
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            return;
        }
        HttpResponseMessage response = null;
        ComfyUser promptComfyUser = null; // /prompt 요청을 제출한 ComfyUser를 추적한다.
        if (context.Request.Method == "POST")
        {
            if (!swarmUser.HasPermission(ComfyUIBackendExtension.PermBackendGenerate))
            {
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("<!DOCTYPE html>\n<html>\n<head><style>body{background-color:#101010;color:#eeeeee;}</style></head>\n<body>\n<span class=\"comfy-failed-to-load\">Permission denied.</span>\n</body>\n</html>");
                await context.Response.CompleteAsync();
                return;
            }
            void givePostError(string error)
            {
                Logs.Debug($"Comfy direct POST request gave Swarm-side error: {error}");
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 400;
                context.Response.WriteAsync(new JObject() { ["error"] = error }.ToString());
                context.Response.CompleteAsync();
            }
            HttpContent content = null;
            if (path == "prompt" || path == "api/prompt")
            {
                try
                {
                    using MemoryStream memStream = new();
                    await context.Request.Body.CopyToAsync(memStream);
                    byte[] data = memStream.ToArray();
                    JObject parsed = StringConversionHelper.UTF8Encoding.GetString(data).ParseToJson();
                    bool redirected = false;
                    if (parsed.TryGetValue("client_id", out JToken clientIdTok))
                    {
                        string sid = clientIdTok.ToString();
                        ComfyUser user = Users.GetValueOrDefault(sid) ?? GetRequestComfyUser(context, swarmUser) ?? GetComfyUsersForSwarmUser(swarmUser).FirstOrDefault();
                        promptComfyUser = user;
                        if (user is not null)
                        {
                            await user.Lock.WaitAsync();
                            try
                            {
                                JObject prompt = parsed["prompt"] as JObject;
                                prompt.Remove("swarm_prefer");
                                if (user.WantsQueuing)
                                {
                                    (_, JObject responseJson) = user.SendPromptQueue(prompt);
                                    user.RegisterOwnedPromptId(responseJson["prompt_id"]?.ToString());
                                    SetTaskQueued(swarmUser, responseJson["prompt_id"]?.ToString(), prompt);
                                    response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Utilities.JSONContent(responseJson) };
                                    redirected = true;
                                    Logs.Info($"Sent Comfy backend direct prompt requested to general queue (from user {swarmUser.UserID})");
                                }
                                else
                                {
                                    // 브라우저가 보낸 prompt_id와 관계없이 이 인스턴스의 prefix를 붙인 ID로 교체한다.
                                    // 이렇게 하면 WS 이벤트 필터링이 prefix 확인만으로 완전히 동작한다.
                                    string instancePrefixedId = $"swarm-{InstanceId}-{Guid.NewGuid():N}";
                                    parsed["prompt_id"] = instancePrefixedId;
                                    user.RegisterOwnedPromptId(instancePrefixedId);
                                    SetTaskQueued(swarmUser, instancePrefixedId, prompt);
                                    ComfyClientData client = await user.SendPromptRegular(prompt, givePostError);
                                    if (client?.SID is not null)
                                    {
                                        client.QueueRemaining++;
                                        webAddress = client.Address;
                                        backend = client.Backend;
                                        parsed["client_id"] = client.SID;
                                        client.FixUpPrompt(prompt);
                                        swarmUser.UpdateLastUsedTime();
                                        Logs.Info($"Sent Comfy backend direct prompt requested to backend #{backend.BackendData.ID} (from user {swarmUser.UserID})");
                                        backend.BackendData.UpdateLastReleaseTime();
                                        redirected = true;
                                    }
                                }
                            }
                            finally
                            {
                                user.Lock.Release();
                            }
                        }
                        else if (doMultiStr == "queue")
                        {
                            givePostError("[SwarmUI] SwarmQueue requested, but Client ID got mixed up. Refresh the page to fix this.");
                            return;
                        }
                    }
                    if (!redirected)
                    {
                        if (backend.MaxUsages <= 0)
                        {
                            if (ComfyUIBackendExtension.ComfyBackendsDirect().Any(b => b.Backend.CanLoadModels && b.Backend.MaxUsages > 0))
                            {
                                givePostError("[SwarmUI] No functional comfy backend available to run this request, but valid backends exist. Hit MultiGPU -> Use All to ensure you're able to use other backends.");
                            }
                            else
                            {
                                givePostError("[SwarmUI] No functional comfy backend available to run this request. Is a backend-scaling currently in progress?");
                            }
                            return;
                        }
                        Logs.Debug($"Was not able to redirect Comfy backend direct prompt request");
                        Logs.Verbose($"Above is for prompt: {parsed.ToDenseDebugString()}");
                        backend.BackendData.UpdateLastReleaseTime();
                        Logs.Info($"Sent Comfy backend improper API call direct prompt requested to backend #{backend.BackendData.ID}");
                    }
                    content = Utilities.JSONContent(parsed);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"ComfyUI redirection failed - prompt json parse: {ex.ReadableString()}");
                }
            }
            else if (path == "interrupt" || path == "api/interrupt")
            {
                using MemoryStream memStream = new();
                await context.Request.Body.CopyToAsync(memStream);
                byte[] inputBytes = memStream.ToArray();
                JObject interruptData = StringConversionHelper.UTF8Encoding.GetString(inputBytes).ParseToJson();
                // TODO: Maybe a global map instead of this per-user hack?
                string userPromptId = interruptData["prompt_id"]?.ToString();
                string realPromptId = Users.Values.Select(u => u.PromptIdMap.TryGetValue(userPromptId, out string r) ? r : null).FirstOrDefault(r => r is not null);
                if (realPromptId is not null)
                {
                    interruptData["prompt_id"] = realPromptId;
                    inputBytes = Encoding.UTF8.GetBytes(interruptData.ToString(Newtonsoft.Json.Formatting.None));
                }
                List<Task<HttpResponseMessage>> tasks = [];
                foreach (ComfyUIBackendExtension.ComfyBackendData back in allBackends)
                {
                    HttpRequestMessage dupRequest = new(new HttpMethod("POST"), $"{back.WebAddress}/{path}") { Content = new ByteArrayContent(inputBytes) };
                    dupRequest.Content.Headers.Add("Content-Type", context.Request.ContentType ?? "application/json");
                    tasks.Add(webClient.SendAsync(dupRequest));
                }
                await Task.WhenAll(tasks);
                List<HttpResponseMessage> responses = [.. tasks.Select(t => t.Result)];
                response = responses.FirstOrDefault(t => t.StatusCode == HttpStatusCode.OK);
                response ??= responses.FirstOrDefault();
            }
            else if (path == "queue" || path == "api/queue") // eg queue delete
            {
                MemoryStream inputCopy = new();
                await context.Request.Body.CopyToAsync(inputCopy);
                string inputText = Encoding.UTF8.GetString(inputCopy.ToArray());
                JObject requestJson = string.IsNullOrWhiteSpace(inputText) ? new JObject() : inputText.ParseToJson();
                HashSet<string> ownedPromptIds = GetOwnedPromptIdsForContext(context, swarmUser);
                JObject filteredRequest = new();
                if (requestJson.TryGetValue("delete", out JToken deleteTok) && deleteTok is JArray deleteArr)
                {
                    JArray allowedDeletes = new(deleteArr.Where(id => ownedPromptIds.Contains(id?.ToString())));
                    if (allowedDeletes.Count > 0)
                    {
                        filteredRequest["delete"] = allowedDeletes;
                    }
                }
                if (requestJson.TryGetValue("clear", out JToken clearTok) && clearTok.Type == JTokenType.Boolean && clearTok.Value<bool>() && ownedPromptIds.Count > 0)
                {
                    filteredRequest["delete"] = new JArray(ownedPromptIds);
                }
                if (!filteredRequest.HasValues)
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                }
                else
                {
                    List<Task<HttpResponseMessage>> tasks = [];
                    byte[] inputBytes = Encoding.UTF8.GetBytes(filteredRequest.ToString());
                    foreach (ComfyUIBackendExtension.ComfyBackendData back in allBackends)
                    {
                        HttpRequestMessage dupRequest = new(new HttpMethod("POST"), $"{back.WebAddress}/{path}") { Content = new ByteArrayContent(inputBytes) };
                        dupRequest.Content.Headers.Add("Content-Type", context.Request.ContentType ?? "application/json");
                        tasks.Add(webClient.SendAsync(dupRequest));
                    }
                    await Task.WhenAll(tasks);
                    List<HttpResponseMessage> responses = [.. tasks.Select(t => t.Result)];
                    response = responses.FirstOrDefault(t => t.StatusCode == HttpStatusCode.OK);
                    response ??= responses.FirstOrDefault();
                }
            }
            else if (path == "history" || path == "api/history")
            {
                MemoryStream inputCopy = new();
                await context.Request.Body.CopyToAsync(inputCopy);
                string inputText = Encoding.UTF8.GetString(inputCopy.ToArray());
                JObject requestJson = string.IsNullOrWhiteSpace(inputText) ? new JObject() : inputText.ParseToJson();
                HashSet<string> ownedPromptIds = GetOwnedPromptIdsForContext(context, swarmUser);
                JObject filteredRequest = new();
                if (requestJson.TryGetValue("delete", out JToken deleteTok) && deleteTok is JArray deleteArr)
                {
                    JArray allowedDeletes = new(deleteArr.Where(id => ownedPromptIds.Contains(id?.ToString())));
                    if (allowedDeletes.Count > 0)
                    {
                        filteredRequest["delete"] = allowedDeletes;
                    }
                }
                if (requestJson.TryGetValue("clear", out JToken clearTok) && clearTok.Type == JTokenType.Boolean && clearTok.Value<bool>() && ownedPromptIds.Count > 0)
                {
                    filteredRequest["delete"] = new JArray(ownedPromptIds);
                }
                if (!filteredRequest.HasValues)
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                }
                else
                {
                    List<Task<HttpResponseMessage>> tasks = [];
                    byte[] inputBytes = Encoding.UTF8.GetBytes(filteredRequest.ToString());
                    foreach (ComfyUIBackendExtension.ComfyBackendData back in allBackends)
                    {
                        HttpRequestMessage dupRequest = new(new HttpMethod("POST"), $"{back.WebAddress}/{path}") { Content = new ByteArrayContent(inputBytes) };
                        dupRequest.Content.Headers.Add("Content-Type", context.Request.ContentType ?? "application/json");
                        tasks.Add(webClient.SendAsync(dupRequest));
                    }
                    await Task.WhenAll(tasks);
                    List<HttpResponseMessage> responses = [.. tasks.Select(t => t.Result)];
                    response = responses.FirstOrDefault(t => t.StatusCode == HttpStatusCode.OK);
                    response ??= responses.FirstOrDefault();
                }
            }
            else
            {
                Logs.Verbose($"Comfy direct POST request to path {path}");
            }
            if (response is null)
            {
                HttpRequestMessage request = new(new HttpMethod("POST"), $"{webAddress}/{path}") { Content = content ?? new StreamContent(context.Request.Body) };
                if (content is null)
                {
                    request.Content.Headers.Add("Content-Type", context.Request.ContentType ?? "application/json");
                }
                response = await webClient.SendAsync(request);
            }
        }
        else
        {
            if (path == "queue" || path.StartsWith("queue?") || path.StartsWith("queue/") || path == "api/queue" || path.StartsWith("api/queue?") || path.StartsWith("api/queue/"))
            {
                List<JObject> rawQueues = [];
                foreach (ComfyUIBackendExtension.ComfyBackendData localBack in allBackends)
                {
                    HttpResponseMessage rawResponse = await localBack.Client.GetAsync($"{localBack.WebAddress}/{path}");
                    rawQueues.Add((await rawResponse.Content.ReadAsStringAsync()).ParseToJson());
                }
                JObject mergedQueue = MergeQueueResponses(rawQueues);
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Utilities.JSONContent(FilterQueueResponse(swarmUser, mergedQueue, context)) };
            }
            else if (path == "history" || path.StartsWith("history?") || path == "api/history" || path.StartsWith("api/history?")
                || path.StartsWith("history/") || path.StartsWith("api/history/"))
            {
                List<JObject> filteredHistories = [];
                foreach (ComfyUIBackendExtension.ComfyBackendData localBack in allBackends)
                {
                    HttpResponseMessage rawResponse = await localBack.Client.GetAsync($"{localBack.WebAddress}/{path}");
                    JObject history = (await rawResponse.Content.ReadAsStringAsync()).ParseToJson();
                    JObject filteredHistory = FilterHistoryResponse(swarmUser, history);
                    if (filteredHistory.HasValues)
                    {
                        await SyncHistoryOutputsToUserGallery(swarmUser, localBack.Client, localBack.WebAddress, filteredHistory);
                    }
                    filteredHistories.Add(filteredHistory);
                }
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Utilities.JSONContent(MergeHistoryResponses(filteredHistories)) };
            }
            else if (path.StartsWith("view?filename=") || path.StartsWith("api/view?filename=") || path.StartsWith("api/vhs/queryvideo?filename="))
            {
                List<Task<HttpResponseMessage>> requests = [];
                foreach (ComfyUIBackendExtension.ComfyBackendData localBack in allBackends)
                {
                    requests.Add(localBack.Client.SendAsync(new(new(context.Request.Method), $"{localBack.WebAddress}/{path}")));
                }
                await Task.WhenAll(requests);
                response = requests.Select(r => r.Result).FirstOrDefault(r => r.StatusCode == HttpStatusCode.OK) ?? requests.First().Result;
            }
            else if ((path == "object_info" || path.StartsWith("object_info?") || path == "api/object_info" || path.StartsWith("api/object_info?")) && Program.ServerSettings.Performance.DoBackendDataCache)
            {
                JObject data = ObjectInfoReadCacher.GetValue();
                if (data is null)
                {
                    ObjectInfoReadCacher.ForceExpire();
                }
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data.ToString(), Encoding.UTF8, "application/json") };
            }
            else if (path == "user.css" || path == "api/userdata/user.css" || path == "api/user.css")
            {
                HttpResponseMessage rawResponse = await webClient.GetAsync($"{webAddress}/{path}");
                string remoteUserThemeText = rawResponse.StatusCode == HttpStatusCode.OK ? await rawResponse.Content.ReadAsStringAsync() : "";
                string theme = swarmUser.Settings.Theme ?? Program.ServerSettings.DefaultUser.Theme;
                if (Program.Web.RegisteredThemes.ContainsKey(theme))
                {
                    string themeText = ComfyThemeData.GetOrCreate(theme, () =>
                    {
                        string path = $"{ComfyUIBackendExtension.Folder}/ThemeCSS/{theme}.css";
                        if (!File.Exists(path))
                        {
                            return null;
                        }
                        return File.ReadAllText(path);
                    });
                    if (themeText is not null)
                    {
                        remoteUserThemeText += $"\n{themeText}\n";
                    }
                }
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(remoteUserThemeText, Encoding.UTF8, "text/css") };
            }
            else
            {
                HttpRequestMessage request = new(new(context.Request.Method), $"{webAddress}/{path}");
                if ((context.Request.Headers.ContentLength ?? 0) > 0)
                {
                    request.Content = new StreamContent(context.Request.Body);
                }
                response = await webClient.SendAsync(request);
            }
        }
        int code = (int)response.StatusCode;
        if (code != 200)
        {
            Logs.Debug($"ComfyUI redirection gave non-200 code: '{code}' for URL: {context.Request.Method} '{path}'");
        }
        //Logs.Verbose($"Comfy Redir status code {code} from {context.Response.StatusCode} and type {response.Content.Headers.ContentType} for {context.Request.Method} '{path}'");
        context.Response.StatusCode = code;
        if (response.Content is not null && code != 204)
        {
            if (response.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }
            if ((path == "prompt" || path == "api/prompt") && response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                string responseText = await response.Content.ReadAsStringAsync();
                JObject responseJson = responseText.ParseToJson();
                if (context.Request.Method == "POST" && responseJson.TryGetValue("prompt_id", out JToken promptIdTok))
                {
                    string pid = promptIdTok.ToString();
                    // promptComfyUser는 위 /prompt 처리 블록에서 설정된다. 없으면 첫 번째 ComfyUser로 폴백한다.
                    ComfyUser targetComfyUser = promptComfyUser ?? GetComfyUsersForSwarmUser(swarmUser).FirstOrDefault();
                    targetComfyUser?.RegisterOwnedPromptId(pid);
                    if (!string.IsNullOrWhiteSpace(pid) && !SwarmTasksByUser.GetValueOrDefault(swarmUser.UserID, new()).ContainsKey(pid))
                    {
                        SetTaskQueued(swarmUser, pid);
                    }
                    Logs.Debug($"[QFilter] prompt 등록: pid={pid} masterSID={targetComfyUser?.MasterSID ?? "null"} promptComfyUser_was_null={promptComfyUser is null}");
                }
                await context.Response.WriteAsync(responseJson.ToString(Newtonsoft.Json.Formatting.None));
                await context.Response.CompleteAsync();
                return;
            }
            await response.Content.CopyToAsync(context.Response.Body);
        }
        await context.Response.CompleteAsync();
    }
}
