using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Media;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.ApplicationModel;
using Wisp.Core;
using AndroidColor = Android.Graphics.Color;
using AndroidProgressBar = Android.Widget.ProgressBar;
using AndroidExifInterface = AndroidX.ExifInterface.Media.ExifInterface;
using AndroidOrientation = Android.Widget.Orientation;
using AndroidRectF = Android.Graphics.RectF;

namespace Wisp.Android;

/// <summary>
/// Native in-app previews for received images, videos and audio. Using Android
/// views directly keeps playback off the MAUI layout thread and avoids pulling a
/// second media stack into the app.
/// </summary>
internal static class MediaViewer
{
    private static Dialog? _current;

    public static bool CanPreview(string path) => LibraryFile.CanPreview(path);

    public static bool Open(string path)
    {
        try
        {
            var host = Platform.CurrentActivity;
            if (host is null || host.IsFinishing || !File.Exists(path)) return false;
            DismissCurrent();
            Dialog? dialog = LibraryFile.KindOf(path) switch
            {
                LibraryKind.Image => new ImageViewerDialog(host, path),
                LibraryKind.Video => new VideoPlayerDialog(host, path),
                LibraryKind.Audio => new AudioPlayerDialog(host, path),
                _ => null
            };
            if (dialog is null) return false;
            _current = dialog;
            dialog.DismissEvent += (_, _) =>
            {
                if (ReferenceEquals(_current, dialog)) _current = null;
            };
            dialog.Show();
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"MediaViewer failed: {exception}");
            return false;
        }
    }

    public static void DismissCurrent()
    {
        var dialog = _current;
        _current = null;
        try { dialog?.Dismiss(); } catch { }
    }
}

internal static class ViewerStyle
{
    public static readonly AndroidColor Background = AndroidColor.ParseColor("#0B0E12")!;
    public static readonly AndroidColor Surface = AndroidColor.ParseColor("#161B22")!;
    public static readonly AndroidColor Mint = AndroidColor.ParseColor("#45E3B4")!;
    public static readonly AndroidColor Text = AndroidColor.ParseColor("#F5F8F7")!;
    public static readonly AndroidColor Muted = AndroidColor.ParseColor("#9AA6A3")!;
    public static readonly AndroidColor Scrim = AndroidColor.ParseColor("#B3000000")!;

    public static int Dp(Context context, float value) =>
        (int)Math.Round(value * context.Resources!.DisplayMetrics!.Density);

    public static TextView Label(Context context, string text, float size, AndroidColor color,
        bool bold = false)
    {
        var view = new TextView(context);
        view.Text = text;
        view.SetTextSize(ComplexUnitType.Sp, size);
        view.SetTextColor(color);
        if (bold) view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        return view;
    }

    public static TextView Action(Context context, string text, AndroidColor color, Action click,
        string? background = null)
    {
        var view = Label(context, text, 13, color, bold: true);
        view.Gravity = GravityFlags.Center;
        view.SetPadding(Dp(context, 14), Dp(context, 8), Dp(context, 14), Dp(context, 8));
        if (background is not null)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(AndroidColor.ParseColor(background)!);
            drawable.SetCornerRadius(Dp(context, 12));
            view.Background = drawable;
        }
        view.Clickable = true;
        view.Click += (_, _) => click();
        return view;
    }

    public static string Duration(int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public static AndroidColor ForKind(LibraryKind kind) => kind switch
    {
        LibraryKind.Image => AndroidColor.ParseColor("#5B8DEF")!,
        LibraryKind.Video => AndroidColor.ParseColor("#9B6BFF")!,
        LibraryKind.Audio => AndroidColor.ParseColor("#FF9A62")!,
        _ => Mint
    };
}

internal abstract class ViewerDialog : Dialog
{
    private readonly AudioManager? _audioManager;
    private AudioFocusListener? _focusListener;

    protected ViewerDialog(Activity host, string path) : base(host)
    {
        Host = host;
        FilePath = path;
        RequestWindowFeature((int)WindowFeatures.NoTitle);
        SetCanceledOnTouchOutside(false);
        var window = Window;
        if (window is not null)
        {
            window.SetBackgroundDrawable(new ColorDrawable(ViewerStyle.Background));
            window.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
            window.SetStatusBarColor(ViewerStyle.Background);
            window.SetNavigationBarColor(ViewerStyle.Background);
        }
        _audioManager = host.GetSystemService(Context.AudioService) as AudioManager;
    }

