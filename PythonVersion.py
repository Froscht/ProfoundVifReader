#!/usr/bin/env python3
# Profound VIF2CSV (Python port of C# implementation )

import argparse
import datetime as _dt
import io
import math
import os
import sys
from dataclasses import dataclass
from typing import Optional, Tuple

FLOAT16_DIVISOR = 1_000_000.0
INT16_DIVISOR = 2.0

RECORD_SIZE_EXPECTED = 68
MIN_RECORD_BYTES = 12
SCAN_CHUNK_SIZE = 256 * 1024
RECORD_BUFFER_SIZE = 70


def log(msg: str) -> None:
    print(msg, file=sys.stderr)


def validate_date_string(s: str) -> bool:
    if not s:
        return False
    if len(s) == 8:  # YY-MM-DD
        return s[2] == "-" and s[5] == "-" and s[:2].isdigit() and s[3:5].isdigit() and s[6:8].isdigit()
    if len(s) == 10:  # YYYY-MM-DD
        return s[4] == "-" and s[7] == "-" and s[:4].isdigit() and s[5:7].isdigit() and s[8:10].isdigit()
    return False


def normalize_date_string(s: str) -> str:
    # Convert YY-MM-DD to YYYY-MM-DD (2000+YY)
    if len(s) == 8:
        yy = int(s[:2])
        return f"{2000 + yy:04d}-{s[3:]}"
    return s


def u16_le(buf: bytes, off: int) -> int:
    return buf[off] | (buf[off + 1] << 8)


def i16_le(buf: bytes, off: int) -> int:
    v = u16_le(buf, off)
    return v - 0x10000 if v & 0x8000 else v


@dataclass
class DirData:
    v: str
    kb: str
    ft: str
    u: str
    a: str
    cv: str
    cf: str
    state: str


