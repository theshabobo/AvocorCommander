"""
Avocor Commander V3.5 - Database Seeder
Creates and populates commands.db with RS-232/IP command data only.

User data (StoredDevices, Groups, Macros, etc.) is NOT in this database —
it lives in userdata.db which is created by the app on first run and never
replaced by updates.

Command rows for A-Series, B-Series, E-50, H-Series, K-Series, S-Series
are generated from the canonical inventory:
    V3.0/canonical_inventory.md
E-Group1 and X-Series are preserved verbatim from the prior seed (not in
inventory). Models now include a BaudRate column.
"""
import sqlite3
import os

DB_PATH = os.path.join(os.path.dirname(__file__), "commands.db")

# ── helpers ──────────────────────────────────────────────────────────────────

def a_series_value(hex_val: int) -> str:
    """A-Series: value encoded as 2 ASCII hex chars, e.g. 0x19 → '31 39'"""
    s = format(hex_val, '02x')          # '19'
    return ' '.join(format(ord(c), '02X') for c in s)  # '31 39'

def decimal_ascii_3(val: int) -> str:
    """E-50 / H-Series / S-Series: 3-digit decimal ASCII, e.g. 25 → '30 32 35'"""
    s = format(val, '03d')              # '025'
    return ' '.join(format(ord(c), '02X') for c in s)  # '30 32 35'

def k_checksum(b1, b2, b3, b4) -> int:
    return (b1 + b2 + b3 + b4) % 256

# Percentage levels: (label, hex_value, decimal_value)
LEVELS = [
    ("0",   0x00,  0),
    ("25",  0x19, 25),
    ("50",  0x32, 50),
    ("75",  0x4B, 75),
    ("100", 0x64, 100),
]

# ── schema ───────────────────────────────────────────────────────────────────

SCHEMA = """
CREATE TABLE IF NOT EXISTS DeviceList (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    SeriesPattern   TEXT    NOT NULL,
    CommandCategory TEXT    NOT NULL,
    CommandName     TEXT    NOT NULL,
    CommandCode     TEXT    NOT NULL,
    Notes           TEXT    DEFAULT '',
    Port            INTEGER NOT NULL,
    CommandFormat   TEXT    NOT NULL CHECK(CommandFormat IN ('HEX','ASCII'))
);

CREATE TABLE IF NOT EXISTS Models (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    ModelNumber   TEXT    NOT NULL UNIQUE,
    SeriesPattern TEXT    NOT NULL,
    BaudRate      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS OUITable (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    OUIPrefix     TEXT    NOT NULL UNIQUE,
    SeriesLabel   TEXT    DEFAULT '',
    SeriesPattern TEXT    NOT NULL,
    Notes         TEXT    DEFAULT ''
);
"""

OUI_SEEDS = [
    ("38:54:39", "Avocor S-Series (AVS-xx10)", "S-Series"),
    ("1C:D1:D7", "Avocor B-Series (AVB-xx10)", "B-Series"),
    ("44:37:0B", "Avocor H-Series (AVH-xx20)", "H-Series"),
]

# ── A-Series commands (canonical) ────────────────────────────────────────────

def a(cat, name, code, notes=""):
    return ("A-Series", cat, name, code, notes, 59595, "HEX")

def a_var(category, name_prefix, cmd2_hex, pct_label, hex_val):
    v = a_series_value(hex_val)
    c2 = format(cmd2_hex, '02X')
    return ("A-Series", category, f"{name_prefix} {pct_label}",
            f"6B {c2} 20 30 30 20 {v}", "", 59595, "HEX")

A_SERIES_FIXED = [
    # Power
    a("Power", "Power On",         "6B 61 20 30 30 20 30 31"),
    a("Power", "Power Off",        "6B 61 20 30 30 20 30 30"),
    a("Power", "Get Power State",  "6B 61 20 30 30 20 66 66"),
    # Source
    a("Source", "Home",            "6B 62 20 30 30 20 30 30"),
    a("Source", "OPS",             "6B 62 20 30 30 20 30 37"),
    a("Source", "Front HDMI",      "6B 62 20 30 30 20 30 38"),
    a("Source", "HDMI 1",          "6B 62 20 30 30 20 30 39"),
    a("Source", "HDMI 2",          "6B 62 20 30 30 20 30 61"),
    a("Source", "HDMI 3",          "6B 62 20 30 30 20 30 62"),
    a("Source", "Display Port",    "6B 62 20 30 30 20 30 63"),
    a("Source", "USB-C",           "6B 62 20 30 30 20 30 64"),
    a("Source", "Front USB-C",     "6B 62 20 30 30 20 30 65"),
    a("Source", "Get Source State","6B 62 20 30 30 20 66 66"),
    # Aspect Ratio
    a("Aspect Ratio", "Aspect 16:9",      "6B 63 20 30 30 20 30 31"),
    a("Aspect Ratio", "Aspect 4:3",       "6B 63 20 30 30 20 30 32"),
    a("Aspect Ratio", "Aspect P2P",       "6B 63 20 30 30 20 30 35"),
    a("Aspect Ratio", "Get Aspect State", "6B 63 20 30 30 20 66 66"),
    # Picture Mode
    a("Picture Mode", "Picture Standard",       "6B 75 20 30 30 20 30 30"),
    a("Picture Mode", "Picture Soft",           "6B 75 20 30 30 20 30 31"),
    a("Picture Mode", "Picture Bright",         "6B 75 20 30 30 20 30 32"),
    a("Picture Mode", "Get Picture Mode State", "6B 75 20 30 30 20 66 66"),
    # Picture
    a("Picture", "Get Contrast State",   "6B 67 20 30 30 20 66 66"),
    a("Picture", "Get Brightness State", "6B 68 20 30 30 20 66 66"),
    a("Picture", "Get Saturation State", "6B 69 20 30 30 20 66 66"),
    a("Picture", "Get Hue State",        "6B 6F 20 30 30 20 66 66"),
    a("Picture", "Freeze On",            "6B 7A 20 30 30 20 30 31"),
    a("Picture", "Freeze Off",           "6B 7A 20 30 30 20 30 30"),
    a("Picture", "Get Freeze State",     "6B 7A 20 30 30 20 66 66"),
    # Sound
    a("Sound", "Mute On",          "6B 65 20 30 30 20 30 31"),
    a("Sound", "Mute Off",         "6B 65 20 30 30 20 30 30"),
    a("Sound", "Get Mute State",   "6B 65 20 30 30 20 66 66"),
    a("Sound", "Volume Up",        "6B 76 20 30 30 20 30 31"),
    a("Sound", "Volume Down",      "6B 76 20 30 30 20 30 30"),
    a("Sound", "Get Volume State", "6B 66 20 30 30 20 66 66"),
    # Remote Control
    a("Remote Control", "Remote Menu",      "6D 63 20 30 30 20 39 35"),
    a("Remote Control", "Remote Left",      "6D 63 20 30 30 20 38 66"),
    a("Remote Control", "Remote Up",        "6D 63 20 30 30 20 38 64"),
    a("Remote Control", "Remote OK",        "6D 63 20 30 30 20 38 63"),
    a("Remote Control", "Remote Right",     "6D 63 20 30 30 20 38 30"),
    a("Remote Control", "Remote Down",      "6D 63 20 30 30 20 38 65"),
    a("Remote Control", "Remote Exit",      "6D 63 20 30 30 20 39 36"),
    a("Remote Control", "Remote Source",    "6D 63 20 30 30 20 61 63"),
    a("Remote Control", "Remote On",        "6D 73 20 30 30 20 30 31"),
    a("Remote Control", "Remote Off",       "6D 73 20 30 30 20 30 30"),
    a("Remote Control", "Get Remote State", "6D 73 20 30 30 20 66 66"),
    # OSD Key Lock
    a("OSD Key Lock", "OSD Lock On",         "6D 6F 20 30 30 20 30 31"),
    a("OSD Key Lock", "OSD Lock Off",        "6D 6F 20 30 30 20 30 30"),
    a("OSD Key Lock", "Get OSD Lock State",  "6D 6F 20 30 30 20 66 66"),
]

def build_a_series():
    rows = list(A_SERIES_FIXED)
    for lbl, hv, dv in LEVELS:
        rows.append(a_var("Sound",   "Set Volume",     0x66, lbl, hv))
        rows.append(a_var("Picture", "Set Contrast",   0x67, lbl, hv))
        rows.append(a_var("Picture", "Set Brightness", 0x68, lbl, hv))
        rows.append(a_var("Picture", "Set Saturation", 0x69, lbl, hv))
        rows.append(a_var("Picture", "Set Hue",        0x6F, lbl, hv))
    return rows

# ── B-Series commands (canonical) ────────────────────────────────────────────

def b(cat, name, code, notes=""):
    return ("B-Series", cat, name, code, notes, 10180, "ASCII")