    protected Activity Host { get; }
    protected string FilePath { get; }

    protected void RequestAudioFocus()
    {
        try
        {
            _focusListener = new AudioFocusListener();
            _audioManager?.RequestAudioFocus(_focusListener, global::Android.Media.Stream.Music,
                AudioFocus.Gain);
        }
        catch
        {
        }
    }

    protected void AbandonAudioFocus()
    {
        try
        {
            if (_focusListener is not null) _audioManager?.AbandonAudioFocus(_focusListener);
        }
        catch
        {
        }
        _focusListener = null;
    }

    private sealed class AudioFocusListener : Java.Lang.Object,
        AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) { }
    }

    public override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
    }

    protected override void OnStart()
    {
        base.OnStart();
        var window = Window;
        if (window is null) return;
        window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        window.SetGravity(GravityFlags.Fill);
        if (window.DecorView is not global::Android.Views.View decor) return;
        decor.SetBackgroundColor(ViewerStyle.Background);
        try
        {
            decor.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation |
                SystemUiFlags.ImmersiveSticky);
        }
        catch
        {
        }
        var contentId = Host.Resources?.GetIdentifier("content", "id", "android") ?? 0;
        if (contentId != 0 && decor.FindViewById(contentId) is global::Android.Views.View content)
        {
            content.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        }
    }

    protected LinearLayout TopBar(string title)
    {
        var top = new LinearLayout(Host)
        {
            Orientation = AndroidOrientation.Horizontal,
            };
        top.SetGravity(GravityFlags.CenterVertical);
        top.SetBackgroundColor(ViewerStyle.Scrim);
        top.SetPadding(ViewerStyle.Dp(Host, 8), ViewerStyle.Dp(Host, 8),
            ViewerStyle.Dp(Host, 12), ViewerStyle.Dp(Host, 8));

        var close = ViewerStyle.Action(Host, "✕", ViewerStyle.Text, Dismiss);
        close.SetTextSize(ComplexUnitType.Sp, 18);
        top.AddView(close, new LinearLayout.LayoutParams(
            ViewerStyle.Dp(Host, 44), ViewerStyle.Dp(Host, 44)));

        var label = ViewerStyle.Label(Host, title, 14, ViewerStyle.Text, bold: true);
        label.SetMaxLines(1);
        label.Ellipsize = TextUtils.TruncateAt.Middle;
        label.SetPadding(ViewerStyle.Dp(Host, 10), 0, 0, 0);
        var labelParams = new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f);
        top.AddView(label, labelParams);
        return top;
    }
}

internal sealed class ImageViewerDialog : ViewerDialog
{
    private readonly ZoomableImageView _image;
    private readonly AndroidProgressBar _spinner;
    private bool _closed;
    private int _loadToken;

    public ImageViewerDialog(Activity host, string path) : base(host, path)
    {
        var root = new FrameLayout(host);
        root.SetBackgroundColor(ViewerStyle.Background);

        _image = new ZoomableImageView(host);
        root.AddView(_image, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _spinner = new AndroidProgressBar(host);
        var spinnerParams = new FrameLayout.LayoutParams(
            ViewerStyle.Dp(host, 44), ViewerStyle.Dp(host, 44), GravityFlags.Center);
        root.AddView(_spinner, spinnerParams);

        var top = TopBar(System.IO.Path.GetFileName(path));
        root.AddView(top, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top));

        var hint = ViewerStyle.Label(host, "Pinch to zoom · double-tap to reset",
            11, ViewerStyle.Muted);
        hint.Gravity = GravityFlags.Center;
        hint.SetPadding(0, 0, 0, ViewerStyle.Dp(host, 18));
        root.AddView(hint, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom));

