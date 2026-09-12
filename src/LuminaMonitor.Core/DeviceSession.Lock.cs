using System.Diagnostics;
using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core;

/// <summary>
/// The phone's lock state, read from the phone rather than guessed from the
/// picture.
/// </summary>
/// <remarks>
/// A screen going dark is not the same as a phone being locked, and it is the
/// lock the person needs to know about — that is when the passcode keypad iOS
/// keeps out of the mirror is what they are looking for. So the answer comes
/// from <see cref="NotificationProxy"/>: the phone posts
/// <c>lockcomplete</c> when it locks and <c>lockstate</c> on every change, and
/// their order tells lock from unlock. A lock is a <c>lockstate</c> with a
/// <c>lockcomplete</c> right behind it; an unlock is a <c>lockstate</c> that
/// stands alone. So <c>lockcomplete</c> latches locked, and a <c>lockstate</c>
/// that no <c>lockcomplete</c> follows within a short grace unlatches it.
/// </remarks>
public sealed partial class DeviceSession
{
    /// <summary>The phone's screen was locked, or unlocked — read from the phone itself.</summary>
    public event Action<bool>? PhoneLockChanged;

    /// <summary>Whether the lock notifier is running, so a caller knows the signal is trustworthy.</summary>
    public bool LockServiceActive => _lockNotifier is not null;

    /// <summary>Last known lock state; false until the first event or when unknown.</summary>
    public bool PhoneLocked { get; private set; }

    /// <summary>How long a lone <c>lockstate</c> waits for a <c>lockcomplete</c> before it counts as an unlock.</summary>
    private static readonly TimeSpan UnlockGrace = TimeSpan.FromMilliseconds(400);

    private long _lastLockCompleteTicks;

    private async Task StartLockNotifierAsync(long deviceId, LockdownClient lockdown, PairRecord record)
    {
        try
        {
            var notifier = await NotificationProxy.OpenAsync(deviceId, lockdown, record);
            await notifier.ObserveAsync(NotificationProxy.LockComplete);
            await notifier.ObserveAsync(NotificationProxy.LockState);
            notifier.Received += OnLockNotification;
            notifier.Start();
            _lockNotifier = notifier;
            Info("Etat de verrouillage suivi par le telephone (notification_proxy).");
        }
        catch (Exception exception)
        {
            // Not fatal: the mirror does not need this, and the banner has its
            // dark-screen fallback. Say why once and carry on.
            Info($"Suivi du verrouillage indisponible ({exception.Message}) — bandeau au juge du debit d'images.");
        }
    }

    private void OnLockNotification(string name)
    {
        if (name == NotificationProxy.LockComplete)
        {
            Volatile.Write(ref _lastLockCompleteTicks, Stopwatch.GetTimestamp());
            SetPhoneLocked(true);
        }
        else if (name == NotificationProxy.LockState)
        {
            // Wait out the grace: a lock's lockstate is chased by a lockcomplete
            // that will have set the state already, and this fires for nothing;
            // an unlock's lockstate is alone, and this is what catches it.
            _ = Task.Delay(UnlockGrace).ContinueWith(_ =>
            {
                double sinceMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastLockCompleteTicks))
                    * 1000.0 / Stopwatch.Frequency;
                if (sinceMs > UnlockGrace.TotalMilliseconds)
                    SetPhoneLocked(false);
            }, TaskScheduler.Default);
        }
    }

    private void SetPhoneLocked(bool locked)
    {
        if (locked == PhoneLocked)
            return;
        PhoneLocked = locked;
        Info(locked ? "Telephone verrouille." : "Telephone deverrouille.");
        PhoneLockChanged?.Invoke(locked);
    }

    private void StopLockNotifier()
    {
        _lockNotifier?.Dispose();
        _lockNotifier = null;
        PhoneLocked = false;
    }
}