B_SERIES_FIXED = [
    b("System",     "Get Version",            "!Version ?"),
    b("Power",      "Power On",               "!Power On"),
    b("Power",      "Power Off",              "!Power Off"),
    b("Power",      "Power Toggle",           "!Power Toggle"),
    b("Power",      "Get Power State",        "!Power ?"),
    b("Backlight",  "Get Backlight State",    "!Backlight ?"),
    b("Volume",     "Volume Up",              "!Volume Up"),
    b("Volume",     "Volume Down",            "!Volume Down"),
    b("Volume",     "Get Volume State",       "!Volume ?"),
    b("Volume",     "Mute On",                "!Mute On"),
    b("Volume",     "Mute Off",               "!Mute Off"),
    b("Volume",     "Mute Toggle",            "!Mute Toggle"),
    b("Volume",     "Get Mute State",         "!Mute ?"),
    b("Input",      "Home",                   "!Input Android 1"),
    b("Input",      "HDMI 1",                 "!Input HDMI 1"),
    b("Input",      "HDMI 2",                 "!Input HDMI 2"),
    b("Input",      "OPS 1",                  "!Input OPS 1"),
    b("Input",      "Get Input State",        "!Input ?"),
    b("Signal Status","Get Status HDMI 1",    "!Status HDMI 1 ?"),
    b("Signal Status","Get Status HDMI 2",    "!Status HDMI 2 ?"),
    b("Signal Status","Get Status OPS 1",     "!Status OPS 1 ?"),
    b("Application","Get Installed Apps",     "!List ?"),
    b("Application","Open App",               "!Open XYZ", "Replace XYZ with app name"),
    b("Application","Close App",              "!Close XYZ", "Replace XYZ with app name"),
    # IR Emulation — manual spelling (lowercase 'k' in !IR Ok)
    b("IR Emulation","IR Home",               "!IR Home"),
    b("IR Emulation","IR Up",                 "!IR Up"),
    b("IR Emulation","IR Down",               "!IR Down"),
    b("IR Emulation","IR Left",               "!IR Left"),
    b("IR Emulation","IR Right",              "!IR Right"),
    b("IR Emulation","IR OK",                 "!IR Ok"),
    b("IR Emulation","IR Back",               "!IR Back"),
]

def build_b_series():
    rows = list(B_SERIES_FIXED)
    for lbl, hv, dv in LEVELS:
        rows.append(b("Backlight", f"Set Backlight {lbl}", f"!Backlight {dv}"))
        rows.append(b("Volume",    f"Set Volume {lbl}",    f"!Volume {dv}"))
    return rows

# ── E-Group1 commands (preserved verbatim from prior seed) ───────────────────

def eg1(cat, name, code, notes=""):
    return ("E-Group1", cat, name, code, notes, 4664, "HEX")

def eg1_var(cat, name_prefix, cmd_bytes_prefix, pct_label, hex_val):
    """Expand variable XX in 07 01 02 XX XX XX XX 08 format"""
    code = f"{cmd_bytes_prefix} {format(hex_val,'02X')} 08"
    return ("E-Group1", cat, f"{name_prefix} {pct_label}", code, "", 4664, "HEX")

