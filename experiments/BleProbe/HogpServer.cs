using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LuminaMonitor.BleProbe;

/// <summary>
/// One input report to expose: its report ID, a label for the logs, and the
/// payload size in bytes that the report map declares for it.
/// </summary>
/// <remarks>
/// The size is not decoration. iOS reads the Report characteristic during HID
/// configuration — the very read that triggers bonding — and the answer must
/// be exactly as long as the report map promises, even before any input was
/// ever sent. Answering a single zero byte there is the kind of mismatch that
/// makes a host abandon the setup without a word.
/// </remarks>
internal sealed record InputReportSpec(byte ReportId, string Label, int Size);

/// <summary>
/// Publishes a HID-over-GATT peripheral through the in-box Windows BLE stack.
/// </summary>
/// <remarks>
/// Everything here is plain user-mode WinRT — no driver, no service, no
/// packaging. The structure follows the HOGP profile as the two reference
/// implementations proved it against real iPhones: HID Information, Report
/// Map, Protocol Mode and Control Point, one Input Report characteristic per
/// report ID (each carrying a Report Reference descriptor 0x2908), plus a
/// separate Battery service that iOS expects from any HID peripheral.
///
/// <para>Protection levels matter for pairing: the encrypted characteristics
/// are what make iOS start bonding when it first reads them. The digitizer
/// reference keeps everything Plain except the Input Report; the mouse
/// reference encrypts the whole HID service. Both worked, so the caller
/// chooses via <paramref name="encryptEverything"/>.</para>
///
/// <para>Every GATT request handler is written defensively: the deferral is
/// completed by <c>using</c>, <c>GetRequestAsync</c> may return null (request
/// withdrawn) or throw (central gone mid-request), and both happen in the
/// real world precisely during the first encrypted read — the iPhone reads,
/// gets Insufficient Authentication, starts bonding, and the original request
/// is pulled from under the handler. An unhandled exception in these
/// <c>async void</c> handlers kills the process at the exact moment the test
/// was about to succeed.</para>
/// </remarks>
internal sealed class HogpServer
{
    private GattServiceProvider? _hid;
    private GattServiceProvider? _battery;
    private readonly Dictionary<byte, GattLocalCharacteristic> _inputs = [];
    private readonly Dictionary<byte, byte[]> _lastReport = [];
    private byte _protocolMode = 0x01; // report mode

