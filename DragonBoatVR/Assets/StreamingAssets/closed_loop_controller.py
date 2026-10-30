"""Uncalibrated mechanical/controller fixture, isolated behind localhost UDP.

The 200 Hz plant runs on its own monotonic clock. Cable force is generated only
by simulated motor tensions; requested hydrodynamic force is diagnostic input.
The CAD fixture additionally models ideal mechanical stops before sampling its
encoders. This model does not establish real hardware stability, performance or
6-DOF capability. Geometry, motor, hand impedance, stops and sensors are assumed.
"""

import argparse
import base64
from collections import OrderedDict
import json
import math
import os
import select
import socket
import struct
import time
import zlib

HEADER = struct.Struct("<4sIIQI")
MAGIC = b"DBCL"
VERSION = 1
MAX_PACKET = 65507
DT = 0.005
WATCHDOG_SECONDS = 0.1
POSITION_SCALE = 10000
ANGLE_SCALE = 100
PROVENANCE = "ASSUMED_SOFTWARE_MECHANICS_AND_PROTOCOL: no measured hardware validation"


def add(a, b):
    return [a[i] + b[i] for i in range(3)]


def sub(a, b):
    return [a[i] - b[i] for i in range(3)]


def scale(a, s):
    return [x * s for x in a]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def norm(a):
    return math.sqrt(dot(a, a))


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def clamp(x, low, high):
    return max(low, min(high, x))


def bounded_vector(value, maximum):
    size = norm(value)
    return scale(value, maximum / size) if size > maximum else value


def q_normalize(q):
    length = norm(q)
    if length < 1e-12 or not math.isfinite(length):
        raise ValueError("invalid quaternion")
    return [x / length for x in q]


def q_multiply(a, b):
    av, bv = a[:3], b[:3]
    vector = add(add(scale(bv, a[3]), scale(av, b[3])), cross(av, bv))
    return vector + [a[3] * b[3] - dot(av, bv)]


def q_inverse(q):
    return [-q[0], -q[1], -q[2], q[3]]


def q_rotate(q, vector):
    uv = cross(q[:3], vector)
    return add(vector, add(scale(uv, 2 * q[3]), scale(cross(q[:3], uv), 2)))


def q_from_vector(rotation):
    angle = norm(rotation)
    if angle < 1e-12:
        return [0.0, 0.0, 0.0, 1.0]
    return scale(rotation, math.sin(angle * 0.5) / angle) + [math.cos(angle * 0.5)]


def q_to_vector(q):
    q = q_normalize(q)
    if q[3] < 0:
        q = [-x for x in q]
    sine = norm(q[:3])
    if sine < 1e-12:
        return [0.0, 0.0, 0.0]
    return scale(q[:3], 2 * math.atan2(sine, q[3]) / sine)


def q_from_unity_euler(euler):
    x, y, z = [math.radians(v) for v in euler]
    return q_normalize(q_multiply(q_multiply(q_from_vector([0, y, 0]), q_from_vector([x, 0, 0])),
                                  q_from_vector([0, 0, z])))


def q_to_unity_euler(q):
    # Unity applies Euler rotations around Z, then X, then Y.
    ex, ey, ez = [q_rotate(q, axis) for axis in ([1, 0, 0], [0, 1, 0], [0, 0, 1])]
    x = math.asin(clamp(-ez[1], -1.0, 1.0))
    if abs(math.cos(x)) > 1e-7:
        y, z = math.atan2(ez[0], ez[2]), math.atan2(ex[1], ey[1])
    else:
        y, z = math.atan2(-ex[2], ex[0]), 0.0
    return [math.degrees(a) for a in (x, y, z)]


def _reject_constant(value):
    raise ValueError("nonfinite JSON number: " + value)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field")
        result[key] = value
    return result


def _finite_tree(value, depth=0):
    if depth > 12:
        raise ValueError("JSON nesting exceeds protocol limit")
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("nonfinite JSON number")
    if isinstance(value, list):
        for item in value:
            _finite_tree(item, depth + 1)
    if isinstance(value, dict):
        for item in value.values():
            _finite_tree(item, depth + 1)


def encode_packet(payload, sequence, session):
    body = json.dumps(payload, separators=(",", ":"), allow_nan=False).encode("utf-8")
    header = HEADER.pack(MAGIC, VERSION, sequence, session, len(body))
    packet = header + body
    if len(packet) + 4 > MAX_PACKET:
        raise ValueError("UDP payload too large")
    return packet + struct.pack("<I", zlib.crc32(packet))


def decode_packet(packet, session):
    if len(packet) < HEADER.size + 6 or len(packet) > MAX_PACKET:
        raise ValueError("invalid packet size")
    magic, version, sequence, incoming_session, size = HEADER.unpack_from(packet)
    if magic != MAGIC or version != VERSION or incoming_session != session:
        raise ValueError("wrong protocol or session")
    if size != len(packet) - HEADER.size - 4:
        raise ValueError("wrong JSON length")
    if struct.unpack_from("<I", packet, len(packet) - 4)[0] != zlib.crc32(packet[:-4]):
        raise ValueError("bad CRC")
    payload = json.loads(packet[HEADER.size:-4].decode("utf-8"), parse_constant=_reject_constant,
                         object_pairs_hook=_unique_object)
    if not isinstance(payload, dict):
        raise ValueError("expected JSON object")
    _finite_tree(payload)
    return sequence, payload


def newer(sequence, previous):
    delta = (sequence - previous) & 0xFFFFFFFF
    return 0 < delta < 0x80000000


