namespace LuminaMonitor.Core.RemoteXpc;

/// <summary>
/// The envelope every CoreDevice feature is invoked with, and the display
/// service that opens the media stream the HID gate depends on.
/// </summary>
/// <remarks>
/// A CoreDevice request is a RemoteXPC dictionary whose keys all start with
/// <c>CoreDevice.</c>: the protocol version, the client version (629.3 —
/// the number devicectl announces, and the number the daemons have been
/// seen to accept), two fresh identifiers, the feature and action names,
/// and an <c>input</c> dictionary. The daemon answers with
/// <c>CoreDevice.output</c>, or with an error dictionary when it will not.
///
/// <para>Starting a video stream is the one call in this project whose
/// purpose is not the video: while the stream runs, backboardd treats our
/// HID surfaces as built-in and delivers the reports. The RTP arriving on
/// the receiver port is drained and discarded.</para>
/// </remarks>
internal static class CoreDevice
{
    private const string Version = "629.3";

    public static Dictionary<string, object?> Envelope(string feature, string action, Dictionary<string, object?> input) => new()
    {
        ["CoreDevice.CoreDeviceDDIProtocolVersion"] = new XpcInt64(2),
        ["CoreDevice.coreDeviceVersion"] = new Dictionary<string, object?>
        {
            ["components"] = Version.Split('.').Select(c => (object?)new XpcUInt64(ulong.Parse(c))).ToList(),
            ["originalComponentsCount"] = new XpcInt64(Version.Split('.').Length),
            ["stringValue"] = Version,
        },
        ["CoreDevice.deviceIdentifier"] = Guid.NewGuid().ToString(),
        ["CoreDevice.input"] = input,
        ["CoreDevice.invocationIdentifier"] = Guid.NewGuid().ToString(),
        ["CoreDevice.featureIdentifier"] = feature,
        ["CoreDevice.action"] = new Dictionary<string, object?>(),
        ["CoreDevice.actionIdentifier"] = action,
    };

    /// <summary>Invokes a feature and returns CoreDevice.output, or throws with the daemon's answer.</summary>
    public static async Task<Dictionary<string, object?>> InvokeAsync(RemoteXpc service, string feature, string action,
        Dictionary<string, object?> input, TimeSpan? timeout = null)
    {
        var reply = await service.SendReceiveAsync(Envelope(feature, action, input), timeout);
        if (reply.GetValueOrDefault("CoreDevice.output") is Dictionary<string, object?> output)
            return output;
        throw new InvalidOperationException($"{feature} refuse : {Xpc.Dump(reply).Trim()}");
    }
}

/// <summary>The display service: media streams over RTP, negotiated through CoreDevice.</summary>
internal static class DisplayService
{
    public const string ServiceName = "com.apple.coredevice.displayservice";

    /// <summary>The bit mask devicectl announces; no other value has ever been seen on the wire.</summary>
    public const ulong DefaultClientSupportedFeatures = 140;

    /// <summary>What a live Xcode session declares its network to be.</summary>
    public const long DefaultAccessNetworkType = 1;

    /// <summary>What a live Xcode session declares its transport to be.</summary>
    public const long DefaultTransportProtocolType = 2;

    public static Task<Dictionary<string, object?>> GetMediaSupportInfoAsync(RemoteXpc service) =>
        CoreDevice.InvokeAsync(service, "com.apple.coredevice.feature.getmediasupportinfo",
            "com.apple.coredevice.action.mediastreamgetsupportinfo", new Dictionary<string, object?>());

    /// <summary>
    /// Starts an RTP video stream of a display towards our tunnel address.
    /// The receiver port must be listening before the call: the phone starts
    /// sending the moment it answers.
    /// </summary>
    public static Task<Dictionary<string, object?>> StartVideoStreamAsync(RemoteXpc service,
        string receiverIp, ushort receiverPort, string senderIp, XpcUuid sessionId,
        byte[] negotiatorOffer, long displayId = 1, ulong timeoutSeconds = 20,
        ulong clientSupportedFeatures = DefaultClientSupportedFeatures,
        long accessNetworkType = DefaultAccessNetworkType,
        long transportProtocolType = DefaultTransportProtocolType)
    {
        var input = new Dictionary<string, object?>
        {
            ["clientSupportedFeatures"] = new XpcUInt64(clientSupportedFeatures),
            ["direction"] = "output",
            ["negotiatorOffer"] = negotiatorOffer,
            ["options"] = new Dictionary<string, object?>
            {
                ["AVCMediaStreamNegotiatorAccessNetworkType"] = new Dictionary<string, object?> { ["int"] = new XpcInt64(accessNetworkType) },
                ["AVCMediaStreamNegotiatorTransportProtocolType"] = new Dictionary<string, object?> { ["int"] = new XpcInt64(transportProtocolType) },
                ["CoreDeviceVideoDisplayMode"] = new Dictionary<string, object?> { ["string"] = "DisplayByID" },
                ["VideoStreamForDisplayID"] = new Dictionary<string, object?> { ["int"] = new XpcInt64(displayId) },
                ["avcMediaStreamOptionClientSessionID"] = new Dictionary<string, object?> { ["uuid"] = sessionId },
            },
            ["receiverIP"] = receiverIp,
            ["receiverPort"] = new XpcUInt64(receiverPort),
            ["senderIP"] = senderIp,
            ["timeout"] = new XpcUInt64(timeoutSeconds),
            ["type"] = "video",
        };
        return CoreDevice.InvokeAsync(service, "com.apple.coredevice.feature.startmediastream",
            "com.apple.coredevice.action.mediastreamstart", input, TimeSpan.FromSeconds(12));
    }

    /// <summary>Best effort: the phone usually drops the channel while stopping, which is success.</summary>
    public static async Task StopMediaStreamAsync(RemoteXpc service, XpcUuid sessionId)
    {
        try
        {
            await CoreDevice.InvokeAsync(service, "com.apple.coredevice.feature.stopmediastream",
                "com.apple.coredevice.action.mediastreamstop",
                new Dictionary<string, object?>
                {
                    ["avcMediaStreamOptionClientSessionID"] = new Dictionary<string, object?> { ["uuid"] = sessionId },
                }, TimeSpan.FromSeconds(3));
        }
        catch (Exception) { }
    }
}
