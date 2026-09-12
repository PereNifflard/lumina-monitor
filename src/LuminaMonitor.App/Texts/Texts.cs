using LuminaMonitor.Core;

namespace LuminaMonitor.App;

/// <summary>
/// Every word the window shows: labels, tooltips, banners, the state pill, the
/// status bar, the counters pane and the file dialog. One member per sentence;
/// <see cref="EnglishTexts"/> and <see cref="FrenchTexts"/> are the two tables.
/// </summary>
/// <remarks>
/// C# rather than <c>.resx</c>, for the same two reasons as Core's
/// <c>CoreTexts</c>. The compiler holds the tables to each other — a sentence
/// declared here and missing from one language does not build — and a second
/// language costs nothing at publication: a <c>.resx</c> per culture is a
/// satellite assembly per culture, one more file for the single-file,
/// compressed executable to carry and for the resource loader to go looking
/// for, chosen by the thread's culture rather than by a setting.
///
/// <para>The language is <see cref="InterfaceLanguage.Current"/>, shared with Core
/// so that the exceptions coming up from it speak the same language as the
/// window around them. It is read each time a sentence is composed, which is
/// what lets the window switch without a restart.</para>
///
/// <para>Not here, on purpose: the journal. Its own lines stay French, and
/// what the status bar says is written into it through <see cref="French"/>
/// whatever the screen shows — it is the maintainer's instrument.</para>
/// </remarks>
internal abstract class Texts
{
    public static readonly Texts English = new EnglishTexts();
    public static readonly Texts French = new FrenchTexts();

    /// <summary>The table of the language currently chosen.</summary>
    public static Texts Current =>
        InterfaceLanguage.Current == DisplayLanguage.French ? French : English;

    // --- Top bar -----------------------------------------------------------------

    public abstract string CountersTooltip { get; }

    // --- State pill --------------------------------------------------------------

    public abstract string PillLocked { get; }
    public abstract string PillFps(double fps, bool driving);
    public abstract string PillNoKeyboard { get; }
    public abstract string PillStreamStopped { get; }
    public abstract string PillWaiting { get; }
    public abstract string PillRestarting { get; }
    public abstract string PillError { get; }
    public abstract string PillOffline { get; }

    // --- Chassis buttons -----------------------------------------------------------

    public abstract string MuteTooltip { get; }
    public abstract string SideButtonTooltip { get; }
    public abstract string VolumeUp { get; }
    public abstract string VolumeDown { get; }
    public abstract string Mute { get; }
    public abstract string NoSessionForButton { get; }
    public abstract string NoSessionForSideButton { get; }
    public abstract string LockingIPhone { get; }
    public abstract string SideButtonFailed(string error);

    // The floating label beside a drawn button: what it does, in large, and
    // which physical button it is, in small. The same words are the buttons'
    // accessible names.
    public abstract string LabelVolumeUp { get; }
    public abstract string LabelVolumeDown { get; }
    public abstract string LabelMute { get; }
    public abstract string LabelLock { get; }
    public abstract string LabelWake { get; }
    public abstract string NameVolumeButton { get; }
    public abstract string NameActionButton { get; }
    public abstract string NameSideButton { get; }
    public abstract string ButtonNotSentNoSession { get; }
    public abstract string ButtonNotSent { get; }

    /// <summary>The first-run bubble pointing at the side buttons.</summary>
    public abstract string SideButtonsHint { get; }

    /// <summary>The system menu entry that shows that bubble again.</summary>
    public abstract string MenuShowSideButtons { get; }

    // --- Waiting card ----------------------------------------------------------------

    public abstract string WaitingTitle { get; }
    public abstract string StepPlugCable { get; }
    public abstract string StepUnlock { get; }
    public abstract string StepDeveloperMode { get; }
    public abstract string NothingToInstall { get; }

    // --- Banners -----------------------------------------------------------------------

    public abstract string OpenAppleDevices { get; }
    public abstract string AppleDevicesOpened { get; }
    public abstract string AppleDevicesFailed(string error);
    public abstract string LockedTitle { get; }
    public abstract string LockedWithCode { get; }
    public abstract string LockedWithoutCode { get; }
    public abstract string WakeScreen { get; }

    // --- Command bar -------------------------------------------------------------------

    public abstract string Home { get; }
    public abstract string HomeTooltip { get; }
    public abstract string ToIPhone { get; }
    public abstract string ToIPhoneTooltip { get; }
    public abstract string FromIPhone { get; }
    public abstract string FromIPhoneTooltip { get; }
    public abstract string StopPasting { get; }
    public abstract string Wheel { get; }

    // --- Start-up, developer image ----------------------------------------------------------

    public abstract string DiagnosticRun(int seconds, string journal);
    public abstract string SettingsUnreadable(string keptAs);
    public abstract string DdiFoundInRepository(string folder);
    public abstract string DiagnosticWithoutDdi { get; }
    public abstract string DdiDialogTitle { get; }
    public abstract string DdiDialogFilter { get; }
    public abstract string ChooseDdiArchive { get; }
    public abstract string NoDdiChosen { get; }
    public abstract string Extracting(string file);
    public abstract string ArchiveScanned(long gigabytes);
    public abstract string NoDdiInArchive(string file);
    public abstract string DdiReady(string? build);
    public abstract string ExtractionFailed(string error);