    public static async Task<HogpServer> StartAsync(
        byte[] reportMap, byte[] hidInformation,
        IReadOnlyList<InputReportSpec> reports, bool encryptEverything)
    {
        var server = new HogpServer();

        var adapter = await BluetoothAdapter.GetDefaultAsync()
            ?? throw new InvalidOperationException("Aucun adaptateur Bluetooth.");
        Log($"Adaptateur : peripheral={adapter.IsPeripheralRoleSupported} " +
            $"central={adapter.IsCentralRoleSupported} le={adapter.IsLowEnergySupported}");
        if (!adapter.IsPeripheralRoleSupported)
            throw new InvalidOperationException(
                "La radio ne supporte pas le role peripherique BLE — le banc ne peut pas servir.");

        var baseLevel = encryptEverything
            ? GattProtectionLevel.EncryptionRequired
            : GattProtectionLevel.Plain;

        // --- HID service ----------------------------------------------------
        var hidResult = await GattServiceProvider.CreateAsync(
            GattServiceUuids.HumanInterfaceDevice);
        if (hidResult.Error != BluetoothError.Success)
            throw new InvalidOperationException($"CreateAsync(0x1812) : {hidResult.Error}");
        server._hid = hidResult.ServiceProvider;
        var hid = server._hid.Service;

        await AddCharacteristic(hid, GattCharacteristicUuids.HidInformation,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                StaticValue = ToBuffer(hidInformation),
                ReadProtectionLevel = baseLevel,
            }, "HID Information");
        await AddCharacteristic(hid, GattCharacteristicUuids.ReportMap,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                StaticValue = ToBuffer(reportMap),
                ReadProtectionLevel = baseLevel,
            }, "Report Map");
        Log($"Report map publie ({reportMap.Length} octets).");

        // Protocol Mode: read + write-without-response. iOS writes 0x01 here
        // on some stacks; answering the read matters more than the storage.
        var protocol = await AddCharacteristic(hid, GattCharacteristicUuids.ProtocolMode,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read
                    | GattCharacteristicProperties.WriteWithoutResponse,
                ReadProtectionLevel = baseLevel,
                WriteProtectionLevel = baseLevel,
            }, "Protocol Mode");
        protocol.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;
                request.RespondWithValue(ToBuffer([server._protocolMode]));
                Log("Lecture Protocol Mode.");
            }
            catch (Exception exception)
            {
                Log($"Lecture Protocol Mode interrompue : {exception.Message}");
            }
        };
        protocol.WriteRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;
                var data = ToBytes(request.Value);
                if (data.Length > 0)
                    server._protocolMode = data[0];
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.Respond();
                Log($"Ecriture Protocol Mode = 0x{(data.Length > 0 ? data[0] : 0):X2}.");
            }
            catch (Exception exception)
            {
                Log($"Ecriture Protocol Mode interrompue : {exception.Message}");
            }
        };

        var control = await AddCharacteristic(hid, GattCharacteristicUuids.HidControlPoint,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel = baseLevel,
            }, "HID Control Point");
        control.WriteRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;
                var data = ToBytes(request.Value);
                Log($"Control Point : {(data is [0x00, ..] ? "suspend" : "exit suspend")}.");
            }
            catch (Exception exception)
            {
                Log($"Ecriture Control Point interrompue : {exception.Message}");
            }
        };

        // Input Reports — the encrypted read is what triggers iOS pairing.
        foreach (var spec in reports)
        {
            var characteristic = await AddCharacteristic(hid, GattCharacteristicUuids.Report,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read
                        | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                }, $"Input Report {spec.Label}");

            var descriptor = await characteristic.CreateDescriptorAsync(
                BluetoothUuidHelper.FromShortId(0x2908),
                new GattLocalDescriptorParameters
                {
                    StaticValue = ToBuffer([spec.ReportId, 0x01]),
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                });
            if (descriptor.Error != BluetoothError.Success)
                throw new InvalidOperationException(
                    $"Descripteur 0x2908 de {spec.Label} : {descriptor.Error}");

            byte id = spec.ReportId;
            string label = spec.Label;
            // Full-length zeros from the start — see InputReportSpec.
            server._lastReport[id] = new byte[spec.Size];

            characteristic.ReadRequested += async (_, args) =>
            {
                using var deferral = args.GetDeferral();
                try
                {
                    var request = await args.GetRequestAsync();
                    if (request is null)
                        return;
                    request.RespondWithValue(ToBuffer(server._lastReport[id]));
                    Log($"Lecture du rapport {label}.");
                }
                catch (Exception exception)
                {
                    Log($"Lecture du rapport {label} interrompue : {exception.Message}");
                }
            };
            characteristic.SubscribedClientsChanged += (sender, _) =>
                Log($"*** {label} : {sender.SubscribedClients.Count} abonne(s) — " +
                    (sender.SubscribedClients.Count > 0
                        ? "l'iPhone ecoute ce rapport ***"
                        : "plus personne n'ecoute ***"));

            server._inputs[id] = characteristic;
        }

        // --- Battery service ------------------------------------------------
        // Plain on purpose in both references; iOS reads it during setup.
        var batteryResult = await GattServiceProvider.CreateAsync(GattServiceUuids.Battery);
        if (batteryResult.Error == BluetoothError.Success)
        {
            server._battery = batteryResult.ServiceProvider;
            var level = await AddCharacteristic(server._battery.Service,
                GattCharacteristicUuids.BatteryLevel,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read
                        | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.Plain,
                }, "Battery Level");
            level.ReadRequested += async (_, args) =>
            {
                using var deferral = args.GetDeferral();
                try
                {
                    var request = await args.GetRequestAsync();
                    if (request is null)
                        return;
                    request.RespondWithValue(ToBuffer([100]));
                }
                catch (Exception exception)
                {
                    Log($"Lecture batterie interrompue : {exception.Message}");
                }
            };
        }
        else
        {
            Log($"Service batterie refuse ({batteryResult.Error}) — on continue sans.");
        }

        // --- Advertising ----------------------------------------------------
        server._hid.AdvertisementStatusChanged += (sender, _) =>
            Log($"Annonce HID : {sender.AdvertisementStatus}.");
        server._hid.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true,
        });
        server._battery?.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = false,
        });

        return server;
    }

    /// <summary>Sends one input report; false when nobody is subscribed yet.</summary>
    public async Task<bool> NotifyAsync(byte reportId, byte[] payload)
    {
        if (!_inputs.TryGetValue(reportId, out var characteristic))
            throw new ArgumentException($"Pas de rapport {reportId} dans ce mode.");

        _lastReport[reportId] = payload;
        if (characteristic.SubscribedClients.Count == 0)
            return false;

        var results = await characteristic.NotifyValueAsync(ToBuffer(payload));
        foreach (var r in results)
            if (r.Status != GattCommunicationStatus.Success)
                Log($"Notification en echec : {r.Status} (erreur {r.ProtocolError?.ToString() ?? "-"}).");
        return true;
    }

    public void Stop()
    {
        try { _hid?.StopAdvertising(); } catch (Exception) { }
        try { _battery?.StopAdvertising(); } catch (Exception) { }
    }

    private static async Task<GattLocalCharacteristic> AddCharacteristic(
        GattLocalService service, Guid uuid,
        GattLocalCharacteristicParameters parameters, string name)
    {
        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Caracteristique {name} : {result.Error}");
        return result.Characteristic;
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
}
