using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace LuminaMonitor.App;

/// <summary>
/// Translates Windows keys into HID usage identifiers.
/// </summary>
/// <remarks>
/// The subtlety that makes this file necessary: <b>Windows reports characters,
/// HID carries physical positions.</b> Press the key marked A on an AZERTY
/// keyboard and Windows says <c>Key.A</c>; usage 0x04 is the key at the QWERTY A
/// position, which on AZERTY is Q. Sent as-is, an A comes out as a Q.
///
/// <para>The first version of this file swapped the five letters that move
/// between AZERTY and QWERTY — A↔Q, Z↔W, M — and left everything else alone, on
/// the stated reasoning that "OEM keys are reported by physical position, so they
/// need no swap". That reasoning is wrong, and it is the reason typing stayed
/// broken after the letters were fixed. <c>VK_OEM_*</c> codes are assigned by the
/// <em>layout</em>, not by position: on a French layout <c>VK_OEM_1</c> is the
/// key marked $, next to Enter; on a US layout it is the semicolon, two rows
/// down. Nine of the twelve punctuation keys were therefore sending the usage of
/// a neighbouring key — typing a comma produced a semicolon, and so on.</para>
///
/// <para>The fix is to stop naming keys and start asking where they are. A
/// <b>scan code</b> is the physical position, and <c>MapVirtualKey</c> converts a
/// virtual key back into one using the same layout Windows used to produce it.
/// The two conversions cancel: press the physical Q position on a French
/// keyboard, Windows says <c>VK_A</c>, the scan code comes back 0x10, which is
/// the Q position, which is usage 0x14. On a US keyboard the same key press
/// gives <c>VK_Q</c>, scan code 0x10, usage 0x14. One table, every layout, and
/// no setting to get wrong.</para>
///
/// <para>Scan codes are only consulted for the <b>character</b> keys, because
/// that is the only place a layout rearranges anything. The navigation cluster
/// cannot use them: <c>MAPVK_VK_TO_VSC_EX</c> does not set the extended-key
/// prefix for the arrows, so Left comes back as 0x4B — indistinguishable from
/// keypad 4. Those keys are named unambiguously by WPF and are the same position
/// on every keyboard, so they are listed directly.</para>
///
/// <para><b>The iPhone must stay on a French hardware-keyboard layout</b>
/// (Réglages → Général → Clavier → Claviers physiques). Sending positions only
/// works if the phone reads them with the same layout the key caps are printed
/// with. Switching the phone to US would line the letters up too, but would put
/// é, è, à and ù out of reach — no US position produces them.</para>
/// </remarks>
public static class HidKeyboard
{
    public const byte ModifierControl = 0x01;
    public const byte ModifierShift = 0x02;
    public const byte ModifierAlt = 0x04;
    public const byte ModifierCommand = 0x08;

    public const byte UsageEnter = 0x28;
    public const byte UsageEscape = 0x29;
    public const byte UsageSpace = 0x2C;

