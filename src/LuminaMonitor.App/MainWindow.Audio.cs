using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LuminaMonitor.Core.Audio;

namespace LuminaMonitor.App;

/// <summary>
/// The Audio panel: the phone's sound on this PC over Bluetooth, and this PC's
/// microphone for a call the phone routes here.
/// </summary>
/// <remarks>
/// <para>Two different mechanisms behind one button, and the panel keeps them
/// apart because they behave differently. The phone's sound (A2DP) flows only
/// while this window holds an <see cref="PhoneAudioLink"/> open — nothing on a
/// stock Windows does, which is why nothing came through before — and Windows
/// plays it on its default output; there is no output parameter to give it.
/// The microphone (hands-free profile) exists only during a call the person
/// sends to this PC, and bridging it is what takes the phone's own microphone
/// away, so it starts on an explicit click and never on its own.
/// docs/BLUETOOTH_AUDIO.md says what is proven and what is not.</para>
///
/// <para>Every Core Audio and WinRT call goes through the thread pool: they are
/// blocking COM calls, and the window's thread is the one the picture needs.
/// Nothing here runs while the panel is shut and both switches are off.</para>
/// </remarks>
public partial class MainWindow
{
    // --- Phone sound (A2DP) ------------------------------------------------------

    /// <summary>The open connection, while the switch is on and a phone was found.</summary>
    private PhoneAudioLink? _phoneLink;

    /// <summary>Non-null while a start is in flight: listing the phones, then opening.</summary>
    private CancellationTokenSource? _phoneStart;

    /// <summary>The start got as far as asking the phone (false: still listing).</summary>
    private bool _phoneAsking;

    private IReadOnlyList<BluetoothPhone> _phones = [];
    private bool _phonesListing;
    private PhoneSoundProblem _phoneProblem;
    private string _phoneError = "";

    /// <summary>What went wrong before a link existed; a refusal is the link's own business.</summary>
    private enum PhoneSoundProblem { None, NoPhone, Unsupported, Failed }

    // --- Call microphone (hands-free) ---------------------------------------------

    private CallAudioBridge? _callBridge;
    private bool _callStarting;

    /// <summary>The phone's hands-free endpoints as last seen, or null: no call can be bridged.</summary>
    private PhoneCallEndpoints? _callEndpoints;

    /// <summary>Why the bridge last stopped or failed to start, until the next start or link.</summary>
    private string? _callMessage;

    // --- Devices -------------------------------------------------------------------

    private string? _defaultOutputName;
    private bool _audioDevicesKnown;
    private bool _audioRefreshing;

    /// <summary>Set while controls are changed from code, so their handlers stay out of it.</summary>
    private bool _audioQuiet;

    private bool _audioPanelOpen;
    private bool _audioClosing;

    /// <summary>
    /// Looks for the phone's hands-free endpoints and the PC's devices every two
    /// seconds — only while the panel is open or the call bridge is armed.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _audioPoll = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    /// <summary>One line of a device list: what a setting remembers, and what a person reads.</summary>
    private sealed record AudioChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    // --- The panel -------------------------------------------------------------------

    private void OnAudioClicked(object sender, RoutedEventArgs e)
    {
        if (_audioPanelOpen)
            CloseAudioPanel();
        else
            OpenAudioPanel();
    }

    private void OpenAudioPanel()
    {
        // The mouse comes back first: the panel lies over the picture, and a
        // pointer still driving the phone would be driving it through the panel.
        Disengage();
        HideButtonLabel();

        _audioPanelOpen = true;
        AudioPanel.Visibility = Visibility.Visible;
        AudioButton.Background = (Brush)FindResource("GlassSurface");
        RefreshAudioPanel();
        UpdateAudioPolling();
        _ = RefreshAudioDevicesAsync();

        // Only to offer a choice when Windows knows several phones; the switch
        // lists them again anyway.
        if (_phoneLink is null && _phoneStart is null)
            _ = ListPhonesAsync();
    }

    private void CloseAudioPanel()
    {
        if (!_audioPanelOpen)
            return;

        bool hadFocus = AudioPanel.IsKeyboardFocusWithin;
        _audioPanelOpen = false;
        AudioPanel.Visibility = Visibility.Collapsed;
        AudioButton.ClearValue(BackgroundProperty);
        UpdateAudioPolling();
        if (hadFocus)
            Stage.Focus();
    }

