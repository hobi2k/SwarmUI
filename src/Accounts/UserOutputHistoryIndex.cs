using FreneticUtilities.FreneticExtensions;
using LiteDB;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Utils;
using System.IO;

namespace SwarmUI.Accounts;

/// <summary>인스턴스 로컬 결과 인덱스를 관리하는 helper다.</summary>
public static class UserOutputHistoryIndex
{
    /// <summary>인스턴스 로컬 결과 인덱스 항목이다.</summary>
    public class OutputEntry
    {
        /// <summary>항목 고유 ID다.</summary>
        [BsonId]
        public string ID { get; set; }

        /// <summary>항목 소유 사용자 ID다.</summary>
        public string UserID { get; set; }

        /// <summary>화면에 보여줄 상대 경로다.</summary>
        public string DisplayPath { get; set; }

        /// <summary>공유 스토리지 기준 실제 파일 절대 경로다.</summary>
        public string LocalPath { get; set; }

        /// <summary>저장 시점의 기본 웹 경로다.</summary>
        public string WebPath { get; set; }

        /// <summary>원본 메타데이터 문자열이다.</summary>
        public string Metadata { get; set; }

        /// <summary>요청 ID다.</summary>
        public string RequestID { get; set; }

        /// <summary>배치 인덱스다.</summary>
        public int BatchIndex { get; set; }

        /// <summary>생성 시각 UnixTimeSeconds 값이다.</summary>
        public long CreatedAt { get; set; }
    }

    /// <summary>정렬 모드다.</summary>
    public enum SortMode
    {
        /// <summary>이름순이다.</summary>
        Name,

        /// <summary>생성 시각순이다.</summary>
        Date
    }

    /// <summary>결과 인덱스에 새 항목을 기록한다.</summary>
    /// <param name="session">현재 사용자 세션이다.</param>
    /// <param name="webPath">사용자에게 반환된 웹 경로다.</param>
    /// <param name="localPath">실제 저장된 로컬 파일 절대 경로다.</param>
    /// <param name="metadata">결과 메타데이터다.</param>
    /// <param name="requestId">생성 요청 ID다.</param>
    /// <param name="batchIndex">배치 인덱스다.</param>
    public static void RecordOutput(Session session, string webPath, string localPath, string metadata, string requestId, int batchIndex)
    {
        if (Program.NoPersist || session?.User is null || string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(webPath))
        {
            return;
        }
        string fullPath = Path.GetFullPath(localPath).Replace('\\', '/');
        string displayPath = BuildDisplayPath(webPath, fullPath);
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            displayPath = Path.GetFileName(fullPath);
        }
        OutputEntry entry = new()
        {
            ID = $"out_{Guid.NewGuid():N}",
            UserID = session.User.UserID,
            DisplayPath = displayPath,
            LocalPath = fullPath,
            WebPath = webPath.Replace('\\', '/'),
            Metadata = metadata ?? "",
            RequestID = requestId ?? "",
            BatchIndex = batchIndex,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        lock (Program.Sessions.DBLock)
        {
            Program.Sessions.OutputHistoryEntries.Upsert(entry);
        }
    }

    /// <summary>현재 사용자에게 속한 결과 인덱스 항목을 가져온다.</summary>
    /// <param name="user">현재 사용자다.</param>
    /// <param name="entryId">항목 ID다.</param>
    /// <returns>현재 사용자 소유 항목이면 반환하고, 아니면 null을 반환한다.</returns>
    public static OutputEntry GetEntry(User user, string entryId)
    {
        if (user is null || string.IsNullOrWhiteSpace(entryId))
        {
            return null;
        }
        lock (Program.Sessions.DBLock)
        {
            OutputEntry entry = Program.Sessions.OutputHistoryEntries.FindById(entryId);
            if (entry?.UserID != user.UserID)
            {
                return null;
            }
            return entry;
        }
    }