        SetContentView(root);
        DismissEvent += (_, _) =>
        {
            _closed = true;
            _loadToken++;
        };
        LoadImage();
    }

    private void LoadImage()
    {
        var token = ++_loadToken;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                bitmap = LoadBitmap(FilePath);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Image load failed: {exception}");
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_closed || token != _loadToken)
                {
                    bitmap?.Recycle();
                    return;
                }
                _spinner.Visibility = ViewStates.Gone;
                if (bitmap is null)
                {
                    Toast.MakeText(Host, "This image could not be opened.",
                        ToastLength.Short)?.Show();
                    Dismiss();
                    return;
                }
                _image.SetBitmap(bitmap);
            });
        });
    }

    private static Bitmap? LoadBitmap(string path)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

        var sample = 1;
        while (bounds.OutWidth / sample > 2048 || bounds.OutHeight / sample > 2048)
        {
            sample *= 2;
        }
        var options = new BitmapFactory.Options
        {
            InSampleSize = sample,
            InPreferredConfig = Bitmap.Config.Argb8888
        };
        var bitmap = BitmapFactory.DecodeFile(path, options);
        if (bitmap is null) return null;

        var rotation = ReadRotation(path);
        if (rotation == 0) return bitmap;
        var matrix = new Matrix();
        matrix.PostRotate(rotation);
        var rotated = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
        if (rotated is null) return null;
        if (!ReferenceEquals(rotated, bitmap)) bitmap.Recycle();
        return rotated;
    }

    private static int ReadRotation(string path)
    {
        try
        {
            using var exif = new AndroidExifInterface(path);
            var orientation = exif.GetAttributeInt("Orientation", 1);
            return orientation switch
            {
                3 => 180,
                6 => 90,
                8 => 270,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }
}

internal sealed class ZoomableImageView : ImageView
{
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly GestureDetector _gestureDetector;
    private readonly Matrix _matrix = new();
    private Bitmap? _bitmap;
    private float _minimumScale = 1f;
    private bool _fitted;

    public ZoomableImageView(Context context) : base(context)
    {
        SetScaleType(ScaleType.Matrix);
        _scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
        _gestureDetector = new GestureDetector(context, new GestureListener(this));
        Clickable = true;
        SetBackgroundColor(ViewerStyle.Background);
    }

    public void SetBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap;
        SetImageBitmap(bitmap);
        _fitted = false;
        if (Width > 0 && Height > 0) Fit();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (!_fitted) Fit();
        else Clamp();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _scaleDetector.OnTouchEvent(e);
        _gestureDetector.OnTouchEvent(e);
        return true;
    }

    private void Fit()
    {
        if (_bitmap is null || Width <= 0 || Height <= 0) return;
        var scale = Math.Min((float)Width / _bitmap.Width, (float)Height / _bitmap.Height);
        _minimumScale = scale;
        _matrix.Reset();
        _matrix.PostScale(scale, scale);
        _matrix.PostTranslate(
            (Width - _bitmap.Width * scale) / 2f,
            (Height - _bitmap.Height * scale) / 2f);
        Apply();
        _fitted = true;
    }

    internal bool HandleScale(ScaleGestureDetector detector)
    {
        if (_bitmap is null) return false;
        var current = _matrix.MapRadius(1f);
        if (current <= 0) return false;
        var target = Math.Clamp(current * detector.ScaleFactor,
            _minimumScale, _minimumScale * 5f);
        var factor = target / current;
        _matrix.PostScale(factor, factor, detector.FocusX, detector.FocusY);
        Clamp();
        Apply();
        return true;
    }

    internal void HandlePan(float dx, float dy)
    {
        if (_bitmap is null || _scaleDetector.IsInProgress) return;
        _matrix.PostTranslate(dx, dy);
        Clamp();
        Apply();
    }

    internal void HandleDoubleTap(float x, float y)
    {
        if (_bitmap is null) return;
        var current = _matrix.MapRadius(1f);
        if (current > _minimumScale * 1.15f)
        {
            Fit();
            return;
        }
        _matrix.PostScale(2.4f, 2.4f, x, y);
        Clamp();
        Apply();
    }

    private void Clamp()
    {
        if (_bitmap is null) return;
        var rect = new AndroidRectF(0, 0, _bitmap.Width, _bitmap.Height);
        _matrix.MapRect(rect);
        float dx = 0;
        float dy = 0;
        if (rect.Width() <= Width) dx = (Width - rect.Width()) / 2f - rect.Left;
        else if (rect.Left > 0) dx = -rect.Left;
        else if (rect.Right < Width) dx = Width - rect.Right;
        if (rect.Height() <= Height) dy = (Height - rect.Height()) / 2f - rect.Top;
        else if (rect.Top > 0) dy = -rect.Top;
        else if (rect.Bottom < Height) dy = Height - rect.Bottom;
        if (dx != 0 || dy != 0) _matrix.PostTranslate(dx, dy);
    }

    private void Apply()
    {
        ImageMatrix = new Matrix(_matrix);
    }

    private sealed class ScaleListener : Java.Lang.Object, ScaleGestureDetector.IOnScaleGestureListener
    {
        private readonly ZoomableImageView _view;

        public ScaleListener(ZoomableImageView view) => _view = view;
        public bool OnScale(ScaleGestureDetector? detector) =>
            detector is not null && _view.HandleScale(detector);
        public bool OnScaleBegin(ScaleGestureDetector? detector) => true;
        public void OnScaleEnd(ScaleGestureDetector? detector) { }
    }

    private sealed class GestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly ZoomableImageView _view;

        public GestureListener(ZoomableImageView view) => _view = view;

        public override bool OnDown(MotionEvent? e) => true;

        public override bool OnScroll(MotionEvent? e1, MotionEvent? e2,
            float distanceX, float distanceY)
        {
            _view.HandlePan(-distanceX, -distanceY);
            return true;
        }

        public override bool OnDoubleTap(MotionEvent? e)
        {
            if (e is not null) _view.HandleDoubleTap(e.GetX(), e.GetY());
            return true;
        }
    }
}

