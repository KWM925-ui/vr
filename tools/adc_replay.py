"""Prepare PC-normalized ADC observations; explicit `send` replays to 127.0.0.1 only.

This is dragonboat.pc-adc.v1, not PCB firmware/USB framing. Values retain their
CSV raw units; neither conversion nor sending supplies calibration or force N.
The `profile` command creates an explicit uncalibrated raw-only PC listener
profile from all four observed channels. It never supplies wiring or calibration.
The send interval is an assumed PC replay cadence, not a firmware sample rate.
"""

import argparse
import csv
from dataclasses import dataclass
import json
import math
from pathlib import Path
import re
import socket
import sys
import time


FORMAT = "dragonboat.pc-adc.v1"
MAX_PACKET_BYTES = 16000
MAX_NODES = 4096
MAX_DEPTH = 48
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
CHANNEL_IDS = {"CH1", "CH2", "CH3", "CH4"}
CSV_REQUIRED = {
    "packet_sequence", "source_id", "channel_id", "sample_sequence",
    "raw_units", "raw", "valid", "saturated",
}
CSV_OPTIONAL = {"device_time_us", "sample_time_us", "has_raw", "raw_text"}
PACKET_FIELDS = {
    "format", "sourceId", "sourceKind", "sequenceValid", "sequence",
    "deviceTimeValid", "deviceTimeUs", "channels",
}
CHANNEL_FIELDS = {
    "channelId", "sampleSequenceValid", "sampleSequence", "sampleTimeValid",
    "sampleTimeUs", "rawUnits", "hasRaw", "raw", "rawText", "valid", "saturated",
}


class ReplayError(ValueError):
    """Input cannot be replayed as explicit PC acquisition evidence."""


@dataclass(frozen=True)
class Counts:
    lines: int
    packets: int
    channels: int
    source_id: str


def newer(current, previous):
    """Same unsigned32 half-range rule as the C# bridge; wrap is supported."""
    return 0 < ((current - previous) & UINT32_MAX) < (1 << 31)


def _nonempty(value, name):
    if not isinstance(value, str) or not value.strip():
        raise ReplayError(f"{name}: nonempty string required")
    return value


def _uint(value, name, maximum=UINT32_MAX):
    if type(value) is not int or not 0 <= value <= maximum:
        raise ReplayError(f"{name}: integer in 0..{maximum} required")
    return value


def _boolean(value, name):
    if type(value) is not bool:
        raise ReplayError(f"{name}: explicit JSON true/false required")
    return value


def _finite(value, name):
    if type(value) not in (int, float):
        raise ReplayError(f"{name}: finite numeric value required")
    try:
        valid = math.isfinite(value)
    except (OverflowError, ValueError):
        valid = False
    if not valid:
        raise ReplayError(f"{name}: finite numeric value required")
    return value


def _field(obj, key):
    if key not in obj:
        raise ReplayError(f"missing required field: {key}")
    return obj[key]


def _object(value, allowed, name):
    if type(value) is not dict:
        raise ReplayError(f"{name}: JSON object required")
    unknown = set(value) - allowed
    if unknown:
        raise ReplayError(f"{name}: unknown fields: {', '.join(sorted(unknown))}")


def validate_packet(packet):
    """Validate explicit evidence before defaults or sequence state are applied."""
    _object(packet, PACKET_FIELDS, "packet")
    if _field(packet, "format") != FORMAT:
        raise ReplayError(f"format must be {FORMAT}")
    _nonempty(_field(packet, "sourceId"), "sourceId")
    _uint(_field(packet, "sourceKind"), "sourceKind", 3)
    if not _boolean(_field(packet, "sequenceValid"), "sequenceValid"):
        raise ReplayError("sequenceValid must be true")
    _uint(_field(packet, "sequence"), "sequence")
    device_time = _boolean(_field(packet, "deviceTimeValid"), "deviceTimeValid")
    if device_time or "deviceTimeUs" in packet:
        _uint(_field(packet, "deviceTimeUs"), "deviceTimeUs", UINT64_MAX)
    channels = _field(packet, "channels")
    if type(channels) is not list or not 1 <= len(channels) <= 4:
        raise ReplayError("channels: 1..4 channel objects required; partial packets are allowed")
    seen = set()
    for channel in channels:
        _object(channel, CHANNEL_FIELDS, "channel")
        channel_id = _field(channel, "channelId")
        if type(channel_id) is not str or channel_id not in CHANNEL_IDS or channel_id in seen:
            raise ReplayError("channelId: unique CH1..CH4 required within each packet")
        seen.add(channel_id)
        _nonempty(_field(channel, "rawUnits"), "rawUnits")
        if not _boolean(_field(channel, "sampleSequenceValid"), "sampleSequenceValid"):
            raise ReplayError("sampleSequenceValid must be true")
        _uint(_field(channel, "sampleSequence"), "sampleSequence")
        sample_time = _boolean(_field(channel, "sampleTimeValid"), "sampleTimeValid")
        if sample_time or "sampleTimeUs" in channel:
            _uint(_field(channel, "sampleTimeUs"), "sampleTimeUs", UINT64_MAX)
        has_raw = _boolean(_field(channel, "hasRaw"), "hasRaw")
        if has_raw or "raw" in channel:
            _finite(_field(channel, "raw"), "raw")
        _boolean(_field(channel, "valid"), "valid")
        _boolean(_field(channel, "saturated"), "saturated")
        if "rawText" in channel and channel["rawText"] is not None and type(channel["rawText"]) is not str:
            raise ReplayError("rawText: string or null required")
    return packet


