/**
 * Avocor Commander V3.0 - Database Seeder (Node.js)
 * Outputs SQL to stdout; pipe to sqlite3.
 * Usage: node seed_database.js | sqlite3 AvocorCommander.db
 */

// ── helpers ──────────────────────────────────────────────────────────────────

/** A-Series: decimal value 0-100 encoded as 2 ASCII hex chars
 *  e.g. 25 → hex "19" → bytes "31 39" */
function aSeriesValue(decVal) {
  const h = decVal.toString(16).padStart(2, '0'); // "19"
  return [...h].map(c => c.charCodeAt(0).toString(16).toUpperCase().padStart(2,'0')).join(' ');
}

/** E-50 / H-L-9200 / S-Series: 3-digit decimal ASCII
 *  e.g. 25 → "025" → bytes "30 32 35" */
function decAscii3(decVal) {
  const s = decVal.toString().padStart(3, '0');
  return [...s].map(c => c.charCodeAt(0).toString(16).toUpperCase().padStart(2,'0')).join(' ');
}

/** K-Series checksum: sum of all 4 command bytes mod 256 */
function kChecksum(b1, b2, b3, b4) {
  return ((b1 + b2 + b3 + b4) & 0xFF).toString(16).toUpperCase().padStart(2,'0');
}

function hex2(n) { return n.toString(16).toUpperCase().padStart(2,'0'); }

const LEVELS = [
  { lbl: '0%',   hv: 0x00, dv: 0   },
  { lbl: '25%',  hv: 0x19, dv: 25  },
  { lbl: '50%',  hv: 0x32, dv: 50  },
  { lbl: '75%',  hv: 0x4B, dv: 75  },
  { lbl: '100%', hv: 0x64, dv: 100 },
];

const LEVELS_20 = [
  { lbl: '0%',   hv: 0x00 },
  { lbl: '25%',  hv: 0x05 },
  { lbl: '50%',  hv: 0x0A },
  { lbl: '75%',  hv: 0x0F },
  { lbl: '100%', hv: 0x14 },
];

// ── SQL output helpers ────────────────────────────────────────────────────────

const lines = [];

