"""Offline codec and capture checks for the paddle hardware handoff contract.

No serial port, network socket or motor is opened. This is the new hardware-side
contract, not the existing DBCL simulation protocol or a finished Unity backend.
See hardware/paddle/PROTOCOL.md for the firmware and PC implementation boundary.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import zlib

from ad7190 import inspect_registers


MAGIC = b"DBPF"
VERSION = 1
HEADER = struct.Struct("<4sHHIIQ")
CRC = struct.Struct("<I")
MAX_PAYLOAD = 16384
TELEMETRY, COMMAND = 1, 2
CHANNELS = ("CH1", "CH2", "CH3", "CH4")
MOTORS = ("M1", "M2", "M3")
STATES = ("BOOT", "DISARMED", "ARMED", "FAULT", "ESTOP")


class ContractError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ContractError(message)


def unsigned(value, bits, name):
    require(type(value) is int and 0 <= value < 1 << bits, name + ": unsigned integer required")
    return value


def number(value, name):
    require(type(value) in (int, float), name + ": finite number required")
    try:
        finite = math.isfinite(value)
    except OverflowError:
        finite = False
    require(finite, name + ": finite number required")
    return value


def vector(value, count, name, *, nonnegative=False):
    require(type(value) is list and len(value) == count, name + ": wrong vector length")
    for item in value:
        number(item, name)
        require(not nonnegative or item >= 0, name + ": negative value")


def exact_fields(value, fields, name):
    require(type(value) is dict and set(value) == set(fields.split()), name + ": missing or unknown fields")


def identifier(value, name):
    require(type(value) is str and re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", value) is not None,
            name + ": use 1..64 ASCII letters, digits, dot, underscore or hyphen")


def digest(value, name, *, nullable=False):
    require((nullable and value is None) or (type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) is not None),
            name + ": SHA-256 hex required")


def boolean(value, name):
    require(type(value) is bool, name + ": explicit boolean required")


def sample_header(sample, now):
    boolean(sample["valid"], "sample.valid")
    sequence, timestamp = sample["sampleSequence"], sample["sampleTimeUs"]
    require((sequence is None) == (timestamp is None), "sample identity and timestamp must both be present or absent")
    if sequence is not None:
        unsigned(sequence, 32, "sampleSequence")
        unsigned(timestamp, 64, "sampleTimeUs")
        require(timestamp <= now, "sample timestamp is ahead of device time")
    require(not sample["valid"] or sequence is not None, "valid sample needs its original acquisition identity")


def validate_telemetry(body):
    exact_fields(body, "deviceId deviceTimeUs simulated state faults configurationId appliedHostSessionId "
                 "appliedCommandSequence motorEnabled targetTensionN adcs forces encoders imu", "telemetry")
    identifier(body["deviceId"], "deviceId")
    now = unsigned(body["deviceTimeUs"], 64, "deviceTimeUs")
    boolean(body["simulated"], "simulated")
    require(body["state"] in STATES, "unknown device state")
    require(type(body["faults"]) is list and len(body["faults"]) <= 16, "faults must be a bounded array")
    for fault in body["faults"]:
        identifier(fault, "fault")
    digest(body["configurationId"], "configurationId", nullable=True)
    if body["appliedHostSessionId"] is not None:
        require(type(body["appliedHostSessionId"]) is str and re.fullmatch(r"[0-9a-f]{16}", body["appliedHostSessionId"]),
                "appliedHostSessionId must be 16 lowercase hex digits")
    if body["appliedCommandSequence"] is not None:
        unsigned(body["appliedCommandSequence"], 32, "appliedCommandSequence")
    require((body["appliedHostSessionId"] is None) == (body["appliedCommandSequence"] is None), "incomplete command acknowledgement")
    require(type(body["motorEnabled"]) is list and len(body["motorEnabled"]) == 3, "three motor enable flags required")
    for flag in body["motorEnabled"]:
        boolean(flag, "motorEnabled")
    vector(body["targetTensionN"], 3, "targetTensionN", nonnegative=True)
    if body["state"] != "ARMED":
        require(not any(body["motorEnabled"]) and not any(body["targetTensionN"]), "non-armed state must revoke motor targets and enables")
    if body["state"] == "ARMED":
        require(body["configurationId"] is not None and not body["faults"], "armed telemetry needs configured, fault-free state")

    require(type(body["adcs"]) is list and len(body["adcs"]) == 2, "U7 and U8 register snapshots required")
    registers = {}
    for adc in body["adcs"]:
        exact_fields(adc, "adcId mode config", "ADC snapshot")
        require(adc["adcId"] in ("U7", "U8") and adc["adcId"] not in registers, "ADC identity duplicate or unknown")
        require((adc["mode"] is None) == (adc["config"] is None), "incomplete ADC register snapshot")
        if adc["mode"] is not None:
            unsigned(adc["mode"], 24, "ADC mode")
            unsigned(adc["config"], 24, "ADC config")
        registers[adc["adcId"]] = adc
    require(type(body["forces"]) is list and len(body["forces"]) == 4, "all four board channels must be explicit")
    require(all(type(s) is dict for s in body["forces"]), "force samples must be objects")
    require(tuple(s.get("channelId") for s in body["forces"]) == CHANNELS, "force channel order must be CH1..CH4")
    for index, sample in enumerate(body["forces"]):
        exact_fields(sample, "channelId sampleSequence sampleTimeUs valid saturated rawCode status forceN calibrationId", "force sample")
        sample_header(sample, now)
        boolean(sample["saturated"], "saturated")
        digest(sample["calibrationId"], "calibrationId", nullable=True)
        if sample["rawCode"] is not None:
            unsigned(sample["rawCode"], 24, "rawCode")
        if sample["status"] is not None:
            unsigned(sample["status"], 8, "status")
        if sample["forceN"] is not None:
            number(sample["forceN"], "forceN")
            require(sample["valid"] and not sample["saturated"] and sample["calibrationId"] is not None,
                    "forceN requires valid unsaturated data and an identified calibration")
        if sample["valid"]:
            require(sample["rawCode"] is not None and sample["status"] is not None and not sample["saturated"],
                    "valid ADC sample needs raw DATA/STATUS without saturation")
            adc = registers["U7" if index < 2 else "U8"]
            require(adc["mode"] is not None, "valid ADC sample needs actual mode/config readback")
            try:
                result = inspect_registers(adc["mode"], adc["config"], adc["adcId"],
                                           data=sample["rawCode"], status=sample["status"])["sample"]
            except ValueError as error:
                raise ContractError(str(error)) from error
            require(not result["faults"] and not result["atCodeRail"], "ADC error or rail may not be marked valid")
            require(result["boardConnector"] == sample["channelId"], "ADC STATUS does not identify the declared board connector")

    require(type(body["encoders"]) is list and len(body["encoders"]) == 3, "three drive encoder slots required")
    require(all(type(s) is dict for s in body["encoders"]), "encoder samples must be objects")
    require(tuple(s.get("channelId") for s in body["encoders"]) == MOTORS, "encoder channel order must be M1..M3")
    for sample in body["encoders"]:
        exact_fields(sample, "channelId sampleSequence sampleTimeUs valid rawCount payoutM calibrationId driveCurrentA", "encoder sample")
        sample_header(sample, now)
        digest(sample["calibrationId"], "calibrationId", nullable=True)
        if sample["rawCount"] is not None:
            # Decimal text preserves int64 across C JSON libraries and JavaScript.
            require(type(sample["rawCount"]) is str and re.fullmatch(r"-?(0|[1-9][0-9]{0,18})", sample["rawCount"]),
                    "rawCount must be canonical signed int64 decimal text")
            require(-(1 << 63) <= int(sample["rawCount"]) < 1 << 63, "rawCount outside int64")
            require(str(int(sample["rawCount"])) == sample["rawCount"], "rawCount is not canonical decimal text")
        require(not sample["valid"] or sample["rawCount"] is not None, "valid encoder needs a real raw count")
        for name in ("payoutM", "driveCurrentA"):
            if sample[name] is not None:
                number(sample[name], name)
                require(sample["valid"], name + " requires a valid encoder/drive observation")
        require(sample["payoutM"] is None or sample["calibrationId"] is not None, "payoutM requires identified length calibration")

    imu = body["imu"]
    exact_fields(imu, "sampleSequence sampleTimeUs valid quaternionXyzw gyroRadS accelerometerMps2 frameId", "IMU sample")
    sample_header(imu, now)
    identifier(imu["frameId"], "IMU frameId")
    for name, count in (("quaternionXyzw", 4), ("gyroRadS", 3), ("accelerometerMps2", 3)):
        if imu[name] is not None:
            vector(imu[name], count, name)
    if imu["valid"]:
        require(imu["quaternionXyzw"] is not None, "valid attitude needs a quaternion")
        require(abs(math.hypot(*imu["quaternionXyzw"]) - 1) <= 1e-3, "IMU quaternion is not normalized")
    return body


def validate_command(body):
    exact_fields(body, "deviceId hostSessionId action configurationId sourceSequence sourceDeviceTimeUs ttlUs tensionsN", "command")
    identifier(body["deviceId"], "deviceId")
    require(type(body["hostSessionId"]) is str and re.fullmatch(r"[0-9a-f]{16}", body["hostSessionId"]), "16 lowercase hex host session required")
    require(body["action"] in ("stop", "arm", "tension"), "unknown command action")
    digest(body["configurationId"], "configurationId", nullable=True)
    vector(body["tensionsN"], 3, "tensionsN", nonnegative=True)
    ttl = unsigned(body["ttlUs"], 32, "ttlUs")
    if body["action"] == "stop":
        require(ttl == 0 and not any(body["tensionsN"]) and body["sourceSequence"] is None
                and body["sourceDeviceTimeUs"] is None, "stop must contain zero targets/TTL and no source requirement")
    else:
        require(0 < ttl <= 100000 and body["configurationId"] is not None, "enabled commands need a configuration and TTL <= 100 ms")
        unsigned(body["sourceSequence"], 32, "sourceSequence")
        unsigned(body["sourceDeviceTimeUs"], 64, "sourceDeviceTimeUs")
        require(body["action"] != "arm" or not any(body["tensionsN"]), "arming cannot apply pretension")
    return body


def validate_body(kind, body):
    require(type(kind) is int and kind in (TELEMETRY, COMMAND), "unknown message kind")
    return validate_telemetry(body) if kind == TELEMETRY else validate_command(body)


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON field: " + key)
        result[key] = value
    return result


def encode(kind, sequence, boot_id, body):
    unsigned(sequence, 32, "sequence")
    unsigned(boot_id, 64, "bootId")
    require(boot_id != 0, "bootId must be a fresh nonzero boot identity")
    validate_body(kind, body)
    payload = json.dumps(body, separators=(",", ":"), ensure_ascii=True, allow_nan=False).encode("utf-8")
    require(len(payload) <= MAX_PAYLOAD, "payload too large")
    data = HEADER.pack(MAGIC, VERSION, kind, len(payload), sequence, boot_id) + payload
    return data + CRC.pack(zlib.crc32(data) & 0xFFFFFFFF)


def decode(data):
    require(len(data) >= HEADER.size + CRC.size, "short frame")
    magic, version, kind, size, sequence, boot_id = HEADER.unpack_from(data)
    require(magic == MAGIC and version == VERSION, "bad magic/version")
    require(0 < size <= MAX_PAYLOAD and len(data) == HEADER.size + size + CRC.size, "frame length mismatch")
    require(boot_id != 0, "bootId must not be zero")
    require(CRC.unpack_from(data, len(data) - 4)[0] == zlib.crc32(data[:-4]) & 0xFFFFFFFF, "CRC32 mismatch")
    try:
        body = json.loads(data[HEADER.size:-4].decode("utf-8"), object_pairs_hook=_pairs,
                          parse_constant=lambda value: (_ for _ in ()).throw(ContractError("nonfinite JSON number")))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ContractError("invalid UTF-8 JSON") from error
    validate_body(kind, body)
    return dict(kind=kind, sequence=sequence, boot_id=boot_id, body=body)


def read_frames(path):
    """Strict capture reader; firmware live parsers must additionally resync."""
    with path.open("rb") as stream:
        while True:
            head = stream.read(HEADER.size)
            if not head:
                break
            require(len(head) == HEADER.size, "truncated frame header")
            magic, version, kind, size, sequence, boot_id = HEADER.unpack(head)
            require(magic == MAGIC and version == VERSION and 0 < size <= MAX_PAYLOAD, "invalid capture header")
            body = stream.read(size + CRC.size)
            require(len(body) == size + CRC.size, "truncated frame payload")
            yield decode(head + body)


def write_decoded_capture(capture, output):
    """Write UTF-8 directly; Windows PowerShell redirection may emit UTF-16."""
    count = 0
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        for frame in read_frames(capture):
            stream.write(json.dumps(frame, ensure_ascii=False, allow_nan=False) + "\n")
            count += 1
    require(count > 0, "empty capture")
    return count


def newer(current, previous):
    return 0 < ((current - previous) & 0xFFFFFFFF) < 1 << 31


def check_capture(path, *, allow_simulated=False):
    packet_state, sample_state, devices = {}, {}, set()
    frames = telemetry = commands = 0
    for message in read_frames(path):
        frames += 1
        body, boot = message["body"], message["boot_id"]
        devices.add(body["deviceId"])
        stream_key = (body["deviceId"], boot, message["kind"], body.get("hostSessionId"))
        previous = packet_state.get(stream_key)
        if previous is not None:
            require(newer(message["sequence"], previous[0]), "duplicate or reordered frame sequence")
        if message["kind"] == COMMAND:
            commands += 1
            packet_state[stream_key] = (message["sequence"], None)
            continue
        telemetry += 1
        require(allow_simulated or not body["simulated"], "simulated capture requires explicit --allow-simulated")
        now = body["deviceTimeUs"]
        require(previous is None or now > previous[1], "device clock did not advance; reboot needs a new bootId")
        packet_state[stream_key] = (message["sequence"], now)
        for name, sample in [(s["channelId"], s) for s in body["forces"] + body["encoders"]] + [("IMU", body["imu"])]:
            key = (body["deviceId"], boot, name)
            if sample["sampleSequence"] is None:
                require(key not in sample_state, "sample identity disappeared within a boot: " + name)
                continue
            old = sample_state.get(key)
            if old:
                if sample["sampleSequence"] == old["sampleSequence"]:
                    held = dict(sample)
                    # A sensor timeout may invalidate the last acquisition without
                    # inventing another sample. Only validity and derived usable
                    # values may be revoked; raw data/time/calibration stay fixed.
                    if old["valid"] and not sample["valid"]:
                        held["valid"] = True
                        for field in ("forceN", "payoutM", "driveCurrentA"):
                            if field in held and held[field] is None:
                                held[field] = old[field]
                    require(held == old, "held sample was rewritten, revived or assigned a fresh timestamp: " + name)
                else:
                    require(newer(sample["sampleSequence"], old["sampleSequence"]) and sample["sampleTimeUs"] > old["sampleTimeUs"],
                            "sensor sequence/time moved backwards: " + name)
            sample_state[key] = sample
    require(frames > 0, "empty capture")
    return dict(result="CAPTURE_STRUCTURE_AND_IDENTITIES_PASS", frames=frames, telemetry=telemetry,
                commands=commands, devices=sorted(devices), hardware_acceptance=False)


def example_telemetry(sequence=1):
    """Synthetic raw-only ADC fixture. It deliberately cannot authorize motors."""
    now = sequence * 10000
    return dict(deviceId="paddle-example", deviceTimeUs=now, simulated=True, state="DISARMED", faults=[],
                configurationId=None, appliedHostSessionId=None, appliedCommandSequence=None,
                motorEnabled=[False] * 3, targetTensionN=[0., 0., 0.],
                adcs=[dict(adcId=name, mode=0x180004, config=0x000357) for name in ("U7", "U8")],
                forces=[dict(channelId=name, sampleSequence=sequence, sampleTimeUs=now - 1000,
                             valid=True, saturated=False, rawCode=0x800000 + sequence, status=i % 2,
                             forceN=None, calibrationId=None) for i, name in enumerate(CHANNELS)],
                encoders=[dict(channelId=name, sampleSequence=None, sampleTimeUs=None, valid=False,
                               rawCount=None, payoutM=None, calibrationId=None, driveCurrentA=None) for name in MOTORS],
                imu=dict(sampleSequence=None, sampleTimeUs=None, valid=False, quaternionXyzw=None,
                         gyroRadS=None, accelerometerMps2=None, frameId="imu_native_unregistered"))


def example_stop():
    return dict(deviceId="paddle-example", hostSessionId="0102030405060708", action="stop",
                configurationId=None, sourceSequence=None, sourceDeviceTimeUs=None, ttlUs=0, tensionsN=[0., 0., 0.])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    check = sub.add_parser("check", help="check a binary capture without opening hardware")
    check.add_argument("capture", type=Path)
    check.add_argument("--allow-simulated", action="store_true")
    inspect = sub.add_parser("inspect", help="decode a capture to JSON lines")
    inspect.add_argument("capture", type=Path)
    inspect.add_argument("--output", type=Path, help="create a UTF-8 JSONL file without overwriting an existing file")
    example = sub.add_parser("examples", help="write a new directory of synthetic byte fixtures")
    example.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.operation == "check":
            print(json.dumps(check_capture(args.capture, allow_simulated=args.allow_simulated), ensure_ascii=False))
        elif args.operation == "inspect":
            if args.output is not None:
                count = write_decoded_capture(args.capture, args.output)
                print(json.dumps(dict(result="DECODED", frames=count, output=str(args.output))))
            else:
                for frame in read_frames(args.capture):
                    print(json.dumps(frame, ensure_ascii=False, allow_nan=False))
        else:
            args.output.mkdir(parents=True, exist_ok=False)
            vectors = []
            for kind, name, body in [(TELEMETRY, "telemetry", example_telemetry()), (COMMAND, "stop", example_stop())]:
                data = encode(kind, 1, 0x1020304050607080, body)
                (args.output / (name + ".bin")).write_bytes(data)
                (args.output / (name + ".json")).write_text(json.dumps(body, indent=2) + "\n", encoding="utf-8")
                vectors.append(dict(name=name, kind=kind, sequence=1, boot_id_hex="1020304050607080",
                                    bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), frame_hex=data.hex()))
            (args.output / "golden_vectors.json").write_text(json.dumps(vectors, indent=2) + "\n", encoding="utf-8")
            print(json.dumps(dict(result="SYNTHETIC_EXAMPLES_WRITTEN", output=str(args.output))))
    except (ContractError, OSError, ValueError) as error:
        parser.exit(2, str(error) + "\n")


if __name__ == "__main__":
    main()
