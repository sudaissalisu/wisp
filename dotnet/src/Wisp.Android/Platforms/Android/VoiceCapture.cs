using Android.App;
using Android.Content;
using Android.OS;
using Android.Speech;

namespace Wisp.Android;

/// <summary>
/// Thin wrapper around Android's SpeechRecognizer. The recognizer is created
/// only while the user is speaking; nothing is recorded to disk and Wisp never
/// uploads the audio itself. Android may satisfy the request with an on-device
/// or network-backed recognizer depending on the device.
/// </summary>
internal static class VoiceCapture
{
    public static async Task<string?> ListenAsync(Activity host,
        TimeSpan? timeout = null)
    {
        if (!SpeechRecognizer.IsRecognitionAvailable(host)) return null;
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SpeechRecognizer? recognizer = null;
        try
        {
            recognizer = SpeechRecognizer.CreateSpeechRecognizer(host);
            if (recognizer is null) return null;
            recognizer.SetRecognitionListener(new Listener(completion));

            var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            intent.PutExtra(RecognizerIntent.ExtraLanguageModel,
                RecognizerIntent.LanguageModelFreeForm);
            intent.PutExtra(RecognizerIntent.ExtraPrompt, "Say what to send and to whom");
            intent.PutExtra(RecognizerIntent.ExtraMaxResults, 3);
            intent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);
            intent.PutExtra(RecognizerIntent.ExtraCallingPackage, host.PackageName);
            recognizer.StartListening(intent);

            var winner = await Task.WhenAny(completion.Task,
                Task.Delay(timeout ?? TimeSpan.FromSeconds(18))).ConfigureAwait(true);
            if (winner != completion.Task) return null;
            return await completion.Task.ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { recognizer?.StopListening(); } catch { }
            try { recognizer?.Cancel(); } catch { }
            try { recognizer?.Destroy(); } catch { }
            try { recognizer?.Dispose(); } catch { }
        }
    }

    private sealed class Listener : Java.Lang.Object,
        IRecognitionListener
    {
        private readonly TaskCompletionSource<string?> _completion;

        public Listener(TaskCompletionSource<string?> completion) =>
            _completion = completion;

        public void OnReadyForSpeech(Bundle? parameters) { }
        public void OnBeginningOfSpeech() { }
        public void OnRmsChanged(float rmsDb) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }

        public void OnError(SpeechRecognizerError error) =>
            _completion.TrySetResult(null);

        public void OnResults(Bundle? results)
        {
            var texts = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            _completion.TrySetResult(texts is { Count: > 0 } ? texts[0] : null);
        }

        public void OnPartialResults(Bundle? partialResults) { }
        public void OnEvent(int eventType, Bundle? parameters) { }
    }
}
