using System.Globalization;
using System.Text;

namespace AvocorCommander.Core;

/// <summary>
/// Translates raw device response bytes into a human-readable description.
/// Series-aware: routes to the correct protocol parser based on SeriesPattern.
/// </summary>
public static class ResponseParser
{
    public static string Parse(byte[] response, byte[] sentBytes, string commandFormat, string seriesPattern = "")
    {
        if (response == null || response.Length == 0)
            return string.Empty;

        // Route to series-specific parser first. H-Series and S-Series share
        // the same ":01 S/G/r <cmd> <data3> <CR>" envelope, as do AVE-9200 and
        // AVL-1050-X which are documented in the same family.
        string? specific = seriesPattern switch
        {
            var s when s.Contains("H-Series") || s.Contains("S-Series") ||
                       s.Contains("H/L")      || s.Contains("9200")     || s.Contains("AVL")
                => ParseHSeries(response),
            var s when s.Contains("K-Series")
                => ParseKSeries(response),
            var s when s.Contains("E-Group1")
                => ParseEGroup1(response),
            var s when s.Contains("E-50")
                => ParseE50(response),
            var s when s.Contains("A-Series")
                => ParseASeries(response, sentBytes),
            var s when s.Contains("B-Series")
                => ParseBSeries(response),
            _ => null
        };
        if (!string.IsNullOrEmpty(specific)) return specific;

        // ── Generic fallbacks ─────────────────────────────────────────────────

        // Standard single-byte codes
        if (response.Length == 1)
        {
            return response[0] switch
            {
                0x06 => "ACK — accepted",
                0x15 => "NAK — rejected",
                0x07 => "Error (BEL)",
                _    => $"0x{response[0]:X2}",
            };
        }

        if (response[0] == 0x06) return $"ACK — {Annotate(response[1..])}";
        if (response[0] == 0x15) return $"NAK — {Annotate(response[1..])}";

        if (sentBytes.Length > 0 && response.SequenceEqual(sentBytes))
            return "Echo — command confirmed";

        if (sentBytes.Length > 0 && response.Length > sentBytes.Length &&
            response.Take(sentBytes.Length).SequenceEqual(sentBytes))
            return $"Echo + {Annotate(response[sentBytes.Length..])}";

        // ASCII-compatible (printable + CR/LF/TAB)
        if (response.All(b => b is (>= 0x20 and < 0x7F) or 0x0D or 0x0A or 0x09))
        {
            var text = DecodeAscii(response);
            if (!string.IsNullOrEmpty(text)) return text;
        }

        // Hex + ASCII sidecar
        return Annotate(response);
    }

    // ── H-Series / S-Series / 9200 ────────────────────────────────────────────
    // Responses are ASCII text.
    // SET ACK:  "401+" (ok) / "401-" (error)  — 3 digits + +/-
    // GET reply: ":01rXDDD"  where X = type char, DDD = 3-digit value

    private static string? ParseHSeries(byte[] rx)
    {
        if (!LooksLikeMostlyAscii(rx)) return null;

        var trimmed = Encoding.ASCII.GetString(rx).Trim('\0', '\r', '\n', ' ', '\t');
        if (trimmed.Length == 0) return null;

        // SET-reply ACK/NAK: "401+" (accepted) or "401-" (rejected by device)
        if (trimmed.Length == 4 &&
            char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) && char.IsDigit(trimmed[2]) &&
            (trimmed[3] == '+' || trimmed[3] == '-'))
        {
            return trimmed[3] == '+'
                ? "Accepted"
                : "Rejected (command not supported or invalid value)";
        }

