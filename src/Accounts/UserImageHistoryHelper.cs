using System.IO;
using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Accounts;

/// <summary>Helper for handling user's image history.</summary>
public class UserImageHistoryHelper
{
    /// <summary>Mapping of exposed folder names that every user can see, to actual file location of the shared data folder source.
    /// <para>Every key should end with a '/'. It is recommended to prefix with a '_' to indicate that it is special. For example, '_myspecial/'.</para>
    /// <para>Real paths should be constructed via <see cref="Path.GetFullPath(string)"/>.</para>
    /// <para>Special folders cannot contain other special folders.</para></summary>
    public static ConcurrentDictionary<string, string> SharedSpecialFolders = [];

    /// <summary>현재 사용자가 공용 special folder에 접근할 수 있는지 반환한다.</summary>
    /// <param name="user">확인할 사용자다.</param>
    /// <returns>공용 special folder 접근 권한이 있으면 true를 반환한다.</returns>
    public static bool CanAccessSharedSpecialFolders(User user)
    {
        return user?.HasPermission(Permissions.ViewOthersOutputs) == true;
    }

    /// <summary>주어진 경로가 공용 special folder 별칭을 직접 가리키는지 확인한다.</summary>
    /// <param name="root">사용자 출력 루트 절대 경로다.</param>
    /// <param name="path">검사할 절대 경로다.</param>
    /// <returns>공용 special folder 별칭이면 해당 별칭을 반환하고, 아니면 null을 반환한다.</returns>
    public static string GetSharedSpecialFolderKeyForPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string folder = Path.GetRelativePath(root, path).Replace('\\', '/').TrimStart('/');
        if (!folder.EndsWith('/'))
        {
            folder += '/';
        }
        if (folder == "./")
        {
            return null;
        }
        foreach (string exposedFolder in SharedSpecialFolders.Keys.OrderByDescending(x => x.Length))
        {
            if (folder.StartsWith(exposedFolder, StringComparison.Ordinal))
            {
                return exposedFolder;
            }
        }
        return null;
    }

    /// <summary>사용자 출력 브라우징 요청이 공용 special folder를 가리킬 때 접근 가능 여부를 검사한다.</summary>
    /// <param name="user">현재 사용자다.</param>
    /// <param name="root">사용자 출력 루트 절대 경로다.</param>
    /// <param name="path">검사할 절대 경로다.</param>
    /// <param name="error">접근이 거부된 경우 사용자용 오류 문구다.</param>
    /// <returns>접근이 허용되면 true를 반환한다.</returns>
    public static bool ValidatePathAccess(User user, string root, string path, out string error)
    {
        error = null;
        string sharedKey = GetSharedSpecialFolderKeyForPath(root, path);
        if (sharedKey is null)
        {
            return true;
        }
        if (CanAccessSharedSpecialFolders(user))
        {
            return true;
        }
        error = "You do not have permission to access shared output folders.";
        return false;
    }

    /// <summary>Adapts a user image history path to the actual file path. Often just returns <paramref name="path"/>, but may adapt for special folders.</summary>
    /// <param name="user">The relevant user.</param>
    /// <param name="path">The relevant image path that may need redirection.</param>
    /// <param name="root">The user's image root. Leave null to implicitly use the user's output directory.</param>
    public static string GetRealPathFor(User user, string path, string root = null)
    {
        if (path is null)
        {
            return null;
        }
        root ??= user.OutputDirectory;
        string folder = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!folder.EndsWith('/'))
        {
            folder += '/';
        }
        if (folder == "./")
        {
            return path;
        }
        if (CanAccessSharedSpecialFolders(user))
        {
            foreach ((string exposedFolder, string realPath) in SharedSpecialFolders)
            {
                if (folder.StartsWith(exposedFolder, StringComparison.Ordinal))
                {
                    string cleaned = folder[exposedFolder.Length..];
                    path = Path.GetFullPath(Path.Combine(realPath, cleaned));
                }
            }
        }
        path = path.Replace('\\', '/');
        while (path.Contains("//"))
        {
            path = path.Replace("//", "/");
        }
        if (path.EndsWith('/'))
        {
            path = path[..^1];
        }
        return path;
    }

    /// <summary>Ffmpeg can get weird with overlapping calls, so max one at a time.</summary>
    public static ManyReadOneWriteLock FfmpegLock = new(1);

    /// <summary>Use ffmpeg to generate a preview for a video file.</summary>
    /// <param name="file">The video file.</param>
    public static async Task DoFfmpegPreviewGeneration(string file)
    {
        string fullPathNoExt = file.BeforeLast('.');
        if (string.IsNullOrWhiteSpace(Utilities.FfmegLocation.Value))
        {
            Logs.Warning("ffmpeg cannot be found, some features will not work including video previews. Please ensure ffmpeg is locatable to use video files.");
        }
        else
        {
            using ManyReadOneWriteLock.WriteClaim claim = FfmpegLock.LockWrite();
            await Utilities.QuickRunProcess(Utilities.FfmegLocation.Value, ["-i", file, "-vf", "select=eq(n\\,0)", "-q:v", "3", fullPathNoExt + ".swarmpreview.jpg"]);
            if (Program.ServerSettings.UI.AllowAnimatedPreviews)
            {
                await Utilities.QuickRunProcess(Utilities.FfmegLocation.Value, ["-i", file, "-vcodec", "libwebp", "-filter:v", "fps=fps=6,scale=-1:128", "-lossless", "0", "-compression_level", "2", "-q:v", "60", "-loop", "0", "-preset", "picture", "-an", "-vsync", "0", "-t", "5", fullPathNoExt + ".swarmpreview.webp"]);
            }
        }
    }
}