E_GROUP1_FIXED = [
    eg1("Power","Power On",          "07 01 02 50 4F 57 01 08"),
    eg1("Power","Power Off",         "07 01 02 50 4F 57 00 08"),
    eg1("Power","Display Status",    "07 01 01 50 4F 57 08"),
    eg1("Power","Never Sleep",       "07 01 02 57 46 53 02 08"),
    eg1("Power","IPC / OPS Off",     "07 01 02 49 59 43 00 08"),
    eg1("Power","IPC / OPS On",      "07 01 02 49 59 43 01 08"),
    eg1("Factory Reset","Restore Factory Default",                "07 01 02 41 4C 4C 00 08"),
    eg1("Factory Reset","Restore Factory Default Except Comm.",   "07 01 02 41 4C 4C 01 08"),
    eg1("Information","Read Serial Number",    "07 01 01 53 45 52 08"),
    eg1("Information","Read Model Name",       "07 01 01 4D 4E 41 08"),
    eg1("Information","Read Firmware Version", "07 01 01 47 56 45 08"),
    eg1("Information","Read RS232 Table Version","07 01 01 52 54 56 08"),
    eg1("Source","VGA",              "07 01 02 4D 49 4E 00 08"),
    eg1("Source","HDMI 1",           "07 01 02 4D 49 4E 09 08"),
    eg1("Source","HDMI 2",           "07 01 02 4D 49 4E 0A 08"),
    eg1("Source","HDMI 3 / Front HDMI","07 01 02 4D 49 4E 0B 08"),
    eg1("Source","HDMI 4",           "07 01 02 4D 49 4E 0C 08"),
    eg1("Source","DisplayPort",      "07 01 02 4D 49 4E 0D 08"),
    eg1("Source","Type-C (AVE-5530 / AVE-xx30-A)","07 01 02 4D 49 4E 0C 08"),
    eg1("Source","Type-C (AVG-xx60 / AVW-xx55)", "07 01 02 4D 49 4E 14 08"),
    eg1("Source","IPC / OPS",        "07 01 02 4D 49 4E 0E 08"),
    eg1("Source","WPS",              "07 01 02 4D 49 4E 13 08"),
    eg1("Picture Settings","Backlight Off",  "07 01 02 42 4C 43 00 08"),
    eg1("Picture Settings","Backlight On",   "07 01 02 42 4C 43 01 08"),
    eg1("Picture Settings","Noise Reduction Off",    "07 01 02 4E 4F 52 00 08"),
    eg1("Picture Settings","Noise Reduction Low",    "07 01 02 4E 4F 52 01 08"),
    eg1("Picture Settings","Noise Reduction Medium", "07 01 02 4E 4F 52 02 08"),
    eg1("Picture Settings","Noise Reduction High",   "07 01 02 4E 4F 52 03 08"),
    eg1("Picture Settings","Color Temperature User",  "07 01 02 43 4F 54 00 08"),
    eg1("Picture Settings","Color Temperature 5000K", "07 01 02 43 4F 54 06 08"),
    eg1("Picture Settings","Color Temperature 6500K", "07 01 02 43 4F 54 01 08"),
    eg1("Picture Settings","Color Temperature 7500K", "07 01 02 43 4F 54 07 08"),
    eg1("Picture Settings","Color Temperature 9300K", "07 01 02 43 4F 54 02 08"),
    eg1("Picture Settings","Gamma Off", "07 01 02 47 41 43 00 08"),
    eg1("Picture Settings","Gamma On",  "07 01 02 47 41 43 01 08"),
    eg1("Scaling","Point-to-Point", "07 01 02 41 53 50 00 08"),
    eg1("Scaling","16:09",          "07 01 02 41 53 50 01 08"),
    eg1("Scaling","4:03",           "07 01 02 41 53 50 02 08"),
    eg1("Scaling","Letterbox",      "07 01 02 41 53 50 03 08"),
    eg1("Scaling","Auto",           "07 01 02 41 53 50 04 08"),
    eg1("Remote Control","MENU",    "07 01 02 52 43 55 00 08"),
    eg1("Remote Control","INFO",    "07 01 02 52 43 55 01 08"),
    eg1("Remote Control","UP",      "07 01 02 52 43 55 02 08"),
    eg1("Remote Control","DOWN",    "07 01 02 52 43 55 03 08"),
    eg1("Remote Control","LEFT",    "07 01 02 52 43 55 04 08"),
    eg1("Remote Control","RIGHT",   "07 01 02 52 43 55 05 08"),
    eg1("Remote Control","OK",      "07 01 02 52 43 55 06 08"),
    eg1("Remote Control","EXIT",    "07 01 02 52 43 55 07 08"),
    eg1("Remote Control","VGA",     "07 01 02 52 43 55 08 08"),
    eg1("Remote Control","HDMI 1",  "07 01 02 52 43 55 0A 08"),
    eg1("Remote Control","HDMI 2",  "07 01 02 52 43 55 0B 08"),
    eg1("Remote Control","HDMI 3",  "07 01 02 52 43 55 1F 08"),
    eg1("Remote Control","DisplayPort (AVE-xx30-A / AVE-5530)", "07 01 02 52 43 55 22 08"),
    eg1("Remote Control","DisplayPort (AVG-xx60 / AVF-xx50)",   "07 01 02 52 43 55 0C 08"),
    eg1("Remote Control","Type-C (AVE-xx30-A / AVE-5530)",      "07 01 02 52 43 55 23 08"),
    eg1("Remote Control","Type-C (AVG-xx60 / AVW-xx55)",        "07 01 02 52 43 55 2E 08"),
    eg1("Remote Control","OPS",     "07 01 02 52 43 55 21 08"),
    eg1("Remote Control","SCALING", "07 01 02 52 43 55 17 08"),
    eg1("Remote Control","FREEZE",  "07 01 02 52 43 55 18 08"),
    eg1("Remote Control","MUTE",    "07 01 02 52 43 55 19 08"),
    eg1("Remote Control","BRIGHTNESS","07 01 02 52 43 55 1A 08"),
    eg1("Remote Control","CONTRAST", "07 01 02 52 43 55 1B 08"),
    eg1("Remote Control","AUTO",    "07 01 02 52 43 55 1C 08"),
    eg1("Remote Control","VOLUME+", "07 01 02 52 43 55 1D 08"),
    eg1("Remote Control","VOLUME-", "07 01 02 52 43 55 1E 08"),
    eg1("Remote Control","Unlock Remote and Panel Keys","07 01 02 4B 4C 43 00 08"),
    eg1("Remote Control","Lock Remote and Panel Keys",  "07 01 02 4B 4C 43 01 08"),
    eg1("GVS","Query Main Scaler Version",   "07 01 02 47 56 53 00 08"),
    eg1("GVS","Query Sub MCU Version",       "07 01 02 47 56 53 01 08"),
    eg1("GVS","Query Network Module Version","07 01 02 47 56 53 02 08"),
    eg1("Audio","Internal Speaker Off","07 01 02 49 4E 53 00 08"),
    eg1("Audio","Internal Speaker On", "07 01 02 49 4E 53 01 08"),
    eg1("Audio","Mute Off",            "07 01 02 4D 55 54 00 08"),
    eg1("Audio","Mute On",             "07 01 02 4D 55 54 01 08"),
    eg1("Video Scheme (AVG/AVW/AVF)","User",   "07 01 02 53 43 4D 00 08"),
    eg1("Video Scheme (AVG/AVW/AVF)","Sport",  "07 01 02 53 43 4D 01 08"),
    eg1("Video Scheme (AVG/AVW/AVF)","Game",   "07 01 02 53 43 4D 02 08"),
    eg1("Video Scheme (AVG/AVW/AVF)","Cinema", "07 01 02 53 43 4D 03 08"),
    eg1("Video Scheme (AVG/AVW/AVF)","Vivid",  "07 01 02 53 43 4D 04 08"),
    eg1("Video Scheme (AVE-xx30-A/5530)","User",   "07 01 02 53 43 4D 00 08"),
    eg1("Video Scheme (AVE-xx30-A/5530)","Vivid",  "07 01 02 53 43 4D 01 08"),
    eg1("Video Scheme (AVE-xx30-A/5530)","Cinema", "07 01 02 53 43 4D 02 08"),
    eg1("Video Scheme (AVE-xx30-A/5530)","Game",   "07 01 02 53 43 4D 03 08"),
    eg1("Video Scheme (AVE-xx30-A/5530)","Sport",  "07 01 02 53 43 4D 04 08"),
    eg1("Eco Mode","On",               "07 01 02 57 46 53 01 08"),
    eg1("Eco Mode","Off",              "07 01 02 57 46 53 02 08"),
    eg1("Eco Mode","Wake on LAN (AVW-xx55)","07 01 02 57 46 53 03 08"),
    eg1("Auto Scan","Off",             "07 01 02 41 54 53 00 08"),
    eg1("Auto Scan","On",              "07 01 02 41 54 53 01 08"),
    eg1("IRFM","Off",                  "07 01 02 49 52 46 00 08"),
    eg1("IRFM","On",                   "07 01 02 49 52 46 01 08"),
    eg1("Smart Light Control","Off",   "07 01 02 53 4C 43 00 08"),
    eg1("Smart Light Control","DCR",   "07 01 02 53 4C 43 01 08"),
    eg1("Smart Light Control","By Time","07 01 02 53 4C 43 03 08"),
    eg1("Power LED","Off",             "07 01 02 4C 45 44 00 08"),
    eg1("Power LED","On",              "07 01 02 4C 45 44 01 08"),
    eg1("HDMI RGB Color Range","Auto Detect","07 01 02 48 43 52 00 08"),
    eg1("HDMI RGB Color Range","0-255",      "07 01 02 48 43 52 01 08"),
    eg1("HDMI RGB Color Range","16-235",     "07 01 02 48 43 52 02 08"),
    eg1("Touch Control (AVF-xx50)","Auto",   "07 01 02 54 4F 43 00 08"),
    eg1("Touch Control (AVF-xx50)","OPS",    "07 01 02 54 4F 43 01 08"),
    eg1("Touch Control (AVF-xx50)","HDMI 3", "07 01 02 54 4F 43 02 08"),
    eg1("Touch Control (AVF-xx50)","Rear USB","07 01 02 54 4F 43 03 08"),
    eg1("Touch Control (AVF-xx50)","HDMI 4", "07 01 02 54 4F 43 04 08"),
    eg1("Touch Control (AVF-xx50)","WPS",    "07 01 02 54 4F 43 53 08"),
    eg1("Touch Control (AVW-xx55)","Auto",        "07 01 02 54 4F 43 00 08"),
    eg1("Touch Control (AVW-xx55)","USB Touch 1", "07 01 02 54 4F 43 02 08"),
    eg1("Touch Control (AVW-xx55)","USB Touch 2", "07 01 02 54 4F 43 03 08"),
    eg1("Touch Control (AVW-xx55)","USB Touch 3", "07 01 02 54 4F 43 04 08"),
    eg1("OSD Language","English",   "07 01 02 4F 53 4C 00 08"),
    eg1("OSD Language","French",    "07 01 02 4F 53 4C 01 08"),
    eg1("OSD Language","German",    "07 01 02 4F 53 4C 02 08"),
    eg1("OSD Language","Dutch",     "07 01 02 4F 53 4C 03 08"),
    eg1("OSD Language","Danish",    "07 01 02 4F 53 4C 08 08"),
    eg1("OSD Language","Italian",   "07 01 02 4F 53 4C 0D 08"),
    eg1("OSD Language","Swedish",   "07 01 02 4F 53 4C 0E 08"),
    eg1("OSD Language","Portuguese","07 01 02 4F 53 4C 0F 08"),
    eg1("OSD Language","Spanish",   "07 01 02 4F 53 4C 10 08"),
    eg1("OSD Timeout","5 Seconds",  "07 01 02 4F 53 4F 05 08"),
    eg1("OSD Timeout","10 Seconds", "07 01 02 4F 53 4F 0A 08"),
    eg1("OSD Timeout","20 Seconds", "07 01 02 4F 53 4F 14 08"),
    eg1("OSD Timeout","30 Seconds", "07 01 02 4F 53 4F 1E 08"),
    eg1("OSD Timeout","60 Seconds", "07 01 02 4F 53 4F 3C 08"),
]

EG1_VAR_CMDS = [
    ("Picture Settings","Backlight Brightness", "07 01 02 42 52 49"),
    ("Picture Settings","Digital Brightness",   "07 01 02 42 52 4C"),
    ("Picture Settings","Contrast",             "07 01 02 43 4F 4E"),
    ("Picture Settings","Hue",                  "07 01 02 48 55 45"),
    ("Picture Settings","Saturation",           "07 01 02 53 41 54"),
    ("Picture Settings","Sharpness",            "07 01 02 53 48 41"),
    ("Picture Settings","Red Gain",             "07 01 02 55 53 52"),
    ("Picture Settings","Green Gain",           "07 01 02 55 53 47"),
    ("Picture Settings","Blue Gain",            "07 01 02 55 53 42"),
    ("Picture Settings","Red Offset",           "07 01 02 55 4F 52"),
    ("Picture Settings","Green Offset",         "07 01 02 55 4F 47"),
    ("Picture Settings","Blue Offset",          "07 01 02 55 4F 42"),
    ("VGA Adjustment","Phase",                  "07 01 02 50 48 41"),
    ("VGA Adjustment","Clock",                  "07 01 02 43 4C 4F"),
    ("VGA Adjustment","Horizontal Position",    "07 01 02 48 4F 52"),
    ("VGA Adjustment","Vertical Position",      "07 01 02 56 45 52"),
    ("VGA Adjustment","Auto Adjust",            "07 01 02 41 44 4A"),
    ("Scaling","Overscan Ratio Adjust",         "07 01 02 5A 4F 4D"),
    ("Audio","Set Volume Level",                "07 01 02 56 4F 4C"),
    ("Audio","Volume+ by Increment",            "07 01 02 56 4F 49"),
    ("Audio","Volume- by Increment",            "07 01 02 56 4F 44"),
    ("OSD Control","OSD Transparency",          "07 01 02 4F 53 54"),
    ("OSD Control","OSD Horizontal Position",   "07 01 02 4F 53 48"),
    ("OSD Control","OSD Vertical Position",     "07 01 02 4F 53 56"),
]