    /// <summary>현재 사용자 소유 인덱스 항목을 삭제한다. 파일도 함께 삭제한다.</summary>
    /// <param name="user">현재 사용자다.</param>
    /// <param name="entryId">삭제할 항목 ID다.</param>
    /// <returns>삭제 성공이면 true, 항목이 없거나 권한이 없으면 false를 반환한다.</returns>
    public static bool DeleteEntry(User user, string entryId)
    {
        OutputEntry entry = GetEntry(user, entryId);
        if (entry is null)
        {
            return false;
        }
        lock (Program.Sessions.DBLock)
        {
            Program.Sessions.OutputHistoryEntries.Delete(entryId);
        }
        try
        {
            if (File.Exists(entry.LocalPath))
            {
                Action<string> deleteFile = Program.ServerSettings.Paths.RecycleDeletedImages ? Utilities.SendFileToRecycle : File.Delete;
                deleteFile(entry.LocalPath);
                string fileBase = entry.LocalPath.BeforeLast('.');
                foreach (string ext in new[] { ".txt", ".metadata.js", ".swarm.json", ".swarmpreview.jpg", ".swarmpreview.webp" })
                {
                    string altFile = $"{fileBase}{ext}";
                    if (File.Exists(altFile))
                    {
                        deleteFile(altFile);
                    }
                }
                OutputMetadataTracker.RemoveMetadataFor(entry.LocalPath);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"DeleteEntry: 파일 삭제 실패 '{entry.LocalPath}': {ex.Message}");
        }
        return true;
    }

    /// <summary>현재 사용자에게 특정 요청 ID 항목이 이미 기록되어 있는지 반환한다.</summary>
    /// <param name="user">현재 사용자다.</param>
    /// <param name="requestId">확인할 요청 ID다.</param>
    /// <returns>같은 요청 ID 항목이 이미 있으면 true를 반환한다.</returns>
    public static bool HasRequest(User user, string requestId)
    {
        if (user is null || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }
        lock (Program.Sessions.DBLock)
        {
            return Program.Sessions.OutputHistoryEntries.Exists(e => e.UserID == user.UserID && e.RequestID == requestId);
        }
    }

    /// <summary>현재 사용자 기준 결과 인덱스 목록을 브라우저 응답 형식으로 반환한다.</summary>
    /// <param name="session">현재 사용자 세션이다.</param>
    /// <param name="path">가상 폴더 상대 경로다.</param>
    /// <param name="depth">탐색 깊이다.</param>
    /// <param name="sortBy">정렬 방식 문자열이다.</param>
    /// <param name="sortReverse">역정렬 여부다.</param>
    /// <returns>브라우저용 폴더/파일 응답이다.</returns>
    public static JObject ListOutputs(Session session, string path, int depth, string sortBy, bool sortReverse)
    {
        if (!Enum.TryParse(sortBy, true, out SortMode sortMode))
        {
            return new JObject() { ["error"] = $"Invalid sort mode '{sortBy}'." };
        }
        SyncExistingOutputs(session);
        string normalizedPath = NormalizeVirtualPath(path);
        List<OutputEntry> entries;
        lock (Program.Sessions.DBLock)
        {
            entries = [.. Program.Sessions.OutputHistoryEntries.Find(e => e.UserID == session.User.UserID)];
        }
        entries = [.. entries.Where(e => EntryExists(e) && !ShouldIgnoreFile(e.LocalPath) && !ShouldIgnoreEntry(e)).Where(e => IsPathVisible(e.DisplayPath, normalizedPath, depth))];
        if (sortMode == SortMode.Name)
        {
            entries = [.. entries.OrderBy(e => e.DisplayPath, StringComparer.OrdinalIgnoreCase)];
        }
        else
        {
            entries = [.. entries.OrderByDescending(e => e.CreatedAt)];
        }
        if (sortReverse)
        {
            entries.Reverse();
        }
        HashSet<string> folders = [];
        foreach (OutputEntry entry in entries)
        {
            foreach (string folder in EnumerateVisibleFolders(entry.DisplayPath, normalizedPath, depth))
            {
                folders.Add(folder);
            }
        }
        JArray files = new();
        foreach (OutputEntry entry in entries)
        {
            string relativeDisplayPath = GetRelativeDisplayPath(entry.DisplayPath, normalizedPath);
            files.Add(new JObject()
            {
                ["src"] = relativeDisplayPath,
                ["metadata"] = string.IsNullOrWhiteSpace(entry.Metadata) ? null : entry.Metadata,
                ["entry_id"] = entry.ID,
                ["url"] = $"OutputIndex/{entry.ID}"
            });
        }
        return new JObject()
        {
            ["folders"] = JArray.FromObject(folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()),
            ["files"] = files
        };
    }