        // GET response: ":01r<cmd><data3>"  where <cmd> is 1 ASCII char.
        if (trimmed.StartsWith(':') && trimmed.Length >= 8 &&
            trimmed[1] == '0' && trimmed[2] == '1' &&
            (trimmed[3] == 'r' || trimmed[3] == 'R'))
        {
            char typeChar = trimmed[4];
            string data3  = trimmed.Length >= 8 ? trimmed.Substring(5, 3) : trimmed[5..];
            bool hasVal   = int.TryParse(data3, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val);

            return typeChar switch
            {
                // 0x30 '0' — Power / Backlight family (one SET cmd byte for both)
                '0' => hasVal ? val switch
                {
                    0 => "Backlight is OFF",
                    1 => "Backlight is ON",
                    2 => "Power is OFF (standby)",
                    3 => "Power is ON",
                    _ => $"Power/Backlight state = {val}"
                } : $"Power/Backlight state = {data3}",

                '1' => hasVal ? $"Treble = {val:+#;-#;0}"     : $"Treble = {data3}",
                '2' => hasVal ? $"Bass = {val:+#;-#;0}"        : $"Bass = {data3}",
                '3' => hasVal ? $"Balance = {val:+#;-#;0}"     : $"Balance = {data3}",
                '4' => hasVal ? $"Contrast = {val}"            : $"Contrast = {data3}",
                '5' => hasVal ? $"Brightness = {val}"          : $"Brightness = {data3}",
                '6' => hasVal ? $"Sharpness = {val}"           : $"Sharpness = {data3}",

                '7' => hasVal ? $"Sound mode = {SoundModeH(val)}" : $"Sound mode code = {data3}",
                '8' => hasVal ? $"Volume = {val}"                  : $"Volume = {data3}",
                '9' => hasVal ? val == 0 ? "Mute is OFF" : val == 1 ? "Mute is ON" : $"Mute = {val}"
                              : $"Mute = {data3}",

                ':' => hasVal ? $"Current source = {InputNameH(val)}" : $"Source code = {data3}",

                '<' => hasVal ? $"Language = {LanguageNameH(val)}"    : $"Language code = {data3}",
                '=' => hasVal ? $"Picture mode = {PictureModeH(val)}" : $"Picture mode code = {data3}",
                '>' => hasVal ? $"Hue = {val}"                        : $"Hue = {data3}",
                '?' => hasVal ? $"Backlight level = {val}"            : $"Backlight level = {data3}",
                '@' => hasVal ? $"Color temperature = {ColorTempH(val)}" : $"Color temp code = {data3}",

                'B' => hasVal ? val == 0 ? "IR is ENABLED"  : val == 1 ? "IR is DISABLED"  : $"IR = {val}"      : $"IR = {data3}",
                'C' => hasVal ? val == 0 ? "Speaker is OFF" : val == 1 ? "Speaker is ON"   : $"Speaker = {val}" : $"Speaker = {data3}",
                'D' => hasVal ? val == 0 ? "Touch is OFF"   : val == 1 ? "Touch is ON"     : $"Touch = {val}"   : $"Touch = {data3}",
                'E' => hasVal ? val == 0 ? "Screen is OFF"  : val == 1 ? "Screen is ON"    : $"Screen = {val}"  : $"Screen = {data3}",

                'F' => hasVal ? $"No-signal power-off = {NoSignalH(val)}" : $"No-signal code = {data3}",

                'H' => hasVal ? val == 0 ? "HDMI Out is OFF" : val == 1 ? "HDMI Out is ON" : $"HDMI Out = {val}" : $"HDMI Out = {data3}",

                'K' => hasVal ? $"Signal status = {SignalStatusH(val)}" : $"Signal status code = {data3}",

                _   => $"Query reply ({typeChar}) value = {(hasVal ? val.ToString() : data3)}"
            };
        }

