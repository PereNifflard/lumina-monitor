using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace LuminaMonitor.App;

/// <summary>
/// The Audio panel. Empty for now.
/// </summary>
/// <remarks>
/// The phone's sound over Bluetooth, and this PC's microphone for a call
/// routed to it, are gone: the radio link between phone and PC proved
/// unreliable on the project's own test hardware — decision of September 10
/// 2026, see docs/AUDIO.md. The sound will come back over the cable, decoded
/// by a codec written into this project; the PC's microphone cannot reach the
/// phone that way either, or any way the phone offers (see docs/AUDIO.md §5).
///
/// <para>Kept on purpose: the button and the panel's open/close mechanics, so
/// the cable path has a place to land in without touching the chassis again.
/// <see cref="LuminaMonitor.Core.Audio.AudioEndpoints"/> and
/// <see cref="LuminaMonitor.Core.Audio.AudioPump"/>, in Core, are kept for the
/// same reason, compilable but with no caller yet.</para>
/// </remarks>
public partial class MainWindow
{
    private bool _audioPanelOpen;

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
    }

    private void CloseAudioPanel()
    {
        if (!_audioPanelOpen)
            return;

        bool hadFocus = AudioPanel.IsKeyboardFocusWithin;
        _audioPanelOpen = false;
        AudioPanel.Visibility = Visibility.Collapsed;
        AudioButton.ClearValue(BackgroundProperty);
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
        AudioLabel.Text = t.Audio;
        AudioButton.ToolTip = t.AudioTooltip;
        AutomationProperties.SetName(AudioButton, t.Audio);
        AudioComingSoon.Text = t.AudioComingSoon;
    }
}