EG1_AUDIO_RANGE20 = [
    ("Audio","Bass",    "07 01 02 42 41 53"),
    ("Audio","Treble",  "07 01 02 54 52 45"),
    ("Audio","Balance", "07 01 02 42 41 4C"),
]
LEVELS_20 = [("0%",0x00),("25%",0x05),("50%",0x0A),("75%",0x0F),("100%",0x14)]

def build_e_group1():
    rows = list(E_GROUP1_FIXED)
    for cat, name_prefix, prefix in EG1_VAR_CMDS:
        for lbl, hv, dv in LEVELS:
            rows.append(("E-Group1", cat, f"{name_prefix} {lbl}%",
                         f"{prefix} {format(hv,'02X')} 08", "", 4664, "HEX"))
    for cat, name_prefix, prefix in EG1_AUDIO_RANGE20:
        for lbl, hv in LEVELS_20:
            rows.append(("E-Group1", cat, f"{name_prefix} {lbl}",
                         f"{prefix} {format(hv,'02X')} 08", "", 4664, "HEX"))
    return rows

# ── E-50 commands (canonical) ────────────────────────────────────────────────

def e50(cat, name, code, notes=""):
    return ("E-50", cat, name, code, notes, 4660, "HEX")

def e50_var(cat, name_prefix, cmd_byte_hex, pct_label, dec_val):
    d = decimal_ascii_3(dec_val)
    code = f"6B 30 31 73 {cmd_byte_hex} {d} 0D"
    return ("E-50", cat, f"{name_prefix} {pct_label}", code, "", 4660, "HEX")

E50_FIXED = [
    e50("Power","Power On",        "6B 30 31 73 41 30 30 31 0D"),
    e50("Power","Power Off",       "6B 30 31 73 41 30 30 30 0D"),
    e50("Power","Get Power State", "6B 30 31 67 69 30 30 30 0D"),
    e50("Source","Home",           "6B 30 31 73 42 30 30 41 0D"),
    e50("Source","OPS",            "6B 30 31 73 42 30 30 37 0D"),
    e50("Source","Display Port",   "6B 30 31 73 42 30 30 39 0D"),
    e50("Source","HDMI 1",         "6B 30 31 73 42 30 31 34 0D"),
    e50("Source","HDMI 2",         "6B 30 31 73 42 30 32 34 0D"),
    e50("Source","VGA",            "6B 30 31 73 42 30 30 36 0D"),
    e50("Source","Front Type-C",   "6B 30 31 73 42 30 30 43 0D"),
    e50("Source","Rear Type-C",    "6B 30 31 73 42 30 30 40 0D"),
    e50("Remote Control","Remote Up",         "6B 30 31 73 5B 30 30 30 0D"),
    e50("Remote Control","Remote Down",       "6B 30 31 73 5B 30 30 31 0D"),
    e50("Remote Control","Remote Left",       "6B 30 31 73 5B 30 30 32 0D"),
    e50("Remote Control","Remote Right",      "6B 30 31 73 5B 30 30 33 0D"),
    e50("Remote Control","Remote Confirm",    "6B 30 31 73 5B 30 30 34 0D"),
    e50("Remote Control","Remote Return",     "6B 30 31 73 5B 30 30 37 0D"),
    e50("Remote Control","Remote Lock",       "6B 30 31 73 56 30 30 30 0D"),
    e50("Remote Control","Remote Unlock",     "6B 30 31 73 56 30 30 31 0D"),
    e50("Remote Control","Get Remote Lock State", "6B 30 31 67 6A 30 30 30 0D"),
    e50("Volume","Mute Off",          "6B 30 31 73 51 30 30 30 0D"),
    e50("Volume","Mute On",           "6B 30 31 73 51 30 30 31 0D"),
    e50("Volume","Get Mute State",    "6B 30 31 67 67 30 30 30 0D"),
    e50("Volume","Volume Up",         "6B 30 31 73 50 32 30 31 0D"),
    e50("Volume","Volume Down",       "6B 30 31 73 50 32 30 30 0D"),
    e50("Volume","Get Volume State",  "6B 30 31 67 66 30 30 30 0D"),
    e50("Color Mode","Color Normal",  "6B 30 31 73 48 30 30 30 0D"),
    e50("Color Mode","Color Warm",    "6B 30 31 73 48 30 30 31 0D"),
    e50("Color Mode","Color Cool",    "6B 30 31 73 48 30 30 32 0D"),
    e50("Color Mode","Color User",    "6B 30 31 73 48 30 30 33 0D"),
    e50("Brightness","Get Brightness State", "6B 30 31 67 62 30 30 30 0D"),
    e50("Contrast", "Get Contrast State",    "6B 30 31 67 61 30 30 30 0D"),
    e50("Sharpness","Get Sharpness State",   "6B 30 31 67 63 30 30 30 0D"),
    e50("Button Lock","Buttons Lock",          "6B 30 31 73 52 30 30 30 0D"),
    e50("Button Lock","Buttons Unlock",        "6B 30 31 73 52 30 30 31 0D"),
    e50("Button Lock","Get Buttons Lock State","6B 30 31 67 6C 30 30 30 0D"),
    e50("Touch Lock","Touch Lock",             "6B 30 31 73 5C 30 30 30 0D"),
    e50("Touch Lock","Touch Unlock",           "6B 30 31 73 5C 30 30 31 0D"),
    e50("Touch Lock","Get Touch Lock State",   "6B 30 31 67 75 30 30 30 0D"),
    e50("Date/Time","Get Uptime",   "6B 30 31 67 6F 30 30 30 0D"),
    e50("Date/Time","Get Year",     "6B 30 31 67 70 48 30 30 0D"),
    e50("Date/Time","Get Month",    "6B 30 31 67 70 4D 30 30 0D"),
    e50("Date/Time","Get Day",      "6B 30 31 67 70 44 30 30 0D"),
    e50("Date/Time","Get Hour",     "6B 30 31 67 71 48 30 30 0D"),
    e50("Date/Time","Get Minute",   "6B 30 31 67 71 4D 30 30 0D"),
    e50("Date/Time","Get Second",   "6B 30 31 67 71 53 30 30 0D"),
    e50("Information","Get Device Name", "6B 30 31 67 72 30 30 30 0D"),
    e50("Information","Get MAC Address", "6B 30 31 67 73 30 30 30 0D"),
    e50("Factory Reset","Factory Reset", "6B 30 31 73 5A 30 30 30 0D"),
]

E50_VAR_CMDS = [
    ("Volume",    "Set Volume",     "50"),
    ("Bass",      "Set Bass",       "4A"),
    ("Treble",    "Set Treble",     "4B"),
    ("Brightness","Set Brightness", "44"),
    ("Contrast",  "Set Contrast",   "43"),
    ("Sharpness", "Set Sharpness",  "45"),
]

def build_e50():
    rows = list(E50_FIXED)
    for cat, name_prefix, cmd_byte in E50_VAR_CMDS:
        for lbl, hv, dv in LEVELS:
            rows.append(e50_var(cat, name_prefix, cmd_byte, lbl, dv))
    return rows

# ── H-Series commands (canonical; covers AVE-9200, AVL-1050-X, AVH-xx20) ────

def h(cat, name, code, notes=""):
    return ("H-Series", cat, name, code, notes, 4664, "HEX")

def h_var(cat, name_prefix, cmd_byte_hex, pct_label, dec_val):
    d = decimal_ascii_3(dec_val)
    code = f"3A 30 31 53 {cmd_byte_hex} {d} 0D"
    return ("H-Series", cat, f"{name_prefix} {pct_label}", code, "", 4664, "HEX")

