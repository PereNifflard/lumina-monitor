using LuminaMonitor.Core.RemoteXpc;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// What a run that was killed rather than closed leaves on the phone, and how
/// to take it back.
/// </summary>
/// <remarks>
/// A media session lives on the phone, not here. <c>startmediastream</c> makes
/// one and <c>stopmediastream</c> ends it, and nothing else does: if this
/// process dies between the two — killed from a task manager, crashed, cable
/// pulled — the phone keeps the session. Two consequences have been seen:
///
/// <list type="bullet">
/// <item>the next offer can be refused outright, error <b>9022</b>, "a phone or
/// VoIP call is in progress", with no call in progress — the ghost session
/// being what the daemon means by a call;</item>
/// <item>and, for an <em>audio</em> stream, iOS treats it as a capture session
/// like a call's: while it stands, the phone's microphone is reserved and the
/// person's other apps hear nothing. That is the symptom this class exists for,
/// and it is a side effect on someone's personal telephone, so the repair
/// cannot wait for them to notice.</item>
/// </list>
///
/// <para>There is no cleanup on the way out that a <c>Stop-Process</c> cannot
/// skip — no finaliser, no <c>ProcessExit</c> handler, no handle — because a
/// killed process runs nothing at all. So the guard is on the way <em>in</em>:
/// every session asks the phone what it still believes is running and ends
/// whatever it finds before opening its own. That is the only order that is
/// robust against a host that can disappear.</para>
/// </remarks>
internal static class MediaHygiene
{
    /// <summary>
    /// Every session identifier the phone's media-stream server still reports.
    /// </summary>
    /// <remarks>
    /// Read out of the answer by shape rather than by a documented path: the
    /// status reply's layout is not published, so every UUID anywhere in it is
    /// taken as a session identifier. That is what <c>stopmediastream</c> takes,
    /// and a UUID that is not one is refused harmlessly.
    /// </remarks>
    public static async Task<List<XpcUuid>> FindSessionsAsync(XpcService display)
    {
        var status = await DisplayService.GetMediaStreamServerStatusAsync(display);
        var found = new List<XpcUuid>();
        Collect(status, found);
        return found;
    }

    /// <summary>
    /// Ends every session the phone still reports, and says how many.
    /// </summary>
    /// <remarks>
    /// Called at the start of a session rather than at the end of one. Every
    /// step is best effort and nothing here throws: a phone that answers
    /// nothing to the status call is a phone we open a stream on anyway, and a
    /// stop that is refused was a stop of something that was not a session.
    /// </remarks>
    /// <param name="keep">
    /// A session to leave alone: the one a stream this host has just opened is
    /// running under. Xcode's mirror groups audio and video under one client
    /// session id, so the audio half's guard would otherwise close the video
    /// half it is being paired with.
    /// </param>
    public static async Task<int> ReleaseOrphansAsync(XpcService display, Action<string>? say = null,
        XpcUuid? keep = null)
    {
        List<XpcUuid> sessions;
        try
        {
            sessions = await FindSessionsAsync(display);
        }
        catch (Exception exception)
        {
            say?.Invoke($"etat du serveur media illisible : {exception.Message}");
            return 0;
        }
        int released = 0;
        foreach (var session in sessions)
        {
            if (keep is XpcUuid ours && ours.Bytes.AsSpan().SequenceEqual(session.Bytes))
                continue;
            try
            {
                await DisplayService.StopMediaStreamAsync(display, session);
                released++;
                say?.Invoke($"session media orpheline liberee : {Convert.ToHexString(session.Bytes)}");
            }
            catch (Exception exception)
            {
                say?.Invoke($"session {Convert.ToHexString(session.Bytes)} non liberee : {exception.Message}");
            }
        }
        return released;
    }

    /// <summary>
    /// The guard every session runs before opening its own stream: on a channel
    /// of its own, ends whatever the phone still believes is running.
    /// </summary>
    /// <remarks>
    /// A channel of its own because the daemon usually closes the channel a
    /// stop was sent on — sharing it with the channel the new stream is about to
    /// be negotiated on would hang up on ourselves. Entirely best effort: a
    /// phone that will not answer the status call is a phone we open a stream on
    /// regardless, since the alternative is refusing to work at all over
    /// something that is usually not there.
    /// </remarks>
    public static async Task<int> ReleaseOrphansAsync(Rsd rsd, Action<string>? say = null, XpcUuid? keep = null)
    {
        try
        {
            var channel = await rsd.OpenAsync(DisplayService.ServiceName);
            try
            {
                int released = await ReleaseOrphansAsync(channel, say, keep);
                if (released > 0)
                    say?.Invoke($"{released} session media orpheline liberee avant l'ouverture du flux"
                        + " — le telephone en gardait la trace d'une execution precedente.");
                return released;
            }
            finally
            {
                try { await channel.CloseAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            }
        }
        catch (Exception exception)
        {
            say?.Invoke($"Etat du serveur media non verifie : {exception.Message}");
            return 0;
        }
    }

    private static void Collect(object? node, List<XpcUuid> found)
    {
        switch (node)
        {
            case XpcUuid uuid:
                if (!found.Any(seen => seen.Bytes.AsSpan().SequenceEqual(uuid.Bytes)))
                    found.Add(uuid);
                break;
            case IDictionary<string, object?> map:
                foreach (object? value in map.Values)
                    Collect(value, found);
                break;
            case System.Collections.IEnumerable list and not string:
                foreach (object? item in list)
                    Collect(item, found);
                break;
        }
    }
}