class VifReader:
    def __init__(
            self,
            print_header: bool,
            print_number: bool,
            filter_today: bool,
            long_format: bool,
            date_filter: Optional[str],
    ):
        self.print_header = print_header
        self.print_number = print_number
        self.filter_today = filter_today
        self.long_format = long_format
        self.date_filter = date_filter

        self.is_kb_mode = False
        self.records_processed = 0
        self.records_skipped = 0
        self.record_counter = 0

    # ----------------------------
    # Public entry
    # ----------------------------
    def process_vif_file(self, filename: str, output_file: Optional[str]) -> None:
        if output_file:
            out = open(output_file, "w", encoding="utf-8", newline="")
        else:
            # emulate CP1252 output like your C# tool
            if hasattr(sys.stdout, "reconfigure"):
                sys.stdout.reconfigure(encoding="cp1252", errors="replace", newline="\n")
            out = sys.stdout

        try:
            with open(filename, "rb") as f:
                # pass 1: detect KB mode
                self.is_kb_mode = self.detect_kb_mode(f)

                # headers
                if self.print_header:
                    out.write(self.header_names() + "\n")
                    out.write(self.header_units() + "\n")

                # pass 2: records
                f.seek(0, io.SEEK_SET)
                self.process_records(f, out)

            log(f"Processed: {self.records_processed}, Skipped: {self.records_skipped}")
        finally:
            if output_file:
                out.flush()
                out.close()

    # ----------------------------
    # Header building
    # ----------------------------
    def header_names(self) -> str:
        parts = []
        parts.append("\"date\",\"time\",")
        if self.print_number:
            parts.append("\"counter\",")
        parts.append("\"state\",\"|v|\",")
        parts.append(self.direction_header("x"))
        parts.append(self.direction_header("y"))
        parts.append(self.direction_header("z"))
        parts.append("\"temperature\",\"battery\",\"memory use\",\"usb powered\",")
        parts.append("\"signal strength\",\"signal quality\",\"transmitted\",\"all transmitted\",")
        parts.append("\"peak type\",\"code\",\"error code\",\"geophone\",\"clock changed\",")
        return "".join(parts).rstrip(",")

    def header_units(self) -> str:
        parts = []
        parts.append("\"YYYY-MM-DD\",\"hh:mm:ss\",")
        if self.print_number:
            parts.append("\"count\",")
        parts.append("\"\",\"mm/s\",")
        parts.append(self.direction_unit())
        parts.append(self.direction_unit())
        parts.append(self.direction_unit())
        parts.append("\"°C\",\"V\",\"%\",\"\",")
        parts.append("\"dBm\",\"\",\"\",\"\",")
        parts.append("\"\",\"\",\"\",\"\",\"\",")
        return "".join(parts).rstrip(",")

    def direction_header(self, axis: str) -> str:
        parts = []
        parts.append(f"\"state({axis})\",")
        parts.append(f"\"v({axis})\",")
        if self.is_kb_mode:
            parts.append(f"\"kb({axis})\",")
        else:
            parts.append(f"\"f_zc({axis})\",")
        parts.append(f"\"f_ft({axis})\",")
        parts.append(f"\"u({axis})\",")
        parts.append(f"\"a({axis})\",")
        parts.append(f"\"v_cat({axis})\",")
        parts.append(f"\"f_cat({axis})\",")
        return "".join(parts)

    @staticmethod
    def direction_unit() -> str:
        return "\"\",\"mm/s\",\"Hz\",\"Hz\",\"mm\",\"m/s2\",\"mm/s\",\"Hz\","

    # ----------------------------
    # KB mode detection
    # ----------------------------
    def detect_kb_mode(self, f) -> bool:
        state = 0
        buf = bytearray(70)

        while True:
            b = f.read(1)
            if not b:
                break
            b0 = b[0]

            if state == 0 and b0 == ord("V"):
                buf[0] = b0
                state = 1
            elif state == 1 and b0 == ord("I"):
                buf[1] = b0
                state = 2
            elif state == 2 and b0 == ord("B"):
                buf[2] = b0
                state = 3

                tail = f.read(9)
                if len(tail) != 9:
                    break
                buf[3:12] = tail

                record_type = buf[3]
                if record_type == 0x8A:
                    return True

                record_size = u16_le(buf, 4)
                remaining = record_size - 12
                if remaining > 0:
                    f.seek(remaining, io.SEEK_CUR)
                state = 0
            else:
                state = 0

        return False

    # ----------------------------
    # Record scanning & processing
    # ----------------------------
    def process_records(self, f, out) -> None:
        window = bytearray(SCAN_CHUNK_SIZE * 2)
        window_len = 0
        scan_idx = 0
        window_start_pos = 0
        record_count = 0

        last_record = None  # type: Optional[bytearray]
        last_record_pos = -1

        def ensure_bytes(required_index: int) -> bool:
            nonlocal window, window_len, scan_idx, window_start_pos
            while required_index > window_len:
                if scan_idx > 0:
                    remaining = window_len - scan_idx
                    if remaining > 0:
                        window[:remaining] = window[scan_idx:window_len]
                    window_start_pos += scan_idx
                    window_len = remaining
                    scan_idx = 0

                if window_len == len(window):
                    new_size = len(window) * 2
                    while new_size < required_index:
                        new_size *= 2
                    bigger = bytearray(new_size)
                    bigger[:window_len] = window[:window_len]
                    window = bigger

                chunk = f.read(len(window) - window_len)
                if not chunk:
                    return False
                window[window_len : window_len + len(chunk)] = chunk
                window_len += len(chunk)

            return True

        while True:
            if not ensure_bytes(scan_idx + 3):
                break

            i = scan_idx
            while i + 2 < window_len:
                if window[i] == ord("V") and window[i + 1] == ord("I") and window[i + 2] == ord("B"):
                    break
                i += 1

            if i + 2 >= window_len:
                scan_idx = i
                continue

            if not ensure_bytes(i + MIN_RECORD_BYTES):
                break

            record_size = window[i + 4] | (window[i + 5] << 8)
            advance = MIN_RECORD_BYTES if record_size < MIN_RECORD_BYTES else record_size
            if not ensure_bytes(i + advance):
                break

            record_start_pos = window_start_pos + i
            record_count += 1

            if last_record is not None and last_record_pos >= 0:
                read_type = self.get_read_type_from_delta(record_start_pos - last_record_pos)
                self.process_record(bytes(last_record), read_type, out)

            if last_record is None:
                last_record = bytearray(RECORD_BUFFER_SIZE)

            copy_len = min(advance, len(last_record))
            last_record[:copy_len] = window[i : i + copy_len]
            if copy_len < len(last_record):
                for k in range(copy_len, len(last_record)):
                    last_record[k] = 0
            last_record_pos = record_start_pos

            scan_idx = i + advance

        if last_record is not None:
            self.process_record(bytes(last_record), 2, out)

        log(f"Total records: {record_count}")

    @staticmethod
    def get_read_type_from_delta(delta: int) -> int:
        return 2 if delta == 68 else 5

    # ----------------------------
    # Record parsing
    # ----------------------------
    def process_record(self, rec: bytes, read_type: int, out) -> None:
        record_type = rec[3]
        record_size = u16_le(rec, 4)

        if not self.is_valid_to_process(record_size, read_type):
            self.records_skipped += 1
            return

        # Validate record type and size (match original tool)
        if (record_type & 0xFD) != 0x88:
            self.records_skipped += 1
            return
        if record_size != RECORD_SIZE_EXPECTED:
            self.records_skipped += 1
            return

        # Datetime: [second][minute][hour][day][month][year] at offsets 6..11
        second = rec[6]
        minute = rec[7]
        hour = rec[8]
        day = rec[9]
        month = rec[10]
        year = 2000 + rec[11]

        if not self.datetime_valid(second, minute, hour, day, month, rec[11]):
            self.records_skipped += 1
            return

        date_s = f"{year:04d}-{month:02d}-{day:02d}"
        time_s = f"{hour:02d}:{minute:02d}:{second:02d}"

        # Filters
        if self.filter_today:
            today = _dt.date.today()
            if (year, month, day) != (today.year, today.month, today.day):
                self.records_skipped += 1
                return
        elif self.date_filter is not None:
            if date_s != self.date_filter:
                self.records_skipped += 1
                return

        self.records_processed += 1
        self.record_counter += 1

        is_extended = (record_type == 0x8A)

        # Counter at offset 64-66 (same odd 24-bit-ish math as your C#)
        counter = ""
        if record_size > 0x43 and len(rec) >= 67:
            counter_value = rec[64] + ((rec[65] + (rec[66] << 8)) << 8)
            counter = str(counter_value)

        # Axis statuses & data
        x_status = self.axis_status(rec, 14)
        y_status = self.axis_status(rec, 28)
        z_status = self.axis_status(rec, 42)
        overall_status = self.get_overall_status(x_status, y_status, z_status)

        x_data = self.parse_direction(rec, 14, is_extended, x_status)
        y_data = self.parse_direction(rec, 28, is_extended, y_status)
        z_data = self.parse_direction(rec, 42, is_extended, z_status)

        # Sensor data
        temperature = rec[56] * 0.5 - 27.5
        voltage = rec[57] * 0.01 + 2.45
        memory_use = rec[58] & 0x7F
        usb_powered = rec[58] >> 7

        ss_raw = rec[59] & 0x1F
        signal_strength = str(2 * ss_raw - 113) if ss_raw != 0 else ""
        signal_quality = self.get_signal_quality(ss_raw)

        transmitted = 1 if (rec[59] & 0x20) else 0
        all_transmitted = 1 if (rec[59] & 0x40) else 0

        peak_type = rec[60] & 3
        peak_type_cat = self.get_peak_type_cat(peak_type)
        code = "SBR" if (rec[60] & 4) else "DIN"

        error_code = rec[61]
        geophone_sn = u16_le(rec, 62)
        geophone = self.format_geophone(geophone_sn)
        clock_changed = rec[60] >> 6

        overall_state = self.status_to_string(overall_status)
        overall_v = "" if overall_status != 0 else self.format_float16(u16_le(rec, 12))

        if self.long_format:
            temp_s = self.format_fixed_odd(temperature, 4)
            volt_s = self.format_fixed_odd(voltage, 4)
        else:
            temp_s = self.format_fixed_odd(temperature, 1)
            volt_s = self.format_fixed_odd(voltage, 2)

        # CSV output (quote everything; same column order as C#)
        fields = []
        fields.append(date_s)
        fields.append(time_s)
        if self.print_number:
            fields.append(counter)
        fields.append(overall_state)
        fields.append(overall_v)

        # X
        fields.extend([x_data.state, x_data.v, x_data.kb, x_data.ft, x_data.u, x_data.a, x_data.cv, x_data.cf])
        # Y
        fields.extend([y_data.state, y_data.v, y_data.kb, y_data.ft, y_data.u, y_data.a, y_data.cv, y_data.cf])
        # Z
        fields.extend([z_data.state, z_data.v, z_data.kb, z_data.ft, z_data.u, z_data.a, z_data.cv, z_data.cf])

        # Sensor
        fields.extend(
            [
                temp_s,
                volt_s,
                str(memory_use),
                str(usb_powered),
                signal_strength,
                signal_quality,
                str(transmitted),
                str(all_transmitted),
                peak_type_cat,
                code,
                str(error_code),
                geophone,
                str(clock_changed),
            ]
        )

        out.write(",".join(f"\"{v}\"" for v in fields) + "\n")

    # ----------------------------
    # Direction parsing
    # ----------------------------
    def parse_direction(self, rec: bytes, off: int, is_extended: bool, status: int) -> DirData:
        if status != 0:
            return DirData("", "", "", "", "", "", "", self.status_to_string(status))

        v_raw = u16_le(rec, off)
        kbzc_raw = i16_le(rec, off + 2)
        ft_raw = i16_le(rec, off + 4)
        u_raw = u16_le(rec, off + 6)
        a_raw = u16_le(rec, off + 8)
        cv_raw = u16_le(rec, off + 10)
        cf_raw = i16_le(rec, off + 12)

        v = self.format_float16(v_raw)

        if is_extended:
            # KB mode
            int_val = self.sv_from_float16(kbzc_raw)
            kb_val = math.sqrt(int_val) * 0.01
            if kb_val <= 0.1:
                kb_val = 0.0
            kb = self.format_fixed_odd(kb_val, 4 if self.long_format else 2)
        else:
            # ZC mode
            if kbzc_raw <= 0:
                kb = ""
            else:
                zc = 1024.0 / kbzc_raw
                kb = self.format_fixed_odd(zc, 4 if self.long_format else 2)

        ft = self.format_int16(ft_raw)
        u = self.format_float16(u_raw)
        a = self.format_float16(a_raw)
        cv = self.format_float16(cv_raw)
        cf = self.format_int16(cf_raw)

        return DirData(v, kb, ft, u, a, cv, cf, "")

    def axis_status(self, rec: bytes, off: int) -> int:
        s = self.sv_is_value_valid(u16_le(rec, off))
        if s == 0:
            s = self.sv_is_value_valid(u16_le(rec, off + 6))
        if s == 0:
            s = self.sv_is_value_valid(u16_le(rec, off + 8))
        if s == 0:
            s = self.sv_is_value_valid(u16_le(rec, off + 10))
        return s

    @staticmethod
    def get_overall_status(x: int, y: int, z: int) -> int:
        if x == sys.maxsize or y == sys.maxsize or z == sys.maxsize:
            return sys.maxsize
        if x != 0:
            return x
        if y != 0:
            return y
        if z != 0:
            return z
        return 0

    # ----------------------------
    # Formatting / validity helpers
    # ----------------------------
    @staticmethod
    def status_to_string(status: int) -> str:
        if status == -1:
            return "DISCONNECTED"
        if status == -2:
            return "DATA INVALID"
        if status == -3:
            return "NO DATA"
        if status == -4:
            return "NOT RESPONDING"
        if status == sys.maxsize:
            return "OVERLOAD"
        return ""

    def format_float16(self, value_u16: int) -> str:
        int_val = self.sv_from_float16(self._to_i16(value_u16))
        if self.is_special_value(int_val) or self.is_overload(int_val):
            return ""
        result = int_val / FLOAT16_DIVISOR
        return self.format_fixed_odd(result, 4 if self.long_format else 2)

    def format_int16(self, value_i16: int) -> str:
        if self.is_special_value(value_i16):
            return ""
        result = value_i16 / INT16_DIVISOR
        return self.format_fixed_odd(result, 4 if self.long_format else 1)

    @staticmethod
    def sv_from_float16(value_i16: int) -> int:
        # direct port of your C# SV_FromFloat16
        result = value_i16
        v2 = ((value_i16 & 0xFFFF) >> 11) - 1
        if v2 <= 0x13:
            v3 = value_i16 & 0x7FF
            v3 |= 0x800
            return v3 << int(v2)
        return result

    @staticmethod
    def is_special_value(v: int) -> bool:
        return -4 <= v <= -1

    @staticmethod
    def is_overload(v: int) -> bool:
        return v > 99_999_999

    @staticmethod
    def sv_is_value_valid(value_u16: int) -> int:
        # direct port of your C# SV_IsValueValid
        v1 = (value_u16 >> 11) - 1

        if v1 > 0x13:
            result = VifReader._to_i16(value_u16)
            if (result & 0xFFFFFFFF) < 0xFFFFFFFC:
                return 0
        else:
            v2 = value_u16 & 0x7FF
            v2 = (v2 & 0xFF) | ((((v2 >> 8) | 8) & 0xFFFF) << 8)
            result = v2 << int(v1)
            if (result + 4) & 0xFFFFFFFF > 3:
                valid = result <= 99_999_999
                result = sys.maxsize
                if valid:
                    return 0
        return result

    @staticmethod
    def get_signal_quality(signal_strength_raw: int) -> str:
        if signal_strength_raw == 0:
            return "Unknown"
        if signal_strength_raw > 23:
            return "Excellent"
        if signal_strength_raw > 15:
            return "Good"
        if signal_strength_raw > 7:
            return "Low"
        return "Bad"

    @staticmethod
    def format_geophone(value_u16: int) -> str:
        typ = value_u16 & 0xC000
        number = value_u16 & 0x3FFF
        if typ == 0x4000:
            return f"TDA{number:05d}"
        if typ == 0x8000:
            return f"TDS{number:05d}"
        if typ == 0xC000:
            return "???00000"
        return f"unknown{number:05d}"

    @staticmethod
    def datetime_valid(second: int, minute: int, hour: int, day: int, month: int, year_yy: int) -> bool:
        if second > 59 or minute > 59 or hour > 23:
            return False
        if year_yy > 99:
            return False
        if month < 1 or month > 12:
            return False

        if month in (1, 3, 5, 7, 8, 10, 12):
            max_day = 31
        elif month in (4, 6, 9, 11):
            max_day = 30
        else:
            max_day = 29 if (year_yy & 3) == 0 else 28

        return 1 <= day <= max_day

    def format_fixed_odd(self, value: float, decimals: int) -> str:
        # Port of your "FormatFixedOdd": rounds ties (x.5) toward the odd integer.
        scale = 10.0 ** decimals
        scaled = value * scale
        sign = 1.0 if scaled >= 0 else -1.0
        abs_scaled = abs(scaled)

        fl = math.floor(abs_scaled)
        frac = abs_scaled - fl

        tie_eps = 1e-7
        if frac > 0.5 + tie_eps:
            rounded = fl + 1.0
        elif frac < 0.5 - tie_eps:
            rounded = fl
        else:
            # exactly tie -> choose odd
            rounded = fl + 1.0 if (int(fl) % 2 == 0) else fl

        rounded *= sign
        result = rounded / scale
        return f"{result:.{decimals}f}"

    @staticmethod
    def is_valid_to_process(record_size: int, read_type: int) -> bool:
        return read_type <= 9 and record_size == 68 and (((1 << read_type) & 0x45) != 0)

    @staticmethod
    def get_peak_type_cat(peak_type: int) -> str:
        if peak_type == 0:
            return "vcatnone"
        if peak_type == 1:
            return "vcat1"
        if peak_type == 2:
            return "vcat2"
        if peak_type == 3:
            return "vcat3"
        return "vcat"

    @staticmethod
    def _to_i16(u: int) -> int:
        u &= 0xFFFF
        return u - 0x10000 if u & 0x8000 else u


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="VIF2CSV",
        add_help=False,
        description="Profound Tool Suite remake (Python)",
    )
    p.add_argument("files", nargs="*", help="one or more VIF files")
    p.add_argument("-h", "--header", action="store_true", help="set header data first")
    p.add_argument("-V", "--version", action="store_true", help="display version")
    p.add_argument("-n", action="store_true", help="add counter column (default: on)")
    p.add_argument("-N", action="store_true", help="remove counter column")
    p.add_argument("-D", "--today", action="store_true", help="output only from today")
    p.add_argument("-L", "--long", action="store_true", help="extended precision")
    p.add_argument("-d", "--day", dest="day", default=None, help='output only from a specified day ("YYYY-MM-DD" or "YY-MM-DD")')
    return p