H_SERIES_FIXED = [
    # Power
    h("Power","Backlight Off",    "3A 30 31 53 30 30 30 30 0D"),
    h("Power","Backlight On",     "3A 30 31 53 30 30 30 31 0D"),
    h("Power","Power Off",        "3A 30 31 53 30 30 30 32 0D"),
    h("Power","Power On",         "3A 30 31 53 30 30 30 33 0D"),
    h("Power","Screen On",        "3A 30 31 53 45 30 30 31 0D"),
    h("Power","Screen Off",       "3A 30 31 53 45 30 30 30 0D"),
    h("Power","Get Power State",  "3A 30 31 47 30 30 30 30 0D"),
    # Treble
    h("Treble","Treble -5",       "3A 30 31 53 31 2D 30 35 0D"),
    h("Treble","Treble -3",       "3A 30 31 53 31 2D 30 33 0D"),
    h("Treble","Treble +3",       "3A 30 31 53 31 2B 30 33 0D"),
    h("Treble","Treble +5",       "3A 30 31 53 31 2B 30 35 0D"),
    # Bass
    h("Bass","Bass -5",           "3A 30 31 53 32 2D 30 35 0D"),
    h("Bass","Bass -3",           "3A 30 31 53 32 2D 30 33 0D"),
    h("Bass","Bass +3",           "3A 30 31 53 32 2B 30 33 0D"),
    h("Bass","Bass +5",           "3A 30 31 53 32 2B 30 35 0D"),
    # Balance
    h("Balance","Balance -50",    "3A 30 31 53 33 2D 35 30 0D"),
    h("Balance","Balance +20",    "3A 30 31 53 33 2B 32 30 0D"),
    # Sound Mode
    h("Sound Mode","Sound Mode Movie",     "3A 30 31 53 37 30 30 30 0D"),
    h("Sound Mode","Sound Mode Standard",  "3A 30 31 53 37 30 30 31 0D"),
    h("Sound Mode","Sound Mode Custom",    "3A 30 31 53 37 30 30 32 0D"),
    h("Sound Mode","Sound Mode Classroom", "3A 30 31 53 37 30 30 33 0D"),
    h("Sound Mode","Sound Mode Meeting",   "3A 30 31 53 37 30 30 34 0D"),
    # Volume (mute)
    h("Volume","Mute On",   "3A 30 31 53 39 30 30 31 0D"),
    h("Volume","Mute Off",  "3A 30 31 53 39 30 30 30 0D"),
    # Source
    h("Source","Get Source State","3A 30 31 47 3A 30 30 30 0D"),
    h("Source","HDMI 1",          "3A 30 31 53 3A 30 30 31 0D"),
    h("Source","HDMI 2",          "3A 30 31 53 3A 30 30 32 0D"),
    h("Source","HDMI 3",          "3A 30 31 53 3A 30 32 31 0D"),
    h("Source","HDMI 4",          "3A 30 31 53 3A 30 32 32 0D"),
    h("Source","Home",            "3A 30 31 53 3A 31 30 31 0D"),
    h("Source","OPS",             "3A 30 31 53 3A 31 30 33 0D"),
    h("Source","Display Port",    "3A 30 31 53 3A 30 30 37 0D"),
    h("Source","USB-C",           "3A 30 31 53 3A 31 30 34 0D"),
    h("Source","USB-C 2",         "3A 30 31 53 3A 31 30 35 0D","AVE-9200 only"),
    h("Source","Get Signal Status","3A 30 31 47 4B 30 30 30 0D"),
    # Language
    h("Language","Language English",  "3A 30 31 53 3C 30 30 30 0D"),
    h("Language","Language Français", "3A 30 31 53 3C 30 30 31 0D"),
    h("Language","Language Español",  "3A 30 31 53 3C 30 30 32 0D"),
    h("Language","Language Dutch",    "3A 30 31 53 3C 30 30 37 0D"),
    h("Language","Language Italian",  "3A 30 31 53 3C 30 31 33 0D"),
    # Picture Mode
    h("Picture Mode","Picture Standard",  "3A 30 31 53 3D 30 30 30 0D"),
    h("Picture Mode","Picture Bright",    "3A 30 31 53 3D 30 30 31 0D"),
    h("Picture Mode","Picture Soft",      "3A 30 31 53 3D 30 30 32 0D"),
    h("Picture Mode","Picture Customer",  "3A 30 31 53 3D 30 30 33 0D"),
    # Color Temperature
    h("Color Temperature","Color Temp Cool",     "3A 30 31 53 40 30 30 30 0D"),
    h("Color Temperature","Color Temp Standard", "3A 30 31 53 40 30 30 31 0D"),
    h("Color Temperature","Color Temp Warm",     "3A 30 31 53 40 30 30 32 0D"),
    # IR
    h("IR","IR Enable",  "3A 30 31 53 42 30 30 30 0D"),
    h("IR","IR Disable", "3A 30 31 53 42 30 30 31 0D"),
    # Speaker
    h("Speaker","Speaker On",  "3A 30 31 53 43 30 30 31 0D"),
    h("Speaker","Speaker Off", "3A 30 31 53 43 30 30 30 0D"),
    # Touch
    h("Touch","Touch On",  "3A 30 31 53 44 30 30 31 0D"),
    h("Touch","Touch Off", "3A 30 31 53 44 30 30 30 0D"),
    # No Signal Power Off
    h("No Signal Power Off","No Signal Off",    "3A 30 31 53 46 30 30 30 0D"),
    h("No Signal Power Off","No Signal 1 Min",  "3A 30 31 53 46 30 30 31 0D"),
    h("No Signal Power Off","No Signal 3 Min",  "3A 30 31 53 46 30 30 33 0D"),
    h("No Signal Power Off","No Signal 5 Min",  "3A 30 31 53 46 30 30 35 0D"),
    h("No Signal Power Off","No Signal 10 Min", "3A 30 31 53 46 30 31 30 0D"),
    h("No Signal Power Off","No Signal 15 Min", "3A 30 31 53 46 30 31 35 0D"),
    h("No Signal Power Off","No Signal 30 Min", "3A 30 31 53 46 30 33 30 0D"),
    h("No Signal Power Off","No Signal 45 Min", "3A 30 31 53 46 30 34 35 0D"),
    h("No Signal Power Off","No Signal 60 Min", "3A 30 31 53 46 30 36 30 0D"),
    # HDMI Out
    h("HDMI Out","HDMI Out On",  "3A 30 31 53 48 30 30 31 0D"),
    h("HDMI Out","HDMI Out Off", "3A 30 31 53 48 30 30 30 0D"),
    # Remote Control
    h("Remote Control","Remote Volume Up",   "3A 30 31 53 41 30 30 30 0D"),
    h("Remote Control","Remote Volume Down", "3A 30 31 53 41 30 30 31 0D"),
    h("Remote Control","Remote Up",          "3A 30 31 53 41 30 31 30 0D"),
    h("Remote Control","Remote Down",        "3A 30 31 53 41 30 31 31 0D"),
    h("Remote Control","Remote Left",        "3A 30 31 53 41 30 31 32 0D"),
    h("Remote Control","Remote Right",       "3A 30 31 53 41 30 31 33 0D"),
    h("Remote Control","Remote Enter",       "3A 30 31 53 41 30 31 34 0D"),
    h("Remote Control","Remote Menu",        "3A 30 31 53 41 30 32 30 0D"),
    h("Remote Control","Remote Input",       "3A 30 31 53 41 30 32 31 0D"),
    h("Remote Control","Remote Back",        "3A 30 31 53 41 30 32 32 0D"),
    h("Remote Control","Remote Blank",       "3A 30 31 53 41 30 33 31 0D"),
    h("Remote Control","Remote Freeze",      "3A 30 31 53 41 30 33 32 0D"),
    h("Remote Control","Remote Mute",        "3A 30 31 53 41 30 33 33 0D"),
    h("Remote Control","Remote Home",        "3A 30 31 53 41 30 33 34 0D"),
]

H_VAR_CMDS = [
    ("Volume",     "Set Volume",     "38"),
    ("Contrast",   "Set Contrast",   "34"),
    ("Brightness", "Set Brightness", "35"),
    ("Sharpness",  "Set Sharpness",  "36"),
    ("Picture",    "Set Hue",        "3E"),
    ("Picture",    "Set Backlight",  "3F"),
]

def build_h_series():
    rows = list(H_SERIES_FIXED)
    for cat, name_prefix, cmd_byte in H_VAR_CMDS:
        for lbl, hv, dv in LEVELS:
            rows.append(h_var(cat, name_prefix, cmd_byte, lbl, dv))
    return rows

# ── K-Series commands (canonical) ────────────────────────────────────────────

def k(cat, name, code, notes=""):
    return ("K-Series", cat, name, code, notes, 59596, "HEX")

def k_vol_row(pct_label, hex_val):
    # Opcode 0x0F (not 0xFF as documented, not 0x88 as previously tested).
    # 0x88 echoes but doesn't change volume; 0x0F is the correct SET Volume opcode.
    cs = k_checksum(0x55, 0x00, 0x0F, hex_val)
    return ("K-Series","Volume",f"Set Volume {pct_label}",
            f"55 00 0F {format(hex_val,'02X')} {format(cs,'02X')}","",59596,"HEX")

def k_backlight_row(pct_label, hex_val):
    cs = k_checksum(0x55, 0x00, 0x89, hex_val)
    return ("K-Series","Backlight",f"Set Backlight {pct_label}",
            f"55 00 89 {format(hex_val,'02X')} {format(cs,'02X')}","",59596,"HEX")

