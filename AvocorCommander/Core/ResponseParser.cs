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

        // Route to series-specific parser first
        string? specific = seriesPattern switch
        {
            var s when s.Contains("H/L") || s.Contains("9200") || s.Contains("S-Series")
                => ParseHSeries(response),
            var s when s.Contains("K-Series")
                => ParseKSeries(response),
            var s when s.Contains("E-Group1")
                => ParseEGroup1(response),
            var s when s.Contains("E-50")
                => ParseE50(response),
            var s when s.Contains("A-Series")
                => ParseASeries(response),
            var s when s.Contains("B-Series")
                => DecodeAscii(response),
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

        // ACK/NAK: "401+" or "401-"
        if (trimmed.Length == 4 &&
            char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) && char.IsDigit(trimmed[2]) &&
            (trimmed[3] == '+' || trimmed[3] == '-'))
        {
            var code = trimmed[..3];
            bool ok  = trimmed[3] == '+';
            return ok ? $"OK (command {code} accepted)" : $"Error (command {code} rejected)";
        }

        // GET response: ":01rXDDD"
        if (trimmed.StartsWith(':') && trimmed.Length >= 8 &&
            trimmed[1] == '0' && trimmed[2] == '1' &&
            (trimmed[3] == 'r' || trimmed[3] == 'R'))
        {
            char typeChar = trimmed[4];
            string data3  = trimmed.Length >= 8 ? trimmed.Substring(5, 3) : trimmed[5..];
            bool hasVal   = int.TryParse(data3, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val);

            return typeChar switch
            {
                '0' => hasVal ? val switch  // Power / Backlight group
                {
                    0 => "Backlight=OFF",
                    1 => "Backlight=ON",
                    2 => "Power=OFF (Standby)",
                    3 => "Power=ON",
                    _ => $"Power/Backlight state={val}"
                } : $"Power/Backlight state={data3}",

                '8' => hasVal ? $"Volume={val}" : $"Volume={data3}",

                '9' => hasVal ? val switch
                {
                    0 => "Mute=OFF",
                    1 => "Mute=ON",
                    _ => $"Mute={val}"
                } : $"Mute={data3}",

                ':' => hasVal ? $"Input={InputNameH(val)}" : $"Input code={data3}",

                _ => hasVal ? $"Reply type={typeChar} value={val}" : $"Reply type={typeChar} data={data3}"
            };
        }

        // Generic ASCII response
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
        103 => "Slot-in PC",
        104 => "USB-C",
        105 => "USB-C 2",
        _   => $"Source {code}"
    };

    // ── K-Series ─────────────────────────────────────────────────────────────
    // Binary frames: AA 00 ... or 55 00 ...

    private static string? ParseKSeries(byte[] rx)
    {
        if (rx.Length < 2) return null;

        if (rx[0] == 0xAA && rx.Length >= 5 && rx[1] == 0x00)
        {
            return rx[2] switch
            {
                0x01 => rx[3] == 0x00 ? "Standby=ON" :
                        rx[3] == 0x01 ? "Standby=OFF" : $"StandbyState=0x{rx[3]:X2}",
                0x02 => rx[3] == 0x00 ? "Power=STANDBY" :
                        rx[3] == 0x01 ? "Power=ON" : $"Power state=0x{rx[3]:X2}",
                0x03 => rx[3] == 0x00 ? "Mute=OFF" :
                        rx[3] == 0x01 ? "Mute=ON" : $"Mute state=0x{rx[3]:X2}",
                0x04 => $"Volume={rx[3]}",
                0x05 => $"Input={InputNameK(rx[3])}",
                _    => $"AA frame cmd=0x{rx[2]:X2} val=0x{rx[3]:X2}"
            };
        }

        if (rx[0] == 0x55 && rx.Length >= 5 && rx[1] == 0x00)
        {
            if (rx[2] == 0x8E)
                return rx[3] == 0x00 ? "Power=ON" :
                       rx[3] == 0x0F ? "Power=OFF (Standby)" : $"Power param=0x{rx[3]:X2}";
            if (rx[2] == 0x8B)
                return $"Backlight={rx[3]}";

            return $"55 frame opcode=0x{rx[2]:X2} param=0x{rx[3]:X2}";
        }

        return null;
    }

    private static string InputNameK(byte code) => code switch
    {
        0x00 => "Home",
        0x01 => "HDMI 1",
        0x02 => "HDMI 2",
        0x03 => "USB-C",
        _    => $"Source 0x{code:X2}"
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

        if (kind is 'n' or 'N') return "OK (ACK)";

        if (kind is 'r' or 'R')
        {
            if (s.Length < 5) return "Reply";
            char cmd     = s[4];
            string pay   = s.Length > 5 ? s[5..] : "";
            bool hasNum  = int.TryParse(pay, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num);

            return cmd switch
            {
                'i' => hasNum ? num switch { 1 => "Power=ON", 0 => "Power=OFF", _ => $"Power state={num}" } : $"Power state={pay}",
                'j' => hasNum ? num switch { 1 => "Remote Lock=ON", 0 => "Remote Lock=OFF", _ => $"Remote Lock={num}" } : $"Remote Lock={pay}",
                'g' => hasNum ? num switch { 1 => "Mute=ON", 0 => "Mute=OFF", _ => $"Mute={num}" } : $"Mute={pay}",
                'b' => hasNum ? $"Brightness={num}" : $"Brightness={pay}",
                'a' => hasNum ? $"Contrast={num}"   : $"Contrast={pay}",
                'c' => hasNum ? $"Value={num}"       : $"Value={pay}",
                'l' => hasNum ? num switch { 1 => "Button Lock=ON", 0 => "Button Lock=OFF", _ => $"Button Lock={num}" } : $"Button Lock={pay}",
                'r' => !string.IsNullOrWhiteSpace(pay) ? $"Device Name={pay}" : "Device Name",
                's' => !string.IsNullOrWhiteSpace(pay) ? $"MAC={FormatMac(pay) ?? pay}" : "MAC",
                _   => $"Reply cmd={cmd} data={pay}"
            };
        }

        return $"Frame kind={kind}";
    }

    // ── A-Series ─────────────────────────────────────────────────────────────
    // Short ASCII replies: "01", "00", "FF", "OK", "ERR"

    private static string? ParseASeries(byte[] rx)
    {
        if (!LooksLikeMostlyAscii(rx)) return null;
        var s = Encoding.ASCII.GetString(rx).Trim('\0', ' ', '\r', '\n', '\t');
        if (string.IsNullOrWhiteSpace(s)) return null;

        if (s.Length <= 16)
        {
            var up = s.ToUpperInvariant();
            if (up.Contains("ERR"))   return "Error";
            if (up.Contains("OK"))    return "OK";
            if (s.All(char.IsDigit))  return $"Value={s}";
            return $"Reply={s}";
        }
        return null;
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