    /// <summary>기존 출력 폴더 파일을 현재 사용자 인덱스에 동기화한다.</summary>
    /// <param name="session">현재 사용자 세션이다.</param>
    public static void SyncExistingOutputs(Session session)
    {
        if (session?.User is null)
        {
            return;
        }
        string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, session.User.OutputDirectory).Replace('\\', '/');
        if (!Directory.Exists(root))
        {
            return;
        }
        HashSet<string> existingPaths;
        lock (Program.Sessions.DBLock)
        {
            existingPaths = new HashSet<string>(
                Program.Sessions.OutputHistoryEntries
                    .Find(e => e.UserID == session.User.UserID)
                    .Select(e => Path.GetFullPath(e.LocalPath).Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string normalizedFile = Path.GetFullPath(file).Replace('\\', '/');
            if (existingPaths.Contains(normalizedFile) || ShouldIgnoreFile(normalizedFile))
            {
                continue;
            }
            string relativePath = Path.GetRelativePath(root, normalizedFile).Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }
            string webPath = Program.ServerSettings.Paths.AppendUserNameToOutputPath ? $"View/{session.User.UserID}/{relativePath}" : $"Output/{relativePath}";
            string metadata = OutputMetadataTracker.GetMetadataFor(normalizedFile, root, session.User.Settings.StarNoFolders)?.Metadata ?? "";
            OutputEntry entry = new()
            {
                ID = $"out_{Guid.NewGuid():N}",
                UserID = session.User.UserID,
                DisplayPath = relativePath,
                LocalPath = normalizedFile,
                WebPath = webPath,
                Metadata = metadata,
                RequestID = "synced-existing-output",
                BatchIndex = 0,
                CreatedAt = ((DateTimeOffset)File.GetLastWriteTimeUtc(normalizedFile)).ToUnixTimeSeconds()
            };
            lock (Program.Sessions.DBLock)
            {
                Program.Sessions.OutputHistoryEntries.Upsert(entry);
            }
            existingPaths.Add(normalizedFile);
        }
    }

