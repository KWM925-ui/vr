"""Offline AD7190 register interpretation for the project's four-channel board.

Sources: SCH_Schematic1_2026-09-22.pdf, sheets 1-6; AD7190 Rev. C,
tables 16-21 and pp. 26-29, 33-35 (local copy in tmp/full_source_exhaustion_20261102).
This reads supplied numbers, never a device. Rates are calculated, not measured.
The AD719x family driver also defines bits that are RESERVED on this AD7190.
"""

import math


GAINS = {0: 1, 3: 8, 4: 16, 5: 32, 6: 64, 7: 128}
MODE_MASK = (7 << 21) | (1 << 20) | (3 << 18) | (1 << 15) | (1 << 13) | 0xFFF
CONFIG_MASK = (1 << 23) | (1 << 20) | 0xFF00 | 0xDF
MODES = (
    "continuous", "single_conversion", "idle", "power_down",
    "internal_zero_calibration", "internal_full_scale_calibration",
    "system_zero_calibration", "system_full_scale_calibration",
)
INPUTS = (
    "AIN1-AIN2", "AIN3-AIN4", "die_temperature", "AIN2-AIN2_short",
    "AIN1-AINCOM", "AIN2-AINCOM", "AIN3-AINCOM", "AIN4-AINCOM",
)


def uint(value, bits, name):
    if type(value) is not int or not 0 <= value < (1 << bits):
        raise ValueError(f"{name}: unsigned {bits}-bit integer required")
    return value


def positive(value, name):
    if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
        raise ValueError(f"{name}: finite positive number required")
    return float(value)


def input_ratio(raw, gain_code, unipolar=False):
    """Ideal differential input / ADC reference; bipolar coding is offset binary.

    The offset/full-scale calibration registers already affect DATA. Applying
    their values again to the data would double-calibrate the observation.
    """
    uint(raw, 24, "data")
    if type(gain_code) is not int or gain_code not in GAINS:
        raise ValueError("AD7190 gain codes 1 and 2 are reserved")
    if type(unipolar) is not bool:
        raise ValueError("unipolar must be a boolean")
    return (raw / (1 << 24) if unipolar else raw / (1 << 23) - 1) / GAINS[gain_code]