internal sealed class VideoPlayerDialog : ViewerDialog
{
    private readonly VideoView _video;
    private readonly SeekBar _seek;
    private readonly TextView _currentLabel;
    private readonly TextView _totalLabel;
    private readonly TextView _speedLabel;
    private readonly TextView _muteLabel;
    private readonly TextView _playButton;
    private readonly LinearLayout _controls;
    private readonly System.Threading.Timer _ticker;
    private bool _userSeeking;
    private bool _muted;
    private bool _closed;
    private int _duration;
    private float _speed = 1f;
    private int _hideToken;
    private MediaPlayer? _videoPlayer;

    public VideoPlayerDialog(Activity host, string path) : base(host, path)
    {
        var root = new FrameLayout(host);
        root.SetBackgroundColor(ViewerStyle.Background);

        _video = new VideoView(host);
        _video.Clickable = true;
        _video.Focusable = true;
        root.AddView(_video, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _controls = new LinearLayout(host) { Orientation = AndroidOrientation.Vertical };
        _controls.SetBackgroundColor(ViewerStyle.Scrim);
        var controlParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom);
        controlParams.SetMargins(0, 0, 0, ViewerStyle.Dp(host, 10));

        _seek = new SeekBar(host);
        _seek.Max = 1;
        _currentLabel = ViewerStyle.Label(host, "0:00", 11, ViewerStyle.Text);
        _totalLabel = ViewerStyle.Label(host, "0:00", 11, ViewerStyle.Text);
        _totalLabel.Gravity = GravityFlags.End;
        var timeRow = new LinearLayout(host) { Orientation = AndroidOrientation.Horizontal };
        timeRow.AddView(_currentLabel, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));
        timeRow.AddView(_totalLabel, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));

        var buttonRow = new LinearLayout(host)
        {
            Orientation = AndroidOrientation.Horizontal
        };
        buttonRow.SetGravity(GravityFlags.CenterVertical);
        var back = ViewerStyle.Action(host, "−10s", ViewerStyle.Text,
            () => Skip(-10_000));
        _playButton = ViewerStyle.Action(host, "▶", ViewerStyle.Mint, TogglePlayback, "#1F45E3B4");
        _playButton.SetTextSize(ComplexUnitType.Sp, 18);
        var forward = ViewerStyle.Action(host, "+10s", ViewerStyle.Text,
            () => Skip(10_000));
        _speedLabel = ViewerStyle.Action(host, "1.0×", ViewerStyle.Text, CycleSpeed);
        _muteLabel = ViewerStyle.Action(host, "Mute", ViewerStyle.Text, ToggleMute);
        buttonRow.AddView(back, new LinearLayout.LayoutParams(
            0, ViewerStyle.Dp(host, 42), 1f));
        buttonRow.AddView(_playButton, new LinearLayout.LayoutParams(
            0, ViewerStyle.Dp(host, 48), 1f));
        buttonRow.AddView(forward, new LinearLayout.LayoutParams(
            0, ViewerStyle.Dp(host, 42), 1f));
        buttonRow.AddView(_speedLabel, new LinearLayout.LayoutParams(
            0, ViewerStyle.Dp(host, 42), 1f));
        buttonRow.AddView(_muteLabel, new LinearLayout.LayoutParams(
            0, ViewerStyle.Dp(host, 42), 1f));

        var controlStack = new LinearLayout(host) { Orientation = AndroidOrientation.Vertical };
        controlStack.SetPadding(ViewerStyle.Dp(host, 14), 0,
            ViewerStyle.Dp(host, 14), ViewerStyle.Dp(host, 6));
        controlStack.AddView(_seek, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewerStyle.Dp(host, 36)));
        controlStack.AddView(timeRow);
        controlStack.AddView(buttonRow);
        _controls.AddView(controlStack);
        root.AddView(_controls, controlParams);

        var top = TopBar(System.IO.Path.GetFileName(path));
        root.AddView(top, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top));

        SetContentView(root);
        _video.SetOnPreparedListener(new PreparedListener(this));
        _video.SetOnCompletionListener(new CompletionListener(this));
        _video.SetOnErrorListener(new ErrorListener(this));
        _video.Click += (_, _) => ToggleControls();
        _seek.SetOnSeekBarChangeListener(new SeekListener(this));
        _ticker = new System.Threading.Timer(_ => MainThread.BeginInvokeOnMainThread(Tick),
            null, 250, 500);
        DismissEvent += (_, _) =>
        {
            _closed = true;
            _hideToken++;
            _ticker.Dispose();
            try { _video.StopPlayback(); } catch { }
            AbandonAudioFocus();
        };
        RequestAudioFocus();
        _video.SetVideoPath(path);
    }

    private void OnPrepared(MediaPlayer? mediaPlayer)
    {
        if (_closed) return;
        _videoPlayer = mediaPlayer;
        _duration = Math.Max(0, _video.Duration);
        _seek.Max = Math.Max(1, _duration);
        _totalLabel.Text = ViewerStyle.Duration(_duration);
        ApplySpeed(_speed);
        _videoPlayer?.SetVolume(_muted ? 0f : 1f, _muted ? 0f : 1f);
        _video.Start();
        ShowControls(scheduleHide: true);
    }

    private void OnCompletion()
    {
        if (_closed) return;
        _playButton.Text = "▶";
        _seek.Progress = 0;
        _currentLabel.Text = "0:00";
        try { _video.SeekTo(0); } catch { }
        ShowControls(scheduleHide: false);
    }

    private void TogglePlayback()
    {
        if (_video.IsPlaying)
        {
            _video.Pause();
            _playButton.Text = "▶";
            ShowControls(scheduleHide: false);
        }
        else
        {
            _video.Start();
            _playButton.Text = "❚❚";
            ShowControls(scheduleHide: true);
        }
    }

    private void Skip(int milliseconds)
    {
        var target = Math.Clamp(_video.CurrentPosition + milliseconds, 0, _duration);
        _video.SeekTo(target);
        _seek.Progress = target;
        _currentLabel.Text = ViewerStyle.Duration(target);
        ShowControls(scheduleHide: _video.IsPlaying);
    }

    private void CycleSpeed()
    {
        var speeds = new[] { 1f, 1.25f, 1.5f, 2f, 0.75f };
        var index = Array.IndexOf(speeds, _speed);
        _speed = speeds[(index + 1 + speeds.Length) % speeds.Length];
        _speedLabel.Text = $"{_speed:0.0#}×";
        ApplySpeed(_speed);
        ShowControls(scheduleHide: _video.IsPlaying);
    }

    private void ApplySpeed(float speed)
    {
        try
        {
            if (_videoPlayer is null) return;
            var wasPlaying = _video.IsPlaying;
            var parameters = new PlaybackParams();
            parameters.SetSpeed(speed);
            _videoPlayer.PlaybackParams = parameters;
            if (!wasPlaying) _video.Pause();
        }
        catch
        {
        }
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        _videoPlayer?.SetVolume(_muted ? 0f : 1f, _muted ? 0f : 1f);
        _muteLabel.Text = _muted ? "Sound" : "Mute";
        ShowControls(scheduleHide: _video.IsPlaying);
    }

    private void Tick()
    {
        if (_closed || _video is null) return;
        try
        {
            if (!_userSeeking && _duration > 0)
            {
                var position = Math.Clamp(_video.CurrentPosition, 0, _duration);
                _seek.Progress = position;
                _currentLabel.Text = ViewerStyle.Duration(position);
            }
        }
        catch
        {
        }
    }

    private void ToggleControls()
    {
        if (_controls.Visibility == ViewStates.Visible) HideControls();
        else ShowControls(scheduleHide: _video.IsPlaying);
    }

    private void ShowControls(bool scheduleHide)
    {
        _hideToken++;
        _controls.Visibility = ViewStates.Visible;
        _playButton.Text = _video.IsPlaying ? "❚❚" : "▶";
        if (scheduleHide) ScheduleHide();
    }

    private void HideControls()
    {
        if (_userSeeking) return;
        _controls.Visibility = ViewStates.Gone;
    }

    private async void ScheduleHide()
    {
        var token = ++_hideToken;
        await Task.Delay(3000);
        if (token == _hideToken && !_closed && _video.IsPlaying && !_userSeeking)
            _controls.Visibility = ViewStates.Gone;
    }

    private sealed class SeekListener : Java.Lang.Object, SeekBar.IOnSeekBarChangeListener
    {
        private readonly VideoPlayerDialog _dialog;

        public SeekListener(VideoPlayerDialog dialog) => _dialog = dialog;

        public void OnProgressChanged(SeekBar? seekBar, int progress, bool fromUser)
        {
            if (!fromUser) return;
            _dialog._currentLabel.Text = ViewerStyle.Duration(progress);
        }

        public void OnStartTrackingTouch(SeekBar? seekBar)
        {
            _dialog._userSeeking = true;
        }

        public void OnStopTrackingTouch(SeekBar? seekBar)
        {
            _dialog._userSeeking = false;
            _dialog._video.SeekTo(seekBar?.Progress ?? 0);
            _dialog.ShowControls(_dialog._video.IsPlaying);
        }
    }

    private sealed class PreparedListener : Java.Lang.Object, MediaPlayer.IOnPreparedListener
    {
        private readonly VideoPlayerDialog _dialog;

        public PreparedListener(VideoPlayerDialog dialog) => _dialog = dialog;
        public void OnPrepared(MediaPlayer? mediaPlayer) => _dialog.OnPrepared(mediaPlayer);
    }

    private sealed class CompletionListener : Java.Lang.Object, MediaPlayer.IOnCompletionListener
    {
        private readonly VideoPlayerDialog _dialog;

        public CompletionListener(VideoPlayerDialog dialog) => _dialog = dialog;
        public void OnCompletion(MediaPlayer? mediaPlayer) => _dialog.OnCompletion();
    }

    private sealed class ErrorListener : Java.Lang.Object, MediaPlayer.IOnErrorListener
    {
        private readonly VideoPlayerDialog _dialog;

        public ErrorListener(VideoPlayerDialog dialog) => _dialog = dialog;

        public bool OnError(MediaPlayer? mediaPlayer, MediaError what, int extra)
        {
            Toast.MakeText(_dialog.Host, "This video could not be played.",
                ToastLength.Short)?.Show();
            _dialog.Dismiss();
            return true;
        }
    }
}