def number(value, name, low, high):
    if type(value) not in (int, float):
        raise ValueError(name + " must be numeric")
    value = float(value)
    if not math.isfinite(value) or not low <= value <= high:
        raise ValueError(name + " outside model bounds")
    return value


def vector(payload, name, count, low=-1000.0, high=1000.0):
    values = payload.get(name)
    if not isinstance(values, list) or len(values) != count:
        raise ValueError(name + " has wrong array size")
    return [number(x, name, low, high) for x in values]


def validate_step(payload):
    if payload.get("kind") != "step":
        raise ValueError("not a step command")
    sequence = payload.get("commandSequence")
    if type(sequence) is not int or not 0 <= sequence <= 0xFFFFFFFF:
        raise ValueError("invalid commandSequence")
    result = {"kind": "step", "commandSequence": sequence,
              "simulationTime": number(payload.get("simulationTime"), "simulationTime", 0, 1e10)}
    for name, maximum in (("sourceFeedbackSequence", 0xFFFFFFFF), ("sourceSampleTick", 0x7FFFFFFFFFFFFFFF)):
        value = payload.get(name)
        if type(value) is not int or not 0 <= value <= maximum:
            raise ValueError("invalid " + name)
        result[name] = value
    for name in ("active", "cableValid", "platformValid"):
        if type(payload.get(name)) is not bool:
            raise ValueError(name + " must be boolean")
        result[name] = payload[name]
    for name in ("desiredGripPosition", "desiredGripVelocity", "requestForce", "requestTorque",
                 "platformTargetPosition", "platformTargetEuler"):
        result[name] = vector(payload, name, 3)
    # Additive field: old diagnostic packets describe a stationary angular
    # reference. A present field still receives the full finite-vector validation.
    result["desiredGripAngularVelocity"] = (vector(payload, "desiredGripAngularVelocity", 3)
        if "desiredGripAngularVelocity" in payload else [0.0] * 3)
    result["desiredGripRotation"] = q_normalize(vector(payload, "desiredGripRotation", 4, -2, 2))
    targets = payload.get("tensionTargets")
    if not isinstance(targets, list) or not 1 <= len(targets) <= 64:
        raise ValueError("expected 1 to 64 motor targets")
    count = len(targets)
    result["tensionTargets"] = vector(payload, "tensionTargets", count, 0, 100000)
    result["maxTensions"] = vector(payload, "maxTensions", count, 0, 10000)
    drag = payload.get("dragFeedbackEnabled", False)
    if type(drag) is not bool:
        raise ValueError("dragFeedbackEnabled must be boolean")
    result["dragFeedbackEnabled"] = drag
    if drag and (not result["active"] or not result["cableValid"]):
        raise ValueError("drag rendering requires an active valid cable command")
    for name in ("sourceGripVelocity", "sourceGripAngularVelocity"):
        if drag and name not in payload:
            raise ValueError("drag rendering requires " + name)
        result[name] = vector(payload, name, 3) if name in payload else [0.0] * 3
    if drag and ("minTensions" not in payload or "dragDissipationW" not in payload):
        raise ValueError("drag rendering requires dissipation and pretension limits")
    result["minTensions"] = (vector(payload, "minTensions", count, 0, 10000)
                             if "minTensions" in payload else [0.0] * count)
    result["dragDissipationW"] = number(payload.get("dragDissipationW", 0.0), "dragDissipationW", 0, 1e9)
    if drag and result["dragDissipationW"] <= 0:
        raise ValueError("drag rendering requires positive relative-flow dissipation")
    for i, minimum in enumerate(result["minTensions"]):
        if minimum > result["maxTensions"][i] or (drag and not minimum <= result["tensionTargets"][i] <= result["maxTensions"][i]):
            raise ValueError("invalid drag tension interval")
    for name in ("anchors", "attachments"):
        result[name] = vector(payload, name, count * 3, -20, 20)
    for name in ("platformBase", "platformTop"):
        result[name] = vector(payload, name, 18, -20, 20)
    result["platformTargetLengthsMm"] = vector(payload, "platformTargetLengthsMm", 6, 0, 100000)
    for name in ("platformMinMm", "platformMaxMm"):
        result[name] = vector(payload, name, 6, 1, 100000)
    bounds = {
        "shaftLength": (0.05, 3), "platformNeutralHeight": (0.05, 10),
        "motorTauSeconds": (0.001, 2), "motorSlewNPerSecond": (1, 100000),
        "platformTauSeconds": (0.001, 5), "platformSpeedMmPerSecond": (1, 10000),
        "gripStiffnessNPerM": (1, 50000), "gripDampingNsPerM": (0.01, 5000),
        "gripMassKg": (0.1, 100), "angularStiffness": (0.01, 10000),
        "angularDamping": (0.001, 1000), "angularInertia": (0.001, 100),
    }
    for name, limits in bounds.items():
        result[name] = number(payload.get(name), name, *limits)
    stops = payload.get("hardwareStopsEnabled", False)
    if type(stops) is not bool:
        raise ValueError("hardwareStopsEnabled must be boolean")
    result["hardwareStopsEnabled"] = stops
    result["hardwareStopGeometry"] = []
    if stops:
        geometry = vector(payload, "hardwareStopGeometry", 8, -10, 10)
        if geometry[0] not in (-1, 1):
            raise ValueError("hardware stop side must be -1 or 1")
        ranges = ((0.05, 1.2), (-1, 10), (0.001, 0.3), (0.001, 0.6),
                  (0.001, 0.1), (0.001, 0.1), (0.001, 0.1))
        for value, limits in zip(geometry[1:], ranges):
            number(value, "hardwareStopGeometry", *limits)
        if geometry[1] + geometry[3] + geometry[7] >= 1.4:
            raise ValueError("hardware stops cannot fit inside the original paddle workspace")
        result["hardwareStopGeometry"] = geometry
    if result["gripStiffnessNPerM"] * DT * DT / result["gripMassKg"] > 0.5:
        raise ValueError("hand stiffness exceeds integration stability bound")
    if result["angularStiffness"] * DT * DT / result["angularInertia"] > 0.5:
        raise ValueError("angular stiffness exceeds integration stability bound")
    for i in range(6):
        low, high = result["platformMinMm"][i], result["platformMaxMm"][i]
        if low >= high:
            raise ValueError("invalid actuator limits")
        if result["platformValid"] and not low <= result["platformTargetLengthsMm"][i] <= high:
            raise ValueError("valid platform target exceeds actuator limits")
    lengths, _ = platform_geometry(result, [0.0] * 3, [0, 0, 0, 1])
    if any(not result["platformMinMm"][i] <= lengths[i] * 1000 <= result["platformMaxMm"][i] for i in range(6)):
        raise ValueError("neutral platform pose exceeds actuator limits")
    return result