def main(argv: list[str]) -> int:
    parser = build_arg_parser()
    args = parser.parse_args(argv)

    if args.version:
        print("(c) Copyright 2019-2025")
        print("Profound VIF2CSV 1.10")
        return 0

    if not args.files:
        parser.print_help()
        return 1

    # defaults like C#:
    option_header = bool(args.header)
    option_today = bool(args.today)
    option_long = bool(args.long)

    # default number ON unless -N; your C# also lets -n force it on
    option_number = True
    if args.N:
        option_number = False
    if args.n:
        option_number = True

    date_filter = None
    if args.day is not None:
        log(f'set date filter to: "{args.day}"')
        if not validate_date_string(args.day):
            print("ERROR: invalid date format. Use YYYY-MM-DD or YY-MM-DD", file=sys.stderr)
            parser.print_help()
            return 2
        date_filter = normalize_date_string(args.day)

    first = True
    for fn in args.files:
        if not os.path.exists(fn):
            print(f'ERROR: can\'t open file: "{fn}"', file=sys.stderr)
            continue

        rdr = VifReader(
            print_header=(option_header and first),
            print_number=option_number,
            filter_today=option_today,
            long_format=option_long,
            date_filter=date_filter,
        )
        rdr.process_vif_file(fn, None)
        first = False

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