internal sealed class AudioPlayerDialog : ViewerDialog
{
    private readonly MediaPlayer _player;
    private readonly SeekBar _seek;
    private readonly TextView _currentLabel;
    private readonly TextView _totalLabel;
    private readonly TextView _speedLabel;
    private readonly TextView _muteLabel;
    private readonly TextView _playButton;
    private readonly System.Threading.Timer _ticker;
    private bool _userSeeking;
    private bool _muted;
    private bool _closed;
    private int _duration;
    private float _speed = 1f;

    public AudioPlayerDialog(Activity host, string path) : base(host, path)
    {
        var root = new LinearLayout(host)
        {
            Orientation = AndroidOrientation.Vertical,
            };
        root.SetBackgroundColor(ViewerStyle.Background);
        root.SetGravity(GravityFlags.CenterHorizontal);

        var top = TopBar(System.IO.Path.GetFileName(path));
        root.AddView(top, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var spacer = new Space(host);
        root.AddView(spacer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var cover = new TextView(host)
        {
            Text = "\uE3A1",
            Background = Rounded(ViewerStyle.ForKind(LibraryKind.Audio),
                ViewerStyle.Dp(host, 32))
        };
        cover.Gravity = GravityFlags.Center;
        cover.SetTextSize(ComplexUnitType.Sp, 54);
        cover.SetTextColor(ViewerStyle.Background);
        var coverSize = ViewerStyle.Dp(host, 132);
        var coverParams = new LinearLayout.LayoutParams(coverSize, coverSize)
        {
            };
        root.AddView(cover, coverParams);

        var title = ViewerStyle.Label(host, System.IO.Path.GetFileName(path), 18,
            ViewerStyle.Text, bold: true);
        title.Gravity = GravityFlags.Center;
        title.SetPadding(ViewerStyle.Dp(host, 24), ViewerStyle.Dp(host, 22),
            ViewerStyle.Dp(host, 24), ViewerStyle.Dp(host, 2));
        root.AddView(title);

        var subtitle = ViewerStyle.Label(host, "Received with Wisp", 12, ViewerStyle.Muted);
        subtitle.Gravity = GravityFlags.Center;
        root.AddView(subtitle);

        _seek = new SeekBar(host);
        _seek.Max = 1;
        var seekParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewerStyle.Dp(host, 36))
        {
            LeftMargin = ViewerStyle.Dp(host, 24),
            RightMargin = ViewerStyle.Dp(host, 24),
            TopMargin = ViewerStyle.Dp(host, 22)
        };
        root.AddView(_seek, seekParams);

        _currentLabel = ViewerStyle.Label(host, "0:00", 11, ViewerStyle.Muted);
        _totalLabel = ViewerStyle.Label(host, "0:00", 11, ViewerStyle.Muted);
        _totalLabel.Gravity = GravityFlags.End;
        var timeRow = new LinearLayout(host) { Orientation = AndroidOrientation.Horizontal };
        timeRow.SetPadding(ViewerStyle.Dp(host, 30), 0, ViewerStyle.Dp(host, 30), 0);
        timeRow.AddView(_currentLabel, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));
        timeRow.AddView(_totalLabel, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(timeRow);

        var row = new LinearLayout(host)
        {
            Orientation = AndroidOrientation.Horizontal
        };
        row.SetGravity(GravityFlags.CenterHorizontal);
        row.SetPadding(0, ViewerStyle.Dp(host, 12), 0, 0);
        var back = ViewerStyle.Action(host, "−10s", ViewerStyle.Text, () => Skip(-10_000));
        _playButton = ViewerStyle.Action(host, "▶", ViewerStyle.Mint, TogglePlayback, "#1F45E3B4");
        _playButton.SetTextSize(ComplexUnitType.Sp, 18);
        var forward = ViewerStyle.Action(host, "+10s", ViewerStyle.Text, () => Skip(10_000));
        row.AddView(back, new LinearLayout.LayoutParams(
            ViewerStyle.Dp(host, 64), ViewerStyle.Dp(host, 48)));
        row.AddView(_playButton, new LinearLayout.LayoutParams(
            ViewerStyle.Dp(host, 64), ViewerStyle.Dp(host, 56)));
        row.AddView(forward, new LinearLayout.LayoutParams(
            ViewerStyle.Dp(host, 64), ViewerStyle.Dp(host, 48)));
        root.AddView(row);

        var secondary = new LinearLayout(host)
        {
            Orientation = AndroidOrientation.Horizontal
        };
        secondary.SetGravity(GravityFlags.CenterHorizontal);
        secondary.SetPadding(0, ViewerStyle.Dp(host, 14), 0, 0);
        _speedLabel = ViewerStyle.Action(host, "1.0×", ViewerStyle.Muted, CycleSpeed);
        _muteLabel = ViewerStyle.Action(host, "Mute", ViewerStyle.Muted, ToggleMute);
        secondary.AddView(_speedLabel);
        secondary.AddView(_muteLabel);
        root.AddView(secondary);

        var bottomSpacer = new Space(host);
        root.AddView(bottomSpacer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1.2f));

        SetContentView(root);
        _player = new MediaPlayer();
        _player.SetOnCompletionListener(new CompletionListener(this));
        _player.SetOnErrorListener(new ErrorListener(this));
        _ticker = new System.Threading.Timer(_ => MainThread.BeginInvokeOnMainThread(Tick),
            null, 250, 500);
        DismissEvent += (_, _) =>
        {
            _closed = true;
            _ticker.Dispose();
            try
            {
                _player.Stop();
                _player.Release();
                _player.Dispose();
            }
            catch
            {
            }
            AbandonAudioFocus();
        };
        _seek.SetOnSeekBarChangeListener(new SeekListener(this));

        try
        {
            _player.SetDataSource(path);
            _player.Prepare();
            _duration = Math.Max(0, _player.Duration);
            _seek.Max = Math.Max(1, _duration);
            _totalLabel.Text = ViewerStyle.Duration(_duration);
            RequestAudioFocus();
            ApplySpeed(_speed);
            _player.Start();
            _playButton.Text = "❚❚";
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Audio open failed: {exception}");
            Toast.MakeText(host, "This audio file could not be played.",
                ToastLength.Short)?.Show();
        }
    }