class CommandSourceWindow:
    """Authorize outputs only from observations this endpoint actually issued.

    Invalid zero/hold heartbeats still obtain fresh sensors during startup and
    recovery. They never authorize either actuator route.
    """

    def __init__(self):
        self.observations = OrderedDict()

    def record(self, sequence, feedback, now):
        self.observations[sequence] = (feedback["plantSampleTick"], now,
                                       feedback["valid"] and not feedback["watchdog"])
        self.observations.move_to_end(sequence)
        while len(self.observations) > 256:
            self.observations.popitem(last=False)

    def authorize(self, command, now):
        source = self.observations.get(command["sourceFeedbackSequence"])
        matches = source is not None and source[0] == command["sourceSampleTick"]
        actuating = command["active"] and (command["cableValid"] or command["platformValid"])
        if actuating and (not matches or not source[2] or not 0 <= now - source[1] <= WATCHDOG_SECONDS):
            raise ValueError("actuator command requires a fresh issued observation")
        if not command["cableValid"] and any(command[name][i] != 0
                for name in ("tensionTargets", "requestForce", "requestTorque")
                for i in range(len(command[name]))):
            raise ValueError("invalid cable command must have zero targets and wrench")
        if not actuating and (any(command["desiredGripVelocity"]) or any(command.get("desiredGripAngularVelocity", [0.0]*3))):
            raise ValueError("zero/hold heartbeat cannot advance the hand intention")
        return source[1] if matches else None


def platform_geometry(command, position, rotation):
    lengths, rows = [], []
    for i in range(6):
        arm = q_rotate(rotation, command["platformTop"][i * 3:i * 3 + 3])
        offset = sub(add(add(position, [0, command["platformNeutralHeight"], 0]), arm),
                     command["platformBase"][i * 3:i * 3 + 3])
        length = norm(offset)
        if length < 1e-8:
            raise ValueError("degenerate actuator geometry")
        direction = scale(offset, 1 / length)
        lengths.append(length)
        rows.append(direction + cross(arm, direction))
    return lengths, rows


def matrix_rank(rows):
    basis = []
    for source in rows:
        row = source[:]
        for unit in basis:
            projection = dot(row, unit)
            row = [row[i] - projection * unit[i] for i in range(6)]
        size = norm(row)
        if size > 1e-5:
            basis.append([x / size for x in row])
    return len(basis)


def solve_linear(matrix, values):
    matrix = [row[:] + [values[i]] for i, row in enumerate(matrix)]
    for column in range(6):
        pivot = max(range(column, 6), key=lambda row: abs(matrix[row][column]))
        if abs(matrix[pivot][column]) < 1e-14:
            raise ValueError("singular damped normal matrix")
        matrix[column], matrix[pivot] = matrix[pivot], matrix[column]
        divisor = matrix[column][column]
        for j in range(column, 7):
            matrix[column][j] /= divisor
        for row in range(6):
            if row == column:
                continue
            multiplier = matrix[row][column]
            for j in range(column, 7):
                matrix[row][j] -= multiplier * matrix[column][j]
    return [row[6] for row in matrix]


def forward_kinematics(command, lengths_mm, position, rotation):
    target = [x * 0.001 for x in lengths_mm]
    position, rotation = position[:], rotation[:]
    damping = 1e-5
    for _ in range(12):
        lengths, jacobian = platform_geometry(command, position, rotation)
        residual = [target[i] - lengths[i] for i in range(6)]
        error = dot(residual, residual)
        if max(abs(x) for x in residual) < 1e-7:
            break
        normal = [[sum(row[i] * row[j] for row in jacobian) + (damping if i == j else 0)
                   for j in range(6)] for i in range(6)]
        gradient = [sum(jacobian[row][i] * residual[row] for row in range(6)) for i in range(6)]
        delta = solve_linear(normal, gradient)
        candidate_position = add(position, bounded_vector(delta[:3], 0.05))
        candidate_rotation = q_normalize(q_multiply(q_from_vector(bounded_vector(delta[3:], 0.1)), rotation))
        predicted, _ = platform_geometry(command, candidate_position, candidate_rotation)
        candidate_error = sum((target[i] - predicted[i]) ** 2 for i in range(6))
        if candidate_error < error:
            position, rotation = candidate_position, candidate_rotation
            damping = max(1e-8, damping * 0.3)
        else:
            damping *= 10
    predicted, rows = platform_geometry(command, position, rotation)
    return position, rotation, max(abs(target[i] - predicted[i]) for i in range(6)) * 1000, matrix_rank(rows)