K_SERIES_FIXED = [
    # Power
    k("Power","Power On",        "55 00 8E 00 E3","used when device is not claimed in Fuse"),
    k("Power","Power Off",       "55 00 8E 0F F2","used to shut down / boot Android"),
    k("Power","Wake",            "AA 00 01 01 AC","used when device is claimed in Fuse"),
    k("Power","Standby",         "AA 00 01 00 AB","used to enter Standby when claimed in Fuse"),
    k("Power","Get Power State", "AA 00 02 00 AC"),
    # Remote Control
    k("Remote Control","Remote Up",      "55 00 00 01 56"),
    k("Remote Control","Remote Down",    "55 00 00 02 57"),
    k("Remote Control","Remote Left",    "55 00 00 03 58"),
    k("Remote Control","Remote Right",   "55 00 00 04 59"),
    k("Remote Control","Remote Confirm", "55 00 00 00 55"),
    k("Remote Control","Remote Return",  "55 00 0A 00 5F"),
    # Volume
    k("Volume","Mute Toggle",      "55 00 1A 00 6F"),
    k("Volume","Get Mute State",   "AA 00 03 00 AD"),
    k("Volume","Volume Up",        "55 00 0C 00 61","steps 1%"),
    k("Volume","Volume Down",      "55 00 0E 00 63","steps 1%"),
    k("Volume","Get Volume State", "AA 00 04 00 AE"),
    # Backlight
    k("Backlight","Get Backlight State","55 00 8B 00 E0"),
    # Source
    k("Source","Home",             "55 00 91 00 E6"),
    k("Source","HDMI 1",           "55 00 80 08 DD"),
    k("Source","HDMI 2",           "55 00 80 09 DE"),
    k("Source","USB-C",            "55 00 80 16 EB"),
    k("Source","Get Source State", "AA 00 05 00 AF"),
]

def build_k_series():
    rows = list(K_SERIES_FIXED)
    for lbl, hv, dv in LEVELS:
        rows.append(k_vol_row(lbl, hv))
        rows.append(k_backlight_row(lbl, hv))
    return rows

# ── S-Series commands (canonical) ────────────────────────────────────────────

def s(cat, name, code, notes=""):
    return ("S-Series", cat, name, code, notes, 4664, "HEX")

def s_var(cat, name_prefix, cmd_byte_hex, pct_label, dec_val):
    d = decimal_ascii_3(dec_val)
    code = f"3A 30 31 53 {cmd_byte_hex} {d} 0D"
    return ("S-Series", cat, f"{name_prefix} {pct_label}", code, "", 4664, "HEX")

# TODO: add S-Series Set Treble/Bass/Balance helpers
#   (signed 2-digit decimal after sign; ranges ±5 / ±5 / ±50)

