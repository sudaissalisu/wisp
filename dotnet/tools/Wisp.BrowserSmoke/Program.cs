using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Wisp.Core;

var root = Path.Combine(Path.GetTempPath(), "wisp-browser-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var server = new BrowserLinkServer(root, "Test Phone", "127.0.0.1");
var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var text = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
server.FileReceived += (_, path) => received.TrySetResult(path);
server.TextReceived += (_, value) => text.TrySetResult(value);
server.BrowserConnected += (_, _) => connected.TrySetResult(true);
server.Start();
Console.WriteLine(server.Url);

using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
var page = await http.GetStringAsync("/?t=" + server.Token);
if (!page.Contains("Send to this phone")) throw new Exception("page missing");
if (!connected.Task.IsCompleted) throw new Exception("connect event missing");
Console.WriteLine($"page ok ({page.Length} chars)");

var data = new byte[1024 * 1024];
Random.Shared.NextBytes(data);
var upload = await http.PostAsync($"/api/upload?t={server.Token}&name=hello%20world.bin", new ByteArrayContent(data));
if (!upload.IsSuccessStatusCode) throw new Exception("upload failed: " + await upload.Content.ReadAsStringAsync());
var uploadedPath = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
var expectedHash = Convert.ToHexString(SHA256.HashData(data));
var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(uploadedPath)));
if (expectedHash != actualHash) throw new Exception("upload hash mismatch");
Console.WriteLine("upload ok: " + Path.GetFileName(uploadedPath));

var filesJson = await http.GetStringAsync("/api/files?t=" + server.Token);
if (!filesJson.Contains("hello world.bin")) throw new Exception("file list missing upload");
var download = await http.GetByteArrayAsync($"/api/download?t={server.Token}&path=hello%20world.bin");
if (Convert.ToHexString(SHA256.HashData(download)) != expectedHash) throw new Exception("download hash mismatch");
Console.WriteLine("download ok: " + download.Length + " bytes");

var textResponse = await http.PostAsync("/api/text?t=" + server.Token, new StringContent("https://example.com", Encoding.UTF8, "text/plain"));
if (!textResponse.IsSuccessStatusCode) throw new Exception("text failed");
var receivedText = await text.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (receivedText != "https://example.com") throw new Exception("text mismatch");
Console.WriteLine("text ok: " + receivedText);

var bad = await http.GetAsync("/api/files?t=wrong");
if ((int)bad.StatusCode != 403) throw new Exception("token check failed: " + bad.StatusCode);
Console.WriteLine("auth ok");
server.Dispose();
Directory.Delete(root, true);
Console.WriteLine("ALL BROWSER TESTS PASSED");

var voiceRoot = Path.Combine(Path.GetTempPath(), "wisp-voice-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(voiceRoot);
void MakeFile(string name, DateTime when)
{
    var path = Path.Combine(voiceRoot, name);
    File.WriteAllText(path, "sample");
    File.SetLastWriteTime(path, when);
}
var today = DateTime.Today;
MakeFile("beach-video.mp4", today.AddDays(-1).AddHours(15));
MakeFile("invoice-june.pdf", new DateTime(today.Year, 6, 5, 10, 0, 0));
MakeFile("song.mp3", today.AddHours(9));
MakeFile("photo.jpg", today.AddDays(-1).AddHours(12));
var voiceFiles = await LibraryFile.ScanAsync(voiceRoot);
var sara = new PeerInfo { Id = "sara", Name = "Sara's phone", Online = true };
var john = new PeerInfo { Id = "john", Name = "John PC", Online = true };
var peers = new List<PeerInfo> { sara, john };
var (video, videoError) = VoiceCommandParser.Parse(
    "send the video from yesterday to Sara", peers, voiceFiles);
if (video is null || video.File.FileName != "beach-video.mp4" || video.Peer.Id != "sara")
    throw new Exception("voice video match failed: " + videoError);
var (invoice, invoiceError) = VoiceCommandParser.Parse(
    "share the invoice from June to John", peers, voiceFiles);
if (invoice is null || invoice.File.FileName != "invoice-june.pdf" || invoice.Peer.Id != "john")
    throw new Exception("voice invoice match failed: " + invoiceError);
var (song, songError) = VoiceCommandParser.Parse(
    "send the song to Sara", peers, voiceFiles);
if (song is null || song.File.FileName != "song.mp3")
    throw new Exception("voice song match failed: " + songError);
var (missing, _) = VoiceCommandParser.Parse(
    "send the photo to Nobody", peers, voiceFiles);
if (missing is not null) throw new Exception("voice should reject unknown peer");
Console.WriteLine("voice parser ok");
Directory.Delete(voiceRoot, true);
Console.WriteLine("ALL BROWSER AND VOICE TESTS PASSED");

var manifestJson = """
{
  "latest": "0.5.1",
  "name": "Wisp 0.5.1",
  "published": "2026-10-28",
  "notes": "test notes",
  "release_url": "https://github.com/sudaissalisu/wisp/releases/tag/v0.5.1",
  "android_url": "https://github.com/sudaissalisu/wisp/releases/download/v0.5.1/Wisp-android-0.5.1.apk",
  "windows_url": "https://github.com/sudaissalisu/wisp/releases/download/v0.5.1/Wisp-Setup-0.5.1.exe",
  "mandatory": false
}
""";
var manifest = UpdateService.ParseManifest(manifestJson);
if (manifest is null || manifest.Latest != "0.5.1" || manifest.Notes != "test notes")
    throw new Exception("update manifest parse failed");
if (!UpdateService.IsNewer("0.5.1", "0.5.0")) throw new Exception("newer version compare failed");
if (UpdateService.IsNewer("0.5.0", "0.5.0")) throw new Exception("equal version compare failed");
if (UpdateService.IsNewer("0.4.9", "0.5.0")) throw new Exception("older version compare failed");
if (!UpdateService.IsNewer("v0.5.1", "0.5.0")) throw new Exception("v-prefix compare failed");
if (UpdateService.ParseManifest("not json") is not null) throw new Exception("invalid manifest accepted");
var androidUrl = UpdateService.DownloadUrl(manifest, "android");
if (androidUrl is null || !androidUrl.EndsWith("Wisp-android-0.5.1.apk", StringComparison.Ordinal))
    throw new Exception("android asset URL failed");
var insecure = manifest with { AndroidUrl = "http://example.com/wisp.apk" };
if (UpdateService.DownloadUrl(insecure, "android") is not null)
    throw new Exception("insecure update URL accepted");
Console.WriteLine("update manifest checks ok");
Console.WriteLine("ALL BROWSER, VOICE AND UPDATE TESTS PASSED");