def _check_tree(value):
    pending = [(value, 0)]
    nodes = 0
    while pending:
        item, depth = pending.pop()
        nodes += 1
        if depth > MAX_DEPTH or nodes > MAX_NODES:
            raise ReplayError("JSON depth/node limit exceeded")
        if isinstance(item, str):
            try:
                item.encode("utf-8", errors="strict")
            except UnicodeError as error:
                raise ReplayError("invalid Unicode surrogate") from error
        elif type(item) is dict:
            for key, child in item.items():
                try:
                    key.encode("utf-8", errors="strict")
                except UnicodeError as error:
                    raise ReplayError("invalid Unicode object key") from error
                pending.append((child, depth + 1))
        elif type(item) is list:
            pending.extend((child, depth + 1) for child in item)
        elif type(item) in (int, float):
            _finite(item, "JSON number")


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ReplayError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_constant(value):
    raise ReplayError(f"nonfinite JSON constant: {value}")


def parse_packet(data):
    """UTF-8 datagram-sized JSON, with duplicate keys and nonfinite values rejected."""
    if isinstance(data, str):
        try:
            data = data.encode("utf-8", errors="strict")
        except UnicodeError as error:
            raise ReplayError("invalid UTF-8 text") from error
    if not data or len(data) > MAX_PACKET_BYTES:
        raise ReplayError(f"packet must be 1..{MAX_PACKET_BYTES} UTF-8 bytes")
    try:
        packet = json.loads(data.decode("utf-8", errors="strict"), object_pairs_hook=_unique_object,
                            parse_constant=_reject_constant)
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError) as error:
        if isinstance(error, ReplayError):
            raise
        raise ReplayError(f"invalid UTF-8 JSON: {error}") from error
    _check_tree(packet)
    return validate_packet(packet)


