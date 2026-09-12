using System.Windows;
using System.Windows.Controls;
using LuminaMonitor.Core.Audio;

namespace LuminaMonitor.App;

/// <summary>
/// What the Audio panel's five controls do: the switch, the output, the volume,
/// the mute and the delay.
/// </summary>
/// <remarks>
/// Only the switch costs anything — it opens or closes a real media session on the
/// phone. The other four are applied to the chain as it runs, which is why moving
/// the volume does not interrupt the sound.
///
/// <para><b>Where the numbers are kept.</b> In <c>settings.json</c>, written when
/// the panel closes rather than on every notch of a slider: a slider dragged across
/// its travel raises a hundred change events, and a hundred atomic file
/// replacements for one gesture is a disk being punished for nothing. The live
/// chain is told immediately all the same — the file is the memory, not the
/// mechanism.</para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>One entry of the output list: what it is called, and what to store.</summary>
    /// <param name="Id">The endpoint identifier, or null for Windows's own default.</param>
    /// <param name="Label">What the list shows.</param>
    /// <remarks>
    /// It renders itself rather than being bound to: a <c>DisplayMemberPath</c>
    /// would put the binding engine to work reflecting over a private nested type,
    /// and <see cref="ToString"/> costs nothing and cannot fail at run time.
    /// </remarks>
    private sealed record AudioChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>True while the controls are being filled from the settings, so handlers stand aside.</summary>
    private bool _audioFilling;

    /// <summary>
    /// The settings into the controls, once, with the handlers held off.
    /// </summary>
    /// <remarks>
    /// The order matters: a slider given its value while its handler is live saves
    /// that value back over the one it was just given, and a combo box filled the
    /// same way announces a selection change nobody asked for. So
    /// <see cref="_audioFilling"/> is up for the whole of it.
    /// </remarks>
    private void FillAudioPanel()
    {
        _audioFilling = true;
        try
        {
            AudioEnabledSwitch.IsChecked = _settings.AudioEnabled;
            AudioVolumeSlider.Value = Math.Clamp(_settings.AudioVolume, 0, 100);
            AudioDelaySlider.Value = Math.Clamp(_settings.AudioDelayMs, 0, AudioOptions.MaxDelayMs);
            AudioVolumeValue.Text = $"{(int)AudioVolumeSlider.Value}";
            AudioDelayValue.Text = T.AudioMilliseconds(AudioDelaySlider.Value);
        }
        finally
        {
            _audioFilling = false;
        }
        FillAudioOutputs();
    }

    /// <summary>
    /// Reads the Windows outputs and puts them in the list, the stored one
    /// selected.
    /// </summary>
    /// <remarks>
    /// On a thread of its own, because the MMDevice enumeration is COM and this is
    /// the window's own thread: <see cref="AudioEndpoints"/> says as much. Active
    /// outputs only — a disabled or unplugged endpoint cannot be played on, and
    /// offering it would be offering silence.
    /// </remarks>
    private void FillAudioOutputs()
    {
        string wanted = _settings.AudioOutputId;
        _ = Task.Run(() =>
        {
            IReadOnlyList<AudioEndpoint> endpoints;
            try
            {
                endpoints = AudioEndpoints.List();
            }
            catch (Exception exception)
            {
                _journal.Write($"sorties audio illisibles : {exception.Message}");
                endpoints = [];
            }
            Dispatcher.InvokeAsync(() => ShowAudioOutputs(endpoints, wanted));
        });
    }

    private void ShowAudioOutputs(IReadOnlyList<AudioEndpoint> endpoints, string wanted)
    {
        _audioFilling = true;
        try
        {
            AudioOutputBox.Items.Clear();
            AudioOutputBox.Items.Add(new AudioChoice(null, T.AudioDefaultOutput));
            foreach (var endpoint in endpoints)
                AudioOutputBox.Items.Add(new AudioChoice(endpoint.Id, endpoint.Name));
            int at = 0;
            for (int index = 1; index < AudioOutputBox.Items.Count; index++)
                if (((AudioChoice)AudioOutputBox.Items[index]!).Id == wanted)
                    at = index;
            AudioOutputBox.SelectedIndex = at;
        }
        finally
        {
            _audioFilling = false;
        }
    }

    // --- What the controls do -------------------------------------------------------

    private void OnAudioEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_audioFilling)
            return;
        bool wanted = AudioEnabledSwitch.IsChecked == true;
        // A no-op change closes and reopens the whole phone stream for nothing —
        // and something was firing this handler without the value moving, which
        // is the churn the log showed. Ignore a change that is not one, and say
        // in the journal who asked, so a real toggle is told from a phantom.
        if (wanted == _settings.AudioEnabled)
        {
            _journal.Write($"son : interrupteur reactive sans changement (toujours {(wanted ? "on" : "off")}) — ignore.");
            return;
        }
        _journal.Write($"son : interrupteur bascule vers {(wanted ? "on" : "off")} (depuis la fenetre).");
        _settings.AudioEnabled = wanted;
        _settings.Save();
        if (_session is { } session)
            _ = session.SetAudioEnabledAsync(_settings.AudioEnabled);
        UpdateAudioStatus();
    }

    private void OnAudioOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioFilling || AudioOutputBox.SelectedItem is not AudioChoice choice)
            return;
        _settings.AudioOutputId = choice.Id ?? "";
        _settings.Save();
        ApplyAudioOptions();
    }

    private void OnAudioVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AudioVolumeValue.Text = $"{(int)AudioVolumeSlider.Value}";
        if (_audioFilling)
            return;
        _settings.AudioVolume = (int)AudioVolumeSlider.Value;
        ApplyAudioOptions();
    }

    private void OnAudioDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AudioDelayValue.Text = T.AudioMilliseconds(AudioDelaySlider.Value);
        if (_audioFilling)
            return;
        _settings.AudioDelayMs = (int)AudioDelaySlider.Value;
        ApplyAudioOptions();
    }

    private void OnAudioRetryClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;
        AudioRetryButton.Visibility = Visibility.Collapsed;
        _journal.Write("son : nouvelle tentative demandee depuis le panneau.");
        _ = session.OpenAudioAsync("reessai panneau");
    }

    /// <summary>The settings as Core wants them, whether or not a session exists.</summary>
    private AudioOptions AudioOptionsFromSettings() => new()
    {
        Enabled = _settings.AudioEnabled,
        DeviceId = _settings.AudioOutputId.Length == 0 ? null : _settings.AudioOutputId,
        Volume = _settings.AudioVolume,
        DelayMs = _settings.AudioDelayMs,
        // The sound plays here or on the phone, never both, and the switch at the
        // top of the panel is what decides: no separate control for it, and no
        // mute of our own either — the volume slider at zero is that.
        SilencePhone = _settings.AudioEnabled,
    };

    /// <summary>Volume, mute, delay and output to the chain already running.</summary>
    private void ApplyAudioOptions()
    {
        if (_session is { } session)
            session.AudioOptions = AudioOptionsFromSettings();
    }

    /// <summary>Writes the numbers, once, when the panel closes.</summary>
    private void SaveAudioSettings()
    {
        if (_audioWired)
            _settings.Save();
    }

    /// <summary>
    /// One line saying what the sound is doing, at four hertz at most.
    /// </summary>
    /// <remarks>
    /// The order of the arms is the order of the questions somebody asks: is it
    /// switched on, is it playing, is there a mirror to attach to at all, is it
    /// still opening, was it refused. A stream that plays is reported as playing
    /// before anything else is considered — the picture's own health is a separate
    /// question and the state pill already answers it. The retry button appears on
    /// exactly one arm.
    /// </remarks>
    private void UpdateAudioStatus()
    {
        Texts t = T;
        var session = _session;
        bool refused = false;
        if (!_settings.AudioEnabled)
        {
            AudioStatus.Text = t.AudioOff;
        }
        else if (session?.AudioStatistics is { Streaming: true } running)
        {
            AudioStatus.Text = t.AudioRunning(
                running.Device.Length > 0 ? running.Device : t.AudioDefaultOutput,
                running.LevelText);
        }
        else if (session is null || !Mirroring)
        {
            AudioStatus.Text = t.AudioNoSession;
        }
        else if (session.AudioOpening || session.AudioFailure is null)
        {
            AudioStatus.Text = t.AudioOpening;
        }
        else
        {
            AudioStatus.Text = t.AudioRefused(Shorten(session.AudioFailure));
            refused = true;
        }
        AudioRetryButton.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A daemon's refusal, on one line and short enough for a panel.</summary>
    private static string Shorten(string reason)
    {
        string flat = reason.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 160 ? flat[..160] + "…" : flat;
    }

    /// <summary>The AUDIO block of the journal's counters line.</summary>
    /// <remarks>
    /// Written whether or not the panel has ever been opened, which is the point:
    /// a <c>--diagnostic</c> run is unattended, and the sound has to be readable
    /// afterwards from the log alone. <see cref="AudioStats"/> composes it — one
    /// place for the format, shared with the probe.
    /// </remarks>
    private string AudioJournalBlock()
    {
        if (_session?.AudioStatistics is { } stats)
            return "  AUDIO " + stats;
        if (!_settings.AudioEnabled)
            return "  AUDIO coupe par reglage";
        return _session?.AudioFailure is { } why
            ? "  AUDIO aucun flux (" + Shorten(why) + ")"
            : "  AUDIO aucun flux";
    }
}
