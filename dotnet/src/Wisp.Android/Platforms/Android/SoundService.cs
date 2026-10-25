using Android.Content;
using Android.Media;
using AndroidApplication = Android.App.Application;

namespace Wisp.Android;

/// <summary>
/// Short UI sounds for peer connect/disconnect and transfer outcomes, played
/// through SoundPool. All clips ship as raw resources.
/// </summary>
public static class SoundService
{
    private const string Tag = "WispSound";

    private static SoundPool? _pool;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> SampleIds = new();
    private static readonly HashSet<int> Loaded = [];
    private static bool _enabled = true;

    public static void SetEnabled(bool enabled)
    {
        lock (Gate) _enabled = enabled;
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_pool is not null) return;
            try
            {
                var attributes = new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.AssistanceSonification)
                    .SetContentType(AudioContentType.Sonification)
                    .Build();
                _pool = new SoundPool.Builder()
                    .SetMaxStreams(3)
                    .SetAudioAttributes(attributes)
                    .Build();
                _pool.LoadComplete += (_, _) => { };
                foreach (var name in new[] { "connect", "disconnect", "success", "failure" })
                {
                    var id = _pool.Load(AndroidApplication.Context, ResourceIdFor(name), 1);
                    SampleIds[name] = id;
                }
            }
            catch
            {
                _pool = null;
            }
        }
    }

    public static void Play(string name)
    {
        SoundPool? pool;
        int sample;
        lock (Gate)
        {
            if (!_enabled || _pool is null) return;
            if (!SampleIds.TryGetValue(name, out sample)) return;
            pool = _pool;
        }
        try
        {
            pool.Play(sample, 0.9f, 0.9f, 1, 0, 1f);
        }
        catch
        {
        }
    }

    private static int ResourceIdFor(string name)
    {
        var context = AndroidApplication.Context;
        var id = context.Resources!.GetIdentifier(name, "raw", context.PackageName);
        return id > 0 ? id : 0;
    }
}