def packet_bytes(packet):
    validate_packet(packet)
    _check_tree(packet)
    try:
        data = json.dumps(packet, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8", errors="strict")
    except (ValueError, UnicodeError) as error:
        raise ReplayError(f"cannot encode finite UTF-8 packet: {error}") from error
    if len(data) > MAX_PACKET_BYTES:
        raise ReplayError(f"packet exceeds {MAX_PACKET_BYTES} UTF-8 bytes")
    return data


class _Order:
    def __init__(self):
        self.source_id = None
        self.packet_sequence = None
        self.channel_sequences = {}

    def accept(self, packet):
        source = packet["sourceId"]
        if self.source_id is not None and source != self.source_id:
            raise ReplayError("sourceId changed within input; no source override is applied")
        sequence = packet["sequence"]
        if self.packet_sequence is not None and not newer(sequence, self.packet_sequence):
            raise ReplayError("packet sequence is duplicate, reversed, or outside unsigned32 half-range")
        for channel in packet["channels"]:
            key, sample = channel["channelId"], channel["sampleSequence"]
            previous = self.channel_sequences.get(key)
            if previous is not None and not newer(sample, previous):
                raise ReplayError(f"{key}: sample sequence is duplicate, reversed, or outside unsigned32 half-range")
        self.source_id, self.packet_sequence = source, sequence
        for channel in packet["channels"]:
            self.channel_sequences[channel["channelId"]] = channel["sampleSequence"]


def _csv_uint(value, name, maximum=UINT32_MAX):
    token = value.strip()
    if not re.fullmatch(r"[0-9]+", token):
        raise ReplayError(f"{name}: unsigned decimal integer required")
    try:
        return _uint(int(token), name, maximum)
    except ValueError as error:
        if isinstance(error, ReplayError):
            raise
        raise ReplayError(f"{name}: invalid unsigned decimal integer") from error


def _csv_boolean(value, name):
    token = value.strip()
    if token not in {"true", "false", "1", "0"}:
        raise ReplayError(f"{name}: use exactly true/false/1/0")
    return token in {"true", "1"}


def _csv_raw(value, has_raw):
    token = value.strip()
    if not token:
        if has_raw:
            raise ReplayError("raw: empty is allowed only when has_raw is false")
        return None
    # Restrict CSV numbers to a decimal syntax also accepted by the JSON evidence parser.
    if not re.fullmatch(r"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", token):
        raise ReplayError("raw: finite decimal number required")
    return _finite(float(token), "raw")


def load_csv(path):
    """Group consecutive packet_sequence rows; never invent missing channels or raw values."""
    packets, rows, channel_count = [], 0, 0
    order = _Order()
    current = None

    def finish():
        if current is not None:
            packet_bytes(current)
            order.accept(current)
            packets.append(current)

    try:
        # BOM is accepted for Excel-exported CSV; all other decoding remains strict UTF-8.
        with Path(path).open("r", encoding="utf-8-sig", errors="strict", newline="") as stream:
            reader = csv.DictReader(stream, strict=True)
            header = reader.fieldnames
            if not header or len(header) != len(set(header)):
                raise ReplayError("CSV requires unique column names")
            missing, unknown = CSV_REQUIRED - set(header), set(header) - CSV_REQUIRED - CSV_OPTIONAL
            if missing or unknown:
                raise ReplayError(f"CSV columns: missing={sorted(missing)}, unknown={sorted(unknown)}")
            for row in reader:
                rows += 1
                if None in row or any(value is None for value in row.values()):
                    raise ReplayError(f"CSV line {reader.line_num}: missing or extra column value")
                try:
                    sequence = _csv_uint(row["packet_sequence"], "packet_sequence")
                    source_id = _nonempty(row["source_id"], "source_id")
                    device_token = row.get("device_time_us", "").strip()
                    device_valid = bool(device_token)
                    device_us = _csv_uint(device_token, "device_time_us", UINT64_MAX) if device_valid else None
                    if current is None or sequence != current["sequence"]:
                        finish()
                        current = {"format": FORMAT, "sourceId": source_id, "sourceKind": 2,
                                   "sequenceValid": True, "sequence": sequence,
                                   "deviceTimeValid": device_valid, "channels": []}
                        if device_valid:
                            current["deviceTimeUs"] = device_us
                    if source_id != current["sourceId"]:
                        raise ReplayError("source_id differs within one packet")
                    if device_valid != current["deviceTimeValid"] or device_us != current.get("deviceTimeUs"):
                        raise ReplayError("device_time_us differs within one packet")
                    has_raw = _csv_boolean(row["has_raw"], "has_raw") if "has_raw" in row else True
                    raw = _csv_raw(row["raw"], has_raw)
                    sample_token = row.get("sample_time_us", "").strip()
                    channel = {"channelId": row["channel_id"], "sampleSequenceValid": True,
                               "sampleSequence": _csv_uint(row["sample_sequence"], "sample_sequence"),
                               "sampleTimeValid": bool(sample_token), "rawUnits": row["raw_units"],
                               "hasRaw": has_raw, "valid": _csv_boolean(row["valid"], "valid"),
                               "saturated": _csv_boolean(row["saturated"], "saturated")}
                    if raw is not None:
                        channel["raw"] = raw
                    if sample_token:
                        channel["sampleTimeUs"] = _csv_uint(sample_token, "sample_time_us", UINT64_MAX)
                    if "raw_text" in row:
                        channel["rawText"] = row["raw_text"]
                    current["channels"].append(channel)
                    validate_packet(current)
                    channel_count += 1
                except ReplayError as error:
                    raise ReplayError(f"CSV line {reader.line_num}: {error}") from error
            finish()
    except (UnicodeError, csv.Error) as error:
        raise ReplayError(f"invalid UTF-8 CSV: {error}") from error
    if not packets:
        raise ReplayError("CSV contains no channel rows")
    return packets, Counts(rows, len(packets), channel_count, order.source_id)


def load_jsonl(path):
    packets, channels = [], 0
    order = _Order()
    with Path(path).open("rb") as stream:
        line_number = 0
        while True:
            line = stream.readline(MAX_PACKET_BYTES + 3)
            if not line:
                break
            line_number += 1
            if line.endswith(b"\n"):
                line = line[:-1]
                if line.endswith(b"\r"):
                    line = line[:-1]
            try:
                packet = parse_packet(line)
                order.accept(packet)
            except ReplayError as error:
                raise ReplayError(f"JSONL line {line_number}: {error}") from error
            packets.append(packet)
            channels += len(packet["channels"])
    if not packets:
        raise ReplayError("JSONL contains no packets")
    return packets, Counts(len(packets), len(packets), channels, order.source_id)


def convert_csv(csv_path, output_path):
    target = Path(output_path)
    if target.exists():
        raise ReplayError(f"output already exists, refusing overwrite: {target}")
    packets, counts = load_csv(csv_path)
    encoded = [packet_bytes(packet) for packet in packets]
    # Exclusive creation also protects against a file appearing during input validation.
    with target.open("xb") as stream:
        for data in encoded:
            stream.write(data + b"\n")
    return counts


def create_raw_profile(input_path, output_path, port):
    """Create a new explicit raw-only profile after validating the complete JSONL."""
    if type(port) is not int or not 1 <= port <= 65535:
        raise ReplayError("port must be an integer in 1..65535")
    target = Path(output_path)
    if target.exists():
        raise ReplayError(f"output already exists, refusing overwrite: {target}")
    packets, counts = load_jsonl(input_path)
    units = {}
    for packet in packets:
        for channel in packet["channels"]:
            key, raw_units = channel["channelId"], channel["rawUnits"]
            if key in units and units[key] != raw_units:
                raise ReplayError(f"{key}: rawUnits changed within input; no unit override is applied")
            units[key] = raw_units
    missing = CHANNEL_IDS - set(units)
    if missing:
        raise ReplayError("profile requires observed rawUnits for every channel: missing " + ", ".join(sorted(missing)))
    channels = []
    for key in sorted(CHANNEL_IDS):
        channels.append({
            "channelId": key, "mapping": "Unassigned", "rawUnits": units[key], "maximumAgeSeconds": 0.1,
            "rawRangeConfigured": False, "minimumRaw": 0, "maximumRaw": 0,
            "calibration": {"mode": 0, "measured": False, "sourceId": "", "version": "", "evidence": "",
                            "rawZero": 0, "scaleNPerRaw": 0, "points": [], "forceRangeConfigured": False,
                            "minimumForceN": 0, "maximumForceN": 0},
        })
    profile = {"schemaVersion": 1, "version": "pc-csv-raw-only-v1", "sourceId": counts.source_id,
               "enableLoopbackUdp": True, "loopbackPort": port, "channels": channels}
    encoded = (json.dumps(profile, ensure_ascii=False, allow_nan=False, indent=2) + "\n").encode("utf-8")
    with target.open("xb") as stream:
        stream.write(encoded)
    return counts


def send_jsonl(path, port, interval=0.02, host="127.0.0.1"):
    if host != "127.0.0.1":
        raise ReplayError("host must be exactly 127.0.0.1; external or device addresses are not supported")
    if type(port) is not int or not 1 <= port <= 65535:
        raise ReplayError("port must be an integer in 1..65535")
    if type(interval) not in (int, float) or not math.isfinite(interval) or interval <= 0:
        raise ReplayError("interval must be finite and greater than zero")
    packets, counts = load_jsonl(path)
    encoded = [packet_bytes(packet) for packet in packets]
    print(f"PC_REPLAY_CADENCE_ASSUMED interval={interval:g}s; firmware sample cadence unverified; "
          f"source_id={counts.source_id}; listener must already be explicitly configured", flush=True)
    # All input was checked before any socket exists. Send each packet once, without a burst
    # catch-up loop. UDP has no acknowledgement: the count is local send calls, not acceptance.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        for index, data in enumerate(encoded):
            if index:
                time.sleep(interval)
            sent = connection.sendto(data, (host, port))
            if sent != len(data):
                raise ReplayError("incomplete localhost UDP datagram send")
    return counts


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    convert = commands.add_parser("convert", help="validate long CSV and create a new normalized JSONL; no network")
    convert.add_argument("--csv", type=Path, required=True)
    convert.add_argument("--out", type=Path, required=True)
    validate = commands.add_parser("validate", help="validate normalized JSONL and sequence order; no network")
    validate.add_argument("--input", type=Path, required=True)
    profile = commands.add_parser("profile", help="create a new explicit raw-only PC listener profile; no network or calibration")
    profile.add_argument("--input", type=Path, required=True)
    profile.add_argument("--out", type=Path, required=True)
    profile.add_argument("--port", type=int, required=True)
    send = commands.add_parser("send", help="explicitly replay validated JSONL to an already enabled localhost listener")
    send.add_argument("--input", type=Path, required=True)
    send.add_argument("--port", type=int, required=True)
    send.add_argument("--host", default="127.0.0.1", help="only the literal 127.0.0.1 is allowed")
    send.add_argument("--interval", type=float, default=0.02,
                      help="assumed PC packet replay interval in seconds (default .02), not firmware sampling")
    args = parser.parse_args(argv)
    try:
        if args.command == "convert":
            counts = convert_csv(args.csv, args.out)
        elif args.command == "validate":
            _, counts = load_jsonl(args.input)
        elif args.command == "profile":
            counts = create_raw_profile(args.input, args.out, args.port)
        else:
            counts = send_jsonl(args.input, args.port, args.interval, args.host)
        verb = "locally_sent" if args.command == "send" else args.command
        print(f"{verb}: lines={counts.lines} packets={counts.packets} channels={counts.channels} source_id={counts.source_id}")
        return 0
    except (ReplayError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Interrupted; local UDP send count is incomplete.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