def inspect_registers(mode, config, adc, *, clock_hz=None, data=None,
                      status=None, reference_v=None, device_id=None, gpocon=None):
    """Interpret a supplied register snapshot; no firmware protocol is assumed.

    DATA and STATUS, if both supplied, must describe the same conversion. This
    routine cannot establish their time, freshness, or physical provenance.
    Board CH1..CH4 are connector names, not AD7190 channel-enable bit numbers.
    """
    uint(mode, 24, "mode")
    uint(config, 24, "config")
    if mode & ~MODE_MASK:
        raise ValueError("reserved AD7190 mode bits are set (including family AVG/CLKDIV bits)")
    if config & ~CONFIG_MASK:
        raise ValueError("reserved AD7190 configuration bits are set (including family PSEUDO bits)")
    if adc not in ("U7", "U8"):
        raise ValueError("adc must identify board component U7 or U8")
    gain_code = config & 7
    if gain_code not in GAINS:
        raise ValueError("AD7190 gain codes 1 and 2 are reserved")
    fs = mode & 0x3FF
    if fs == 0:
        raise ValueError("FS must be in 1..1023")
    enabled = [i for i in range(8) if config & (1 << (8 + i))]
    if not enabled:
        raise ValueError("no ADC channel enabled")
    if device_id is not None:
        uint(device_id, 8, "id")
        if device_id & 15 != 4:
            raise ValueError("ID low nibble does not identify AD7190")
    if gpocon is not None:
        uint(gpocon, 8, "gpocon")
        if gpocon & 0x80:
            raise ValueError("reserved GPOCON bit 7 is set")
    if reference_v is not None:
        reference_v = positive(reference_v, "reference voltage")

    clock_code = (mode >> 18) & 3
    if clock_hz is None:
        clock_hz = 4.92e6 if clock_code >= 2 else None
        clock_basis = "datasheet_nominal_internal" if clock_code >= 2 else "external_frequency_not_supplied"
    else:
        clock_hz = positive(clock_hz, "clock frequency")
        clock_basis = "caller_supplied_not_verified"
    chop = bool(config & (1 << 23))
    order = 3 if mode & (1 << 15) else 4
    zero_latency = bool(mode & (1 << 11))
    mode_code = (mode >> 21) & 7
    reference = 2 if config & (1 << 20) else 1
    board_channels = {0: "CH1", 1: "CH2"} if adc == "U7" else {0: "CH3", 1: "CH4"}
    diagnostics = []
    if len(enabled) > 1 and not mode & (1 << 20):
        diagnostics.append("DAT_STA disabled: read conversion identity without assuming alternating USB rows")
    if reference == 2:
        diagnostics.append("REFIN2 is unconnected on this schematic; board excitation ratio unavailable")
    if config & 0x80 and (not config & 0x10 or chop):
        diagnostics.append("burnout currents require buffered input and chop disabled")
    if config & 0x80:
        diagnostics.append("burnout currents are a sensor diagnostic, not normal force acquisition")
    if any(i >= 4 for i in enabled):
        diagnostics.append("AINCOM is unconnected on this schematic; pseudo-differential inputs are not board load channels")
    if gpocon is not None and gpocon & 0x40:
        diagnostics.append("BPDSW is unconnected: this bit cannot turn off this board's bridge excitation")

    timing = None
    if clock_hz is not None:
        base_rate = clock_hz / (1024 * fs)
        continuous_rate = base_rate / order if chop else base_rate
        settling = 2 / continuous_rate if chop else order / base_rate
        settled_each_time = zero_latency or len(enabled) > 1 or mode_code == 1
        output_rate = 1 / settling if settled_each_time else continuous_rate
        timing = {
            "clockHz": clock_hz, "clockBasis": clock_basis,
            "filterFirstNotchHz": base_rate,
            "singleChannelContinuousHz": continuous_rate,
            "filterSettlingSeconds": settling,
            "continuousScanConversionsPerSecond": output_rate if mode_code == 0 else None,
            "continuousScanPerEnabledChannelHz": output_rate / len(enabled) if mode_code == 0 else None,
            "scanSeconds": settling * len(enabled) if len(enabled) > 1 else None,
            "adjacentChannelSkewSeconds": settling if len(enabled) > 1 else None,
            "singleConversionClockStartupMaxSeconds": .001 if mode_code == 1 and clock_code >= 2 else None,
            "measured": False,
        }

    sample = None
    if status is not None:
        uint(status, 8, "status")
        if status & 8:
            raise ValueError("reserved AD7190 status bit 3 is set")
    if data is not None:
        uint(data, 24, "data")
        if status is None and len(enabled) != 1:
            raise ValueError("multiple channels enabled: same-conversion STATUS required to interpret DATA")
        channel = (status & 7) if status is not None else enabled[0]
        if channel not in enabled:
            raise ValueError("DATA channel in STATUS is not enabled by supplied configuration")
        faults = []
        if status is not None and status & 0x40:
            faults.append("ADC_ERR")
        if status is not None and status & 0x20 and config & 0x40:
            faults.append("NO_REFERENCE")
        parity_ok = None
        if mode & (1 << 13):
            if status is None:
                faults.append("PARITY_STATUS_MISSING")
            else:
                parity_ok = (data.bit_count() & 1) == ((status >> 4) & 1)
                if not parity_ok:
                    faults.append("DATA_PARITY_MISMATCH")
        if mode_code not in (0, 1):
            faults.append("NOT_IN_CONVERSION_MODE")
        sample = {
            "rawCode": data, "adcInput": INPUTS[channel], "statusChannel": channel,
            "boardConnector": board_channels.get(channel),
            "identityBasis": "supplied_same_conversion_status" if status is not None else "sole_enabled_channel",
            "readyBitLow": None if status is None else not bool(status & 0x80),
            "freshnessVerified": False, "parityMatches": parity_ok,
            "atCodeRail": data in (0, 0xFFFFFF), "faults": faults,
            "inputOverReference": None, "inputOverExcitation": None,
            "inputVoltageV": None, "uncalibratedDieTemperatureC": None, "forceN": None,
        }
        if not faults:
            if channel == 2:
                if gain_code == 0 and not config & 8:
                    # Approximate sensitivity only: this is die temperature, not water temperature.
                    sample["uncalibratedDieTemperatureC"] = (data - 0x800000) / 2815 - 273.15
                else:
                    sample["faults"].append("TEMPERATURE_REQUIRES_BIPOLAR_GAIN_1")
            else:
                ratio = input_ratio(data, gain_code, bool(config & 8))
                sample["inputOverReference"] = ratio
                if reference == 1 and channel in board_channels:
                    sample["inputOverExcitation"] = ratio
                if reference_v is not None:
                    sample["inputVoltageV"] = ratio * reference_v

    # Differential-mode: R*(2*Cdiff+Ccommon); common-mode: R*Ccommon.
    # These are unloaded, ideal-RC estimates. Sensor source resistance, ADC
    # loading, tolerances, digital filtering and transport are not included.
    r, cd, cc = 200., 100e-9, 10e-9
    return {
        "scope": "OFFLINE_REGISTER_CALCULATION_NOT_ACQUISITION",
        "adc": adc, "spiChipSelectGpio": 10 if adc == "U7" else 9,
        "registers": {"mode": mode, "config": config, "status": status, "id": device_id, "gpocon": gpocon},
        "mode": MODES[mode_code], "gainCode": gain_code, "gain": GAINS[gain_code],
        "unipolar": bool(config & 8), "buffered": bool(config & 16),
        "referenceInput": reference, "referenceDetectEnabled": bool(config & 64),
        "filterOrder": order, "chop": chop, "zeroLatency": zero_latency,
        "rej60": bool(mode & 1024), "fs": fs,
        "enabledInputs": [{"statusCode": i, "input": INPUTS[i], "boardConnector": board_channels.get(i)} for i in enabled],
        "timing": timing, "sample": sample,
        "boardInputRcEstimate": {"seriesOhmsPerLeg": r, "differentialCapacitanceF": cd,
            "commonCapacitanceFPerLeg": cc, "differentialPoleHz": 1 / (2 * math.pi * r * (2 * cd + cc)),
            "commonModePoleHz": 1 / (2 * math.pi * r * cc), "differentialTimeConstantSeconds": r * (2 * cd + cc)},
        "crossAdcSimultaneityVerified": False,
        "diagnostics": diagnostics,
    }