    private bool AnyAudioListOpen =>
        PhoneChoice.IsDropDownOpen || CallMicrophone.IsDropDownOpen || CallOutput.IsDropDownOpen;

    /// <summary>Escape closes the panel — after a dropped-down list, which it closes first.</summary>
    private bool AudioPanelEscape(Key key)
    {
        if (key != Key.Escape || !_audioPanelOpen || AnyAudioListOpen)
            return false;
        CloseAudioPanel();
        return true;
    }

    /// <summary>
    /// A press outside the panel and its button closes the panel. True when the
    /// press must go no further: one on the picture only closes, it does not
    /// also take the mouse.
    /// </summary>
    private bool AudioPanelPress(MouseButtonEventArgs e)
    {
        if (!_audioPanelOpen || AnyAudioListOpen || AudioPanel.IsMouseOver || AudioButton.IsMouseOver)
            return false;

        CloseAudioPanel();
        Point landing = e.GetPosition(Screen);
        return landing.X >= 0 && landing.Y >= 0 &&
               landing.X < Screen.ActualWidth && landing.Y < Screen.ActualHeight;
    }

    /// <summary>The panel's fixed words, in the current language; called by ApplyTexts.</summary>
    private void ApplyAudioTexts(Texts t)
    {
        AudioLabel.Text = t.Audio;
        AudioButton.ToolTip = t.AudioTooltip;
        AutomationProperties.SetName(AudioButton, t.Audio);

        PhoneSoundTitle.Text = t.PhoneSoundTitle;
        AutomationProperties.SetName(PhoneSoundSwitch, t.PhoneSoundTitle);
        PhoneChoiceLabel.Text = t.PhoneChoiceLabel;
        AutomationProperties.SetName(PhoneChoice, t.PhoneChoiceLabel);
        PhoneSoundRetry.Content = t.Retry;
        BluetoothSettingsButton.Content = t.BluetoothSettings;
        ChooseOutputButton.Content = t.ChooseOutput;
        ChooseOutputNote.Text = t.ChooseOutputNote;

        CallTitle.Text = t.CallTitle;
        AutomationProperties.SetName(CallSwitch, t.CallTitle);
        CallHowTo.Text = t.CallHowTo;
        CallMicrophoneLabel.Text = t.CallMicrophoneLabel;
        AutomationProperties.SetName(CallMicrophone, t.CallMicrophoneLabel);
        CallOutputLabel.Text = t.CallOutputLabel;
        AutomationProperties.SetName(CallOutput, t.CallOutputLabel);
        CallWarning.Text = t.CallWarning;

        RefreshAudioPanel();
    }

