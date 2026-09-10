using LuminaMonitor.Core.Usb;

namespace LuminaMonitor.Core;

/// <summary>
/// Says when a phone is plugged in and when it is pulled out.
/// </summary>
/// <remarks>
/// The multiplexer keeps a socket open once it has been sent <c>Listen</c> and
/// pushes an <c>Attached</c> or <c>Detached</c> plist down it. That is the only
/// event source on this side of the cable, and it is worth having: without it
/// the window has nothing to do but retry the whole climb on a timer, and a
/// cable plugged in a second after a failed attempt would wait out the timer
/// for no reason.
///
/// <para>The watch is a loop rather than a subscription because the
/// multiplexer's own service restarts — with the Apple Devices app, with a
/// Windows update — and takes the socket with it. A watcher that gave up then
/// would leave the window deaf until it was restarted, which is exactly the
/// failure the retry loop exists to avoid.</para>
/// </remarks>
public sealed class DeviceWatcher : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>A device appeared on the multiplexer, with its usbmux device id.</summary>
    public event Action<long>? Attached;

    /// <summary>A device went away.</summary>
    public event Action<long>? Detached;

    /// <summary>Anything the loop could not do; it retries regardless.</summary>
    public event Action<string>? Trouble;

    public void Start() => _loop ??= Task.Run(() => WatchAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task WatchAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var mux = await UsbmuxClient.ConnectAsync();
                await mux.RequestAsync("Listen");
                while (!cancellation.IsCancellationRequested)
                {
                    var message = await mux.ReceiveAsync().WaitAsync(cancellation);
                    long id = message.GetValueOrDefault("DeviceID") is long value ? value : 0;
                    switch (message.GetValueOrDefault("MessageType")?.ToString())
                    {
                        case "Attached": Attached?.Invoke(id); break;
                        case "Detached": Detached?.Invoke(id); break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Trouble?.Invoke(exception.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellation); }
            catch (OperationCanceledException) { return; }
        }
    }
}
