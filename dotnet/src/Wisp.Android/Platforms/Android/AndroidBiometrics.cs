using Android.App;
using AndroidX.Fragment.App;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using Microsoft.Maui.ApplicationModel;

namespace Wisp.Android;

/// <summary>
/// Biometric (or device-credential) gate used for app unlock and for accepting
/// incoming files. Uses AndroidX BiometricPrompt so it works consistently from
/// API 26 upward.
/// </summary>
public static class AndroidBiometrics
{
    public static bool IsAvailable
    {
        get
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity is null) return false;
                var manager = BiometricManager.From(activity);
                var authenticators = BiometricManager.Authenticators.BiometricWeak |
                                     BiometricManager.Authenticators.DeviceCredential;
                return manager.CanAuthenticate(authenticators) == BiometricManager.BiometricSuccess;
            }
            catch
            {
                return false;
            }
        }
    }

    public static Task<bool> AuthenticateAsync(string title, string subtitle)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsAvailable) return Task.FromResult(true);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var executor = ContextCompat.GetMainExecutor(activity);
                var callback = new PromptCallback(tcs);
                var prompt = new BiometricPrompt((FragmentActivity)activity, executor, callback);
                var info = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle(title)
                    .SetSubtitle(subtitle)
                    .SetAllowedAuthenticators(
                        BiometricManager.Authenticators.BiometricWeak |
                        BiometricManager.Authenticators.DeviceCredential)
                    .Build();
                prompt.Authenticate(info);
            }
            catch (Exception)
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private sealed class PromptCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _completion;

        public PromptCallback(TaskCompletionSource<bool> completion)
        {
            _completion = completion;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            _completion.TrySetResult(true);
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString)
        {
            _completion.TrySetResult(false);
        }

        public override void OnAuthenticationFailed()
        {
            // Keep the prompt open; the system will let the user retry.
        }
    }
}
