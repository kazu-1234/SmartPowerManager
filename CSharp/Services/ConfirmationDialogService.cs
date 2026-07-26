using Microsoft.UI.Dispatching;
using SmartPowerManager.Views;

namespace SmartPowerManager.Services;

public sealed class ConfirmationDialogService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private CountdownConfirmWindow? _activeWindow;

    public ConfirmationDialogService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public Task<bool> ShowConfirmationAsync(string action, string triggerLabel)
    {
        return ShowInternalAsync(action, triggerLabel, isPreview: false, playBeep: true);
    }

    private Task<bool> ShowInternalAsync(string action, string triggerLabel, bool isPreview, bool playBeep)
    {
        var tcs = new TaskCompletionSource<bool>();

        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_activeWindow != null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var window = new CountdownConfirmWindow(action, triggerLabel, isPreview, playBeep);
                _activeWindow = window;
                window.StartCountdown();
                bool result = await window.Result;
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _activeWindow = null;
            }
        });

        return tcs.Task;
    }
}
