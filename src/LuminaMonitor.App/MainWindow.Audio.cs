using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LuminaMonitor.App;

/// <summary>
/// The Audio panel's chassis: how it opens, how it closes, and the words on it.
/// </summary>
/// <remarks>
/// The phone's sound over the cable, on the Windows output of one's choice. What
/// the five controls actually do is in <c>MainWindow.AudioSettings.cs</c>; the
/// chain behind them is Core's, described in §11 of <c>docs/AUDIO.md</c>.
///
/// <para><b>No microphone, no Bluetooth.</b> Not an omission: the phone
/// advertises no incoming audio over CoreDevice at all (docs/AUDIO.md §5), and the
/// Bluetooth path was tried and found unreliable (§9). Neither is coming back, so
/// neither has a control here.</para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How often the status line is recomputed while the panel is open.</summary>
    private static readonly TimeSpan AudioStatusInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _audioStatusTimer = new() { Interval = AudioStatusInterval };
    private bool _audioPanelOpen;

    /// <summary>Whether the panel has ever been opened, and so whether its controls hold anything.</summary>
    private bool _audioWired;

    private void OnAudioClicked(object sender, RoutedEventArgs e)
    {
        if (_audioPanelOpen)
            CloseAudioPanel();
        else
            OpenAudioPanel();
    }

    /// <summary>
    /// Opens the panel, and fills it the first time.
    /// </summary>
    /// <remarks>
    /// Filled on first opening rather than at start-up, deliberately: the output
    /// list is an MMDevice enumeration, which is COM, and a window has better
    /// things to do in its first second than ask Windows about sound cards nobody
    /// has asked to see.
    /// </remarks>
    private void OpenAudioPanel()
    {
        // The mouse comes back first: the panel lies over the picture, and a
        // pointer still driving the phone would be driving it through the panel.
        Disengage();
        HideButtonLabel();

        _audioPanelOpen = true;
        AudioPanel.Visibility = Visibility.Visible;
        AudioButton.Background = (Brush)FindResource("GlassSurface");

        if (!_audioWired)
        {
            _audioWired = true;
            _audioStatusTimer.Tick += (_, _) => UpdateAudioStatus();
            FillAudioPanel();
        }
        UpdateAudioStatus();
        _audioStatusTimer.Start();
    }

    /// <summary>Closes the panel, and that is where its four numbers reach the disk.</summary>
    private void CloseAudioPanel()
    {
        if (!_audioPanelOpen)
            return;

        bool hadFocus = AudioPanel.IsKeyboardFocusWithin;
        _audioPanelOpen = false;
        _audioStatusTimer.Stop();
        AudioPanel.Visibility = Visibility.Collapsed;
        AudioButton.ClearValue(BackgroundProperty);
        SaveAudioSettings();
        if (hadFocus)
            Stage.Focus();
    }

    /// <summary>Escape closes the panel.</summary>
    private bool AudioPanelEscape(Key key)
    {
        if (key != Key.Escape || !_audioPanelOpen)
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
        if (!_audioPanelOpen || AudioPanel.IsMouseOver || AudioButton.IsMouseOver)
            return false;

        CloseAudioPanel();
        Point landing = e.GetPosition(Screen);
        return landing.X >= 0 && landing.Y >= 0 &&
               landing.X < Screen.ActualWidth && landing.Y < Screen.ActualHeight;
    }

    /// <summary>The panel's fixed words, in the current language; called by ApplyTexts.</summary>
    private void ApplyAudioTexts(Texts t)
    {
        AudioButton.ToolTip = t.AudioTooltip;
        AutomationProperties.SetName(AudioButton, t.Audio);

        AudioSectionLabel.Text = t.AudioSection;
        AutomationProperties.SetName(AudioEnabledSwitch, t.AudioSection);
        AudioOutputLabel.Text = t.AudioOutput;
        AutomationProperties.SetName(AudioOutputBox, t.AudioOutput);
        AudioVolumeLabel.Text = t.AudioVolume;
        AutomationProperties.SetName(AudioVolumeSlider, t.AudioVolume);
        AudioDelayLabel.Text = t.AudioDelay;
        AutomationProperties.SetName(AudioDelaySlider, t.AudioDelay);
        AudioDelayNote.Text = t.AudioDelayHint;
        AudioRetryButton.Content = t.AudioRetry;
        AudioVolumeValue.Text = $"{_settings.AudioVolume}";
        AudioDelayValue.Text = t.AudioMilliseconds(_settings.AudioDelayMs);

        // The output list's first entry is a sentence, so the list is rebuilt with
        // the language — but only once the panel has been opened: before that there
        // is nothing in it to rename.
        if (_audioWired)
            FillAudioOutputs();
        if (_audioPanelOpen)
            UpdateAudioStatus();
    }
}