    private const uint MapVirtualKeyToScanCode = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint type);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanExW(char character, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint code, uint type, IntPtr layout);

    /// <summary>
    /// Language of the layout the <b>phone</b> reads with. French (France) by
    /// default, because that is what its hardware keyboard is set to.
    /// </summary>
    /// <remarks>
    /// Settable so a phone on another layout can be matched without a rebuild.
    /// </remarks>
    public static int PhoneLanguageId { get; set; } = 0x040C;

    private static IntPtr _phoneLayout;

    /// <summary>
    /// The Windows layout that matches the phone's, whatever Windows is set to
    /// right now.
    /// </summary>
    /// <remarks>
    /// This exists because the paste asked the wrong question. It converts
    /// characters into positions, and the only layout that can answer "which key
    /// makes this character" correctly is <em>the phone's</em> — but the code
    /// used <c>GetKeyboardLayout(0)</c>, which is whatever the user last picked
    /// in the taskbar.
    ///
    /// <para>Both French and US are installed on this machine, and Alt+Shift
    /// switches between them. One stray Alt+Shift and every paste since composed
    /// against US: <c>a</c> arrived as <c>q</c>, <c>m</c> as a comma, <c>1</c> as
    /// an ampersand. The letters were wrong in a way that reads as a broken
    /// translation rather than a changed setting, and it took aligning the
    /// output character by character to see that the positions were US ones.</para>
    ///
    /// <para>Live typing is untouched and does not want this: there, Windows has
    /// already turned a physical key into a virtual key using the current
    /// layout, and mapping it back through that same layout returns the position
    /// the finger actually pressed. The two conversions cancel whatever the
    /// layout is. Only the character-to-position direction needs pinning down.</para>
    /// </remarks>
    private static IntPtr PhoneLayout
    {
        get
        {
            if (_phoneLayout != IntPtr.Zero)
                return _phoneLayout;

            // Search what is installed rather than calling LoadKeyboardLayout:
            // that was tried and handed back the US layout, which would have
            // reintroduced the very fault this is here to remove.
            int count = GetKeyboardLayoutList(0, []);
            if (count > 0)
            {
                var layouts = new IntPtr[count];
                GetKeyboardLayoutList(count, layouts);
                foreach (IntPtr layout in layouts)
                {
                    if ((layout.ToInt64() & 0xFFFF) == PhoneLanguageId)
                    {
                        _phoneLayout = layout;
                        return _phoneLayout;
                    }
                }
            }

            // Not installed. Falling back to the current layout is wrong more
            // often than not, but it is the only thing left, and the paste
            // reports what it could not type.
            _phoneLayout = GetKeyboardLayout(0);
            return _phoneLayout;
        }
    }

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
                                          StringBuilder buffer, int size, uint flags, IntPtr layout);

    /// <summary>Ask what a key produces without arming anything. Windows 10 1607 and later.</summary>
    private const uint DoNotChangeKeyboardState = 0x4;

    /// <summary>
    /// Scan code (set 1) to HID usage, for the character block only.
    /// </summary>
    /// <remarks>
    /// Both sides of this table describe physical positions, so it is fixed for
    /// every keyboard ever made — the layout has already been undone by the time
    /// a scan code is in hand. Positions are named after the US key caps purely
    /// because that is the conventional way to say "the key third from the left
    /// on the home row"; nothing here assumes the keyboard is American.
    /// Zero means a position that carries no character.
    /// </remarks>
    private static readonly byte[] ScanCodeUsage = BuildScanCodeTable();

    private static byte[] BuildScanCodeTable()
    {
        var table = new byte[0x57];

        // Digit row: 1 2 3 4 5 6 7 8 9 0 - =
        for (int i = 0; i < 10; i++)
            table[0x02 + i] = (byte)(0x1E + i);
        table[0x0C] = 0x2D;
        table[0x0D] = 0x2E;

        // Upper letter row: Q W E R T Y U I O P [ ]
        byte[] upper = [0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30];
        for (int i = 0; i < upper.Length; i++)
            table[0x10 + i] = upper[i];

        // Home row: A S D F G H J K L ; ' `
        byte[] home = [0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x35];
        for (int i = 0; i < home.Length; i++)
            table[0x1E + i] = home[i];

        // Lower row: \ Z X C V B N M , . /
        byte[] lower = [0x31, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38];
        for (int i = 0; i < lower.Length; i++)
            table[0x2B + i] = lower[i];

        // The 102nd key: the extra one an ISO keyboard has beside the left
        // shift and an ANSI keyboard does not. On a French layout it carries
        // < and >.
        table[0x56] = 0x64;

        return table;
    }

    /// <summary>
    /// The modifiers to send, with Caps Lock folded into Shift.
    /// </summary>
    /// <remarks>
    /// Caps Lock is <b>never forwarded</b> as a key — see <see cref="Named"/> —
    /// and this is why. It is a latch held on the receiving side, and there is no
    /// way to read the phone's copy of it back: HID input reports go one way.
    /// The two latches only stayed aligned by luck. Press Caps Lock while the
    /// window is out of focus, or while the mouse is not engaged, and Windows
    /// toggles alone; from then on every letter arrives in the wrong case, typed
    /// and pasted alike, with nothing on screen to say so.
    ///
    /// <para>So the phone's latch is left permanently off and case is carried by
    /// Shift, which is stateless. Caps Lock on the PC then behaves as a person
    /// expects — it makes capitals — because the intended case is <c>Shift XOR
    /// CapsLock</c>, which is what a keyboard means by it.</para>
    ///
    /// <para>A phone whose Caps Lock was already latched on before this change
    /// stays that way until it is cleared on the phone itself. Nothing here can
    /// see it, so nothing here can fix it.</para>
    /// </remarks>
    public static byte CurrentModifiers()
    {
        byte modifiers = 0;
        var state = Keyboard.Modifiers;
        if ((state & ModifierKeys.Control) != 0) modifiers |= ModifierControl;
        if ((state & ModifierKeys.Alt) != 0) modifiers |= ModifierAlt;
        if ((state & ModifierKeys.Windows) != 0) modifiers |= ModifierCommand;

        bool shift = (state & ModifierKeys.Shift) != 0;
        if (shift ^ Console.CapsLock)
            modifiers |= ModifierShift;

        return modifiers;
    }

    /// <summary>Returns 0 for keys that carry no usage worth sending.</summary>
    public static byte Translate(Key key)
    {
        // Named keys first. They occupy the same place on every keyboard, and
        // several of them share a scan code with the numeric keypad, so asking
        // for their position would answer the wrong question.
        byte named = Named(key);
        if (named != 0)
            return named;

        return UsageForVirtualKey(KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>The usage for the physical key a virtual key sits on, under the current layout.</summary>
    public static byte UsageForVirtualKey(int virtualKey) =>
        UsageForVirtualKey(virtualKey, GetKeyboardLayout(0));

    /// <summary>The same, under a named layout.</summary>
    /// <remarks>
    /// The layout has to be the one the virtual key came from. A key looked up
    /// with <c>VkKeyScanEx</c> against the phone's layout and then mapped back
    /// through Windows' current one would cross two different keyboards halfway,
    /// and land on a position neither of them meant.
    /// </remarks>
    public static byte UsageForVirtualKey(int virtualKey, IntPtr layout)
    {
        if (virtualKey <= 0)
            return 0;

        uint scanCode = MapVirtualKeyExW((uint)virtualKey, MapVirtualKeyToScanCode, layout);
        return scanCode < ScanCodeUsage.Length ? ScanCodeUsage[scanCode] : (byte)0;
    }

    /// <summary>One key press: which modifiers to hold, and which key to strike.</summary>
    public readonly record struct Keystroke(byte Modifiers, byte Usage);

    /// <summary>
    /// Works out how to type a single character on the keyboard as it is now.
    /// </summary>
    /// <remarks>
    /// <c>VkKeyScanEx</c> answers the question "which key, with which modifiers,
    /// produces this character under this layout" — the exact inverse of what
    /// the app normally does, and it composes with the scan-code translation for
    /// free: the virtual key it returns goes through the same path a real key
    /// press would.
    ///
    /// <para>The high byte carries the modifiers, and <b>Ctrl + Alt together
    /// means AltGr</b>, which is how Windows encodes the third level of a French
    /// layout — where @, #, €, the brackets and the backslash all live. Sending
    /// that as left Ctrl plus left Alt would ask iOS for a keyboard shortcut, not
    /// a character. Right Alt (0x40) is what iOS reads as AltGr, so the pair is
    /// translated rather than forwarded. Getting this wrong would have produced
    /// silence exactly where an email address or a URL needs an @.</para>
    ///
    /// <para>Returns false for characters this layout cannot reach with one
    /// stroke — see <see cref="Compose"/>, which handles the dead keys.</para>
    /// </remarks>
    /// <summary>
    /// The third level, measured on the phone rather than derived from Windows.
    /// </summary>
    /// <remarks>
    /// <b>Apple's French layout is not Microsoft's.</b> The letters agree; the
    /// third level does not, and that is where @, the euro and the brackets
    /// live. Windows says @ is AltGr on the à key; sending that position with
    /// Option produced <c>ø</c> on the phone, and the tilde produced <c>ë</c> —
    /// wrong characters, silently, in the middle of an email address.
    ///
    /// <para>The round-trip check that passed this code originally could never
    /// have caught it: it verified the translation against <em>Windows</em>, and
    /// Windows is not the thing reading the reports.</para>
    ///
    /// <para>So these were measured through the mirror instead — Option held
    /// against each position in turn, reading the result off the phone's own
    /// screen. Positions are named by what the key produces unshifted on a
    /// French keyboard:</para>
    ///
    /// <code>
    /// *  0x31 -> @        $  0x30 -> €        (  0x22 -> {        )  0x2D -> }
    /// </code>
    ///
    /// <para>Each of those four was then confirmed on the phone by pasting them
    /// and reading the note. A fifth, <c>|</c> on the l key, was in this table
    /// and is not any more: it was read off a screenshot where <c>|</c> and
    /// <c>¬</c> are a few pixels apart, and pasting it produced <c>¬</c>. The
    /// difference between a measurement and a reading of a measurement is
    /// exactly the width of that mistake.</para>
    ///
    /// <para>So <c>|</c> joins <c>[</c>, <c>]</c>, <c>\</c>, <c>#</c> and
    /// <c>~</c>: refused and counted rather than guessed at, until someone
    /// measures it properly.</para>
    ///
    /// <para>Alt is 0x04 here, not the 0x40 the AltGr translation used: iOS
    /// reads either as Option, but 0x04 is the one that was actually tested.</para>
    /// </remarks>
    private static readonly Dictionary<char, Keystroke> AppleThirdLevel = new()
    {
        ['@'] = new Keystroke(ModifierAlt, 0x31),
        ['€'] = new Keystroke(ModifierAlt, 0x30),
        ['{'] = new Keystroke(ModifierAlt, 0x22),
        ['}'] = new Keystroke(ModifierAlt, 0x2D),
    };

    public static bool TryTranslate(char character, out Keystroke stroke)
    {
        stroke = default;

        switch (character)
        {
            case '\n': stroke = new Keystroke(0, UsageEnter); return true;
            case '\t': stroke = new Keystroke(0, 0x2B); return true;
            case ' ': stroke = new Keystroke(0, UsageSpace); return true;
            case '\r': return false;          // swallowed; \n carries the newline
        }

        // A control character has no key. VkKeyScanEx answers for them anyway,
        // by mapping them onto the Ctrl+letter chord that would produce them —
        // a vertical tab becomes Ctrl+K, a form feed Ctrl+L, an ESC Ctrl+[.
        // Pasted text picked up from a PDF or a terminal carries these, and
        // sending them to a phone is not typing, it is issuing commands.
        if (char.IsControl(character))
            return false;

        // Half of an emoji is not a character anything can type, and asking the
        // layout about one gets the best-fit answer rather than a refusal.
        if (char.IsSurrogate(character))
            return false;

        // Measured beats derived. Anything in here is known to be right on the
        // phone, which is more than the layout arithmetic can promise.
        if (AppleThirdLevel.TryGetValue(character, out stroke))
            return true;

        IntPtr layout = PhoneLayout;
        if (TryDirect(character, layout, out stroke))
            return true;

        // An accented capital has no key of its own on a French layout, so
        // VkKeyScanEx gives up on É, À, Ù and Ç — and the paste dropped them
        // silently, which in French is most of a sentence's capitals. A
        // keyboard makes them the way a person does: the lower-case key, with
        // Shift.
        char lower = char.ToLowerInvariant(character);
        if (lower != character &&
            TryDirect(lower, layout, out var basis) &&
            (basis.Modifiers & ModifierShift) == 0)
        {
            stroke = new Keystroke((byte)(basis.Modifiers | ModifierShift), basis.Usage);
            return true;
        }

        stroke = default;
        return false;
    }

    /// <summary>One character, one key, under a named layout — or nothing.</summary>
    private static bool TryDirect(char character, IntPtr layout, out Keystroke stroke)
    {
        stroke = default;

        short scan = VkKeyScanExW(character, layout);
        if (scan == -1)
            return false;

        int virtualKey = scan & 0xFF;
        int state = (scan >> 8) & 0xFF;

        // Ctrl+Alt is how Windows encodes AltGr, its third level — and the
        // positions on that level do not agree with Apple's. Anything that
        // needed it and is not in the measured table is refused rather than
        // guessed: a character that fails to appear is reported, while a
        // character that appears wrong is not, and the second is worse.
        if ((state & 2) != 0 && (state & 4) != 0)
            return false;

        if (!Produces(virtualKey, state, character, layout))
            return false;

        byte usage = UsageForVirtualKey(virtualKey, layout);
        if (usage == 0)
            return false;

        byte modifiers = 0;
        if ((state & 1) != 0) modifiers |= ModifierShift;
        if ((state & 2) != 0) modifiers |= ModifierControl;
        if ((state & 4) != 0) modifiers |= ModifierAlt;

        stroke = new Keystroke(modifiers, usage);
        return true;
    }

    /// <summary>
    /// Does that key, with those modifiers, actually make that character?
    /// </summary>
    /// <remarks>
    /// <c>VkKeyScanEx</c> does not answer "no" when a character is absent from
    /// the layout's code page — it answers with the <b>closest thing it has</b>.
    /// An emoji comes back as the key for a question mark; a Greek letter comes
    /// back as some Latin one. The paste then types a plausible wrong character
    /// and reports nothing, which is precisely the failure this code is written
    /// to avoid everywhere else.
    ///
    /// <para>Asking the layout what the key really produces closes that. A dead
    /// key returns a negative count and is allowed through — it is a real key
    /// and <see cref="Compose"/> commits it with a space.</para>
    /// </remarks>
    private static bool Produces(int virtualKey, int state, char expected, IntPtr layout)
    {
        byte[] keys = new byte[256];
        if ((state & 1) != 0) { keys[0x10] = 0x80; keys[0xA0] = 0x80; }
        if ((state & 2) != 0) { keys[0x11] = 0x80; keys[0xA2] = 0x80; }
        if ((state & 4) != 0) { keys[0x12] = 0x80; keys[0xA5] = 0x80; }

        var buffer = new StringBuilder(8);
        int count = ToUnicodeEx((uint)virtualKey,
                                MapVirtualKeyExW((uint)virtualKey, MapVirtualKeyToScanCode, layout),
                                keys, buffer, buffer.Capacity, DoNotChangeKeyboardState, layout);

        if (count < 0)
            return true;                      // dead key, handled by the caller

        string produced = buffer.ToString();
        return count == 1 && produced.Length >= 1 && produced[0] == expected;
    }

    /// <summary>
    /// Combining marks a French layout reaches through a dead key, and the
    /// keystroke that arms each one.
    /// </summary>
    /// <remarks>
    /// Both live on the same physical key — the one at the QWERTY <c>[</c>
    /// position, marked ^ on an AZERTY keyboard — with the diaeresis on shift.
    /// Grave and acute are not here on purpose: à, è, é and ù have keys of their
    /// own on this layout, so they never reach the decomposition path, and there
    /// is no dead key for the letters that would need one.
    /// </remarks>
    private static readonly Dictionary<char, Keystroke> DeadKeys = new()
    {
        ['̂'] = new Keystroke(0, 0x2F),               // circumflex
        ['̈'] = new Keystroke(ModifierShift, 0x2F),   // diaeresis
    };

    /// <summary>
    /// Turns text into the keystrokes that reproduce it, reporting what it could
    /// not.
    /// </summary>
    /// <remarks>
    /// A character the layout cannot type in one stroke is decomposed first:
    /// â is a, preceded by the dead circumflex. Anything still unreachable is
    /// counted and dropped rather than silently replaced, because a paste that
    /// quietly loses a character is worse than one that says it did.
    /// </remarks>
    private static readonly Dictionary<char, bool> DeadKeyCache = [];

    /// <summary>
    /// Does this character sit on a dead key under the current layout?
    /// </summary>
    /// <remarks>
    /// On a French keyboard the circumflex, the tilde (AltGr+2) and the grave
    /// accent (AltGr+7) all wait for a second key before producing anything.
    /// <c>ToUnicodeEx</c> says so by returning a negative count.
    ///
    /// <para>It is asked with <c>DoNotChangeKeyboardState</c>, and that flag is
    /// the whole reason this is safe to call. Without it, probing a dead key
    /// arms it inside Windows itself — so the next character the user typed
    /// anywhere on the machine would come out accented, from a paste they had
    /// already finished.</para>
    /// </remarks>
    private static bool IsDeadKey(char character)
    {
        if (DeadKeyCache.TryGetValue(character, out bool known))
            return known;

        bool dead = false;
        IntPtr layout = PhoneLayout;
        short scan = VkKeyScanExW(character, layout);

        if (scan != -1)
        {
            uint virtualKey = (uint)(scan & 0xFF);
            int state = (scan >> 8) & 0xFF;

            byte[] keys = new byte[256];
            if ((state & 1) != 0) { keys[0x10] = 0x80; keys[0xA0] = 0x80; }   // shift
            if ((state & 2) != 0) { keys[0x11] = 0x80; keys[0xA2] = 0x80; }   // ctrl
            if ((state & 4) != 0) { keys[0x12] = 0x80; keys[0xA5] = 0x80; }   // alt

            var buffer = new StringBuilder(8);
            dead = ToUnicodeEx(virtualKey, MapVirtualKeyExW(virtualKey, MapVirtualKeyToScanCode, layout),
                               keys, buffer, buffer.Capacity, DoNotChangeKeyboardState, layout) < 0;
        }

        DeadKeyCache[character] = dead;
        return dead;
    }

    public static List<Keystroke> Compose(string text, out int skipped)
    {
        // Composed form first. Text copied from macOS or iOS routinely arrives
        // decomposed, where ê is an e followed by a combining circumflex. Left
        // as it comes, the letter types and the mark is refused — so tête
        // arrived as te^te, because the decomposition path below then armed the
        // dead key in the wrong order. Normalising makes the two forms the same
        // problem, which is the one already solved.
        //
        // Guarded, because Normalize throws on a lone surrogate — half of an
        // emoji, which is exactly what a truncated copy leaves behind. Uncaught
        // it would come out of an async void handler and take the window with
        // it. Text that cannot be normalised is used as it came; every
        // character in it still has to survive the checks below.
        try
        {
            text = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
        }

        var strokes = new List<Keystroke>(text.Length + 8);
        skipped = 0;

        foreach (char character in text)
        {
            if (character == '\r')
                continue;

            if (TryTranslate(character, out var stroke))
            {
                strokes.Add(stroke);

                // A dead key produces nothing on its own and accents whatever
                // arrives next. Left alone, pasting "~/chemin" would arm the
                // tilde and hand it to the slash, and "~n" would come out as ñ —
                // a corrupted character rather than a missing one, which is the
                // worse of the two failures. A space after it commits the accent
                // as itself.
                if (IsDeadKey(character))
                    strokes.Add(new Keystroke(0, UsageSpace));
                continue;
            }

            // Nothing below can help a surrogate, and Normalize throws on one
            // rather than declining — the same crash as above, one line further
            // down, reached only once TryTranslate has already refused it.
            if (char.IsSurrogate(character))
            {
                skipped++;
                continue;
            }

            // Split into a base letter and its accent, then arm the dead key.
            string decomposed = character.ToString().Normalize(NormalizationForm.FormD);
            if (decomposed.Length == 2 &&
                DeadKeys.TryGetValue(decomposed[1], out var dead) &&
                TryTranslate(decomposed[0], out var basis))
            {
                strokes.Add(dead);
                strokes.Add(basis);
                continue;
            }

            skipped++;
        }

        return strokes;
    }

    private static byte Named(Key key) => key switch
    {
        Key.Enter => UsageEnter,
        Key.Escape => UsageEscape,
        Key.Back => 0x2A,
        Key.Tab => 0x2B,
        Key.Space => UsageSpace,

        // Caps Lock is deliberately absent. Forwarding it latched a state on the
        // phone that nothing here can read back or reset, and the two copies
        // drifted apart the first time the key was pressed with the window out
        // of focus. Case travels as Shift instead — see CurrentModifiers.

        Key.F1 => 0x3A, Key.F2 => 0x3B, Key.F3 => 0x3C, Key.F4 => 0x3D,
        Key.F5 => 0x3E, Key.F6 => 0x3F, Key.F7 => 0x40, Key.F8 => 0x41,
        Key.F9 => 0x42, Key.F10 => 0x43, Key.F11 => 0x44, Key.F12 => 0x45,

        Key.Insert => 0x49,
        Key.Home => 0x4A,
        Key.PageUp => 0x4B,
        Key.Delete => 0x4C,
        Key.End => 0x4D,
        Key.PageDown => 0x4E,
        Key.Right => 0x4F,
        Key.Left => 0x50,
        Key.Down => 0x51,
        Key.Up => 0x52,

        Key.NumPad0 => 0x62, Key.NumPad1 => 0x59, Key.NumPad2 => 0x5A,
        Key.NumPad3 => 0x5B, Key.NumPad4 => 0x5C, Key.NumPad5 => 0x5D,
        Key.NumPad6 => 0x5E, Key.NumPad7 => 0x5F, Key.NumPad8 => 0x60,
        Key.NumPad9 => 0x61,
        Key.Divide => 0x54, Key.Multiply => 0x55,
        Key.Subtract => 0x56, Key.Add => 0x57, Key.Decimal => 0x63,

        _ => 0,
    };

    /// <summary>
    /// Usage for a single digit, taken from the numeric keypad.
    /// </summary>
    /// <remarks>
    /// The keypad, not the top row. On an AZERTY layout the top row produces
    /// &amp; é " ' ( unshifted — a numeric passcode field discards those outright,
    /// which is exactly why nothing appeared on screen when the digits were sent
    /// from there. Keypad usages mean the same digit on every layout.
    /// </remarks>
    public static byte Digit(char c) => c switch
    {
        >= '1' and <= '9' => (byte)(0x59 + (c - '1')),
        '0' => 0x62,
        _ => 0,
    };
}