    /// <summary>
    /// Everything the panel and the bar's dot say, from the state as it stands.
    /// Cheap on purpose — no COM, no I/O — so every event can call it.
    /// </summary>
    private void RefreshAudioPanel()
    {
        Texts t = T;

        // --- Phone sound.
        bool on = PhoneSoundSwitch.IsChecked == true;
        string state;
        string? hint = null, detail = null;
        string dot = "OutlineVariant";
        bool retry = false, bluetoothSettings = false, retryEnabled = true;
        bool flowing = false;

        if (!on)
        {
            state = t.PhoneSoundOff;
        }
        else if (_phoneLink is { } link)
        {
            detail = link.Description;
            switch (link.State)
            {
                case PhoneAudioState.Open:
                    state = t.PhoneSoundOn;
                    dot = "SuccessPulse";
                    flowing = true;
                    break;
                case PhoneAudioState.Waiting:
                    state = t.PhoneSoundWaiting;
                    dot = "Warn";
                    retry = true;
                    break;
                case PhoneAudioState.Refused:
                    state = t.PhoneSoundRefused(RefusalText(t, link));
                    dot = "Error";
                    retry = true;
                    if (link.Refusal == PhoneAudioRefusal.NotAvailable)
                        bluetoothSettings = true;
                    else
                        hint = t.PhoneSoundStillListening;
                    break;
                case PhoneAudioState.Closed:
                    state = t.PhoneSoundOff;
                    break;
                default:   // Opening, and a Retry in flight
                    state = t.PhoneSoundConnecting;
                    dot = "Warn";
                    break;
            }
        }
        else if (_phoneStart is not null)
        {
            state = _phoneAsking ? t.PhoneSoundConnecting : t.PhoneSoundLooking;
            dot = "Warn";
        }
        else
        {
            switch (_phoneProblem)
            {
                case PhoneSoundProblem.NoPhone:
                    state = t.PhoneSoundNoPhone;
                    dot = "Error";
                    retry = bluetoothSettings = true;
                    break;
                case PhoneSoundProblem.Unsupported:
                    state = t.PhoneSoundUnsupported;
                    dot = "Error";
                    break;
                case PhoneSoundProblem.Failed:
                    state = t.PhoneSoundFailed(_phoneError);
                    dot = "Error";
                    retry = true;
                    break;
                default:
                    state = t.PhoneSoundConnecting;
                    dot = "Warn";
                    retryEnabled = false;
                    break;
            }
        }

        PhoneSoundState.Text = state;
        PhoneSoundState.ToolTip = detail;
        PhoneSoundDot.Fill = (Brush)FindResource(dot);
        PhoneSoundHint.Text = hint ?? "";
        PhoneSoundHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        PhoneSoundRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        PhoneSoundRetry.IsEnabled = retryEnabled;
        BluetoothSettingsButton.Visibility = bluetoothSettings ? Visibility.Visible : Visibility.Collapsed;
        PhoneSoundActions.Visibility = retry || bluetoothSettings ? Visibility.Visible : Visibility.Collapsed;
        PhoneChoice.IsEnabled = _phoneStart is null;
        PhoneSoundOutput.Text = _audioDevicesKnown ? t.SoundOutput(_defaultOutputName) : "";

        // --- Call microphone.
        bool running = _callBridge is not null;
        bool linked = _callEndpoints is not null;
        bool microphones = CallMicrophone.Items.Count > 0, outputs = CallOutput.Items.Count > 0;

        CallSwitch.IsEnabled = running || (!_callStarting && linked && microphones && outputs);
        CallMicrophone.IsEnabled = CallOutput.IsEnabled = !running && !_callStarting;
        CallHowTo.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        (string callState, string callDot) =
            _callStarting ? (t.CallStarting, "Warn")
            : running ? (t.CallRunning, "SuccessPulse")
            : _callMessage is not null ? (_callMessage, "Warn")
            : _audioDevicesKnown && !microphones ? (t.CallNoMicrophone, "Error")
            : linked ? (t.CallLinkReady, "Primary")
            : (t.CallNoLink, "OutlineVariant");
        CallState.Text = callState;
        CallDot.Fill = (Brush)FindResource(callDot);

        // --- The bar: green while something flows, amber while the sound is
        // switched on but not coming through, nothing when all is off.
        if (flowing || running)
        {
            AudioDot.Fill = (Brush)FindResource("SuccessPulse");
            AudioDot.Visibility = Visibility.Visible;
        }
        else if (on)
        {
            AudioDot.Fill = (Brush)FindResource("Warn");
            AudioDot.Visibility = Visibility.Visible;
        }
        else
        {
            AudioDot.Visibility = Visibility.Collapsed;
        }
        // What the dot says, for whoever cannot see it.
        AutomationProperties.SetHelpText(AudioButton,
            string.Join(" ", new[] { on ? state : null, running ? callState : null }.Where(s => s is not null)));
    }

    /// <summary>A refusal in a few words; the whole sentence is the state line's tooltip.</summary>
    private static string RefusalText(Texts t, PhoneAudioLink link) => link.Refusal switch
    {
        PhoneAudioRefusal.TimedOut => t.RefusalNoAnswer,
        // What this PC answers when the phone never replies to the Bluetooth
        // page: "unknown" in the status, the phone's radio in fact.
        PhoneAudioRefusal.UnknownFailure when link.ErrorCode == PhoneAudioLink.PhoneDidNotAnswer => t.RefusalNoAnswer,
        PhoneAudioRefusal.DeniedBySystem => t.RefusalDenied(link.ErrorCode),
        PhoneAudioRefusal.NotAvailable => t.RefusalNotPaired,
        _ => t.RefusalFailed(link.ErrorCode),
    };