    // --- Replay --------------------------------------------------------------------------------

    public abstract string ReplayEmpty(string path);
    public abstract string ReplayRunning(string file, long datagrams, double loopSeconds);
    public abstract string ReplayFailed(string error);

    // --- Session -------------------------------------------------------------------------------

    public abstract string IPhonePlugged { get; }
    public abstract string IPhoneUnplugged { get; }
    public abstract string MultiplexerTrouble(string error);
    public abstract string Connecting { get; }
    public abstract string MirrorOpen { get; }
    public abstract string MirrorOpenHowTo { get; }
    public abstract string RetryIn(string error, double seconds);
    public abstract string Step(SessionState state);
    public abstract string ActionFailed(string action, string error);
    public abstract string InputFault(string error);
    public abstract string InputReopening { get; }
    public abstract string InputRestored { get; }
    public abstract string InputLost { get; }

    // --- Screen, stream, orientation ------------------------------------------------------------

    public abstract string IPhoneLocked { get; }
    public abstract string ScreenOn { get; }
    public abstract string StreamResumed { get; }
    public abstract string StreamStopped(DateTime at);
    public abstract string IdleDrivable { get; }
    public abstract string WaitingForIPhone { get; }
    public abstract string LandscapeIslandLeft { get; }
    public abstract string LandscapeIslandRight { get; }
    public abstract string LandscapeUnknown { get; }
    public abstract string Portrait { get; }

    // --- Wake, unlock --------------------------------------------------------------------------

    public abstract string WakingScreen { get; }
    public abstract string WakeFailed(string error);
    public abstract string ScreenOnUnlockYourself { get; }
    public abstract string PasscodeHint { get; }
    public abstract string Unlocking(int characters);
    public abstract string CodeSent { get; }

    // --- Driving --------------------------------------------------------------------------------

    public abstract string WindowLeft { get; }
    public abstract string Driving { get; }
    public abstract string MouseReturned { get; }
    public abstract string ClickToResume(string reason);

    // --- Clipboard -------------------------------------------------------------------------------

    public abstract string NoSessionToPaste { get; }
    public abstract string ClipboardUnreadable { get; }
    public abstract string ClipboardEmpty { get; }
    public abstract string SendingClipboard(int characters);
    public abstract string ClipboardSent(int characters);
    public abstract string ClipboardServiceFallback(string error);
    public abstract string NoSessionToFetch { get; }
    public abstract string ReadingPhoneClipboard { get; }
    public abstract string FetchedText(int characters);
    public abstract string WindowsClipboardLocked { get; }
    public abstract string PhoneClipboardImage(string type, int bytes);
    public abstract string PhoneClipboardData(string type, int bytes);
    public abstract string PhoneClipboardEmpty { get; }
    public abstract string PhoneClipboardUnreadable(string error);
    public abstract string NothingTypeable { get; }
    public abstract string PasteCancelled(int keystrokes);
    public abstract string PasteSessionLost(int done, int total);
    public abstract string PasteProgress(int done, int total);
    public abstract string Pasted(int keystrokes, int skipped, int? cutAt);
    public abstract string PasteFailed(string error);

    // --- Audio panel ------------------------------------------------------------------------------
    // The command bar's Audio button and the panel it opens: the phone's sound
    // over the cable, on the Windows output of one's choice. No microphone and no
    // Bluetooth anywhere in it, and neither is coming back — docs/AUDIO.md §5 and
    // "Decision, September 10 2026".

    public abstract string Audio { get; }
    public abstract string AudioTooltip { get; }
    public abstract string AudioSection { get; }
    public abstract string AudioOutput { get; }
    public abstract string AudioDefaultOutput { get; }
    public abstract string AudioVolume { get; }
    public abstract string AudioDelay { get; }
    public abstract string AudioDelayHint { get; }
    public abstract string AudioRetry { get; }
    public abstract string AudioMilliseconds(double milliseconds);

    // The status line, one arm per thing that can be true of the sound.
    public abstract string AudioOff { get; }
    public abstract string AudioNoSession { get; }
    public abstract string AudioOpening { get; }
    public abstract string AudioRefused(string reason);
    public abstract string AudioRunning(string device, string level);

    // --- Brightness -------------------------------------------------------------------------------

    public abstract string DimGestureInvalid { get; }
    public abstract string OpeningControlCentre { get; }
    public abstract string Dimming { get; }
    public abstract string Dimmed { get; }
    public abstract string DimFailed(string error);

    // --- Counters pane (F3) ---------------------------------------------------------------------

    public abstract string Counters(in CounterSample sample);
    public abstract string CountersNoPicture(bool driving, double mouseHz, SessionState state);

    // --- Language ----------------------------------------------------------------------------------

    /// <summary>The system menu entry that hands the choice back to Windows.</summary>
    public abstract string MenuLanguageAuto { get; }
    public abstract string LanguageSaved(string setting);
}

/// <summary>What the counters pane shows, gathered once a second.</summary>
internal readonly record struct CounterSample(
    bool Driving,
    int Width, int Height, double ShownWidth,
    double ReceivedFps, double ShownFps, double DecodeMs,
    double MouseHz, double SentHz, double DroppedHz, int Queue,
    SessionState State, long Errors);