S_SERIES_FIXED = [
    # Power
    s("Power","Backlight Off",   "3A 30 31 53 30 30 30 30 0D"),
    s("Power","Backlight On",    "3A 30 31 53 30 30 30 31 0D"),
    s("Power","Power Off",       "3A 30 31 53 30 30 30 32 0D"),
    s("Power","Power On",        "3A 30 31 53 30 30 30 33 0D"),
    s("Power","Get Power State", "3A 30 31 47 30 30 30 30 0D"),
    # Treble
    s("Treble","Treble -5",        "3A 30 31 53 31 2D 30 35 0D"),
    s("Treble","Treble -3",        "3A 30 31 53 31 2D 30 33 0D"),
    s("Treble","Treble +3",        "3A 30 31 53 31 2B 30 33 0D"),
    s("Treble","Treble +5",        "3A 30 31 53 31 2B 30 35 0D"),
    s("Treble","Get Treble State", "3A 30 31 47 31 30 30 30 0D"),
    # Bass
    s("Bass","Bass -5",          "3A 30 31 53 32 2D 30 35 0D"),
    s("Bass","Bass -3",          "3A 30 31 53 32 2D 30 33 0D"),
    s("Bass","Bass +3",          "3A 30 31 53 32 2B 30 33 0D"),
    s("Bass","Bass +5",          "3A 30 31 53 32 2B 30 35 0D"),
    s("Bass","Get Bass State",   "3A 30 31 47 32 30 30 30 0D"),
    # Balance
    s("Balance","Balance -50",        "3A 30 31 53 33 2D 35 30 0D"),
    s("Balance","Balance +20",        "3A 30 31 53 33 2B 32 30 0D"),
    s("Balance","Get Balance State",  "3A 30 31 47 33 30 30 30 0D"),
    # Contrast / Brightness / Sharpness Get
    s("Contrast",  "Get Contrast State",   "3A 30 31 47 34 30 30 30 0D"),
    s("Brightness","Get Brightness State", "3A 30 31 47 35 30 30 30 0D"),
    s("Sharpness", "Get Sharpness State",  "3A 30 31 47 36 30 30 30 0D"),
    # Sound Mode
    s("Sound Mode","Sound Mode Standard",  "3A 30 31 53 37 30 30 31 0D"),
    s("Sound Mode","Sound Mode Custom",    "3A 30 31 53 37 30 30 32 0D"),
    s("Sound Mode","Sound Mode Classroom", "3A 30 31 53 37 30 30 33 0D"),
    s("Sound Mode","Sound Mode Meeting",   "3A 30 31 53 37 30 30 34 0D"),
    s("Sound Mode","Get Sound Mode State", "3A 30 31 47 37 30 30 30 0D"),
    # Mute
    s("Mute","Mute Off",       "3A 30 31 53 39 30 30 30 0D"),
    s("Mute","Mute On",        "3A 30 31 53 39 30 30 31 0D"),
    s("Mute","Get Mute State", "3A 30 31 47 39 30 30 30 0D"),
    # Source
    s("Source","VGA",               "3A 30 31 53 3A 30 30 30 0D"),
    s("Source","HDMI 1",            "3A 30 31 53 3A 30 30 31 0D"),
    s("Source","HDMI 2",            "3A 30 31 53 3A 30 30 32 0D"),
    s("Source","HDMI 3",            "3A 30 31 53 3A 30 32 31 0D"),
    s("Source","Home",              "3A 30 31 53 3A 31 30 31 0D"),
    s("Source","OPS",               "3A 30 31 53 3A 31 30 33 0D"),
    s("Source","Type-C 1",          "3A 30 31 53 3A 31 30 34 0D"),
    s("Source","Get Source State",  "3A 30 31 47 3A 30 30 30 0D"),
    # Aspect Ratio (GET only)
    s("Aspect Ratio","Get Aspect Ratio State", "3A 30 31 47 3B 30 30 30 0D","0=16:9,1=4:3,2=PTP"),
    # Language
    s("Language","Language English",              "3A 30 31 53 3C 30 30 30 0D"),
    s("Language","Language Français",             "3A 30 31 53 3C 30 30 31 0D"),
    s("Language","Language Español",              "3A 30 31 53 3C 30 30 32 0D"),
    s("Language","Language Traditional Chinese",  "3A 30 31 53 3C 30 30 33 0D"),
    s("Language","Language Simplified Chinese",   "3A 30 31 53 3C 30 30 34 0D"),
    s("Language","Language Portuguese",           "3A 30 31 53 3C 30 30 35 0D"),
    s("Language","Language German",               "3A 30 31 53 3C 30 30 36 0D"),
    s("Language","Language Dutch",                "3A 30 31 53 3C 30 30 37 0D"),
    s("Language","Language Polish",               "3A 30 31 53 3C 30 30 38 0D"),
    s("Language","Language Russian",              "3A 30 31 53 3C 30 30 39 0D"),
    s("Language","Language Czech",                "3A 30 31 53 3C 30 31 30 0D"),
    s("Language","Language Danish",               "3A 30 31 53 3C 30 31 31 0D"),
    s("Language","Language Swedish",              "3A 30 31 53 3C 30 31 32 0D"),
    s("Language","Language Italian",              "3A 30 31 53 3C 30 31 33 0D"),
    s("Language","Language Romanian",             "3A 30 31 53 3C 30 31 34 0D"),
    s("Language","Language Norwegian",            "3A 30 31 53 3C 30 31 35 0D"),
    s("Language","Language Finnish",              "3A 30 31 53 3C 30 31 36 0D"),
    s("Language","Language Greek",                "3A 30 31 53 3C 30 31 37 0D"),
    s("Language","Language Turkish",              "3A 30 31 53 3C 30 31 38 0D"),
    s("Language","Language Arabic",               "3A 30 31 53 3C 30 31 39 0D"),
    s("Language","Language Japanese",             "3A 30 31 53 3C 30 32 30 0D"),
    s("Language","Language Ukrainian",            "3A 30 31 53 3C 30 32 31 0D"),
    s("Language","Language Korean",               "3A 30 31 53 3C 30 32 32 0D"),
    s("Language","Language Hungarian",            "3A 30 31 53 3C 30 32 33 0D"),
    s("Language","Language Persian",              "3A 30 31 53 3C 30 32 34 0D"),
    s("Language","Language Vietnamese",           "3A 30 31 53 3C 30 32 35 0D"),
    s("Language","Language Thai",                 "3A 30 31 53 3C 30 32 36 0D"),
    s("Language","Language Catalan",              "3A 30 31 53 3C 30 32 37 0D"),
    s("Language","Language Lithuanian",           "3A 30 31 53 3C 30 32 38 0D"),
    s("Language","Language Croatian",             "3A 30 31 53 3C 30 32 39 0D"),
    s("Language","Language Estonian",             "3A 30 31 53 3C 30 33 30 0D"),
    s("Language","Get Language State",            "3A 30 31 47 3C 30 30 30 0D"),
    # Picture Mode
    s("Picture Mode","Picture Standard",         "3A 30 31 53 3D 30 30 30 0D"),
    s("Picture Mode","Picture Bright",           "3A 30 31 53 3D 30 30 31 0D"),
    s("Picture Mode","Picture Soft",             "3A 30 31 53 3D 30 30 32 0D"),
    s("Picture Mode","Picture Customer",         "3A 30 31 53 3D 30 30 33 0D"),
    s("Picture Mode","Get Picture Mode State",   "3A 30 31 47 3D 30 30 30 0D"),
    # Hue / Backlight Get
    s("Hue",       "Get Hue State",       "3A 30 31 47 3E 30 30 30 0D"),
    s("Backlight", "Get Backlight State", "3A 30 31 47 3F 30 30 30 0D"),
    # Color Temperature
    s("Color Temperature","Color Temp Cool",     "3A 30 31 53 40 30 30 30 0D"),
    s("Color Temperature","Color Temp Standard", "3A 30 31 53 40 30 30 31 0D"),
    s("Color Temperature","Color Temp Warm",     "3A 30 31 53 40 30 30 32 0D"),
    s("Color Temperature","Get Color Temp State","3A 30 31 47 40 30 30 30 0D"),
    # Remote Control
    s("Remote Control","Remote Volume Up",   "3A 30 31 53 41 30 30 30 0D"),
    s("Remote Control","Remote Volume Down", "3A 30 31 53 41 30 30 31 0D"),
    s("Remote Control","Remote Up",          "3A 30 31 53 41 30 31 30 0D"),
    s("Remote Control","Remote Down",        "3A 30 31 53 41 30 31 31 0D"),
    s("Remote Control","Remote Left",        "3A 30 31 53 41 30 31 32 0D"),
    s("Remote Control","Remote Right",       "3A 30 31 53 41 30 31 33 0D"),
    s("Remote Control","Remote Enter",       "3A 30 31 53 41 30 31 34 0D"),
    s("Remote Control","Remote Menu",        "3A 30 31 53 41 30 32 30 0D"),
    s("Remote Control","Remote Input",       "3A 30 31 53 41 30 32 31 0D"),
    s("Remote Control","Remote Back",        "3A 30 31 53 41 30 32 32 0D"),
    s("Remote Control","Remote Blank",       "3A 30 31 53 41 30 33 31 0D"),
    s("Remote Control","Remote Freeze",      "3A 30 31 53 41 30 33 32 0D"),
    s("Remote Control","Remote Mute",        "3A 30 31 53 41 30 33 33 0D"),
    s("Remote Control","Remote Home",        "3A 30 31 53 41 30 33 34 0D"),
    # IR
    s("IR","IR Enable",    "3A 30 31 53 42 30 30 30 0D"),
    s("IR","IR Disable",   "3A 30 31 53 42 30 30 31 0D"),
    s("IR","Get IR State", "3A 30 31 47 42 30 30 30 0D"),
    # Speaker
    s("Speaker","Speaker Off",       "3A 30 31 53 43 30 30 30 0D"),
    s("Speaker","Speaker On",        "3A 30 31 53 43 30 30 31 0D"),
    s("Speaker","Get Speaker State", "3A 30 31 47 43 30 30 30 0D"),
    # Touch
    s("Touch","Touch Off",       "3A 30 31 53 44 30 30 30 0D"),
    s("Touch","Touch On",        "3A 30 31 53 44 30 30 31 0D"),
    s("Touch","Get Touch State", "3A 30 31 47 44 30 30 30 0D"),
    # Screen
    s("Screen","Screen Off",        "3A 30 31 53 45 30 30 30 0D"),
    s("Screen","Screen On",         "3A 30 31 53 45 30 30 31 0D"),
    s("Screen","Get Screen State",  "3A 30 31 47 45 30 30 30 0D"),
    # No Signal Power Off
    s("No Signal Power Off","No Signal Disabled", "3A 30 31 53 46 30 30 30 0D"),
    s("No Signal Power Off","No Signal 1 Min",    "3A 30 31 53 46 30 30 31 0D"),
    s("No Signal Power Off","No Signal 3 Min",    "3A 30 31 53 46 30 30 33 0D"),
    s("No Signal Power Off","No Signal 5 Min",    "3A 30 31 53 46 30 30 35 0D"),
    s("No Signal Power Off","No Signal 10 Min",   "3A 30 31 53 46 30 31 30 0D"),
    s("No Signal Power Off","No Signal 15 Min",   "3A 30 31 53 46 30 31 35 0D"),
    s("No Signal Power Off","No Signal 30 Min",   "3A 30 31 53 46 30 33 30 0D"),
    s("No Signal Power Off","No Signal 45 Min",   "3A 30 31 53 46 30 34 35 0D"),
    s("No Signal Power Off","No Signal 60 Min",   "3A 30 31 53 46 30 36 30 0D"),
    s("No Signal Power Off","Get No Signal State","3A 30 31 47 46 30 30 30 0D"),
    # HDMI Out
    s("HDMI Out","HDMI Out Off",        "3A 30 31 53 48 30 30 30 0D"),
    s("HDMI Out","HDMI Out On",         "3A 30 31 53 48 30 30 31 0D"),
    s("HDMI Out","Get HDMI Out State",  "3A 30 31 47 48 30 30 30 0D"),
    # HDMI Encryption
    s("HDMI Encryption","HDMI Encryption Off",       "3A 30 31 53 49 30 30 30 0D"),
    s("HDMI Encryption","HDMI Encryption On",        "3A 30 31 53 49 30 30 31 0D"),
    s("HDMI Encryption","Get HDMI Encryption State", "3A 30 31 47 49 30 30 30 0D"),
    # HDMI Out Mode
    s("HDMI Out Mode","HDMI Out Mode Off",       "3A 30 31 53 50 30 30 30 0D"),
    s("HDMI Out Mode","HDMI Out Mode Auto",      "3A 30 31 53 50 30 30 31 0D"),
    s("HDMI Out Mode","HDMI Out Mode HDCP 1.4",  "3A 30 31 53 50 30 30 32 0D"),
    s("HDMI Out Mode","HDMI Out Mode HDCP 2.2",  "3A 30 31 53 50 30 30 33 0D"),
    s("HDMI Out Mode","Get HDMI Out Mode State", "3A 30 31 47 50 30 30 30 0D"),
    # Information
    s("Information","Get HDCP 1.X",        "3A 30 31 47 53 30 30 30 0D"),
    s("Information","Get HDCP 2.X",        "3A 30 31 47 53 30 30 31 0D"),
    s("Information","Get Machine Serial",  "3A 30 31 47 53 30 30 32 0D"),
    s("Information","Get MAC Address",     "3A 30 31 47 53 30 30 33 0D"),
    s("Information","Get Model Name",      "3A 30 31 47 53 30 30 34 0D"),
    s("Information","Get Model Version",   "3A 30 31 47 53 30 30 35 0D"),
    s("Information","Get Board Serial",    "3A 30 31 47 53 30 30 36 0D"),
    s("Information","Get Region",          "3A 30 31 47 53 30 30 37 0D"),
]

S_VAR_CMDS = [
    ("Volume",    "Set Volume",     "38"),
    ("Contrast",  "Set Contrast",   "34"),
    ("Brightness","Set Brightness", "35"),
    ("Sharpness", "Set Sharpness",  "36"),
    ("Hue",       "Set Hue",        "3E"),
    ("Backlight", "Set Backlight",  "3F"),
]

def build_s_series():
    rows = list(S_SERIES_FIXED)
    for cat, name_prefix, cmd_byte in S_VAR_CMDS:
        for lbl, hv, dv in LEVELS:
            rows.append(s_var(cat, name_prefix, cmd_byte, lbl, dv))
    return rows

# ── X-Series commands (preserved verbatim from prior seed) ───────────────────

def x(cat, name, code, notes=""):
    return ("X-Series", cat, name, code, notes, 59596, "HEX")

# X-Series base frame: 01 03 01 D0 00 D1 [CMD] [CAT] 00 00 FF*17 00 [PARAM_COUNT] [VALUE]
X_PREFIX = "01 03 01 D0 00 D1"
FF17     = "FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF"