    // --- Phone sound -----------------------------------------------------------------

    /// <summary>Opens the connection again at start-up when it was left on — never in a timed diagnostic run.</summary>
    private void StartAudioAtLaunch()
    {
        if (!_settings.PhoneAudio || _diagnosticSeconds > 0)
            return;
        SetSwitch(PhoneSoundSwitch, true);
        _ = StartPhoneSoundAsync();
    }

    private async void OnPhoneSoundSwitched(object sender, RoutedEventArgs e)
    {
        if (_audioQuiet)
            return;

        bool on = PhoneSoundSwitch.IsChecked == true;
        _settings.PhoneAudio = on;
        _settings.Save();
        if (on)
            await StartPhoneSoundAsync();
        else
            StopPhoneSound();
    }

    /// <summary>
    /// Lists the phones Windows can take sound from, picks one and opens the
    /// connection. A refusal is not a failure here: the link stays listening,
    /// and the panel says what to do on the phone.
    /// </summary>
    private async Task StartPhoneSoundAsync()
    {
        StopPhoneSound();

        var cancel = new CancellationTokenSource();
        _phoneStart = cancel;
        _phoneAsking = false;
        RefreshAudioPanel();
        try
        {
            if (!await Task.Run(() => BluetoothAudio.IsSupported))
            {
                _phoneProblem = PhoneSoundProblem.Unsupported;
                return;
            }

            var phones = await BluetoothAudio.ListPhonesAsync(cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            ShowPhones(phones);
            if (ChoosePhone(phones) is not { } phone)
            {
                _phoneProblem = PhoneSoundProblem.NoPhone;
                _journal.Write("son Bluetooth : aucun telephone appaire comme source audio");
                return;
            }

            _phoneAsking = true;
            RefreshAudioPanel();
            _journal.Write($"son Bluetooth : ouverture vers {phone.Name}");
            var link = await BluetoothAudio.StartListeningAsync(phone.Id, phone.Name, cancel.Token);
            if (cancel.IsCancellationRequested || !ReferenceEquals(_phoneStart, cancel) || _audioClosing)
            {
                _ = Task.Run(link.Dispose);
                return;
            }
            _phoneLink = link;
            link.StateChanged += OnPhoneLinkStateChanged;
            NotePhoneLink(link);
        }
        catch (OperationCanceledException)
        {
            // Switched off, or another phone chosen, while it was on its way.
        }
        catch (Exception exception)
        {
            _phoneProblem = PhoneSoundProblem.Failed;
            _phoneError = exception.Message;
            _journal.Write("ERREUR son Bluetooth : " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_phoneStart, cancel))
                _phoneStart = null;
            cancel.Dispose();
            _phoneAsking = false;
            RefreshAudioPanel();
        }
    }

    /// <summary>Cuts the sound: the phone goes back to its own speaker or previous output.</summary>
    private void StopPhoneSound()
    {
        _phoneStart?.Cancel();
        _phoneStart = null;
        _phoneProblem = PhoneSoundProblem.None;
        _phoneError = "";

        var link = _phoneLink;
        _phoneLink = null;
        if (link is not null)
        {
            link.StateChanged -= OnPhoneLinkStateChanged;
            // Closing waits on the pool for the service to let go, up to five
            // seconds: not on the window's thread.
            _ = Task.Run(link.Dispose);
            _journal.Write("son Bluetooth : coupe");
        }
        RefreshAudioPanel();
    }

