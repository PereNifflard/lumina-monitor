using LuminaMonitor.BleProbe;

// Bench for the BLE HID question: can THIS Windows PC drive the owner's
// iPhone directly, and with which pointer semantics? See ReportMaps for the
// three modes and what each one settles.
//
// Usage:  LuminaMonitor.BleProbe <relative|absolute|digitizer> [--cmd <file>]
//
// The probe is driven by appending text lines to the command file, so a test
// session can be conducted from outside the console (the probe usually runs
// as a background task while someone holds the phone):
//
//   keys <texte>            type letters/digits/space/enter (RID 1 modes)
//   move <dx> <dy>          relative pointer move
//   click                   left press + release
//   down / up               hold and release the left button
//   pos <x%> <y%>           absolute: place pointer; digitizer: hover
//   tap <x%> <y%>           absolute: move+click; digitizer: touch down+up
//   drag <x1> <y1> <x2> <y2>  interpolated drag between two % points
//   wheel <n>               wheel notches (mouse modes)
//   status                  log a heartbeat
//   quit                    stop the probe
//
// Percentages are 0..100 of the phone screen, origin top-left.

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "relative";
string cmdPath = "blesonde-cmd.txt";
for (int i = 1; i < args.Length - 1; i++)
    if (args[i] == "--cmd")
        cmdPath = args[i + 1];

(byte[] map, InputReportSpec[] reports, bool encryptAll) = mode switch
{
    "relative" => (ReportMaps.RelativeMap,
        new InputReportSpec[] { new(1, "clavier (RID1)", 8), new(2, "pointeur relatif (RID2)", 6) },
        true),
    "absolute" => (ReportMaps.AbsoluteMap,
        new InputReportSpec[] { new(1, "clavier (RID1)", 8), new(2, "pointeur absolu (RID2)", 6) },
        true),
    "digitizer" => (ReportMaps.DigitizerMap,
        new InputReportSpec[] { new(1, "digitizer (RID1)", 5) },
        false),   // WinBleTouch keeps everything Plain except the input report
    _ => throw new ArgumentException($"Mode inconnu : {mode}"),
};

Say($"=== Banc BLE HID — mode {mode} ===");
Say($"Fichier de commandes : {Path.GetFullPath(cmdPath)}");

var server = await HogpServer.StartAsync(map, ReportMaps.HidInformation, reports, encryptAll);
Say("En annonce. Sur l'iPhone : Reglages > Bluetooth > appairer ce PC.");
if (mode == "digitizer")
    Say("Digitizer : activer d'abord Reglages > Accessibilite > Zoom (plein ecran, 1x, controleur off).");
else
    Say("Pointeur : activer AssistiveTouch pour voir le curseur (le clavier marche sans rien).");

// Pointer state carried between commands so click/down/up compose with pos.
double posX = 50, posY = 50;
byte buttons = 0;

if (!File.Exists(cmdPath))
    File.Create(cmdPath).Dispose();
long offset = new FileInfo(cmdPath).Length;   // only obey lines written from now on