def x_get(cmd_byte_hex, cat_byte_hex, notes=""):
    return f"{X_PREFIX} {cmd_byte_hex} {cat_byte_hex} 00 00 {FF17} 00 00"

def x_set(cmd_byte_hex, cat_byte_hex, val_hex):
    return f"{X_PREFIX} {cmd_byte_hex} {cat_byte_hex} 00 00 {FF17} 00 01 {val_hex}"

def x_var(cat, name_prefix, cmd_byte_hex, cat_byte_hex, pct_label, hex_val):
    code = x_set(cmd_byte_hex, cat_byte_hex, format(hex_val,'02X'))
    return ("X-Series", cat, f"{name_prefix} {pct_label}", code, "", 59596, "HEX")

X_SERIES_FIXED = [
    x("Status", "Get Status",        x_get("00","D0")),
    x("Power",  "GET Power State",   x_get("05","C0")),
    x("Power",  "SET Power On",      x_set("03","C0","01")),
    x("Power",  "SET Power Off",     x_set("03","C0","00")),
    x("Volume", "GET Volume",        x_get("01","C2")),
    x("Brightness","GET Brightness", x_get("1D","C2")),
    x("Contrast",  "GET Contrast",   x_get("15","C2")),
    x("Source","GET Source",         x_get("11","C2")),
    x("Source","SET Home (Android)", x_set("13","C2","00")),
    x("Source","SET HDMI 1",         x_set("13","C2","02")),
    x("Source","SET HDMI 2",         x_set("13","C2","03")),
    x("Source","SET HDMI 3",         x_set("13","C2","04")),
    x("LED Gain","GET Red Gain",     x_get("21","C2")),
    x("LED Gain","GET Green Gain",   x_get("25","C2")),
    x("LED Gain","GET Blue Gain",    x_get("29","C2")),
    x("Aspect Ratio","GET Ratio",    x_get("0D","C2")),
    x("Aspect Ratio","SET 4:3",      x_set("0F","C2","01")),
    x("Aspect Ratio","SET 16:9",     x_set("0F","C2","02")),
    x("Aspect Ratio","SET Full-Screen",x_set("0F","C2","03")),
    x("Aspect Ratio","SET Original", x_set("0F","C2","04")),
    x("Image Presets","SET Conference",  x_set("45","C2","00")),
    x("Image Presets","SET Standard",    x_set("45","C2","01")),
    x("Image Presets","SET Energy Save", x_set("45","C2","02")),
    x("Image Presets","SET User",        x_set("45","C2","03")),
    x("Color Temperature","SET Standard",x_set("1B","C2","01")),
    x("Color Temperature","SET Warm",    x_set("1B","C2","02")),
    x("Color Temperature","SET Cool",    x_set("1B","C2","03")),
    x("Color Temperature","SET User",    x_set("1B","C2","04")),
]

X_VAR_CMDS = [
    ("Volume",    "SET Volume",     "03", "C2"),
    ("Brightness","SET Brightness", "1F", "C2"),
    ("Contrast",  "SET Contrast",   "17", "C2"),
    ("LED Gain",  "SET Red Gain",   "23", "C2"),
    ("LED Gain",  "SET Green Gain", "27", "C2"),
    ("LED Gain",  "SET Blue Gain",  "2B", "C2"),
]

def build_x_series():
    rows = list(X_SERIES_FIXED)
    for cat, name_prefix, cmd_b, cat_b in X_VAR_CMDS:
        for lbl, hv, dv in LEVELS:
            rows.append(x_var(cat, name_prefix, cmd_b, cat_b, lbl, hv))
    return rows

# ── Models table (now with BaudRate) ─────────────────────────────────────────
# Baud per series:
#   A-Series: 38400    K-Series: 38400    X-Series: 38400
#   B-Series: 115200   E-50:     115200
#   H-Series: 9600     S-Series: 9600     AVE-9200: 9600 (under H-Series)

MODELS = [
    # A-Series (AVA-xx20)
    ("AVA-4320","A-Series", 38400),
    ("AVA-5020","A-Series", 38400),
    ("AVA-5520","A-Series", 38400),
    ("AVA-6520","A-Series", 38400),
    ("AVA-7520","A-Series", 38400),
    ("AVA-8620","A-Series", 38400),
    # B-Series
    ("AVB-4310","B-Series", 115200),
    ("AVB-5010","B-Series", 115200),
    ("AVB-5510","B-Series", 115200),
    ("AVB-6510","B-Series", 115200),
    ("AVB-7510","B-Series", 115200),
    ("AVB-8610","B-Series", 115200),
    # E-Group1 (xx20)
    ("AVE-6520","E-Group1", 115200),
    ("AVE-7520","E-Group1", 115200),
    ("AVE-8620","E-Group1", 115200),
    # E-Group1 (xx30)
    ("AVE-5530","E-Group1", 115200),
    ("AVE-6530","E-Group1", 115200),
    ("AVE-7530","E-Group1", 115200),
    ("AVE-8630","E-Group1", 115200),
    # E-Group1 (xx30-A)
    ("AVE-6530-A","E-Group1", 115200),
    ("AVE-7530-A","E-Group1", 115200),
    ("AVE-8630-A","E-Group1", 115200),
    # E-Group1 (xx40)
    ("AVE-5540","E-Group1", 115200),
    ("AVE-6540","E-Group1", 115200),
    ("AVE-7540","E-Group1", 115200),
    ("AVE-8640","E-Group1", 115200),
    # E-50
    ("AVE-5550","E-50", 115200),
    ("AVE-6550","E-50", 115200),
    ("AVE-7550","E-50", 115200),
    ("AVE-8650","E-50", 115200),
    # H-Series (AVH-xx20, AVL-1050-X, AVE-9200)
    ("AVH-6520","H-Series", 9600),
    ("AVH-7520","H-Series", 9600),
    ("AVH-8620","H-Series", 9600),
    ("AVL-1050-X","H-Series", 9600),
    ("AVE-9200","H-Series", 9600),
    # K-Series
    ("AVK-5510","K-Series", 38400),
    ("AVK-6510","K-Series", 38400),
    ("AVK-7510","K-Series", 38400),
    ("AVK-8610","K-Series", 38400),
    ("AVK-9810","K-Series", 38400),
    # S-Series (AVS-xx10 / AVS-xx10E)
    ("AVS-5510","S-Series", 9600),
    ("AVS-6510","S-Series", 9600),
    ("AVS-7510","S-Series", 9600),
    ("AVS-8610","S-Series", 9600),
    # X-Series
    ("AVX-1320","X-Series", 38400),
    ("AVX-1380","X-Series", 38400),
]

# ── main ─────────────────────────────────────────────────────────────────────

def main():
    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
        print(f"Removed existing database: {DB_PATH}")

    con = sqlite3.connect(DB_PATH)
    cur = con.cursor()
    cur.executescript(SCHEMA)

    INSERT_DL = """INSERT INTO DeviceList
        (SeriesPattern, CommandCategory, CommandName, CommandCode, Notes, Port, CommandFormat)
        VALUES (?,?,?,?,?,?,?)"""

    all_commands = (
        build_a_series()
        + build_b_series()
        + build_e_group1()
        + build_e50()
        + build_h_series()
        + build_k_series()
        + build_s_series()
        + build_x_series()
    )

    cur.executemany(INSERT_DL, all_commands)

    INSERT_M = "INSERT INTO Models (ModelNumber, SeriesPattern, BaudRate) VALUES (?,?,?)"
    cur.executemany(INSERT_M, MODELS)

    INSERT_OUI = "INSERT OR IGNORE INTO OUITable (OUIPrefix, SeriesLabel, SeriesPattern) VALUES (?,?,?)"
    cur.executemany(INSERT_OUI, OUI_SEEDS)

    con.commit()

    cur.execute("SELECT COUNT(*) FROM DeviceList")
    dl_count = cur.fetchone()[0]
    cur.execute("SELECT COUNT(*) FROM Models")
    m_count = cur.fetchone()[0]
    cur.execute("SELECT COUNT(*) FROM OUITable")
    oui_count = cur.fetchone()[0]
    cur.execute(
        "SELECT SeriesPattern, COUNT(*) FROM DeviceList "
        "GROUP BY SeriesPattern ORDER BY SeriesPattern"
    )
    by_series = cur.fetchall()

    con.close()

    print(f"\ncommands.db created: {DB_PATH}")
    print(f"  DeviceList rows : {dl_count}")
    print(f"  Models rows     : {m_count}")
    print(f"  OUITable rows   : {oui_count}")
    print("\nCommands by series:")
    for series, cnt in by_series:
        print(f"  {series:<15} {cnt:>4} commands")
    print("\nNote: User data (StoredDevices, Groups, Macros, etc.) is NOT in this database.")
    print("      It lives in userdata.db, created by the app on first run.")


if __name__ == "__main__":
    main()