    private void OnPhoneLinkStateChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (sender is not PhoneAudioLink link || !ReferenceEquals(link, _phoneLink))
                return;
            NotePhoneLink(link);
            RefreshAudioPanel();
        });

    private void NotePhoneLink(PhoneAudioLink link) =>
        _journal.Write($"son Bluetooth : {link.State}"
                       + (link.State == PhoneAudioState.Refused
                           ? $" ({link.Refusal})" + (link.ErrorCode is int code ? $" 0x{code:X8}" : "")
                           : ""));

    private async void OnPhoneSoundRetry(object sender, RoutedEventArgs e)
    {
        if (_phoneLink is not { } link)
        {
            // Nothing to reopen — no phone was found, or the start failed: from the top.
            if (PhoneSoundSwitch.IsChecked == true)
                await StartPhoneSoundAsync();
            return;
        }

        PhoneSoundRetry.IsEnabled = false;
        try
        {
            _journal.Write("son Bluetooth : nouvelle tentative");
            await link.ReopenAsync();
        }
        catch (Exception exception)
        {
            _journal.Write("ERREUR son Bluetooth : " + exception.Message);
        }
        finally
        {
            RefreshAudioPanel();
        }
    }

    /// <summary>Asks Windows for its phones while the panel is open, to offer a choice when there are several.</summary>
    private async Task ListPhonesAsync()
    {
        if (_phonesListing)
            return;
        _phonesListing = true;
        try
        {
            if (!await Task.Run(() => BluetoothAudio.IsSupported))
                return;
            var phones = await BluetoothAudio.ListPhonesAsync();
            if (_phoneStart is null && !_audioClosing)
                ShowPhones(phones);
        }
        catch (Exception exception)
        {
            _journal.Write("son Bluetooth : liste des telephones illisible : " + exception.Message);
        }
        finally
        {
            _phonesListing = false;
        }
    }

    /// <summary>Fills the phone list; it only shows when there is a choice to make.</summary>
    private void ShowPhones(IReadOnlyList<BluetoothPhone> phones)
    {
        _phones = phones;
        var chosen = ChoosePhone(phones);
        _audioQuiet = true;
        try
        {
            PhoneChoice.Items.Clear();
            foreach (var phone in phones)
            {
                var item = new AudioChoice(phone.Id, phone.Name);
                PhoneChoice.Items.Add(item);
                if (chosen is not null && phone.Id == chosen.Id)
                    PhoneChoice.SelectedItem = item;
            }
        }
        finally
        {
            _audioQuiet = false;
        }
        PhoneChoiceRow.Visibility = phones.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The phone chosen before, else the one named like the iPhone on the
    /// cable, else the first Windows lists.
    /// </summary>
    private BluetoothPhone? ChoosePhone(IReadOnlyList<BluetoothPhone> phones)
    {
        if (phones.Count == 0)
            return null;
        if (phones.FirstOrDefault(p => p.Id == _settings.PhoneAudioDevice) is { } remembered)
            return remembered;
        if (_session?.Device?.Name is { Length: > 0 } cable &&
            phones.FirstOrDefault(p => SameName(p.Name, cable)) is { } named)
            return named;
        return phones[0];

        // Windows and lockdown spell the same name, give or take the kind of
        // apostrophe iOS puts in "Jane's iPhone".
        static bool SameName(string a, string b)
        {
            a = a.Replace('’', '\'').Trim();
            b = b.Replace('’', '\'').Trim();
            return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async void OnPhoneChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioQuiet || PhoneChoice.SelectedItem is not AudioChoice choice)
            return;
        _settings.PhoneAudioDevice = choice.Id;
        _settings.Save();
        if (PhoneSoundSwitch.IsChecked == true)
            await StartPhoneSoundAsync();
    }

    private void OnChooseOutput(object sender, RoutedEventArgs e)
    {
        try
        {
            BluetoothAudio.OpenSoundSettings();
        }
        catch (Exception exception)
        {
            Report(t => t.SettingsPageFailed(exception.Message));
        }
    }

    private void OnBluetoothSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = "ms-settings:bluetooth", UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Report(t => t.SettingsPageFailed(exception.Message));
        }
    }

    // --- Devices -----------------------------------------------------------------------

    private void UpdateAudioPolling()
    {
        bool wanted = !_audioClosing && (_audioPanelOpen || _callBridge is not null || _callStarting);
        if (wanted && !_audioPoll.IsEnabled)
            _audioPoll.Start();
        else if (!wanted && _audioPoll.IsEnabled)
            _audioPoll.Stop();
    }

    private void OnAudioPoll(object? sender, EventArgs e) => _ = RefreshAudioDevicesAsync();

    /// <summary>
    /// Reads the endpoints once, off the window's thread: the phone's
    /// hands-free pair, the PC's microphones and outputs, the default output.
    /// </summary>
    /// <remarks>
    /// Enumerating only reads property stores. It opens no stream, so it cannot
    /// wake the hands-free voice channel — only starting the bridge does.
    /// </remarks>
    private async Task RefreshAudioDevicesAsync()
    {
        if (_audioRefreshing)
            return;
        _audioRefreshing = true;
        try
        {
            var all = await Task.Run(() => AudioEndpoints.List());
            if (!_audioClosing)
                ApplyAudioDevices(all);
        }
        catch (Exception exception)
        {
            _journal.Write("audio : lecture des peripheriques impossible : " + exception.Message);
        }
        finally
        {
            _audioRefreshing = false;
        }
    }

    private void ApplyAudioDevices(IReadOnlyList<AudioEndpoint> all)
    {
        // The same test as CallAudioBridge.FindPhoneEndpoints, on the list
        // already in hand rather than a second enumeration.
        var handsFree = all.Where(e => e.BluetoothRole == BluetoothAudioRole.HandsFree).ToList();
        var fromPhone = handsFree.FirstOrDefault(e => e.Flow == AudioFlow.Input);
        var toPhone = handsFree.FirstOrDefault(e => e.Flow == AudioFlow.Output);
        var endpoints = fromPhone is not null && toPhone is not null ? new PhoneCallEndpoints(fromPhone, toPhone) : null;
        if ((endpoints is null) != (_callEndpoints is null))
        {
            _journal.Write(endpoints is null
                ? "appel par le PC : liaison mains-libres disparue"
                : $"appel par le PC : liaison mains-libres presente ({toPhone!.Name})");
            // A new link is a new story: whatever ended the last one is over.
            if (endpoints is not null)
                _callMessage = null;
        }
        _callEndpoints = endpoints;

        _defaultOutputName = all.FirstOrDefault(e => e.Flow == AudioFlow.Output && e.IsDefault)?.Name;

        // The phone's own endpoints are not choices: the bridge wires them itself.
        static bool Local(AudioEndpoint e) => e.BluetoothRole is not (BluetoothAudioRole.HandsFree or BluetoothAudioRole.Media);
        FillDevices(CallMicrophone, all.Where(e => e.Flow == AudioFlow.Input && Local(e)).ToList(), _settings.CallMicrophone);
        FillDevices(CallOutput, all.Where(e => e.Flow == AudioFlow.Output && Local(e)).ToList(), _settings.CallOutput);

        _audioDevicesKnown = true;
        RefreshAudioPanel();
    }

    /// <summary>
    /// Puts a device list in a combo box, only when it changed. Selected: what
    /// was selected, else what the settings remember, else Windows's default,
    /// else its default for calls, else the first.
    /// </summary>
    /// <remarks>
    /// The ordinary default before the one for calls: on the machine this was
    /// written on, the default for calls was a virtual cable nobody listens to,
    /// and preselecting it would have sent the call nowhere.
    /// </remarks>
    private void FillDevices(ComboBox combo, List<AudioEndpoint> endpoints, string remembered)
    {
        if (combo.IsDropDownOpen)
            return;

        var items = endpoints.Select(e => new AudioChoice(e.Id, e.Name)).ToList();
        if (combo.Items.Count == items.Count && items.Select((item, i) => item.Equals(combo.Items[i])).All(same => same))
            return;

        string? current = (combo.SelectedItem as AudioChoice)?.Id;
        string? wanted = items.Any(c => c.Id == current) ? current
            : items.Any(c => c.Id == remembered) ? remembered
            : (endpoints.FirstOrDefault(e => e.IsDefault) ??
               endpoints.FirstOrDefault(e => e.IsDefaultForCommunications) ??
               endpoints.FirstOrDefault())?.Id;

        _audioQuiet = true;
        try
        {
            combo.Items.Clear();
            foreach (var item in items)
            {
                combo.Items.Add(item);
                if (item.Id == wanted)
                    combo.SelectedItem = item;
            }
        }
        finally
        {
            _audioQuiet = false;
        }
    }

    private void OnCallDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioQuiet)
            return;
        if (ReferenceEquals(sender, CallMicrophone) && CallMicrophone.SelectedItem is AudioChoice microphone)
            _settings.CallMicrophone = microphone.Id;
        else if (ReferenceEquals(sender, CallOutput) && CallOutput.SelectedItem is AudioChoice output)
            _settings.CallOutput = output.Id;
        else
            return;
        _settings.Save();
    }

    // --- Call microphone -----------------------------------------------------------------

    private async void OnCallSwitched(object sender, RoutedEventArgs e)
    {
        if (_audioQuiet)
            return;
        if (CallSwitch.IsChecked == true)
            await StartCallAsync();
        else
            StopCall();
    }

    /// <summary>
    /// Starts the bridge on the endpoints last seen. Only from the switch: this
    /// is what hands the phone's microphone over to the PC.
    /// </summary>
    private async Task StartCallAsync()
    {
        var phone = _callEndpoints;
        var microphone = CallMicrophone.SelectedItem as AudioChoice;
        var output = CallOutput.SelectedItem as AudioChoice;
        if (phone is null || microphone is null || output is null || _callBridge is not null || _callStarting)
        {
            SetSwitch(CallSwitch, _callBridge is not null);
            RefreshAudioPanel();
            return;
        }

        _callMessage = null;
        _callStarting = true;
        RefreshAudioPanel();
        UpdateAudioPolling();
        _journal.Write($"appel par le PC : demarrage, {microphone.Name} -> {phone.ToPhone.Name}, {phone.FromPhone.Name} -> {output.Name}");
        try
        {
            var bridge = await Task.Run(() => CallAudioBridge.Start(microphone.Id, output.Id, phone));
            if (_audioClosing || CallSwitch.IsChecked != true)
            {
                _ = Task.Run(bridge.Dispose);
                return;
            }
            _callBridge = bridge;
            bridge.Stopped += OnCallBridgeStopped;

            // Died before anybody listened: the event may already be spent.
            if (!bridge.IsRunning && bridge.StopReason is not null)
                OnCallStopped(bridge);
        }
        catch (Exception exception)
        {
            _callMessage = T.CallStartFailed(exception.Message);
            _journal.Write("ERREUR appel par le PC : " + exception.Message);
            SetSwitch(CallSwitch, false);
        }
        finally
        {
            _callStarting = false;
            RefreshAudioPanel();
            UpdateAudioPolling();
        }
    }

    private void OnCallBridgeStopped(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (sender is CallAudioBridge bridge)
                OnCallStopped(bridge);
        });

    /// <summary>The bridge stopped by itself — usually the call ended: the switch goes back to off, with the reason.</summary>
    private void OnCallStopped(CallAudioBridge bridge)
    {
        if (!ReferenceEquals(bridge, _callBridge))
            return;
        _callBridge = null;
        bridge.Stopped -= OnCallBridgeStopped;
        _callMessage = bridge.StopReason;
        _ = Task.Run(bridge.Dispose);
        _journal.Write("appel par le PC : arrete de lui-meme");
        SetSwitch(CallSwitch, false);
        RefreshAudioPanel();
        UpdateAudioPolling();
        _ = RefreshAudioDevicesAsync();
    }

    /// <summary>Stops the bridge and gives the phone its microphone back.</summary>
    private void StopCall()
    {
        var bridge = _callBridge;
        _callBridge = null;
        if (bridge is not null)
        {
            bridge.Stopped -= OnCallBridgeStopped;
            _ = Task.Run(bridge.Dispose);
            _journal.Write("appel par le PC : arrete");
        }
        RefreshAudioPanel();
        UpdateAudioPolling();
    }

    private void SetSwitch(CheckBox toggle, bool on)
    {
        _audioQuiet = true;
        try
        {
            toggle.IsChecked = on;
        }
        finally
        {
            _audioQuiet = false;
        }
    }

    /// <summary>
    /// Closes both on the way out, waiting for them: the phone gets its
    /// microphone back and its sound returns to its own speaker.
    /// </summary>
    private void ShutDownAudio()
    {
        _audioClosing = true;
        _audioPoll.Stop();
        _phoneStart?.Cancel();

        var link = _phoneLink;
        _phoneLink = null;
        if (link is not null)
        {
            link.StateChanged -= OnPhoneLinkStateChanged;
            try { link.Dispose(); }
            catch (Exception) { /* leaving anyway */ }
        }

        var bridge = _callBridge;
        _callBridge = null;
        if (bridge is not null)
        {
            bridge.Stopped -= OnCallBridgeStopped;
            try { bridge.Dispose(); }
            catch (Exception) { /* leaving anyway */ }
        }
    }
}