function esc(s) { return s.replace(/'/g, "''"); }

function insertDL(series, cat, name, code, notes, port, fmt) {
  lines.push(`INSERT INTO DeviceList (SeriesPattern,CommandCategory,CommandName,CommandCode,Notes,Port,CommandFormat) VALUES ('${esc(series)}','${esc(cat)}','${esc(name)}','${esc(code)}','${esc(notes)}',${port},'${fmt}');`);
}

function insertModel(model, series) {
  lines.push(`INSERT INTO Models (ModelNumber,SeriesPattern) VALUES ('${esc(model)}','${esc(series)}');`);
}

// ── Schema ────────────────────────────────────────────────────────────────────

lines.push(`
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
  SeriesPattern TEXT    NOT NULL
);
CREATE TABLE IF NOT EXISTS StoredDevices (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  DeviceName  TEXT,
  ModelNumber TEXT,
  IPAddress   TEXT,
  Port        INTEGER,
  BaudRate    INTEGER,
  Notes       TEXT
);
CREATE TABLE IF NOT EXISTS MACFilter (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  MACAddress  TEXT NOT NULL UNIQUE,
  DeviceName  TEXT,
  Notes       TEXT
);
`);

// ── A-Series ──────────────────────────────────────────────────────────────────

const A = (cat,n,c,notes='') => insertDL('A-Series',cat,n,c,notes,59595,'HEX');
const aCmd = (prefix, val) => `${prefix} ${aSeriesValue(val)}`;

// Fixed A-Series
A('Power','Power On',               '6B 61 20 30 30 20 30 31');
A('Power','Power Off',              '6B 61 20 30 30 20 30 30');
A('Power','Get Current State',      '6B 61 20 30 30 20 66 66');
A('Video Source','Home',            '6B 62 20 30 30 20 30 30','Set Home Source');
A('Video Source','OPS',             '6B 62 20 30 30 20 30 37','Set OPS Source');
A('Video Source','Front HDMI',      '6B 62 20 30 30 20 30 38','Set Front HDMI Source');
A('Video Source','HDMI 1',          '6B 62 20 30 30 20 30 39','Set HDMI1 Source');
A('Video Source','HDMI 2',          '6B 62 20 30 30 20 30 61','Set HDMI2 Source');
A('Video Source','HDMI 3',          '6B 62 20 30 30 20 30 62','Set HDMI3 Source');
A('Video Source','Display Port',    '6B 62 20 30 30 20 30 63','Set Display Port Source');
A('Video Source','USB-C',           '6B 62 20 30 30 20 30 64','Set USB-C Source');
A('Video Source','Front USB-C',     '6B 62 20 30 30 20 30 65','Front USB-C');
A('Video Source','Get Current State','6B 62 20 30 30 20 66 66','Get Current Source');
A('ARC','16:09',                    '6B 63 20 30 30 20 30 31');
A('ARC','4:03',                     '6B 63 20 30 30 20 30 32');
A('ARC','P2P',                      '6B 63 20 30 30 20 30 35');
A('ARC','Get Current State',        '6B 63 20 30 30 20 66 66');
A('Picture','Standard',             '6B 75 20 30 30 20 30 30');
A('Picture','Soft',                 '6B 75 20 30 30 20 30 31');
A('Picture','Bright',               '6B 75 20 30 30 20 30 32');
A('Picture','Get Current State',    '6B 75 20 30 30 20 66 66');
A('Picture','Get Contrast State',   '6B 67 20 30 30 20 66 66');
A('Picture','Get Brightness State', '6B 68 20 30 30 20 66 66');
A('Picture','Get Saturation State', '6B 69 20 30 30 20 66 66');
A('Picture','Get Hue State',        '6B 6F 20 30 30 20 66 66');
A('Picture','Freeze ON',            '6B 7A 20 30 30 20 30 31');
A('Picture','Freeze OFF',           '6B 7A 20 30 30 20 30 30');
A('Picture','Get Freeze State',     '6B 7A 20 30 30 20 66 66');
A('Sound','Mute ON',                '6B 65 20 30 30 20 30 31');
A('Sound','Mute OFF',               '6B 65 20 30 30 20 30 30');
A('Sound','Get Mute State',         '6B 65 20 30 30 20 66 66');
A('Sound','Volume UP',              '6B 76 20 30 30 20 30 31');
A('Sound','Volume DOWN',            '6B 76 20 30 30 20 30 30');
A('Sound','Get Volume State',       '6B 66 20 30 30 20 66 66');
A('Remote Control','Menu',          '6D 63 20 30 30 20 39 35');
A('Remote Control','Left',          '6D 63 20 30 30 20 38 66');
A('Remote Control','Up',            '6D 63 20 30 30 20 38 64');
A('Remote Control','OK',            '6D 63 20 30 30 20 38 63');
A('Remote Control','Right',         '6D 63 20 30 30 20 38 30');
A('Remote Control','Down',          '6D 63 20 30 30 20 38 65');
A('Remote Control','Exit',          '6D 63 20 30 30 20 39 36');
A('Remote Control','Source',        '6D 63 20 30 30 20 61 63');
A('Remote Control','On',            '6D 73 20 30 30 20 30 31');
A('Remote Control','Off',           '6D 73 20 30 30 20 30 30');
A('Remote Control','Read',          '6D 73 20 30 30 20 66 66');
A('OSD Key Lock','On',              '6D 6F 20 30 30 20 30 31');
A('OSD Key Lock','Off',             '6D 6F 20 30 30 20 30 30');
A('OSD Key Lock','Current State',   '6D 6F 20 30 30 20 66 66');

// Variable A-Series (0/25/50/75/100%)
const A_VAR = [
  ['Sound',   'Set Volume',     '6B 66 20 30 30 20'],
  ['Picture', 'Set Contrast',   '6B 67 20 30 30 20'],
  ['Picture', 'Set Brightness', '6B 68 20 30 30 20'],
  ['Picture', 'Set Saturation', '6B 69 20 30 30 20'],
  ['Picture', 'Set Hue',        '6B 6F 20 30 30 20'],
];
for (const [cat, name, prefix] of A_VAR) {
  for (const {lbl, dv} of LEVELS) {
    insertDL('A-Series', cat, `${name} ${lbl}`, `${prefix}${aSeriesValue(dv)}`, '', 59595, 'HEX');
  }
}

// ── B-Series ──────────────────────────────────────────────────────────────────

const B = (cat,n,c,notes='') => insertDL('B-Series',cat,n,c,notes,10180,'ASCII');

B('System',        'Version Check',          '!Version ?');
B('Power',         'Power On',               '!Power On');
B('Power',         'Standby',                '!Power Off');
B('Power',         'Power Toggle',           '!Power Toggle');
B('Power',         'Current State',          '!Power ?');
B('Backlight',     'Set Level 0%',           '!Backlight 0');
B('Backlight',     'Set Level 25%',          '!Backlight 25');
B('Backlight',     'Set Level 50%',          '!Backlight 50');
B('Backlight',     'Set Level 75%',          '!Backlight 75');
B('Backlight',     'Set Level 100%',         '!Backlight 100');
B('Backlight',     'Current State',          '!Backlight ?');
B('Volume',        'Volume Up',              '!Volume Up');
B('Volume',        'Volume Down',            '!Volume Down');
B('Volume',        'Set Volume 0%',          '!Volume 0');
B('Volume',        'Set Volume 25%',         '!Volume 25');
B('Volume',        'Set Volume 50%',         '!Volume 50');
B('Volume',        'Set Volume 75%',         '!Volume 75');
B('Volume',        'Set Volume 100%',        '!Volume 100');
B('Volume',        'Current State',          '!Volume ?');
B('Volume',        'Mute On',                '!Mute On');
B('Volume',        'Mute Off',               '!Mute Off');
B('Volume',        'Mute Toggle',            '!Mute Toggle');
B('Volume',        'Mute Current State',     '!Mute ?');
B('Input',         'Home',                   '!Input Android 1');
B('Input',         'HDMI 1',                 '!Input HDMI 1');
B('Input',         'HDMI 2',                 '!Input HDMI 2');
B('Input',         'OPS 1',                  '!Input OPS 1');
B('Signal Status', 'Status HDMI 1',          '!Status HDMI 1 ?');
B('Signal Status', 'Status HDMI 2',          '!Status HDMI 2 ?');
B('Signal Status', 'Status OPS 1',           '!Status OPS 1 ?');
B('Application',   'Get All Installed Apps', '!List ?');
B('Application',   'Open Application',       '!Open XYZ',   'Replace XYZ with app name');
B('Application',   'Close Application',      '!Close XYZ',  'Replace XYZ with app name');
B('IR Emulation',  'Home',                   '!IR Home');
B('IR Emulation',  'Cursor Up',              '!IR Cursor Up');
B('IR Emulation',  'Cursor Down',            '!IR Cursor Down');
B('IR Emulation',  'Cursor Left',            '!IR Cursor Left');
B('IR Emulation',  'Cursor Right',           '!IR Cursor Right');
B('IR Emulation',  'OK',                     '!IR OK');
B('IR Emulation',  'Back',                   '!IR Back');

// ── E-Group1 ──────────────────────────────────────────────────────────────────

const EG = (cat,n,c,notes='') => insertDL('E-Group1',cat,n,c,notes,4664,'HEX');

EG('Power','Power On',          '07 01 02 50 4F 57 01 08');
EG('Power','Power Off',         '07 01 02 50 4F 57 00 08');
EG('Power','Display Status',    '07 01 01 50 4F 57 08');
EG('Power','Never Sleep',       '07 01 02 57 46 53 02 08');
EG('Power','IPC / OPS Off',     '07 01 02 49 59 43 00 08');
EG('Power','IPC / OPS On',      '07 01 02 49 59 43 01 08');
EG('Factory Reset','Restore Factory Default',              '07 01 02 41 4C 4C 00 08');
EG('Factory Reset','Restore Factory Default Except Comm.','07 01 02 41 4C 4C 01 08');
EG('Information','Read Serial Number',     '07 01 01 53 45 52 08');
EG('Information','Read Model Name',        '07 01 01 4D 4E 41 08');
EG('Information','Read Firmware Version',  '07 01 01 47 56 45 08');
EG('Information','Read RS232 Table Version','07 01 01 52 54 56 08');
EG('Source','VGA',                '07 01 02 4D 49 4E 00 08');
EG('Source','HDMI 1',             '07 01 02 4D 49 4E 09 08');
EG('Source','HDMI 2',             '07 01 02 4D 49 4E 0A 08');
EG('Source','HDMI 3 / Front HDMI','07 01 02 4D 49 4E 0B 08');
EG('Source','HDMI 4',             '07 01 02 4D 49 4E 0C 08');
EG('Source','DisplayPort',        '07 01 02 4D 49 4E 0D 08');
EG('Source','Type-C (AVE-5530 / AVE-xx30-A)','07 01 02 4D 49 4E 0C 08');
EG('Source','Type-C (AVG-xx60 / AVW-xx55)',  '07 01 02 4D 49 4E 14 08');
EG('Source','IPC / OPS',          '07 01 02 4D 49 4E 0E 08');
EG('Source','WPS',                '07 01 02 4D 49 4E 13 08');
EG('Picture Settings','Backlight Off',  '07 01 02 42 4C 43 00 08');
EG('Picture Settings','Backlight On',   '07 01 02 42 4C 43 01 08');
EG('Picture Settings','Noise Reduction Off',    '07 01 02 4E 4F 52 00 08');
EG('Picture Settings','Noise Reduction Low',    '07 01 02 4E 4F 52 01 08');
EG('Picture Settings','Noise Reduction Medium', '07 01 02 4E 4F 52 02 08');
EG('Picture Settings','Noise Reduction High',   '07 01 02 4E 4F 52 03 08');
EG('Picture Settings','Color Temperature User',  '07 01 02 43 4F 54 00 08');
EG('Picture Settings','Color Temperature 5000K', '07 01 02 43 4F 54 06 08');
EG('Picture Settings','Color Temperature 6500K', '07 01 02 43 4F 54 01 08');
EG('Picture Settings','Color Temperature 7500K', '07 01 02 43 4F 54 07 08');
EG('Picture Settings','Color Temperature 9300K', '07 01 02 43 4F 54 02 08');
EG('Picture Settings','Gamma Off', '07 01 02 47 41 43 00 08');
EG('Picture Settings','Gamma On',  '07 01 02 47 41 43 01 08');
EG('Scaling','Point-to-Point', '07 01 02 41 53 50 00 08');
EG('Scaling','16:09',          '07 01 02 41 53 50 01 08');
EG('Scaling','4:03',           '07 01 02 41 53 50 02 08');
EG('Scaling','Letterbox',      '07 01 02 41 53 50 03 08');
EG('Scaling','Auto',           '07 01 02 41 53 50 04 08');
EG('Remote Control','MENU',    '07 01 02 52 43 55 00 08');
EG('Remote Control','INFO',    '07 01 02 52 43 55 01 08');
EG('Remote Control','UP',      '07 01 02 52 43 55 02 08');
EG('Remote Control','DOWN',    '07 01 02 52 43 55 03 08');
EG('Remote Control','LEFT',    '07 01 02 52 43 55 04 08');
EG('Remote Control','RIGHT',   '07 01 02 52 43 55 05 08');
EG('Remote Control','OK',      '07 01 02 52 43 55 06 08');
EG('Remote Control','EXIT',    '07 01 02 52 43 55 07 08');
EG('Remote Control','VGA',     '07 01 02 52 43 55 08 08');
EG('Remote Control','HDMI 1',  '07 01 02 52 43 55 0A 08');
EG('Remote Control','HDMI 2',  '07 01 02 52 43 55 0B 08');
EG('Remote Control','HDMI 3',  '07 01 02 52 43 55 1F 08');
EG('Remote Control','DisplayPort (AVE-xx30-A / AVE-5530)','07 01 02 52 43 55 22 08');
EG('Remote Control','DisplayPort (AVG-xx60 / AVF-xx50)',  '07 01 02 52 43 55 0C 08');
EG('Remote Control','Type-C (AVE-xx30-A / AVE-5530)',     '07 01 02 52 43 55 23 08');
EG('Remote Control','Type-C (AVG-xx60 / AVW-xx55)',       '07 01 02 52 43 55 2E 08');
EG('Remote Control','OPS',     '07 01 02 52 43 55 21 08');
EG('Remote Control','SCALING', '07 01 02 52 43 55 17 08');
EG('Remote Control','FREEZE',  '07 01 02 52 43 55 18 08');
EG('Remote Control','MUTE',    '07 01 02 52 43 55 19 08');
EG('Remote Control','BRIGHTNESS','07 01 02 52 43 55 1A 08');
EG('Remote Control','CONTRAST', '07 01 02 52 43 55 1B 08');
EG('Remote Control','AUTO',    '07 01 02 52 43 55 1C 08');
EG('Remote Control','VOLUME+', '07 01 02 52 43 55 1D 08');
EG('Remote Control','VOLUME-', '07 01 02 52 43 55 1E 08');
EG('Remote Control','Unlock Remote and Panel Keys','07 01 02 4B 4C 43 00 08');
EG('Remote Control','Lock Remote and Panel Keys',  '07 01 02 4B 4C 43 01 08');
EG('GVS','Query Main Scaler Version',    '07 01 02 47 56 53 00 08');
EG('GVS','Query Sub MCU Version',        '07 01 02 47 56 53 01 08');
EG('GVS','Query Network Module Version', '07 01 02 47 56 53 02 08');
EG('Audio','Internal Speaker Off', '07 01 02 49 4E 53 00 08');
EG('Audio','Internal Speaker On',  '07 01 02 49 4E 53 01 08');
EG('Audio','Mute Off',             '07 01 02 4D 55 54 00 08');
EG('Audio','Mute On',              '07 01 02 4D 55 54 01 08');
EG('Video Scheme (AVG/AVW/AVF)','User',   '07 01 02 53 43 4D 00 08');
EG('Video Scheme (AVG/AVW/AVF)','Sport',  '07 01 02 53 43 4D 01 08');
EG('Video Scheme (AVG/AVW/AVF)','Game',   '07 01 02 53 43 4D 02 08');
EG('Video Scheme (AVG/AVW/AVF)','Cinema', '07 01 02 53 43 4D 03 08');
EG('Video Scheme (AVG/AVW/AVF)','Vivid',  '07 01 02 53 43 4D 04 08');
EG('Video Scheme (AVE-xx30-A/5530)','User',   '07 01 02 53 43 4D 00 08');
EG('Video Scheme (AVE-xx30-A/5530)','Vivid',  '07 01 02 53 43 4D 01 08');
EG('Video Scheme (AVE-xx30-A/5530)','Cinema', '07 01 02 53 43 4D 02 08');
EG('Video Scheme (AVE-xx30-A/5530)','Game',   '07 01 02 53 43 4D 03 08');
EG('Video Scheme (AVE-xx30-A/5530)','Sport',  '07 01 02 53 43 4D 04 08');
EG('Eco Mode','On',  '07 01 02 57 46 53 01 08');
EG('Eco Mode','Off', '07 01 02 57 46 53 02 08');
EG('Eco Mode','Wake on LAN (AVW-xx55)','07 01 02 57 46 53 03 08');
EG('Auto Scan','Off','07 01 02 41 54 53 00 08');
EG('Auto Scan','On', '07 01 02 41 54 53 01 08');
EG('IRFM','Off','07 01 02 49 52 46 00 08');
EG('IRFM','On', '07 01 02 49 52 46 01 08');
EG('Smart Light Control','Off',    '07 01 02 53 4C 43 00 08');
EG('Smart Light Control','DCR',    '07 01 02 53 4C 43 01 08');
EG('Smart Light Control','By Time','07 01 02 53 4C 43 03 08');
EG('Power LED','Off','07 01 02 4C 45 44 00 08');
EG('Power LED','On', '07 01 02 4C 45 44 01 08');
EG('HDMI RGB Color Range','Auto Detect','07 01 02 48 43 52 00 08');
EG('HDMI RGB Color Range','0-255',      '07 01 02 48 43 52 01 08');
EG('HDMI RGB Color Range','16-235',     '07 01 02 48 43 52 02 08');
EG('Touch Control (AVF-xx50)','Auto',    '07 01 02 54 4F 43 00 08');
EG('Touch Control (AVF-xx50)','OPS',     '07 01 02 54 4F 43 01 08');
EG('Touch Control (AVF-xx50)','HDMI 3',  '07 01 02 54 4F 43 02 08');
EG('Touch Control (AVF-xx50)','Rear USB','07 01 02 54 4F 43 03 08');
EG('Touch Control (AVF-xx50)','HDMI 4',  '07 01 02 54 4F 43 04 08');
EG('Touch Control (AVF-xx50)','WPS',     '07 01 02 54 4F 43 53 08');
EG('Touch Control (AVW-xx55)','Auto',        '07 01 02 54 4F 43 00 08');
EG('Touch Control (AVW-xx55)','USB Touch 1', '07 01 02 54 4F 43 02 08');
EG('Touch Control (AVW-xx55)','USB Touch 2', '07 01 02 54 4F 43 03 08');
EG('Touch Control (AVW-xx55)','USB Touch 3', '07 01 02 54 4F 43 04 08');
EG('OSD Language','English',    '07 01 02 4F 53 4C 00 08');
EG('OSD Language','French',     '07 01 02 4F 53 4C 01 08');
EG('OSD Language','German',     '07 01 02 4F 53 4C 02 08');
EG('OSD Language','Dutch',      '07 01 02 4F 53 4C 03 08');
EG('OSD Language','Danish',     '07 01 02 4F 53 4C 08 08');
EG('OSD Language','Italian',    '07 01 02 4F 53 4C 0D 08');
EG('OSD Language','Swedish',    '07 01 02 4F 53 4C 0E 08');
EG('OSD Language','Portuguese', '07 01 02 4F 53 4C 0F 08');
EG('OSD Language','Spanish',    '07 01 02 4F 53 4C 10 08');
EG('OSD Timeout','5 Seconds',   '07 01 02 4F 53 4F 05 08');
EG('OSD Timeout','10 Seconds',  '07 01 02 4F 53 4F 0A 08');
EG('OSD Timeout','20 Seconds',  '07 01 02 4F 53 4F 14 08');
EG('OSD Timeout','30 Seconds',  '07 01 02 4F 53 4F 1E 08');
EG('OSD Timeout','60 Seconds',  '07 01 02 4F 53 4F 3C 08');

// Variable E-Group1 (00~64 hex = 0-100 decimal, 25% steps)
const EG_VAR = [
  ['Picture Settings','Backlight Brightness','07 01 02 42 52 49'],
  ['Picture Settings','Digital Brightness',  '07 01 02 42 52 4C'],
  ['Picture Settings','Contrast',            '07 01 02 43 4F 4E'],
  ['Picture Settings','Hue',                 '07 01 02 48 55 45'],
  ['Picture Settings','Saturation',          '07 01 02 53 41 54'],
  ['Picture Settings','Sharpness',           '07 01 02 53 48 41'],
  ['Picture Settings','Red Gain',            '07 01 02 55 53 52'],
  ['Picture Settings','Green Gain',          '07 01 02 55 53 47'],
  ['Picture Settings','Blue Gain',           '07 01 02 55 53 42'],
  ['Picture Settings','Red Offset',          '07 01 02 55 4F 52'],
  ['Picture Settings','Green Offset',        '07 01 02 55 4F 47'],
  ['Picture Settings','Blue Offset',         '07 01 02 55 4F 42'],
  ['VGA Adjustment','Phase',                 '07 01 02 50 48 41'],
  ['VGA Adjustment','Clock',                 '07 01 02 43 4C 4F'],
  ['VGA Adjustment','Horizontal Position',   '07 01 02 48 4F 52'],
  ['VGA Adjustment','Vertical Position',     '07 01 02 56 45 52'],
  ['VGA Adjustment','Auto Adjust',           '07 01 02 41 44 4A'],
  ['Scaling','Overscan Ratio Adjust',        '07 01 02 5A 4F 4D'],
  ['Audio','Set Volume Level',               '07 01 02 56 4F 4C'],
  ['Audio','Volume+ by Increment',           '07 01 02 56 4F 49'],
  ['Audio','Volume- by Increment',           '07 01 02 56 4F 44'],
  ['OSD Control','OSD Transparency',         '07 01 02 4F 53 54'],
  ['OSD Control','OSD Horizontal Position',  '07 01 02 4F 53 48'],
  ['OSD Control','OSD Vertical Position',    '07 01 02 4F 53 56'],
];
for (const [cat, name, prefix] of EG_VAR) {
  for (const {lbl, hv} of LEVELS) {
    insertDL('E-Group1', cat, `${name} ${lbl}`, `${prefix} ${hex2(hv)} 08`, '', 4664, 'HEX');
  }
}

// Bass/Treble/Balance range 00~14 hex (0-20 dec), 25% steps
const EG_AUDIO20 = [
  ['Audio','Bass',    '07 01 02 42 41 53'],
  ['Audio','Treble',  '07 01 02 54 52 45'],
  ['Audio','Balance', '07 01 02 42 41 4C'],
];
for (const [cat, name, prefix] of EG_AUDIO20) {
  for (const {lbl, hv} of LEVELS_20) {
    insertDL('E-Group1', cat, `${name} ${lbl}`, `${prefix} ${hex2(hv)} 08`, '', 4664, 'HEX');
  }
}

// ── E-50 ──────────────────────────────────────────────────────────────────────

const E5 = (cat,n,c,notes='') => insertDL('E-50',cat,n,c,notes,4660,'HEX');

E5('Power','Power On',           '6B 30 31 73 41 30 30 31 0D');
E5('Power','Power Off',          '6B 30 31 73 41 30 30 30 0D');
E5('Power','Read Power State',   '6B 30 31 67 69 30 30 30 0D');
E5('Source','Home',              '6B 30 31 73 42 30 30 41 0D');
E5('Source','OPS',               '6B 30 31 73 42 30 30 37 0D');
E5('Source','DisplayPort',       '6B 30 31 73 42 30 30 39 0D');
E5('Source','HDMI 1',            '6B 30 31 73 42 30 31 34 0D');
E5('Source','HDMI 2',            '6B 30 31 73 42 30 32 34 0D');
E5('Source','VGA',               '6B 30 31 73 42 30 30 36 0D');
E5('Source','Front Type-C',      '6B 30 31 73 42 30 30 43 0D');
E5('Source','Rear Type-C',       '6B 30 31 73 42 30 30 40 0D');
E5('Remote Control','Up',        '6B 30 31 73 5B 30 30 30 0D');
E5('Remote Control','Down',      '6B 30 31 73 5B 30 30 31 0D');
E5('Remote Control','Left',      '6B 30 31 73 5B 30 30 32 0D');
E5('Remote Control','Right',     '6B 30 31 73 5B 30 30 33 0D');
E5('Remote Control','Confirm',   '6B 30 31 73 5B 30 30 34 0D');
E5('Remote Control','Return',    '6B 30 31 73 5B 30 30 37 0D');
E5('Remote Control','Lock Remote',   '6B 30 31 73 56 30 30 30 0D');
E5('Remote Control','Unlock Remote', '6B 30 31 73 56 30 30 31 0D');
E5('Remote Control','Read Remote Lock','6B 30 31 67 6A 30 30 30 0D');
E5('Volume','Mute Off',          '6B 30 31 73 51 30 30 30 0D');
E5('Volume','Mute On',           '6B 30 31 73 51 30 30 31 0D');
E5('Volume','Read Mute State',   '6B 30 31 67 67 30 30 30 0D');
E5('Volume','Volume Up',         '6B 30 31 73 50 32 30 31 0D');
E5('Volume','Volume Down',       '6B 30 31 73 50 32 30 30 0D');
E5('Volume','Read Volume',       '6B 30 31 67 66 30 30 30 0D');
E5('Color Mode','Normal','6B 30 31 73 48 30 30 30 0D');
E5('Color Mode','Warm',  '6B 30 31 73 48 30 30 31 0D');
E5('Color Mode','Cool',  '6B 30 31 73 48 30 30 32 0D');
E5('Color Mode','User',  '6B 30 31 73 48 30 30 33 0D');
E5('Brightness','Read Brightness', '6B 30 31 67 62 30 30 30 0D');
E5('Contrast',  'Read Contrast',   '6B 30 31 67 61 30 30 30 0D');
E5('Sharpness', 'Read Sharpness',  '6B 30 31 67 63 30 30 30 0D');
E5('Button Lock','Lock Buttons',   '6B 30 31 73 52 30 30 30 0D');
E5('Button Lock','Unlock Buttons', '6B 30 31 73 52 30 30 31 0D');
E5('Button Lock','Read Button Lock','6B 30 31 67 6C 30 30 30 0D');
E5('Touch Lock','Lock Touch',      '6B 30 31 73 5C 30 30 30 0D');
E5('Touch Lock','Unlock Touch',    '6B 30 31 73 5C 30 30 31 0D');
E5('Touch Lock','Read Touch Lock', '6B 30 31 67 75 30 30 30 0D');
E5('Date/Time','Read Uptime',  '6B 30 31 67 6F 30 30 30 0D');
E5('Date/Time','Read Year',    '6B 30 31 67 70 48 30 30 0D');
E5('Date/Time','Read Month',   '6B 30 31 67 70 4D 30 30 0D');
E5('Date/Time','Read Day',     '6B 30 31 67 70 44 30 30 0D');
E5('Date/Time','Read Hour',    '6B 30 31 67 71 48 30 30 0D');
E5('Date/Time','Read Minute',  '6B 30 31 67 71 4D 30 30 0D');
E5('Date/Time','Read Second',  '6B 30 31 67 71 53 30 30 0D');
E5('Information','Read Device Name','6B 30 31 67 72 30 30 30 0D');
E5('Information','Read MAC Address','6B 30 31 67 73 30 30 30 0D');
E5('Factory Reset','Factory Reset', '6B 30 31 73 5A 30 30 30 0D');

// Variable E-50 (3-digit decimal ASCII encoding)
const E50_VAR = [
  ['Volume',    'Set Volume',     '50'],
  ['Bass',      'Set Bass',       '4A'],
  ['Treble',    'Set Treble',     '4B'],
  ['Brightness','Set Brightness', '44'],
  ['Contrast',  'Set Contrast',   '43'],
  ['Sharpness', 'Set Sharpness',  '45'],
];
for (const [cat, name, cmdByte] of E50_VAR) {
  for (const {lbl, dv} of LEVELS) {
    const d = decAscii3(dv);
    insertDL('E-50', cat, `${name} ${lbl}`, `6B 30 31 73 ${cmdByte} ${d} 0D`, '', 4660, 'HEX');
  }
}

// ── H/L/9200 ─────────────────────────────────────────────────────────────────

const HL = (cat,n,c,notes='') => insertDL('H/L/9200',cat,n,c,notes,4664,'HEX');

HL('Power','Backlight Off',    '3A 30 31 53 30 30 30 30 0D','Backlight off');
HL('Power','Backlight On',     '3A 30 31 53 30 30 30 31 0D','Backlight on');
HL('Power','Power Off',        '3A 30 31 53 30 30 30 32 0D','Power off');
HL('Power','Power On',         '3A 30 31 53 30 30 30 33 0D','Power on');
HL('Power','Screen On',        '3A 30 31 53 45 30 30 31 0D','Turn ON Screen');
HL('Power','Screen Off',       '3A 30 31 53 45 30 30 30 0D','Turn OFF Screen');
HL('Power','Get Current State','3A 30 31 47 30 30 30 30 0D');
HL('Treble','-5',              '3A 30 31 53 31 2D 30 35 0D');
HL('Treble','-3',              '3A 30 31 53 31 2D 30 33 0D');
HL('Treble','+3',              '3A 30 31 53 31 2B 30 33 0D');
HL('Treble','+5',              '3A 30 31 53 31 2B 30 35 0D');
HL('Bass','-5',                '3A 30 31 53 32 2D 30 35 0D');
HL('Bass','-3',                '3A 30 31 53 32 2D 30 33 0D');
HL('Bass','+3',                '3A 30 31 53 32 2B 30 33 0D');
HL('Bass','+5',                '3A 30 31 53 32 2B 30 35 0D');
HL('Balance','-50',            '3A 30 31 53 33 2D 35 30 0D');
HL('Balance','+20',            '3A 30 31 53 33 2B 32 30 0D');
HL('Sound Mode','Movie',       '3A 30 31 53 37 30 30 30 0D');
HL('Sound Mode','Standard',    '3A 30 31 53 37 30 30 31 0D');
HL('Sound Mode','Custom',      '3A 30 31 53 37 30 30 32 0D');
HL('Sound Mode','Classroom',   '3A 30 31 53 37 30 30 33 0D');
HL('Sound Mode','Meeting',     '3A 30 31 53 37 30 30 34 0D');
HL('Volume','Mute On',         '3A 30 31 53 39 30 30 31 0D','Mute On');
HL('Volume','Mute Off',        '3A 30 31 53 39 30 30 30 0D','Mute Off');
HL('Video Source','Get Current State',    '3A 30 31 47 3A 30 30 30 0D');
HL('Video Source','HDMI 1',              '3A 30 31 53 3A 30 30 31 0D');
HL('Video Source','HDMI 2',              '3A 30 31 53 3A 30 30 32 0D');
HL('Video Source','HDMI 3',              '3A 30 31 53 3A 30 32 31 0D');
HL('Video Source','HDMI 4',              '3A 30 31 53 3A 30 32 32 0D');
HL('Video Source','Home',                '3A 30 31 53 3A 31 30 31 0D');
HL('Video Source','OPS',                 '3A 30 31 53 3A 31 30 33 0D');
HL('Video Source','Display Port',        '3A 30 31 53 3A 30 30 37 0D');
HL('Video Source','USB-C',               '3A 30 31 53 3A 31 30 34 0D');
HL('Video Source','USB-C 2 (AVE-9200)',  '3A 30 31 53 3A 31 30 35 0D');
HL('Video Source','Get Source Signal Status','3A 30 31 47 4B 30 30 30 0D');
HL('Language','English',  '3A 30 31 53 3C 30 30 30 0D');
HL('Language','Français', '3A 30 31 53 3C 30 30 31 0D');
HL('Language','Español',  '3A 30 31 53 3C 30 30 32 0D');
HL('Language','Dutch',    '3A 30 31 53 3C 30 30 37 0D');
HL('Language','Italian',  '3A 30 31 53 3C 30 31 33 0D');
HL('Picture Settings','Standard',  '3A 30 31 53 3D 30 30 30 0D');
HL('Picture Settings','Bright',    '3A 30 31 53 3D 30 30 31 0D');
HL('Picture Settings','Soft',      '3A 30 31 53 3D 30 30 32 0D');
HL('Picture Settings','Customer',  '3A 30 31 53 3D 30 30 33 0D');
HL('Color Temperature','Cool',     '3A 30 31 53 40 30 30 30 0D');
HL('Color Temperature','Standard', '3A 30 31 53 40 30 30 31 0D');
HL('Color Temperature','Warm',     '3A 30 31 53 40 30 30 32 0D');
HL('IR','Enable',  '3A 30 31 53 42 30 30 30 0D');
HL('IR','Disable', '3A 30 31 53 42 30 30 31 0D');
HL('Speaker','On',  '3A 30 31 53 43 30 30 31 0D');
HL('Speaker','Off', '3A 30 31 53 43 30 30 30 0D');
HL('Touch','On',    '3A 30 31 53 44 30 30 31 0D');
HL('Touch','Off',   '3A 30 31 53 44 30 30 30 0D');
HL('Power','No Signal Off',    '3A 30 31 53 46 30 30 30 0D');
HL('Power','No Signal 1 Min',  '3A 30 31 53 46 30 30 31 0D');
HL('Power','No Signal 3 Min',  '3A 30 31 53 46 30 30 33 0D');
HL('Power','No Signal 5 Min',  '3A 30 31 53 46 30 30 35 0D');
HL('Power','No Signal 10 Min', '3A 30 31 53 46 30 31 30 0D');
HL('Power','No Signal 15 Min', '3A 30 31 53 46 30 31 35 0D');
HL('Power','No Signal 30 Min', '3A 30 31 53 46 30 33 30 0D');
HL('Power','No Signal 45 Min', '3A 30 31 53 46 30 34 35 0D');
HL('Power','No Signal 60 Min', '3A 30 31 53 46 30 36 30 0D');
HL('HDMI Out','On',  '3A 30 31 53 48 30 30 31 0D');
HL('HDMI Out','Off', '3A 30 31 53 48 30 30 30 0D');
HL('Remote Control','Vol+',      '3A 30 31 53 41 30 30 30 0D');
HL('Remote Control','Vol-',      '3A 30 31 53 41 30 30 31 0D');
HL('Remote Control','Up',        '3A 30 31 53 41 30 31 30 0D');
HL('Remote Control','Down',      '3A 30 31 53 41 30 31 31 0D');
HL('Remote Control','Left',      '3A 30 31 53 41 30 31 32 0D');
HL('Remote Control','Right',     '3A 30 31 53 41 30 31 33 0D');
HL('Remote Control','Enter',     '3A 30 31 53 41 30 31 34 0D');
HL('Remote Control','Menu',      '3A 30 31 53 41 30 32 30 0D');
HL('Remote Control','Input',     '3A 30 31 53 41 30 32 31 0D');
HL('Remote Control','Back/Exit', '3A 30 31 53 41 30 32 32 0D');
HL('Remote Control','Blank',     '3A 30 31 53 41 30 33 31 0D');
HL('Remote Control','Freeze',    '3A 30 31 53 41 30 33 32 0D');
HL('Remote Control','Mute',      '3A 30 31 53 41 30 33 33 0D');
HL('Remote Control','Home',      '3A 30 31 53 41 30 33 34 0D');

// Variable H/L/9200 (3-digit decimal ASCII encoding)
const HL_VAR = [
  ['Volume',           'Set Volume',     '38'],
  ['Contrast',         'Set Contrast',   '34'],
  ['Brightness',       'Set Brightness', '35'],
  ['Sharpness',        'Set Sharpness',  '36'],
  ['Picture Settings', 'Set Hue Color',  '3E'],
  ['Picture Settings', 'Set Backlight',  '3F'],
];
for (const [cat, name, cmdByte] of HL_VAR) {
  for (const {lbl, dv} of LEVELS) {
    const d = decAscii3(dv);
    insertDL('H/L/9200', cat, `${name} ${lbl}`, `3A 30 31 53 ${cmdByte} ${d} 0D`, '', 4664, 'HEX');
  }
}

// ── K-Series ─────────────────────────────────────────────────────────────────

const K = (cat,n,c,notes='') => insertDL('K-Series',cat,n,c,notes,59596,'HEX');

K('Power','On',                '55 00 8E 00 E3','Power On');
K('Power','Off',               '55 00 8E 0F F2','Power Off');
K('Power','Wake',              'AA 00 01 01 AC','Wake Up');
K('Power','Standby',           'AA 00 01 00 AB','Standby');
K('Power','Get Current State', 'AA 00 02 00 AC');
K('Remote Control','Up',       '55 00 00 01 56');
K('Remote Control','Down',     '55 00 00 02 57');
K('Remote Control','Left',     '55 00 00 03 58');
K('Remote Control','Right',    '55 00 00 04 59');
K('Remote Control','Confirm',  '55 00 00 00 55');
K('Remote Control','Return/Back','55 00 0A 00 5F');
K('Volume','Toggle Mute',      '55 00 1A 00 6F');
K('Volume','Get Mute Status',  'AA 00 03 00 AD');
K('Volume','Volume Up by 1',   '55 00 0C 00 61','Volume up by 1%');
K('Volume','Volume Down by 1', '55 00 0E 00 63','Volume down by 1%');
K('Volume','Get Volume State', 'AA 00 04 00 AE');
K('Backlight','Get Current State','55 00 8B 00 E0');
K('Source','Home',   '55 00 91 00 E6','Set Home Source');
K('Source','HDMI 1', '55 00 80 08 DD','Set HDMI1 Source');
K('Source','HDMI 2', '55 00 80 09 DE','Set HDMI2 Source');
K('Source','USB-C',  '55 00 80 16 EB','Set USB-C Source');
K('Source','Get Current State','AA 00 05 00 AF');

// Variable K-Series with checksums
for (const {lbl, hv} of LEVELS) {
  // Volume: 55 00 FF XX YY
  const volCS = kChecksum(0x55, 0x00, 0xFF, hv);
  insertDL('K-Series','Volume',`Set Volume ${lbl}`,`55 00 FF ${hex2(hv)} ${volCS}`,'',59596,'HEX');
  // Backlight: 55 00 89 XX YY
  const blCS = kChecksum(0x55, 0x00, 0x89, hv);
  insertDL('K-Series','Backlight',`Set Backlight ${lbl}`,`55 00 89 ${hex2(hv)} ${blCS}`,'',59596,'HEX');
}

// ── S-Series ─────────────────────────────────────────────────────────────────

const S = (cat,n,c,notes='') => insertDL('S-Series',cat,n,c,notes,4664,'HEX');

S('Power','Backlight Off', '3A 30 31 53 30 30 30 30 0D');
S('Power','Backlight On',  '3A 30 31 53 30 30 30 31 0D');
S('Power','Power Off',     '3A 30 31 53 30 30 30 32 0D');
S('Power','Power On',      '3A 30 31 53 30 30 30 33 0D');
S('Treble','-5',           '3A 30 31 53 31 2D 30 35 0D');
S('Treble','-3',           '3A 30 31 53 31 2D 30 33 0D');
S('Treble','+3',           '3A 30 31 53 31 2B 30 33 0D');
S('Treble','+5',           '3A 30 31 53 31 2B 30 35 0D');
S('Bass','-5',             '3A 30 31 53 32 2D 30 35 0D');
S('Bass','-3',             '3A 30 31 53 32 2D 30 33 0D');
S('Bass','+3',             '3A 30 31 53 32 2B 30 33 0D');
S('Bass','+5',             '3A 30 31 53 32 2B 30 35 0D');
S('Balance','-50',         '3A 30 31 53 33 2D 35 30 0D');
S('Balance','+20',         '3A 30 31 53 33 2B 32 30 0D');
S('Sound Mode','Standard', '3A 30 31 53 37 30 30 31 0D');
S('Sound Mode','Custom',   '3A 30 31 53 37 30 30 32 0D');
S('Sound Mode','Classroom','3A 30 31 53 37 30 30 33 0D');
S('Sound Mode','Meeting',  '3A 30 31 53 37 30 30 34 0D');
S('Mute','Mute Off',       '3A 30 31 53 39 30 30 30 0D');
S('Mute','Mute On',        '3A 30 31 53 39 30 30 31 0D');
S('Source','VGA',           '3A 30 31 53 3A 30 30 30 0D');
S('Source','HDMI 1',        '3A 30 31 53 3A 30 30 31 0D');
S('Source','HDMI 2',        '3A 30 31 53 3A 30 30 32 0D');
S('Source','HDMI 3',        '3A 30 31 53 3A 30 32 31 0D');
S('Source','Home (Android)','3A 30 31 53 3A 31 30 31 0D');
S('Source','Slot in PC',    '3A 30 31 53 3A 31 30 33 0D');
S('Source','Type-C 1',      '3A 30 31 53 3A 31 30 34 0D');
S('Language','English',    '3A 30 31 53 3C 30 30 30 0D');
S('Language','Français',   '3A 30 31 53 3C 30 30 31 0D');
S('Language','Español',    '3A 30 31 53 3C 30 30 32 0D');
S('Language','Czech',      '3A 30 31 53 3C 30 31 30 0D');
S('Language','Danish',     '3A 30 31 53 3C 30 31 31 0D');
S('Language','Swedish',    '3A 30 31 53 3C 30 31 32 0D');
S('Language','Italian',    '3A 30 31 53 3C 30 31 33 0D');
S('Language','Romanian',   '3A 30 31 53 3C 30 31 34 0D');
S('Language','Norwegian',  '3A 30 31 53 3C 30 31 35 0D');
S('Language','Finnish',    '3A 30 31 53 3C 30 31 36 0D');
S('Language','Greek',      '3A 30 31 53 3C 30 31 37 0D');
S('Language','Turkish',    '3A 30 31 53 3C 30 31 38 0D');
S('Language','Arabic',     '3A 30 31 53 3C 30 31 39 0D');
S('Language','Japanese',   '3A 30 31 53 3C 30 32 30 0D');
S('Language','Ukrainian',  '3A 30 31 53 3C 30 32 31 0D');
S('Language','Korean',     '3A 30 31 53 3C 30 32 32 0D');
S('Language','Hungarian',  '3A 30 31 53 3C 30 32 33 0D');
S('Language','Persian',    '3A 30 31 53 3C 30 32 34 0D');
S('Language','Vietnamese', '3A 30 31 53 3C 30 32 35 0D');
S('Language','Thai',       '3A 30 31 53 3C 30 32 36 0D');
S('Language','Catalan',    '3A 30 31 53 3C 30 32 37 0D');
S('Language','Lithuanian', '3A 30 31 53 3C 30 32 38 0D');
S('Language','Croatian',   '3A 30 31 53 3C 30 32 39 0D');
S('Language','Estonian',   '3A 30 31 53 3C 30 33 30 0D');
S('Picture Mode','Standard', '3A 30 31 53 3D 30 30 30 0D');
S('Picture Mode','Bright',   '3A 30 31 53 3D 30 30 31 0D');
S('Picture Mode','Soft',     '3A 30 31 53 3D 30 30 32 0D');
S('Picture Mode','Customer', '3A 30 31 53 3D 30 30 33 0D');
S('Color Temperature','Cool',     '3A 30 31 53 40 30 30 30 0D');
S('Color Temperature','Standard', '3A 30 31 53 40 30 30 31 0D');
S('Color Temperature','Warm',     '3A 30 31 53 40 30 30 32 0D');
S('Remote Control','Vol+',      '3A 30 31 53 41 30 30 30 0D');
S('Remote Control','Vol-',      '3A 30 31 53 41 30 30 31 0D');
S('Remote Control','Up',        '3A 30 31 53 41 30 31 30 0D');
S('Remote Control','Down',      '3A 30 31 53 41 30 31 31 0D');
S('Remote Control','Left',      '3A 30 31 53 41 30 31 32 0D');
S('Remote Control','Right',     '3A 30 31 53 41 30 31 33 0D');
S('Remote Control','Enter',     '3A 30 31 53 41 30 31 34 0D');
S('Remote Control','Menu',      '3A 30 31 53 41 30 32 30 0D');
S('Remote Control','Input',     '3A 30 31 53 41 30 32 31 0D');
S('Remote Control','Back/Exit', '3A 30 31 53 41 30 32 32 0D');
S('Remote Control','Blank',     '3A 30 31 53 41 30 33 31 0D');
S('Remote Control','Freeze',    '3A 30 31 53 41 30 33 32 0D');
S('Remote Control','Mute',      '3A 30 31 53 41 30 33 33 0D');
S('Remote Control','Home',      '3A 30 31 53 41 30 33 34 0D');
S('IR','Enable',  '3A 30 31 53 42 30 30 30 0D');
S('IR','Disable', '3A 30 31 53 42 30 30 31 0D');
S('Speaker','Off','3A 30 31 53 43 30 30 30 0D');
S('Speaker','On', '3A 30 31 53 43 30 30 31 0D');
S('Touch','Off',  '3A 30 31 53 44 30 30 30 0D');
S('Touch','On',   '3A 30 31 53 44 30 30 31 0D');
S('Screen','Off', '3A 30 31 53 45 30 30 30 0D');
S('Screen','On',  '3A 30 31 53 45 30 30 31 0D');
S('No Signal Power Off','Disabled',  '3A 30 31 53 46 30 30 30 0D');
S('No Signal Power Off','1 Minute',  '3A 30 31 53 46 30 30 31 0D');
S('No Signal Power Off','3 Minutes', '3A 30 31 53 46 30 30 33 0D');
S('No Signal Power Off','5 Minutes', '3A 30 31 53 46 30 30 35 0D');
S('No Signal Power Off','10 Minutes','3A 30 31 53 46 30 31 30 0D');
S('No Signal Power Off','15 Minutes','3A 30 31 53 46 30 31 35 0D');
S('No Signal Power Off','30 Minutes','3A 30 31 53 46 30 33 30 0D');
S('No Signal Power Off','45 Minutes','3A 30 31 53 46 30 34 35 0D');
S('No Signal Power Off','60 Minutes','3A 30 31 53 46 30 36 30 0D');

// Variable S-Series
const S_VAR = [
  ['Volume',    'Set Volume',     '38'],
  ['Contrast',  'Set Contrast',   '34'],
  ['Brightness','Set Brightness', '35'],
  ['Sharpness', 'Set Sharpness',  '36'],
  ['Hue',       'Set Hue',        '3E'],
  ['Backlight', 'Set Backlight',  '3F'],
];
for (const [cat, name, cmdByte] of S_VAR) {
  for (const {lbl, dv} of LEVELS) {
    const d = decAscii3(dv);
    insertDL('S-Series', cat, `${name} ${lbl}`, `3A 30 31 53 ${cmdByte} ${d} 0D`, '', 4664, 'HEX');
  }
}

// ── X-Series ─────────────────────────────────────────────────────────────────

const XP = '01 03 01 D0 00 D1';
const FF = 'FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF';
const xGet = (cmdB, catB) => `${XP} ${cmdB} ${catB} 00 00 ${FF} 00 00`;
const xSet = (cmdB, catB, val) => `${XP} ${cmdB} ${catB} 00 00 ${FF} 00 01 ${val}`;

const X = (cat,n,c,notes='') => insertDL('X-Series',cat,n,c,notes,59596,'HEX');

X('Status',  'Get Status',          xGet('00','D0'));
X('Power',   'GET Power State',     xGet('05','C0'));
X('Power',   'SET Power On',        xSet('03','C0','01'));
X('Power',   'SET Power Off',       xSet('03','C0','00'));
X('Volume',  'GET Volume',          xGet('01','C2'));
X('Brightness','GET Brightness',    xGet('1D','C2'));
X('Contrast', 'GET Contrast',       xGet('15','C2'));
X('Source',  'GET Source',          xGet('11','C2'));
X('Source',  'SET Home (Android)',  xSet('13','C2','00'));
X('Source',  'SET HDMI 1',          xSet('13','C2','02'));
X('Source',  'SET HDMI 2',          xSet('13','C2','03'));
X('Source',  'SET HDMI 3',          xSet('13','C2','04'));
X('LED Gain','GET Red Gain',        xGet('21','C2'));
X('LED Gain','GET Green Gain',      xGet('25','C2'));
X('LED Gain','GET Blue Gain',       xGet('29','C2'));
X('Aspect Ratio','GET Ratio',       xGet('0D','C2'));
X('Aspect Ratio','SET 4:3',         xSet('0F','C2','01'));
X('Aspect Ratio','SET 16:9',        xSet('0F','C2','02'));
X('Aspect Ratio','SET Full-Screen', xSet('0F','C2','03'));
X('Aspect Ratio','SET Original',    xSet('0F','C2','04'));
X('Image Presets','SET Conference', xSet('45','C2','00'));
X('Image Presets','SET Standard',   xSet('45','C2','01'));
X('Image Presets','SET Energy Save',xSet('45','C2','02'));
X('Image Presets','SET User',       xSet('45','C2','03'));
X('Color Temperature','SET Standard',xSet('1B','C2','01'));
X('Color Temperature','SET Warm',    xSet('1B','C2','02'));
X('Color Temperature','SET Cool',    xSet('1B','C2','03'));
X('Color Temperature','SET User',    xSet('1B','C2','04'));

// Variable X-Series
const X_VAR = [
  ['Volume',    'SET Volume',     '03','C2'],
  ['Brightness','SET Brightness', '1F','C2'],
  ['Contrast',  'SET Contrast',   '17','C2'],
  ['LED Gain',  'SET Red Gain',   '23','C2'],
  ['LED Gain',  'SET Green Gain', '27','C2'],
  ['LED Gain',  'SET Blue Gain',  '2B','C2'],
];
for (const [cat, name, cmdB, catB] of X_VAR) {
  for (const {lbl, hv} of LEVELS) {
    insertDL('X-Series', cat, `${name} ${lbl}`, xSet(cmdB, catB, hex2(hv)), '', 59596, 'HEX');
  }
}

// ── Models ────────────────────────────────────────────────────────────────────

const MODELS = [
  // A-Series
  ['AVA-6520','A-Series'], ['AVA-7520','A-Series'], ['AVA-8620','A-Series'],
  // B-Series
  ['AVB-4310','B-Series'], ['AVB-5010','B-Series'], ['AVB-5510','B-Series'],
  ['AVB-6510','B-Series'], ['AVB-7510','B-Series'], ['AVB-8610','B-Series'],
  // E-Group1 (xx20)
  ['AVE-6520','E-Group1'], ['AVE-7520','E-Group1'], ['AVE-8620','E-Group1'],
  // E-Group1 (xx30)
  ['AVE-5530','E-Group1'], ['AVE-6530','E-Group1'], ['AVE-7530','E-Group1'], ['AVE-8630','E-Group1'],
  // E-Group1 (xx30-A)
  ['AVE-6530-A','E-Group1'],['AVE-7530-A','E-Group1'],['AVE-8630-A','E-Group1'],
  // E-Group1 (xx40)
  ['AVE-5540','E-Group1'], ['AVE-6540','E-Group1'], ['AVE-7540','E-Group1'], ['AVE-8640','E-Group1'],
  // E-Group1 (AVF-xx50)
  ['AVF-6550','E-Group1'], ['AVF-7550','E-Group1'], ['AVF-8650','E-Group1'],
  // E-Group1 (AVG-xx60)
  ['AVG-6560','E-Group1'], ['AVG-7560','E-Group1'], ['AVG-8560','E-Group1'],
  // E-Group1 (AVW-xx55)
  ['AVW-5555','E-Group1'], ['AVW-6555','E-Group1'],
  // E-50
  ['AVE-5550','E-50'], ['AVE-6550','E-50'], ['AVE-7550','E-50'], ['AVE-8650','E-50'],
  // H/L/9200
  ['AVE-9200','H/L/9200'],
  ['AVH-6520','H/L/9200'], ['AVH-7520','H/L/9200'], ['AVH-8620','H/L/9200'],
  ['AVH-652GV','H/L/9200'],['AVH-752GV','H/L/9200'],['AVH-862GV','H/L/9200'],
  ['AVL-1050-D','H/L/9200'],['AVL-1050-T','H/L/9200'],
  // K-Series
  ['AVK-5510','K-Series'], ['AVK-6510','K-Series'], ['AVK-7510','K-Series'],
  ['AVK-8610','K-Series'], ['AVK-9810','K-Series'],
  // S-Series
  ['AVS-6510','S-Series'],  ['AVS-7510','S-Series'],  ['AVS-8610','S-Series'],
  ['AVS-6510E','S-Series'], ['AVS-7510E','S-Series'], ['AVS-8610E','S-Series'],
  // X-Series
  ['AVX-1320','X-Series'], ['AVX-1380','X-Series'],
];
for (const [model, series] of MODELS) insertModel(model, series);

// ── Output ────────────────────────────────────────────────────────────────────

process.stdout.write(lines.join('\n') + '\n');