        // Generic ASCII fallbacks
        if (trimmed.Contains("ERR", StringComparison.OrdinalIgnoreCase)) return "Error";
        if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))    return "OK";

        return trimmed;
    }

    private static string InputNameH(int code) => code switch
    {
        1   => "HDMI 1",
        2   => "HDMI 2",
        7   => "DisplayPort",
        21  => "HDMI 3",
        22  => "HDMI 4",
        101 => "Home",
        103 => "OPS",
        104 => "USB-C",
        105 => "USB-C 2",
        _   => $"Source code {code}"
    };

    private static string SoundModeH(int code) => code switch
    {
        0 => "Movie", 1 => "Standard", 2 => "Custom", 3 => "Classroom", 4 => "Meeting",
        _ => $"mode {code}"
    };

    private static string PictureModeH(int code) => code switch
    {
        0 => "Standard", 1 => "Bright", 2 => "Soft", 3 => "Customer",
        _ => $"mode {code}"
    };

    private static string ColorTempH(int code) => code switch
    {
        0 => "Cool", 1 => "Standard", 2 => "Warm",
        _ => $"code {code}"
    };

    private static string NoSignalH(int code) => code switch
    {
        0  => "disabled",
        1  => "1 min",
        3  => "3 min",
        5  => "5 min",
        10 => "10 min",
        15 => "15 min",
        30 => "30 min",
        45 => "45 min",
        60 => "60 min",
        _  => $"code {code}"
    };

    private static string SignalStatusH(int code) => code switch
    {
        0 => "no signal",
        1 => "signal present",
        _ => $"code {code}"
    };

    private static string LanguageNameH(int code) => code switch
    {
        0  => "English",   1  => "Français",
        2  => "Español",   3  => "Traditional Chinese",
        4  => "Simplified Chinese", 5 => "Portuguese",
        6  => "German",    7  => "Dutch",
        8  => "Polish",    9  => "Russian",
        10 => "Czech",     11 => "Danish",
        12 => "Swedish",   13 => "Italian",
        14 => "Romanian",  15 => "Norwegian",
        16 => "Finnish",   17 => "Greek",
        18 => "Turkish",   19 => "Arabic",
        20 => "Japanese",  21 => "Ukrainian",
        22 => "Korean",    23 => "Hungarian",
        24 => "Persian",   25 => "Vietnamese",
        26 => "Thai",      27 => "Catalan",
        28 => "Lithuanian",29 => "Croatian",
        30 => "Estonian",
        _  => $"language code {code}"
    };

    // ── K-Series ─────────────────────────────────────────────────────────────
    // Binary 5-byte frames.
    //   55 00 <cmd> <param> <cksum>   — SET (the display echoes this back as the ACK)
    //   AA 00 <cmd> <value> <cksum>   — GET / status
    //
    // For SET the reply bytes are identical to what we sent, so we decode every
    // opcode+param on the way in regardless of whether it's the command going
    // out or its echo coming back.

    private static string? ParseKSeries(byte[] rx)
    {
        if (rx.Length < 5) return null;

        // ── AA frames (GET replies, standby transitions) ───────────────────
        if (rx[0] == 0xAA && rx[1] == 0x00)
        {
            return rx[2] switch
            {
                0x01 => rx[3] switch
                {
                    0x00 => "Display entered standby",
                    0x01 => "Display exited standby",
                    _    => $"Standby state = 0x{rx[3]:X2}"
                },
                0x02 => rx[3] switch
                {
                    0x00 => "Power is OFF (standby)",
                    0x01 => "Power is ON",
                    _    => $"Power state = 0x{rx[3]:X2}"
                },
                0x03 => rx[3] switch
                {
                    0x00 => "Mute is OFF",
                    0x01 => "Mute is ON",
                    _    => $"Mute state = 0x{rx[3]:X2}"
                },
                0x04 => $"Volume = {rx[3]}",
                0x05 => $"Current source = {InputGetK(rx[3])}",
                _    => $"GET reply: cmd=0x{rx[2]:X2}, value=0x{rx[3]:X2}"
            };
        }

        // ── 55 frames (SET commands, echoed by the display) ────────────────
        if (rx[0] == 0x55 && rx[1] == 0x00)
        {
            return (rx[2], rx[3]) switch
            {
                // Remote buttons — opcode 0x00, param selects which button
                (0x00, 0x00) => "Remote: Confirm",
                (0x00, 0x01) => "Remote: Up",
                (0x00, 0x02) => "Remote: Down",
                (0x00, 0x03) => "Remote: Left",
                (0x00, 0x04) => "Remote: Right",
                (0x00, var p) => $"Remote: button 0x{p:X2}",

                (0x0A, _)    => "Remote: Return/Back",
                (0x0C, _)    => "Volume Up (step)",
                (0x0E, _)    => "Volume Down (step)",
                (0x1A, _)    => "Mute toggled",

                // Source select (SET-side codes; different value set to GET)
                (0x80, var p) => $"Source set to {InputSetK(p)}",
                (0x91, _)     => "Source set to Home",

                // Volume set — opcode 0x88 (PDF incorrectly documents 0xFF).
                (0x88, var p) => $"Volume set to {p}",

                // Backlight
                (0x89, var p) => $"Backlight set to {p}",
                (0x8B, var p) => $"Backlight = {p}",     // GET reply reuses 55-prefix

                // Power (opcode 0x8E). 0x00 = on, 0x0F = off.
                (0x8E, 0x00) => "Power ON (confirmed)",
                (0x8E, 0x0F) => "Power OFF (entered standby)",
                (0x8E, var p) => $"Power opcode param=0x{p:X2}",

                (var op, var p) => $"SET frame opcode=0x{op:X2}, param=0x{p:X2}"
            };
        }

        return null;
    }

    /// <summary>Maps the SET-side source parameter bytes to input names.</summary>
    private static string InputSetK(byte code) => code switch
    {
        0x08 => "HDMI 1",
        0x09 => "HDMI 2",
        0x0A => "HDMI 3",
        0x0B => "HDMI 4",
        0x16 => "USB-C",
        0x17 => "Type-C",
        _    => $"source 0x{code:X2}"
    };

    /// <summary>Maps the GET-side source reply bytes to input names.</summary>
    private static string InputGetK(byte code) => code switch
    {
        0x00 => "Home",
        0x01 => "HDMI 1",
        0x02 => "HDMI 2",
        0x03 => "HDMI 3",
        0x04 => "HDMI 4",
        0x05 => "USB-C",
        _    => $"source 0x{code:X2}"
    };

    // ── E-Group1 (AVE / AVF / AVW / AVG) ─────────────────────────────────────
    // Binary frames: 07 01 <type> <C1> <C2> <C3> [payload...] 08

    private static string? ParseEGroup1(byte[] rx)
    {
        if (rx.Length < 7) return null;
        if (rx[0] != 0x07 || rx[1] != 0x01) return null;

        int etx = Array.IndexOf(rx, (byte)0x08);
        if (etx < 6) return null;

        string cmd     = new(new[] { SafeAscii(rx[3]), SafeAscii(rx[4]), SafeAscii(rx[5]) });
        int payLen     = etx - 6;
        byte[] payload = payLen > 0 ? rx[6..etx] : [];

        return cmd.ToUpperInvariant() switch
        {
            "POW" => payLen == 1 ? payload[0] switch
            {
                0x00 => "Power=OFF",
                0x01 => "Power=ON",
                _    => $"Power value=0x{payload[0]:X2}"
            } : "Power (query)",

            "MIN" => payLen == 1 ? $"Input={InputNameE(payload[0])}" : "Input (query)",

            "VOL" => payLen == 1 ? $"Volume={payload[0]}" : "Volume (query)",

            "MUT" => payLen == 1 ? payload[0] switch
            {
                0x00 => "Mute=OFF",
                0x01 => "Mute=ON",
                _    => $"Mute value=0x{payload[0]:X2}"
            } : "Mute (query)",

            _ => payLen > 0
                ? $"Reply {cmd} ({payLen} byte{(payLen == 1 ? "" : "s")})"
                : $"Reply {cmd}"
        };
    }

    private static string InputNameE(byte code) => code switch
    {
        0x00 => "VGA",
        0x09 => "HDMI 1",
        0x0A => "HDMI 2",
        0x0B => "HDMI 3",
        0x0C => "HDMI 4 / USB-C",
        0x0D => "DisplayPort",
        0x0E => "OPS",
        0x13 => "WPS",
        0x14 => "USB-C",
        _    => $"Source 0x{code:X2}"
    };

    // ── E-50 (AVE-5550 / 6550 / 7550 / 8650) ─────────────────────────────────
    // ASCII frames starting with "k01"
    // ACK:   k01n
    // Reply: k01r<cmd><payload>

    private static string? ParseE50(byte[] rx)
    {
        if (rx.Length < 4) return null;
        if (rx[0] != (byte)'k' || rx[1] != (byte)'0' || rx[2] != (byte)'1') return null;

        var s = Encoding.ASCII.GetString(rx).Trim('\0', '\r', '\n', ' ', '\t');
        if (s.Length < 4) return null;

        char kind = s[3];

        // SET ACKs: the E-50 uses both `k01n…` (after Brightness/Contrast/Sharpness
        // Set commands) and `k01y…` (after Power/Mute/Volume/Source/etc Set
        // commands). Treat both as "accepted".
        if (kind is 'n' or 'N' or 'y' or 'Y') return "Accepted";

        if (kind is 'r' or 'R')
        {
            if (s.Length < 5) return "Reply";
            char cmd     = s[4];
            string pay   = s.Length > 5 ? s[5..] : "";
            bool hasNum  = int.TryParse(pay, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num);

            // Date/time replies have a sub-select letter as first payload char:
            //   cmd=p : Year (H), Month (M), Day (D)
            //   cmd=q : Hour (H), Minute (M), Second (S)
            if ((cmd == 'p' || cmd == 'q') && pay.Length >= 1)
            {
                char sub  = pay[0];
                string nn = pay.Length > 1 ? pay[1..] : "";
                bool isNum = int.TryParse(nn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                string label = (cmd, sub) switch
                {
                    ('p', 'H') => "Year",
                    ('p', 'M') => "Month",
                    ('p', 'D') => "Day",
                    ('q', 'H') => "Hour",
                    ('q', 'M') => "Minute",
                    ('q', 'S') => "Second",
                    _          => $"Date/time {cmd}{sub}"
                };
                return isNum ? $"{label} = {n}" : $"{label} = {nn}";
            }

            return cmd switch
            {
                'i' => hasNum ? num switch { 1 => "Power is ON",  0 => "Power is OFF",  _ => $"Power state = {num}" } : $"Power state = {pay}",
                'j' => hasNum ? num switch { 1 => "Remote Lock is ON", 0 => "Remote Lock is OFF", _ => $"Remote Lock = {num}" } : $"Remote Lock = {pay}",
                'g' => hasNum ? num switch { 1 => "Mute is ON",   0 => "Mute is OFF",   _ => $"Mute = {num}" } : $"Mute = {pay}",
                'b' => hasNum ? $"Brightness = {num}" : $"Brightness = {pay}",
                'a' => hasNum ? $"Contrast = {num}"   : $"Contrast = {pay}",
                'c' => hasNum ? $"Sharpness = {num}"  : $"Sharpness = {pay}",
                'f' => hasNum ? $"Volume = {num}"     : $"Volume = {pay}",
                'l' => hasNum ? num switch { 1 => "Button Lock is ON", 0 => "Button Lock is OFF", _ => $"Button Lock = {num}" } : $"Button Lock = {pay}",
                'o' => !string.IsNullOrWhiteSpace(pay) ? $"Uptime = {pay}" : "Uptime",
                'r' => !string.IsNullOrWhiteSpace(pay) ? $"Device Name = {pay}" : "Device Name",
                's' => !string.IsNullOrWhiteSpace(pay) ? $"MAC = {FormatMac(pay) ?? pay}" : "MAC",
                '?' => hasNum ? num switch { 1 => "Touch Lock is ON", 0 => "Touch Lock is OFF", _ => $"Touch Lock = {num}" } : $"Touch Lock = {pay}",
                _   => $"Query reply ({cmd}) value = {pay}"
            };
        }

        return $"Frame kind = {kind}";
    }

    // ── A-Series ─────────────────────────────────────────────────────────────
    // SET reply:  "<prefix><cmd> 00 <2-hex-value>"   e.g. "kh 00 32" (Brightness=50)
    // GET reply:  just the 2-hex value               e.g. "32"       (current value)
    // prefix 'k' = display commands, 'm' = remote/OSD commands.

    private static string? ParseASeries(byte[] rx, byte[]? sent = null)
    {
        if (!LooksLikeMostlyAscii(rx)) return null;
        var s = Encoding.ASCII.GetString(rx).Trim('\0', ' ', '\r', '\n', '\t');
        if (string.IsNullOrWhiteSpace(s)) return null;

        var up = s.ToUpperInvariant();
        if (up.Contains("ERR")) return "Error";
        if (up == "OK")         return "OK";

        // SET-echo reply: "<prefix><cmdChar> <id> <2-hex-value>"
        // e.g. "kh 00 32", "kz 00 01", "ku 00 02", "mc 00 8c"
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 3 && tokens[0].Length == 2 &&
            int.TryParse(tokens[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int setVal))
        {
            char prefix = char.ToLowerInvariant(tokens[0][0]);
            char cmd    = char.ToLowerInvariant(tokens[0][1]);
            return DescribeASeriesSet(prefix, cmd, setVal, tokens[2]);
        }

        // GET-reply: bare 2-hex value (e.g. "32" → 50).
        // Use the sent command byte to determine what the value means.
        // A-Series sent frame: byte[0]=prefix(6B/6D), byte[1]=cmdChar, bytes[2-3]=ID, bytes[4-5]=query(ff)
        if (s.Length == 2 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int getVal))
        {
            if (sent != null && sent.Length >= 2)
            {
                char sentPrefix = char.ToLowerInvariant((char)sent[0]);
                char sentCmd    = char.ToLowerInvariant((char)sent[1]);
                return DescribeASeriesGet(sentPrefix, sentCmd, getVal);
            }
            return $"Value = {getVal} (0x{getVal:X2})";
        }

        if (s.All(c => char.IsDigit(c) || char.IsLetter(c)))
            return $"Reply = {s}";

        return $"Reply = {s}";
    }

    /// <summary>Decodes A-Series GET response values using the sent command byte for context.</summary>
    private static string DescribeASeriesGet(char prefix, char cmd, int val)
    {
        if (prefix == 'k') return cmd switch
        {
            'a' => val switch { 0 => "Power is OFF", 1 => "Power is ON", _ => $"Power state = {val}" },
            'b' => $"Current source = {InputNameA(val)}",
            'c' => val switch { 1 => "Aspect = 16:9", 2 => "Aspect = 4:3", 5 => "Aspect = P2P", _ => $"Aspect = code {val}" },
            'e' => val switch { 0 => "Mute is OFF", 1 => "Mute is ON", _ => $"Mute = {val}" },
            'f' => $"Volume = {val}",
            'g' => $"Contrast = {val}",
            'h' => $"Brightness = {val}",
            'i' => $"Saturation = {val}",
            'o' => $"Hue = {val}",
            'u' => val switch { 0 => "Picture Mode = Standard", 1 => "Picture Mode = Soft", 2 => "Picture Mode = Bright", 3 => "Picture Mode = Customer", _ => $"Picture Mode = code {val}" },
            'z' => val switch { 0 => "Freeze is OFF", 1 => "Freeze is ON", _ => $"Freeze = {val}" },
            _   => $"Value = {val} (cmd={cmd})"
        };

        if (prefix == 'm') return cmd switch
        {
            's' => val switch { 0 => "Remote control is OFF", 1 => "Remote control is ON", _ => $"Remote = {val}" },
            'o' => val switch { 0 => "OSD Lock is OFF", 1 => "OSD Lock is ON", _ => $"OSD Lock = {val}" },
            _   => $"Value = {val} (cmd=m{cmd})"
        };

        return $"Value = {val} (0x{val:X2})";
    }

    private static string DescribeASeriesSet(char prefix, char cmd, int val, string hex)
    {
        // prefix 'k' — display commands
        if (prefix == 'k') return cmd switch
        {
            'a' => val switch { 0 => "Power is OFF", 1 => "Power is ON", _ => $"Power state = {val}" },
            'b' => $"Source set to {InputNameA(val)}",
            'c' => val switch { 1 => "Aspect = 16:9", 2 => "Aspect = 4:3", 5 => "Aspect = P2P", _ => $"Aspect code = {val}" },
            'e' => val switch { 0 => "Mute is OFF", 1 => "Mute is ON", _ => $"Mute state = {val}" },
            'f' => $"Volume set to {val}",
            'g' => $"Contrast set to {val}",
            'h' => $"Brightness set to {val}",
            'i' => $"Saturation set to {val}",
            'o' => $"Hue set to {val}",
            'u' => val switch { 0 => "Picture Mode = Standard", 1 => "Picture Mode = Soft", 2 => "Picture Mode = Bright", _ => $"Picture Mode code = {val}" },
            'v' => val switch { 0 => "Volume Down (step)", 1 => "Volume Up (step)", _ => $"Volume step = {val}" },
            'z' => val switch { 0 => "Freeze OFF", 1 => "Freeze ON", _ => $"Freeze state = {val}" },
            _   => $"SET reply k{cmd}: value = {val}"
        };

        // prefix 'm' — remote / OSD commands
        if (prefix == 'm') return cmd switch
        {
            'c' => $"Remote button {RemoteButtonA(val)} pressed",
            's' => val switch { 0 => "Remote control OFF", 1 => "Remote control ON", _ => $"Remote state = {val}" },
            'o' => val switch { 0 => "OSD Key Lock OFF", 1 => "OSD Key Lock ON", _ => $"OSD Lock state = {val}" },
            _   => $"SET reply m{cmd}: value = {val}"
        };

        return $"SET reply {prefix}{cmd}: value = {val}";
    }

    private static string InputNameA(int code) => code switch
    {
        0x00 => "Home",
        0x07 => "OPS",
        0x08 => "Front HDMI",
        0x09 => "HDMI 1",
        0x0A => "HDMI 2",
        0x0B => "HDMI 3",
        0x0C => "DisplayPort",
        0x0D => "USB-C",
        0x0E => "Front USB-C",
        _    => $"source 0x{code:X2}"
    };

    private static string RemoteButtonA(int code) => code switch
    {
        0x80 => "Right",
        0x8C => "OK",
        0x8D => "Up",
        0x8E => "Down",
        0x8F => "Left",
        0x95 => "Menu",
        0x96 => "Exit",
        0xAC => "Source",
        _    => $"0x{code:X2}"
    };

    // ── B-Series ──────────────────────────────────────────────────────────────
    // ASCII protocol: responses start with "~" followed by the response text.
    // e.g. "~Power On", "~Volume 50", "~Mute Off", "~Error !Power On"

    private static string? ParseBSeries(byte[] rx)
    {
        if (!LooksLikeMostlyAscii(rx)) return null;
        var s = Encoding.ASCII.GetString(rx).Trim('\0', '\r', '\n', ' ', '\t');
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Strip the ~ prefix
        if (s.StartsWith('~'))
            s = s[1..].Trim();

        if (string.IsNullOrEmpty(s)) return "Accepted";

        // Error response: "Error <original command>"
        if (s.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            return "Rejected (" + s + ")";

        // Status query responses: "Power On", "Volume 50", "Mute Off", etc.
        // These are already human-readable — just return them with context.

        // Power state
        if (s.Equals("Power On", StringComparison.OrdinalIgnoreCase))
            return "Power is ON";
        if (s.Equals("Power Off", StringComparison.OrdinalIgnoreCase))
            return "Power is OFF (standby)";

        // Mute state
        if (s.Equals("Mute On", StringComparison.OrdinalIgnoreCase))
            return "Mute is ON";
        if (s.Equals("Mute Off", StringComparison.OrdinalIgnoreCase))
            return "Mute is OFF";

        // Volume / Backlight with numeric value: "Volume 50", "Backlight 75"
        if (s.StartsWith("Volume ", StringComparison.OrdinalIgnoreCase) && s.Length > 7)
        {
            var numPart = s[7..].Trim();
            if (int.TryParse(numPart, out int vol))
                return $"Volume = {vol}";
        }
        if (s.StartsWith("Backlight ", StringComparison.OrdinalIgnoreCase) && s.Length > 10)
        {
            var numPart = s[10..].Trim();
            if (int.TryParse(numPart, out int bl))
                return $"Backlight = {bl}";
        }

        // Input responses: "Input HDMI 1", "Input Android 1", etc.
        if (s.StartsWith("Input ", StringComparison.OrdinalIgnoreCase))
            return $"Current source = {s[6..].Trim()}";

        // Status responses: "Status HDMI 1 Connected", etc.
        if (s.StartsWith("Status ", StringComparison.OrdinalIgnoreCase))
            return s;

        // IR responses (echo of what was sent without the !)
        if (s.StartsWith("IR ", StringComparison.OrdinalIgnoreCase))
            return "Accepted";

        // Generic: already readable text — return as-is
        return s;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DecodeAscii(byte[] rx)
        => Encoding.ASCII.GetString(rx).Replace("\r", "").Replace("\n", " ").Trim();

    private static bool LooksLikeMostlyAscii(byte[] rx, double threshold = 0.85)
    {
        if (rx.Length == 0) return false;
        int printable = rx.Count(b => b is (>= 0x20 and < 0x7F) or 0x0D or 0x0A or 0x09);
        return (double)printable / rx.Length >= threshold;
    }

    private static char SafeAscii(byte b) => b is >= 32 and <= 126 ? (char)b : '.';

    private static string? FormatMac(string raw)
    {
        var t = raw.Trim();
        if (t.Length != 12 || !t.All(c => Uri.IsHexDigit(c))) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => t.Substring(i * 2, 2).ToUpperInvariant()));
    }

    // Returns "XX XX XX  [abc]"
    private static string Annotate(byte[] bytes)
    {
        var hex  = string.Join(" ", bytes.Select(b => $"{b:X2}"));
        var text = new string(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        return text.All(c => c == '.') ? hex : $"{hex}  [{text}]";
    }
}
