using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using AndroidEnvironment = Android.OS.Environment;
using Microsoft.Maui.Storage;

namespace Wisp.Android;

/// <summary>
/// Bridges Android content:// URIs and scoped storage to the native engine,
/// which works with ordinary local filesystem paths.
/// </summary>
public static class AndroidFileService
{
    private static Context Context => global::Android.App.Application.Context;

    public static string ReceivedRoot
    {
        get
        {
            var external = Context.GetExternalFilesDir(null)?.AbsolutePath;
            var root = Path.Combine(external ?? Context.FilesDir!.AbsolutePath, "Wisp");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string StagingRoot
    {
        get
        {
            var root = Path.Combine(FileSystem.CacheDirectory, "outgoing");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static void ResetStaging()
    {
        try
        {
            var root = Path.Combine(FileSystem.CacheDirectory, "outgoing");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
        }
        catch
        {
        }
    }

    public static async Task<List<string>> StageFilesAsync(IEnumerable<FileResult> results)
    {
        var paths = new List<string>();
        foreach (var result in results)
        {
            try
            {
                var name = SafeName(result.FileName);
                var target = UniquePath(StagingRoot, name);
                await using var input = await result.OpenReadAsync();
                await using var output = File.Create(target);
                await input.CopyToAsync(output);
                paths.Add(target);
            }
            catch
            {
                // Skip items the provider no longer grants access to.
            }
        }
        return paths;
    }

    public static async Task<List<string>> StageUrisAsync(IEnumerable<global::Android.Net.Uri> uris)
    {
        var paths = new List<string>();
        foreach (var uri in uris)
        {
            try
            {
                var name = SafeName(DisplayNameFor(uri));
                var target = UniquePath(StagingRoot, name);
                await using var input = Context.ContentResolver!.OpenInputStream(uri);
                if (input is null) continue;
                await using var output = File.Create(target);
                await input.CopyToAsync(output);
                paths.Add(target);
            }
            catch
            {
            }
        }
        return paths;
    }

    /// <summary>
    /// Copies received files into public Downloads/Wisp on Android 10+. Returns
    /// the user-facing location, or null when the platform cannot do so safely.
    /// </summary>
    public static async Task<string?> ExportToDownloadsAsync(string sourceDirectory)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q || !Directory.Exists(sourceDirectory))
            return null;

        var exported = 0;
        var downloads = AndroidEnvironment.DirectoryDownloads;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(sourceDirectory, source);
                var relativeDirectory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                var values = new ContentValues();
                values.Put(MediaStore.IMediaColumns.DisplayName, Path.GetFileName(source));
                values.Put(MediaStore.IMediaColumns.MimeType, MimeTypeOf(source));
                values.Put(MediaStore.IMediaColumns.RelativePath,
                    string.IsNullOrEmpty(relativeDirectory)
                        ? $"{downloads}/Wisp"
                        : $"{downloads}/Wisp/{relativeDirectory}");
                var uri = Context.ContentResolver!.Insert(MediaStore.Downloads.ExternalContentUri!, values);
                if (uri is null) continue;
                await using var input = File.OpenRead(source);
                await using var output = Context.ContentResolver.OpenOutputStream(uri);
                if (output is null) continue;
                await input.CopyToAsync(output);
                exported++;
            }
            catch
            {
            }
        }
        return exported > 0 ? "Downloads/Wisp" : null;
    }

    /// <summary>
    /// Opens a received file in whichever installed app handles its MIME type.
    /// Files live in app-private external storage, so the URI must come from the
    /// FileProvider declared in the manifest.
    /// </summary>
    public static bool OpenWithSystem(string path)
    {
        try
        {
            var file = new Java.IO.File(path);
            if (!file.Exists()) return false;
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                Context, $"{Context.PackageName}.fileprovider", file);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, MimeTypeOf(path));
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            Context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"OpenWithSystem failed: {exception}");
            return false;
        }
    }

    private static string DisplayNameFor(global::Android.Net.Uri uri)    {
        try
        {
            using var cursor = Context.ContentResolver!.Query(uri,
                new[] { OpenableColumns.DisplayName }, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (index >= 0)
                {
                    var value = cursor.GetString(index);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch
        {
        }
        return uri.LastPathSegment ?? "shared-file";
    }

    private static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var value = name;
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        value = value.Trim().Trim('.');
        return string.IsNullOrEmpty(value) ? "file" : value;
    }

    private static string UniquePath(string directory, string name)
    {
        var target = Path.Combine(directory, name);
        if (!File.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; i < 10000; i++)
        {
            target = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static string MimeTypeOf(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension)
               ?? "application/octet-stream";
    }
}