    /// <summary>웹 경로를 기반으로 화면 표시용 상대 경로를 계산한다.</summary>
    /// <param name="webPath">기본 웹 경로다.</param>
    /// <param name="localPath">실제 파일 절대 경로다.</param>
    /// <returns>히스토리와 갤러리에 표시할 상대 경로다.</returns>
    public static string BuildDisplayPath(string webPath, string localPath)
    {
        string normalized = webPath.Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("View/", StringComparison.Ordinal))
        {
            string[] parts = normalized.Split('/', 3);
            if (parts.Length >= 3)
            {
                return parts[2];
            }
        }
        if (normalized.StartsWith("Output/", StringComparison.Ordinal))
        {
            return normalized["Output/".Length..];
        }
        return Path.GetFileName(localPath).Replace('\\', '/');
    }

    /// <summary>인덱스 동기화 대상에서 제외할 파일인지 반환한다.</summary>
    /// <param name="path">검사할 절대 경로다.</param>
    /// <returns>결과 인덱스에 넣지 않을 파일이면 true를 반환한다.</returns>
    public static bool ShouldIgnoreFile(string path)
    {
        string normalized = path.Replace('\\', '/');
        string extension = Path.GetExtension(normalized).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return true;
        }
        if (extension != "html" && MediaType.GetByExtension(extension) is null)
        {
            return true;
        }
        return normalized.EndsWith(".swarm.json", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".swarmpreview.jpg", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".swarmpreview.webp", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>현재 Gallery에 표시하지 않을 인덱스 항목인지 반환한다.</summary>
    /// <param name="entry">검사할 인덱스 항목이다.</param>
    /// <returns>표시 대상에서 제외해야 하면 true를 반환한다.</returns>
    public static bool ShouldIgnoreEntry(OutputEntry entry)
    {
        if (entry is null)
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(entry.Metadata))
        {
            try
            {
                JObject metadata = JObject.Parse(entry.Metadata);
                if ($"{metadata["source"]}" == "comfy_workflow" && NormalizeVirtualPath(entry.DisplayPath).StartsWith("raw/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 메타데이터가 깨진 경우에는 다른 기본 규칙으로만 판정한다.
            }
        }
        return false;
    }

    /// <summary>현재 항목이 실제로 아직 존재하는지 반환한다.</summary>
    /// <param name="entry">검사할 항목이다.</param>
    /// <returns>실제 파일이 있거나 저장 중이면 true를 반환한다.</returns>
    public static bool EntryExists(OutputEntry entry)
    {
        string path = entry?.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) || Session.StillSavingFiles.ContainsKey(fullPath);
    }

    /// <summary>가상 경로를 정규화한다.</summary>
    /// <param name="path">원본 가상 경로다.</param>
    /// <returns>정규화된 가상 경로다.</returns>
    public static string NormalizeVirtualPath(string path)
    {
        string normalized = (path ?? "").Replace('\\', '/').Trim('/');
        while (normalized.Contains("//"))
        {
            normalized = normalized.Replace("//", "/");
        }
        return normalized;
    }

    /// <summary>표시 경로가 현재 브라우징 경로 아래에 있는지 확인한다.</summary>
    /// <param name="displayPath">항목 표시 경로다.</param>
    /// <param name="currentPath">현재 가상 폴더 경로다.</param>
    /// <param name="depth">탐색 깊이다.</param>
    /// <returns>현재 브라우징 범위에 포함되면 true를 반환한다.</returns>
    public static bool IsPathVisible(string displayPath, string currentPath, int depth)
    {
        string relative = GetRelativeDisplayPath(displayPath, currentPath);
        if (relative is null)
        {
            return false;
        }
        if (depth < 0)
        {
            return true;
        }
        int slashCount = relative.Count(c => c == '/');
        return slashCount <= depth;
    }

    /// <summary>현재 가상 폴더 기준 상대 표시 경로를 반환한다.</summary>
    /// <param name="displayPath">원본 표시 경로다.</param>
    /// <param name="currentPath">현재 가상 폴더다.</param>
    /// <returns>현재 폴더 기준 상대 경로다. 범위 밖이면 null을 반환한다.</returns>
    public static string GetRelativeDisplayPath(string displayPath, string currentPath)
    {
        string normalizedDisplay = NormalizeVirtualPath(displayPath);
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return normalizedDisplay;
        }
        if (!normalizedDisplay.StartsWith(currentPath + "/", StringComparison.Ordinal))
        {
            return null;
        }
        return normalizedDisplay[(currentPath.Length + 1)..];
    }

    /// <summary>현재 폴더에서 보여야 하는 하위 폴더 목록을 반환한다.</summary>
    /// <param name="displayPath">항목 표시 경로다.</param>
    /// <param name="currentPath">현재 가상 폴더다.</param>
    /// <param name="depth">탐색 깊이다.</param>
    /// <returns>현재 브라우저에 노출할 하위 폴더 경로 목록이다.</returns>
    public static IEnumerable<string> EnumerateVisibleFolders(string displayPath, string currentPath, int depth)
    {
        string relative = GetRelativeDisplayPath(displayPath, currentPath);
        if (relative is null || !relative.Contains('/'))
        {
            yield break;
        }
        string[] parts = relative.Split('/');
        int folderDepth = Math.Max(0, Math.Min(depth, parts.Length - 2));
        string accum = "";
        for (int i = 0; i <= folderDepth; i++)
        {
            accum = accum == "" ? parts[i] : $"{accum}/{parts[i]}";
            yield return accum;
        }
    }
}
