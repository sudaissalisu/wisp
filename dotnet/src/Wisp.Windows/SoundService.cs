using System.Runtime.InteropServices;

namespace Wisp.Windows;

/// <summary>
/// Small UI sounds for peer presence changes and transfer outcomes, played via
/// the Windows multimedia wave-out API from wav files next to the executable.
/// </summary>
public sealed class SoundService
{
    private const int SND_FILENAME = 0x00020000;
    private const int SND_ASYNC = 0x0001;
    private const int SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, int fdwSound);

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _enabled = true;

    public SoundService(string soundDirectory)
    {
        foreach (var name in new[] { "connect", "disconnect", "success", "failure" })
        {
            var path = Path.Combine(soundDirectory, $"{name}.wav");
            if (File.Exists(path)) _files[name] = path;
        }
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public bool HasSounds
    {
        get { lock (_gate) return _files.Count > 0; }
    }

    public void Play(string name)
    {
        if (!Enabled) return;
        string? path;
        lock (_gate) _files.TryGetValue(name, out path);
        if (path is null) return;
        try
        {
            PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch
        {
        }
    }
}