def moving_frame_acceleration(rotation, origin_acceleration, angular_velocity,
                              angular_acceleration, position, velocity, relative_angular_velocity):
    """Rigid-frame inertial terms; platform rates are expressed in the base frame.

    Grip coordinates and relative velocities use the moving platform axes. Neutral
    gravity/support are absorbed into the assumed hand equilibrium, as before.
    """
    inverse = q_inverse(rotation)
    acceleration = q_rotate(inverse, origin_acceleration)
    omega = q_rotate(inverse, angular_velocity)
    alpha = q_rotate(inverse, angular_acceleration)
    translation = scale(add(add(acceleration, cross(alpha, position)),
                            add(cross(omega, cross(omega, position)),
                                scale(cross(omega, velocity), 2))), -1)
    # Isotropic effective inertia: d(relative omega)/dt includes frame transport.
    angular = scale(add(alpha, cross(omega, relative_angular_velocity)), -1)
    return translation, angular


class ControllerModel:
    def __init__(self):
        self.command = None
        self.last_command_time = None
        self.command_source_time = None
        self.position = [0.0] * 3
        self.rotation = [0.0, 0.0, 0.0, 1.0]
        self.velocity = [0.0] * 3
        self.angular_velocity = [0.0] * 3
        self.tensions = []
        self.platform_position = [0.0] * 3
        self.platform_rotation = [0.0, 0.0, 0.0, 1.0]
        self.platform_velocity = [0.0] * 3
        self.platform_angular_velocity = [0.0] * 3
        self.platform_acceleration = [0.0] * 3
        self.platform_angular_acceleration = [0.0] * 3
        self.platform_induced_acceleration = [0.0] * 3
        self.platform_induced_angular_acceleration = [0.0] * 3
        self.platform_lengths = []
        self.platform_residual = 0.0
        self.geometry_rank = 0
        self.force = [0.0] * 3
        self.torque = [0.0] * 3
        self.effort = [0.0] * 3
        self.motor_saturated = False
        self.workspace_constrained = False
        self.motion_limited = False
        self.hardware_stop_active = False
        self.hardware_stop_reaction_force = [0.0] * 3
        self.hardware_stop_reaction_torque = [0.0] * 3
        self.hardware_stop_projection_m = 0.0
        self.hardware_stop_projection_radians = 0.0
        self.fault = ""

    def set_command(self, command, now, source_time=None):
        immutable = ("shaftLength", "anchors", "attachments", "platformBase", "platformTop",
                     "platformNeutralHeight", "platformMinMm", "platformMaxMm",
                     "hardwareStopsEnabled", "hardwareStopGeometry")
        if self.command is not None and (len(command["tensionTargets"]) != len(self.tensions)
                                        or any(command[name] != self.command[name] for name in immutable)):
            raise ValueError("geometry change requires an explicit reset")
        if self.command is None:
            self.position = command["desiredGripPosition"][:]
            self.rotation = command["desiredGripRotation"][:]
            self.tensions = [0.0] * len(command["tensionTargets"])
            lengths, rows = platform_geometry(command, self.platform_position, self.platform_rotation)
            self.platform_lengths = [x * 1000 for x in lengths]
            self.geometry_rank = matrix_rank(rows)
        self.command = command
        self.last_command_time = now
        self.command_source_time = source_time
        self.constrain_workspace()
        if command["hardwareStopsEnabled"]:
            self.constrain_hardware_stops(DT)

    def watchdog(self, now):
        return self.last_command_time is None or now - self.last_command_time > WATCHDOG_SECONDS

    def execution_fresh(self, now):
        # Direct deterministic model fixtures have no transport source. The UDP
        # boundary always requires one before accepting an actuator command.
        return not self.watchdog(now) and (self.command_source_time is None
            or 0 <= now - self.command_source_time <= WATCHDOG_SECONDS)

    def cable_wrench(self):
        command = self.command
        blade = sub(self.position, q_rotate(self.rotation, [0, command["shaftLength"], 0]))
        force, torque, lengths = [0.0] * 3, [0.0] * 3, []
        for i, tension in enumerate(self.tensions):
            attachment = add(blade, q_rotate(self.rotation, command["attachments"][i * 3:i * 3 + 3]))
            offset = sub(command["anchors"][i * 3:i * 3 + 3], attachment)
            length = norm(offset)
            if length < 1e-6:
                raise ValueError("degenerate cable geometry")
            cable_force = scale(offset, tension / length)
            force = add(force, cable_force)
            torque = add(torque, cross(sub(attachment, self.position), cable_force))
            lengths.append(length * 1000)
        return force, torque, lengths

    def constrain_workspace(self):
        shaft = q_rotate(self.rotation, [0, self.command["shaftLength"], 0])
        blade = sub(self.position, shaft)
        rotation_velocity = cross(self.angular_velocity, shaft)
        self.workspace_constrained = False
        for i, (low, high) in enumerate(((-1.4, 1.4), (-1.2, 2.4), (-4.6, 4.6))):
            constrained = clamp(blade[i], low, high)
            blade_velocity = self.velocity[i] - rotation_velocity[i]
            if constrained != blade[i]:
                self.workspace_constrained = True
                self.position[i] += constrained - blade[i]
            if (constrained <= low and blade_velocity < 0) or (constrained >= high and blade_velocity > 0):
                self.workspace_constrained = True
                self.velocity[i] = rotation_velocity[i]

    def hardware_stop_contacts(self):
        """Conservative reachable set, not a reconstruction of real joint stops.

        The full blade stays outboard. The shaft capsule stays outboard until
        above the displayed hull's highest point. This envelope deliberately
        includes decorative geometry; no collision tolerance is weakened.
        """
        command = self.command
        side, beam, top, hw, hl, ht, radius, clearance = command["hardwareStopGeometry"]
        normal = [side, 0.0, 0.0]
        contacts = []
        length = command["shaftLength"]
        for x in (-hw, hw):
            for y in (-hl, hl):
                for z in (-ht, ht):
                    arm = q_rotate(self.rotation, [x, y - length, z])
                    point = add(self.position, arm)
                    contacts.append((side * point[0] - beam - clearance, normal, arm))
        up = q_rotate(self.rotation, [0.0, 1.0, 0.0])
        blade_arm = scale(up, -length)
        blade = add(self.position, blade_arm)
        height = top + radius + clearance
        if min(blade[1], self.position[1]) <= height:
            if max(blade[1], self.position[1]) <= height:
                low, high = 0.0, length
            else:
                crossing = clamp((height - blade[1]) / up[1], 0.0, length)
                low, high = (0.0, crossing) if up[1] > 0 else (crossing, length)
            reach = low if side * up[0] >= 0 else high
            arm = scale(up, reach - length)
            point = add(self.position, arm)
            contacts.append((side * point[0] - beam - radius - clearance, normal, arm))
        # Retain the original sensor workspace as simultaneous constraints.
        for i, (low, high) in enumerate(((-1.4, 1.4), (-1.2, 2.4), (-4.6, 4.6))):
            axis = [0.0] * 3
            axis[i] = 1.0
            contacts.append((blade[i] - low, axis, blade_arm))
            contacts.append((high - blade[i], scale(axis, -1.0), blade_arm))
        return contacts

    def constrain_hardware_stops(self, dt):
        """Frictionless, zero restitution stop projection at the plant boundary.

        Motor wrench stays unchanged. The stop supplies a separate reaction,
        changes the actual rigid-body state, and therefore the subsequent cable
        geometry and encoder packet. It is never a rendering-only correction.
        """
        before_position, before_rotation = self.position[:], self.rotation[:]
        self.hardware_stop_active = False
        self.hardware_stop_reaction_force = [0.0] * 3
        self.hardware_stop_reaction_torque = [0.0] * 3
        inverse_mass = 1.0 / self.command["gripMassKg"]
        inverse_inertia = 1.0 / self.command["angularInertia"]
        for _ in range(96):
            distance, normal, arm = min(self.hardware_stop_contacts(), key=lambda value: value[0])
            if distance >= -1e-10:
                break
            angular = cross(arm, normal)
            mobility = inverse_mass + inverse_inertia * dot(angular, angular)
            correction = -distance / mobility
            if norm(angular) * correction * inverse_inertia > 0.1:
                correction = 0.1 / (norm(angular) * inverse_inertia)
            self.position = add(self.position, scale(normal, correction * inverse_mass))
            self.rotation = q_normalize(q_multiply(q_from_vector(scale(angular, correction * inverse_inertia)), self.rotation))
            self.hardware_stop_active = True
        if min(value[0] for value in self.hardware_stop_contacts()) < -1e-7:
            raise ValueError("ideal hardware stop position projection did not converge")
        # Remove only velocity entering an active stop. Each normal impulse
        # dissipates kinetic energy; free tangential movement remains available.
        for _ in range(96):
            active = [(dot(normal, add(self.velocity, cross(self.angular_velocity, arm))), normal, arm)
                      for distance, normal, arm in self.hardware_stop_contacts() if distance <= 1e-7]
            if not active:
                break
            speed, normal, arm = min(active, key=lambda value: value[0])
            if speed >= -1e-9:
                break
            angular = cross(arm, normal)
            impulse = -speed / (inverse_mass + inverse_inertia * dot(angular, angular))
            self.velocity = add(self.velocity, scale(normal, impulse * inverse_mass))
            self.angular_velocity = add(self.angular_velocity, scale(angular, impulse * inverse_inertia))
            self.hardware_stop_reaction_force = add(self.hardware_stop_reaction_force, scale(normal, impulse / dt))
            self.hardware_stop_reaction_torque = add(self.hardware_stop_reaction_torque, scale(angular, impulse / dt))
            self.hardware_stop_active = True
        self.hardware_stop_projection_m = norm(sub(self.position, before_position))
        self.hardware_stop_projection_radians = norm(q_to_vector(q_multiply(self.rotation, q_inverse(before_rotation))))

    def actuator_targets(self, enabled):
        """Reduce obsolete drag locally; never invent a faster physical motor.

        At the source, drag dissipates D watts relative to the sampled water.
        Holding its spatial wrench while grip twist changes gives predicted
        dissipation D - W dot (V - V_source). Its nonnegative ratio gives the
        quadratic-drag reduction (exact for collinear uniform flow). The ratio
        is capped at one: new/increased resistance requires a new host sample.
        Distributed wave/current work is included in D, so positive world-space
        work from flowing water is allowed. This is a local drag approximation,
        not a passivity proof for the lagged, constrained three-cable mechanism.
        """
        command = self.command
        if not enabled or not command["cableValid"]:
            return [0.0] * len(self.tensions), 1.0
        if not command["dragFeedbackEnabled"]:
            return command["tensionTargets"], 1.0
        change = (dot(command["requestForce"], sub(self.velocity, command["sourceGripVelocity"]))
                  + dot(command["requestTorque"], sub(self.angular_velocity, command["sourceGripAngularVelocity"])))
        ratio = clamp(1.0 - change / command["dragDissipationW"], 0.0, 1.0)
        gain = ratio * ratio
        return [low + (target - low) * gain for low, target in
                zip(command["minTensions"], command["tensionTargets"])], gain

    def step(self, dt, now):
        if self.command is None:
            return
        if not math.isfinite(dt) or not 0 < dt <= DT + 1e-12:
            raise ValueError("plant integration requires 0 < dt <= 5 ms")
        command = self.command
        enabled = command["active"] and self.execution_fresh(now) and not self.fault
        self.motor_saturated = False
        try:
            targets, _ = self.actuator_targets(enabled)
            for i, actual in enumerate(self.tensions):
                requested = targets[i]
                target = clamp(requested, 0, command["maxTensions"][i])
                delta = (target - actual) * dt / (command["motorTauSeconds"] + dt)
                limit = command["motorSlewNPerSecond"] * dt
                change = clamp(delta, -limit, limit)
                self.motor_saturated |= target != requested or change != delta
                self.tensions[i] = clamp(actual + change, 0, command["maxTensions"][i])

            previous_velocity = self.platform_velocity[:]
            previous_angular_velocity = self.platform_angular_velocity[:]
            if enabled and command["platformValid"]:
                previous_position = self.platform_position[:]
                previous_rotation = self.platform_rotation[:]
                for i, length in enumerate(self.platform_lengths):
                    delta = (command["platformTargetLengthsMm"][i] - length) * dt / (command["platformTauSeconds"] + dt)
                    maximum = command["platformSpeedMmPerSecond"] * dt
                    self.platform_lengths[i] = clamp(length + clamp(delta, -maximum, maximum),
                                                     command["platformMinMm"][i], command["platformMaxMm"][i])
                self.platform_position, self.platform_rotation, self.platform_residual, self.geometry_rank = forward_kinematics(
                    command, self.platform_lengths, self.platform_position, self.platform_rotation)
                self.platform_velocity = scale(sub(self.platform_position, previous_position), 1 / dt)
                self.platform_angular_velocity = scale(q_to_vector(q_multiply(
                    self.platform_rotation, q_inverse(previous_rotation))), 1 / dt)
            else:
                # Hold the actual cylinders on timeout; the held platform has zero velocity.
                self.platform_velocity = [0.0] * 3
                self.platform_angular_velocity = [0.0] * 3
            self.platform_acceleration = scale(sub(self.platform_velocity, previous_velocity), 1 / dt)
            self.platform_angular_acceleration = scale(sub(
                self.platform_angular_velocity, previous_angular_velocity), 1 / dt)
            self.platform_induced_acceleration, self.platform_induced_angular_acceleration = moving_frame_acceleration(
                self.platform_rotation, self.platform_acceleration, self.platform_angular_velocity,
                self.platform_angular_acceleration, self.position, self.velocity, self.angular_velocity)

            self.force, self.torque, _ = self.cable_wrench()
            desired_velocity = command["desiredGripVelocity"] if enabled else [0.0] * 3
            mass, damping = command["gripMassKg"], command["gripDampingNsPerM"]
            spring = scale(sub(command["desiredGripPosition"], self.position), command["gripStiffnessNPerM"])
            numerator = add(self.velocity, scale(add(add(spring, scale(desired_velocity, damping)),
                                                   add(self.force, scale(self.platform_induced_acceleration, mass))), dt / mass))
            next_velocity = scale(numerator, 1 / (1 + dt * damping / mass))
            self.effort = add(spring, scale(sub(desired_velocity, next_velocity), damping))
            self.motion_limited = norm(next_velocity) > 15
            self.velocity = bounded_vector(next_velocity, 15)
            self.position = add(self.position, scale(self.velocity, dt))

            angular_error = q_to_vector(q_multiply(command["desiredGripRotation"], q_inverse(self.rotation)))
            inertia, angular_damping = command["angularInertia"], command["angularDamping"]
            desired_angular_velocity = command["desiredGripAngularVelocity"] if enabled else [0.0] * 3
            angular_force = add(add(scale(angular_error, command["angularStiffness"]), self.torque),
                                add(scale(desired_angular_velocity, angular_damping),
                                    scale(self.platform_induced_angular_acceleration, inertia)))
            angular_velocity = scale(add(self.angular_velocity, scale(angular_force, dt / inertia)),
                                     1 / (1 + dt * angular_damping / inertia))
            self.motion_limited |= norm(angular_velocity) > 30
            self.angular_velocity = bounded_vector(angular_velocity, 30)
            self.rotation = q_normalize(q_multiply(q_from_vector(scale(self.angular_velocity, dt)), self.rotation))
            self.constrain_workspace()
            if command["hardwareStopsEnabled"]:
                self.constrain_hardware_stops(dt)
            self.force, self.torque, _ = self.cable_wrench()
            state = (self.position + self.rotation + self.velocity + self.angular_velocity + self.tensions
                     + self.platform_lengths + self.platform_position + self.platform_rotation + self.force + self.torque
                     + self.platform_velocity + self.platform_angular_velocity + self.platform_acceleration
                     + self.platform_angular_acceleration + self.platform_induced_acceleration
                     + self.platform_induced_angular_acceleration)
            if any(not math.isfinite(x) for x in state):
                raise ValueError("nonfinite plant state")
        except (ValueError, OverflowError, ZeroDivisionError) as error:
            self.fault = str(error)
            self.tensions = [0.0] * len(self.tensions)
            self.force = self.torque = self.velocity = self.angular_velocity = [0.0] * 3

    def feedback(self, now, controller_time, encoder_sequence, *, controller_wall_time=None,
                 sample_tick=None, dropped_ticks=0):
        # controller_time is the elapsed time actually integrated by the plant.
        # The wall clock remains separate: watchdog age must never use this clock.
        if controller_wall_time is None:
            controller_wall_time = now
        if sample_tick is None:
            sample_tick = round(controller_time / DT)
        command = self.command
        if command is None:
            return None
        watchdog = self.watchdog(now)
        valid = not watchdog and command["active"] and not self.fault
        status = ["SOFTWARE_DYNAMICS", "UNMEASURED", "CAPABILITY_LIMITED"]
        if watchdog:
            status.append("WATCHDOG")
        if not self.execution_fresh(now):
            status.append("COMMAND_EXECUTION_EXPIRED")
        if not command["active"]:
            status.append("INACTIVE")
        if not command["cableValid"]:
            status.append("CABLE_REQUEST_INVALID_ZERO_TARGET")
        if not command["platformValid"]:
            status.append("PLATFORM_REQUEST_INVALID_HOLD")
        if self.workspace_constrained:
            status.append("WORKSPACE_CONSTRAINED")
        if self.motion_limited:
            status.append("VELOCITY_LIMITED")
        if command["hardwareStopsEnabled"]:
            status.append("IDEAL_HARDWARE_STOPS_ASSUMED")
            if self.hardware_stop_active:
                status.append("HARDWARE_STOP_ACTIVE")
        if dropped_ticks:
            status.append("PLANT_SCHEDULER_DROPPED_TICKS")
        if self.fault:
            status.append("MODEL_FAULT:" + self.fault)
        if self.platform_residual > 0.5:
            # Length sensors remain meaningful, but do not label a poor pose fit as valid.
            valid = False
            status.append("PLATFORM_FK_RESIDUAL_EXCEEDS_0.5_MM")
        blade = sub(self.position, q_rotate(self.rotation, [0, command["shaftLength"], 0]))
        force, torque, lengths = self.cable_wrench() if not self.fault else ([0.0] * 3, [0.0] * 3, [0.0] * len(self.tensions))
        counts = [int(round(x * POSITION_SCALE)) for x in blade]
        counts += [int(round(x * ANGLE_SCALE)) for x in q_to_unity_euler(self.rotation)]
        # The independent sensor endpoint owns serialization as well as counts.
        # Unity receives these exact DBEP bytes; it must not manufacture a packet
        # locally and then treat its own serialization as transport evidence.
        encoder = struct.pack('<IIIQ6if', 0x44424550, 1, encoder_sequence,
                              int(controller_time * 1e6), *counts, norm(self.effort))
        encoder += struct.pack('<I', zlib.crc32(encoder))
        execution = valid and self.execution_fresh(now)
        effective_targets, drag_scale = self.actuator_targets(execution)
        effective_targets = [clamp(target, 0, command["maxTensions"][i]) for i, target in enumerate(effective_targets)]
        drag_active = execution and command["cableValid"] and command["dragFeedbackEnabled"]
        if drag_active and drag_scale < 1.0:
            status.append("LOCAL_DRAG_TARGET_REDUCED")
        return {"kind": "feedback", "commandSequence": command["commandSequence"],
                "commandSourceFeedbackSequence": command["sourceFeedbackSequence"],
                "commandSourceSampleTick": command["sourceSampleTick"],
                "commandSourceAgeSeconds": now - self.command_source_time if self.command_source_time is not None else -1.0,
                "commandAgeSeconds": now - self.last_command_time,
                "commandExecutionFresh": execution,
                "cableExecutionEnabled": execution and command["cableValid"],
                "platformExecutionEnabled": execution and command["platformValid"],
                "encoderSequence": encoder_sequence, "controllerTimeSeconds": controller_time,
                "controllerWallTimeSeconds": controller_wall_time,
                "plantSampleTick": sample_tick, "plantStepSeconds": DT, "plantDroppedTicks": dropped_ticks,
                "valid": valid, "watchdog": watchdog, "capabilityLimited": True,
                "status": ";".join(status), "provenance": PROVENANCE, "measured": False,
                "rawEncoderCounts": counts, "tensionN": self.tensions[:], "cableLengthsMm": lengths,
                "encoderPacketBase64": base64.b64encode(encoder).decode('ascii'),
                "forceLocal": force, "torqueLocal": torque, "platformPosition": self.platform_position[:],
                "platformEuler": q_to_unity_euler(self.platform_rotation), "platformLengthsMm": self.platform_lengths[:],
                "platformVelocity": self.platform_velocity[:],
                "platformAngularVelocity": self.platform_angular_velocity[:],
                "platformAcceleration": self.platform_acceleration[:],
                "platformAngularAcceleration": self.platform_angular_acceleration[:],
                "platformInducedAcceleration": self.platform_induced_acceleration[:],
                "platformInducedAngularAcceleration": self.platform_induced_angular_acceleration[:],
                "platformFitResidualMm": self.platform_residual, "gripPosition": self.position[:],
                "gripVelocity": self.velocity[:], "gripAngularVelocity": self.angular_velocity[:],
                # Current local setpoints are separate from actual lagged tension.
                "effectiveTensionTargets": effective_targets, "dragFeedbackActive": drag_active,
                "dragTargetScale": drag_scale, "motorSaturated": self.motor_saturated,
                "geometryRank": self.geometry_rank, "actualGripEffortN": self.effort[:],
                "workspaceConstrained": self.workspace_constrained, "motionLimited": self.motion_limited,
                "hardwareStopsEnabled": command["hardwareStopsEnabled"],
                "hardwareStopGeometry": command["hardwareStopGeometry"][:],
                "hardwareStopActive": self.hardware_stop_active,
                "hardwareStopReactionForce": self.hardware_stop_reaction_force[:],
                "hardwareStopReactionTorque": self.hardware_stop_reaction_torque[:],
                "hardwareStopProjectionM": self.hardware_stop_projection_m,
                "hardwareStopProjectionRadians": self.hardware_stop_projection_radians,
                "countsPerMeter": POSITION_SCALE, "countsPerDegree": ANGLE_SCALE}