    private static GradientDrawable Rounded(AndroidColor color, float radius)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(radius);
        return drawable;
    }

    private void TogglePlayback()
    {
        try
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _playButton.Text = "▶";
            }
            else
            {
                _player.Start();
                _playButton.Text = "❚❚";
            }
        }
        catch
        {
        }
    }

    private void Skip(int milliseconds)
    {
        var target = Math.Clamp(_player.CurrentPosition + milliseconds, 0, _duration);
        _player.SeekTo(target);
        _seek.Progress = target;
        _currentLabel.Text = ViewerStyle.Duration(target);
    }

    private void CycleSpeed()
    {
        var speeds = new[] { 1f, 1.25f, 1.5f, 2f, 0.75f };
        var index = Array.IndexOf(speeds, _speed);
        _speed = speeds[(index + 1 + speeds.Length) % speeds.Length];
        _speedLabel.Text = $"{_speed:0.0#}×";
        ApplySpeed(_speed);
    }

    private void ApplySpeed(float speed)
    {
        try
        {
            var wasPlaying = _player.IsPlaying;
            var parameters = new PlaybackParams();
            parameters.SetSpeed(speed);
            _player.PlaybackParams = parameters;
            if (!wasPlaying) _player.Pause();
        }
        catch
        {
        }
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        _player.SetVolume(_muted ? 0f : 1f, _muted ? 0f : 1f);
        _muteLabel.Text = _muted ? "Sound" : "Mute";
    }

    private void Tick()
    {
        if (_closed) return;
        try
        {
            if (_userSeeking) return;
            var position = Math.Clamp(_player.CurrentPosition, 0, _duration);
            _seek.Progress = position;
            _currentLabel.Text = ViewerStyle.Duration(position);
        }
        catch
        {
        }
    }

    private sealed class SeekListener : Java.Lang.Object, SeekBar.IOnSeekBarChangeListener
    {
        private readonly AudioPlayerDialog _dialog;

        public SeekListener(AudioPlayerDialog dialog) => _dialog = dialog;

        public void OnProgressChanged(SeekBar? seekBar, int progress, bool fromUser)
        {
            if (!fromUser) return;
            _dialog._currentLabel.Text = ViewerStyle.Duration(progress);
        }

        public void OnStartTrackingTouch(SeekBar? seekBar)
        {
            _dialog._userSeeking = true;
        }

        public void OnStopTrackingTouch(SeekBar? seekBar)
        {
            _dialog._userSeeking = false;
            try { _dialog._player.SeekTo(seekBar?.Progress ?? 0); } catch { }
        }
    }

    private sealed class CompletionListener : Java.Lang.Object, MediaPlayer.IOnCompletionListener
    {
        private readonly AudioPlayerDialog _dialog;

        public CompletionListener(AudioPlayerDialog dialog) => _dialog = dialog;

        public void OnCompletion(MediaPlayer? mediaPlayer)
        {
            _dialog._playButton.Text = "▶";
            _dialog._seek.Progress = 0;
            _dialog._currentLabel.Text = "0:00";
            try { _dialog._player.SeekTo(0); } catch { }
        }
    }

    private sealed class ErrorListener : Java.Lang.Object, MediaPlayer.IOnErrorListener
    {
        private readonly AudioPlayerDialog _dialog;

        public ErrorListener(AudioPlayerDialog dialog) => _dialog = dialog;

        public bool OnError(MediaPlayer? mediaPlayer, MediaError what, int extra)
        {
            Toast.MakeText(_dialog.Host, "This audio file could not be played.",
                ToastLength.Short)?.Show();
            _dialog.Dismiss();
            return true;
        }
    }
}