bool running = true;
while (running)
{
    await Task.Delay(150);
    long length = new FileInfo(cmdPath).Length;
    if (length <= offset)
        continue;

    using var stream = new FileStream(cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    stream.Seek(offset, SeekOrigin.Begin);
    using var readerStream = new StreamReader(stream);
    string chunk = await readerStream.ReadToEndAsync();
    offset = length;

    foreach (var raw in chunk.Split('\n'))
    {
        var line = raw.Trim();
        if (line.Length == 0)
            continue;
        Say($"> {line}");
        try
        {
            running &= await ExecuteAsync(line);
        }
        catch (Exception exception)
        {
            Say($"Erreur : {exception.Message}");
        }
    }
}

server.Stop();
Say("Banc arrete.");
return;

async Task<bool> ExecuteAsync(string line)
{
    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    switch (p[0].ToLowerInvariant())
    {
        case "keys":
        {
            if (mode == "digitizer") { Say("Pas de clavier dans le mode digitizer."); break; }
            string text = line[4..].Trim();
            foreach (char c in text.ToLowerInvariant())
            {
                byte usage = c switch
                {
                    >= 'a' and <= 'z' => (byte)(0x04 + (c - 'a')),
                    >= '1' and <= '9' => (byte)(0x1E + (c - '1')),
                    '0' => 0x27,
                    ' ' => 0x2C,
                    '\r' or '\n' => 0x28,
                    _ => 0,
                };
                if (usage == 0)
                    continue;
                await Send(1, ReportMaps.KeyboardReport(0, usage));
                await Task.Delay(15);
                await Send(1, ReportMaps.KeyboardReport(0, 0));
                await Task.Delay(15);
            }
            break;
        }

        case "move":
            RequireMode("relative");
            await Send(2, ReportMaps.MouseRelativeReport(buttons,
                int.Parse(p[1]), int.Parse(p[2]), 0));
            break;

        case "click":
            if (mode == "digitizer") { await Tap(posX, posY); break; }
            await SendPointer(1);
            await Task.Delay(40);
            await SendPointer(0);
            break;

        case "down":
            buttons = 1;
            await SendPointer(buttons);
            break;

        case "up":
            buttons = 0;
            await SendPointer(buttons);
            break;

        case "pos":
            posX = double.Parse(p[1]);
            posY = double.Parse(p[2]);
            if (mode == "absolute")
                await Send(2, ReportMaps.MouseAbsoluteReport(buttons, Sx(posX), Sy(posY), 0));
            else if (mode == "digitizer")
                await Send(1, ReportMaps.DigitizerReport(0x02, Sx(posX), Sy(posY)));
            else
                Say("pos n'a pas de sens en mode relatif — utiliser move.");
            break;

        case "tap":
            await Tap(double.Parse(p[1]), double.Parse(p[2]));
            break;

        case "drag":
        {
            double x1 = double.Parse(p[1]), y1 = double.Parse(p[2]);
            double x2 = double.Parse(p[3]), y2 = double.Parse(p[4]);
            const int steps = 24;
            if (mode == "digitizer")
            {
                for (int i = 0; i <= steps; i++)
                {
                    await Send(1, ReportMaps.DigitizerReport(0x03,
                        Sx(x1 + (x2 - x1) * i / steps), Sy(y1 + (y2 - y1) * i / steps)));
                    await Task.Delay(16);
                }
                await Send(1, ReportMaps.DigitizerReport(0x00, Sx(x2), Sy(y2)));
            }
            else if (mode == "absolute")
            {
                await Send(2, ReportMaps.MouseAbsoluteReport(0, Sx(x1), Sy(y1), 0));
                await Task.Delay(30);
                for (int i = 0; i <= steps; i++)
                {
                    await Send(2, ReportMaps.MouseAbsoluteReport(1,
                        Sx(x1 + (x2 - x1) * i / steps), Sy(y1 + (y2 - y1) * i / steps), 0));
                    await Task.Delay(16);
                }
                await Send(2, ReportMaps.MouseAbsoluteReport(0, Sx(x2), Sy(y2), 0));
            }
            else
            {
                Say("drag n'est pas cable en mode relatif.");
            }
            posX = x2; posY = y2;
            break;
        }

        case "wheel":
            RequireMode("relative", "absolute");
            if (mode == "relative")
                await Send(2, ReportMaps.MouseRelativeReport(buttons, 0, 0, int.Parse(p[1])));
            else
                await Send(2, ReportMaps.MouseAbsoluteReport(buttons, Sx(posX), Sy(posY), int.Parse(p[1])));
            break;

        case "status":
            Say($"Mode {mode}, position {posX:0.#}% / {posY:0.#}%, boutons {buttons}.");
            break;

        case "quit":
            return false;

        default:
            Say($"Commande inconnue : {p[0]}");
            break;
    }
    return true;

    void RequireMode(params string[] allowed)
    {
        if (!allowed.Contains(mode))
            throw new InvalidOperationException($"'{p[0]}' ne vaut que pour : {string.Join(", ", allowed)}.");
    }
}

async Task Tap(double x, double y)
{
    posX = x; posY = y;
    if (mode == "digitizer")
    {
        await Send(1, ReportMaps.DigitizerReport(0x03, Sx(x), Sy(y)));
        await Task.Delay(60);
        await Send(1, ReportMaps.DigitizerReport(0x00, Sx(x), Sy(y)));
    }
    else if (mode == "absolute")
    {
        await Send(2, ReportMaps.MouseAbsoluteReport(0, Sx(x), Sy(y), 0));
        await Task.Delay(40);
        await Send(2, ReportMaps.MouseAbsoluteReport(1, Sx(x), Sy(y), 0));
        await Task.Delay(40);
        await Send(2, ReportMaps.MouseAbsoluteReport(0, Sx(x), Sy(y), 0));
    }
    else
    {
        Say("tap n'a pas de sens en mode relatif — utiliser move puis click.");
    }
}

async Task SendPointer(byte state)
{
    if (mode == "relative")
        await Send(2, ReportMaps.MouseRelativeReport(state, 0, 0, 0));
    else if (mode == "absolute")
        await Send(2, ReportMaps.MouseAbsoluteReport(state, Sx(posX), Sy(posY), 0));
}

async Task Send(byte reportId, byte[] payload)
{
    if (!await server.NotifyAsync(reportId, payload))
        Say("(aucun abonne — l'iPhone n'ecoute pas encore ce rapport)");
}

int Sx(double percent) => (int)Math.Round(percent / 100.0 * (mode == "digitizer" ? 10000 : 32767));
int Sy(double percent) => (int)Math.Round(percent / 100.0 * (mode == "digitizer" ? 10000 : 32767));

static void Say(string message) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