def parent_alive(pid):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return ctypes.get_last_error() == 5  # Access denied does not prove parent exit.
        try:
            code = wintypes.DWORD()
            return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except ProcessLookupError:
        return False


class PlantSampleClock:
    """Track integrated time separately from the monotonic scheduling clock."""

    def __init__(self, started):
        self.started = started
        self.sample_tick = 0
        self.dropped_ticks = 0

    @property
    def sample_time(self):
        return self.sample_tick * DT

    @property
    def next_tick(self):
        return self.started + (self.sample_tick + self.dropped_ticks + 1) * DT

    def accept_command(self, model, command, now, source_time=None):
        # A command received now cannot act on overdue steps from before its
        # receipt. Finish those steps with the previously accepted command.
        self.advance(model, now)
        if source_time is None:
            model.set_command(command, now)
        else:
            model.set_command(command, now, source_time)

    def advance(self, model, now):
        # Keep the existing 20-step work bound. Skipped wall time is visible in
        # telemetry and cannot masquerade as time that advanced the mechanics.
        advanced = 0
        while advanced < 20 and self.next_tick <= now:
            model.step(DT, now)
            self.sample_tick += 1
            advanced += 1
        if self.next_tick <= now:
            self.dropped_ticks += math.floor((now - self.next_tick) / DT) + 1
        return advanced


