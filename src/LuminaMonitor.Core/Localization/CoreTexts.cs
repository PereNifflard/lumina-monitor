namespace LuminaMonitor.Core;

/// <summary>
/// Every sentence Core hands up to a person: the messages of
/// <see cref="LuminaException"/>, the texts of <c>UnlockRequired</c> and
/// <c>RestartRequired</c>, and the few low-level refusals that surface as they
/// are. One member per sentence; <see cref="EnglishCoreTexts"/> and
/// <see cref="FrenchCoreTexts"/> are the two tables.
/// </summary>
/// <remarks>
/// A class in C# rather than <c>.resx</c> files, for two reasons. The
/// compiler checks the tables: a sentence added here and forgotten in one
/// language does not build, where a missing resource falls back silently at
/// run time. And there is nothing to ship beside the assembly — a second
/// language in <c>.resx</c> is a satellite assembly per culture, one more
/// thing for the single-file, compressed publication to bundle and for the
/// resource loader to find, and its choice follows the thread's culture
/// rather than a setting.
///
/// <para>Log lines (<see cref="ILog"/>, progress) are not here and stay
/// French: they are the journal.</para>
/// </remarks>
internal abstract class CoreTexts
{
    public static readonly CoreTexts English = new EnglishCoreTexts();
    public static readonly CoreTexts French = new FrenchCoreTexts();

    /// <summary>The table of the language currently chosen.</summary>
    public static CoreTexts Current =>
        InterfaceLanguage.Current == DisplayLanguage.French ? French : English;

    // --- Apple's multiplexer ----------------------------------------------------

    public abstract string MultiplexerNotResponding { get; }
    public abstract string MultiplexerNotRunning { get; }

    // --- The climb --------------------------------------------------------------

    public abstract string NotConnected { get; }
    public abstract string NoDeviceAttached { get; }
    public abstract string DeviceWithoutUdid { get; }
    public abstract string NotPaired { get; }
    public abstract string PairRecordUnreadable { get; }
    public abstract string StartSessionRefused(string refusal);
    public abstract string DeveloperModeOff(object status);
    public abstract string DdiUnavailable(object status);
    public abstract string HidServiceMissing { get; }
    public abstract string ServiceMissing(string name);
    public abstract string TcpNoAnswer(int port);
    public abstract string RemoteXpcNoSettings { get; }
    public abstract string RemoteXpcNoReply { get; }

    // --- The media stream refused ----------------------------------------------

    public abstract string CallInProgress { get; }
    public abstract string RemoteControlNeedsIos27 { get; }
    public abstract string StreamRefused(int? code);

    // --- Lock, restart ------------------------------------------------------------

    public abstract string UnlockForUnmount { get; }
    public abstract string UnlockForUpload { get; }
    public abstract string UnlockToRestartMirror { get; }
    public abstract string RestartIPhone { get; }

    // --- The developer image on the phone ----------------------------------------

    public abstract string BuildManifestUnreadable { get; }
    public abstract string NoPersonalizationIdentifiers { get; }
    public abstract string NoNonce { get; }
    public abstract string NoBuildIdentity(long boardId, long chipId);
    public abstract string AppleSigningServer(string status, int httpStatus);
    public abstract string PhoneNoAnswer(string command, double seconds);
    public abstract string ImageUploadStalled(long sent, long size);

    // --- The developer image out of Apple's archives ----------------------------

    public abstract string FileNotFound(string file);
    public abstract string FileTooShort(string file, long bytes);
    public abstract string XarWithoutContent(string file);
    public abstract string UnexpectedFile(string file, string detail);
    public abstract string NoContentEntry(string file);
    public abstract string ArchiveDamaged(string file, double gigabytes, string detail);
    public abstract string SystemResourcesMissing(string file);

    // --- Input and clipboard ------------------------------------------------------

    public abstract string UnknownButton(string name, string expected);
    public abstract string HidChannelStuck(double milliseconds);
    public abstract string ClipboardNeedsTunnel { get; }
    public abstract string ClipboardServiceMissing { get; }
}