def serve(session, parent_pid, max_idle_seconds=30.0):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    print("READY", sock.getsockname()[1], os.getpid(), flush=True)
    model, endpoint, last_sequence = ControllerModel(), None, None
    sources = CommandSourceWindow()
    response_sequence = 0
    started = time.monotonic()
    plant_clock = PlantSampleClock(started)
    next_feedback, next_parent_check = started + 0.01, started + 0.5
    last_valid_message = started
    try:
        while True:
            now = time.monotonic()
            timeout = max(0.0, min(plant_clock.next_tick, next_feedback, next_parent_check) - now)
            readable, _, _ = select.select([sock], [], [], min(timeout, 0.02))
            if readable:
                for _ in range(64):
                    try:
                        packet, sender = sock.recvfrom(MAX_PACKET + 1)
                    except BlockingIOError:
                        break
                    if endpoint is not None and sender != endpoint:
                        continue
                    try:
                        sequence, payload = decode_packet(packet, session)
                        if last_sequence is not None and not newer(sequence, last_sequence):
                            continue
                        kind = payload.get("kind")
                        if kind == "step":
                            command = validate_step(payload)
                            if command["commandSequence"] != sequence:
                                raise ValueError("command body/envelope sequence mismatch")
                            received_at = time.monotonic()
                            source_time = sources.authorize(command, received_at)
                            plant_clock.accept_command(model, command, received_at, source_time)
                        elif kind not in ("reset", "shutdown"):
                            continue
                    except (ValueError, TypeError, KeyError, OverflowError, RecursionError):
                        continue  # Rejected packets never refresh the watchdog or sequence window.
                    endpoint, last_sequence = sender, sequence
                    last_valid_message = time.monotonic()
                    if kind in ("reset", "shutdown"):
                        if kind == "reset":
                            plant_clock.advance(model, time.monotonic())
                            model = ControllerModel()
                            sources = CommandSourceWindow()
                        response_sequence = (response_sequence + 1) & 0xFFFFFFFF
                        sock.sendto(encode_packet({"kind": kind + "_ack", "commandSequence": sequence},
                                                  response_sequence, session), endpoint)
                        if kind == "shutdown":
                            return
            now = time.monotonic()
            plant_clock.advance(model, now)
            if next_feedback <= now:
                next_feedback = now + 0.01
                if endpoint is not None and model.command is not None:
                    response_sequence = (response_sequence + 1) & 0xFFFFFFFF
                    payload = model.feedback(now, plant_clock.sample_time, response_sequence,
                                             controller_wall_time=now - started,
                                             sample_tick=plant_clock.sample_tick,
                                             dropped_ticks=plant_clock.dropped_ticks)
                    sock.sendto(encode_packet(payload, response_sequence, session), endpoint)
                    sources.record(response_sequence, payload, now)
            if now >= next_parent_check:
                next_parent_check = now + 0.5
                if not parent_alive(parent_pid) or now - last_valid_message > max_idle_seconds:
                    return
    finally:
        sock.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", type=int, required=True)
    parser.add_argument("--parent-pid", type=int, required=True)
    parser.add_argument("--max-idle-seconds", type=float, default=30.0)
    args = parser.parse_args()
    if not 0 < args.session <= 0xFFFFFFFFFFFFFFFF or args.parent_pid <= 0:
        parser.error("session and parent PID must be positive valid integers")
    if not math.isfinite(args.max_idle_seconds) or not 0.2 <= args.max_idle_seconds <= 3600:
        parser.error("max idle duration must be 0.2 to 3600 seconds")
    serve(args.session, args.parent_pid, args.max_idle_seconds)


if __name__ == "__main__":
    main()
