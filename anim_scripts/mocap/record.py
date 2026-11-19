"""Records Vive tracker and wand takes from SteamVR, one CSV per take.

    python3 anim_scripts/mocap/record.py [out folder]

SteamVR must be running with the trackers and wands on. Takes go to the out folder
(animDLs/mocap/<date>/ by default) as <clip>_<nn>.csv.

Starting and stopping a take: hold both wands together above your head for a second,
or, with the wands on a tool (they can't come together), hold both above your head
and still for GESTURE_STILL_HOLD seconds (a swing passes overhead far quicker than
that). It says "rest" as a take starts: lower your arms and stand in the rest pose (feet
apart, arms hanging, facing the tape arrow) until it says "go", then act, come back
to the rest pose, and raise the wands together again to stop. SteamVR gives a
background app no button input, so it goes by that gesture instead. Enter at the
keyboard does the same.

Keyboard commands:
    n <clip>   name the takes that follow (chop, hammer, ...); numbered on from the
               last take of that name in the folder
    c          check which tracker is which: hold the rest pose for the countdown
    s          each device's tracking state
    Enter      start or stop a take
    q          quit

Which tracker is which is worked out from the rest pose at the start of each take,
not from SteamVR's tracker roles: the highest is the head, the two lowest the feet,
the pair of the other four furthest apart side to side the elbows (or the knees, when
they are below the other two: trackers worn on the outside of the shins, just below the
knee), and of the last two the higher the chest and the lower the hips (take.assign).
The hips tracker may be worn anywhere on the belt (only --tool takes need it at the
back). Forward is the way the
two wands point together, so hold them pointing forward in the rest pose; it gives
left and right.

Each CSV starts with # lines: the clip, each device's serial and part, the rest
window and "go" in seconds from the start, and where the stop gesture began. Then one
row per device per sample: t (seconds), part, x, y, z (metres, SteamVR's standing
space: +y up, the floor at 0), qw, qx, qy, qz, ok (1 tracked, 0 lost).
"""

import datetime
import os
import re
import subprocess
import sys
import threading
import time

import numpy as np
import openvr

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from take import PARTS, assign

RATE = 120              # samples a second
REST_FROM = 2.5         # seconds into a take the rest pose is measured from (the arms are down by then)...
REST_TO = 3.5           # ...to, when it says "go"
GESTURE_HOLD = 1.0      # seconds the wands are held together overhead to start or stop
GESTURE_NEAR = 0.15     # metres apart the wands count as together
GESTURE_ABOVE = 0.05    # metres above the head tracker they must be
GESTURE_APART = 0.3     # metres apart before the gesture counts again
GESTURE_STILL_HOLD = 1.5   # seconds both wands are held still above the head (however far apart) to start or stop
GESTURE_STILL = 0.08    # metres each wand may move from where it was when the hold began and still count as held still
GESTURE_REARM = 0.5     # seconds both wands are below the head before the held-still gesture counts again
RAISE_TIME = 0.8        # seconds before the wands were overhead the arms began rising
LOST_AFTER = 0.3        # seconds a device is lost before it is called out
LOST_REPEAT = 3.0       # seconds between call-outs of the same device



def say(text):
    """Speaks without waiting, so the recording loop doesn't stall."""
    try:
        subprocess.Popen(['spd-say', text],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        pass
    print('  [' + text + ']')


def quat_of(m):
    """A unit quaternion (w, x, y, z) from the 3x3 part of SteamVR's 3x4 matrix."""
    r = np.array([[m[i][j] for j in range(3)] for i in range(3)])
    t = r[0, 0] + r[1, 1] + r[2, 2]
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        q = (0.25 * s, (r[2, 1] - r[1, 2]) / s, (r[0, 2] - r[2, 0]) / s, (r[1, 0] - r[0, 1]) / s)
    elif r[0, 0] > r[1, 1] and r[0, 0] > r[2, 2]:
        s = np.sqrt(1.0 + r[0, 0] - r[1, 1] - r[2, 2]) * 2
        q = ((r[2, 1] - r[1, 2]) / s, 0.25 * s, (r[0, 1] + r[1, 0]) / s, (r[0, 2] + r[2, 0]) / s)
    elif r[1, 1] > r[2, 2]:
        s = np.sqrt(1.0 + r[1, 1] - r[0, 0] - r[2, 2]) * 2
        q = ((r[0, 2] - r[2, 0]) / s, (r[0, 1] + r[1, 0]) / s, 0.25 * s, (r[1, 2] + r[2, 1]) / s)
    else:
        s = np.sqrt(1.0 + r[2, 2] - r[0, 0] - r[1, 1]) * 2
        q = ((r[1, 0] - r[0, 1]) / s, (r[0, 2] + r[2, 0]) / s, (r[1, 2] + r[2, 1]) / s, 0.25 * s)
    q = np.array(q)
    return q / np.linalg.norm(q)


class Recorder:
    def __init__(self, out):
        self.out = out
        self.clip = 'take'
        self.vr = openvr.init(openvr.VRApplication_Background)
        self.lock = threading.Lock()
        self.devices = {}       # index: (serial, 'tracker' or 'wand')
        self.find_devices()
        self.recording = False
        self.rows = []
        self.started = 0.0
        self.said_go = False
        self.toggle = False     # Enter pressed: the loop starts or stops a take
        self.check_until = None
        self.check_samples = {}
        self.together_since = None
        self.above_since = None
        self.armed = True
        self.still_since = None
        self.below_since = None
        self.still_armed = True
        self.still_from = None   # {serial: xyz} where the wands were when the held-still gesture began
        self.lost_since = {}
        self.lost_said = {}
        self.parts = {}
        self.running = True
        self.last = {}          # serial: (xyz, ok) as last seen

    def find_devices(self):
        kinds = {openvr.TrackedDeviceClass_GenericTracker: 'tracker',
                 openvr.TrackedDeviceClass_Controller: 'wand'}
        found = {}
        for i in range(openvr.k_unMaxTrackedDeviceCount):
            kind = kinds.get(self.vr.getTrackedDeviceClass(i))
            if kind:
                found[i] = (self.vr.getStringTrackedDeviceProperty(i, openvr.Prop_SerialNumber_String), kind)
        self.devices = found

    def sample(self):
        poses = self.vr.getDeviceToAbsoluteTrackingPose(
            openvr.TrackingUniverseStanding, 0, openvr.k_unMaxTrackedDeviceCount)
        now = time.perf_counter()
        seen = []
        for i, (serial, kind) in self.devices.items():
            p = poses[i]
            ok = bool(p.bPoseIsValid) and p.eTrackingResult == openvr.TrackingResult_Running_OK
            m = p.mDeviceToAbsoluteTracking
            xyz = np.array([m[0][3], m[1][3], m[2][3]])
            seen.append((serial, kind, ok, xyz, quat_of(m) if ok else np.array([1.0, 0, 0, 0])))
            self.last[serial] = (xyz, ok)
        return now, seen

    def loop(self):
        step = 1.0 / RATE
        next_at = time.perf_counter()
        found_at = 0.0
        while self.running:
            now, seen = self.sample()
            if now - found_at > 2.0 and not self.recording:
                self.find_devices()     # a tracker switched on late
                found_at = now
            self.gesture(now, seen)
            with self.lock:
                if self.toggle:
                    self.toggle = False
                    self.stop(now) if self.recording else self.start(now)
            if self.recording:
                t = now - self.started
                for serial, kind, ok, xyz, q in seen:
                    self.rows.append((t, serial, xyz, q, ok))
                if not self.said_go and t >= REST_TO:
                    self.said_go = True
                    say('go')
                self.call_lost(now, seen)
            if self.check_until is not None:
                self.check(now, seen)
            next_at += step
            time.sleep(max(0.0, next_at - time.perf_counter()))
            if time.perf_counter() - next_at > 0.5:
                next_at = time.perf_counter()

    def gesture(self, now, seen):
        """Both wands held together over the head for GESTURE_HOLD, or held still over it for GESTURE_STILL_HOLD however
        far apart (on a tool), starts or stops a take."""
        wand_at = {serial: xyz for serial, kind, ok, xyz, q in seen if kind == 'wand' and ok}
        wands = list(wand_at.values())
        trackers = [xyz for serial, kind, ok, xyz, q in seen if kind == 'tracker' and ok]
        if len(wands) != 2 or not trackers:
            self.together_since = self.above_since = self.still_since = None
            return
        head = max(t[1] for t in trackers)
        apart = np.linalg.norm(wands[0] - wands[1])
        above = min(w[1] for w in wands) > head + GESTURE_ABOVE
        if above:
            self.above_since = self.above_since or now
            self.below_since = None
        else:
            self.above_since = None
            self.below_since = self.below_since or now
            if now - self.below_since >= GESTURE_REARM:
                self.still_armed = True
        # Held still overhead, however far apart (wands on a tool): each within GESTURE_STILL of where it was as the hold
        # began, which starts again whenever one moves further.
        if self.still_armed and above:
            if self.still_since is None or set(self.still_from) != set(wand_at) or any(
                    np.linalg.norm(wand_at[k] - self.still_from[k]) > GESTURE_STILL for k in wand_at):
                self.still_since, self.still_from = now, {k: v.copy() for k, v in wand_at.items()}
            if now - self.still_since >= GESTURE_STILL_HOLD:
                self.still_armed = self.armed = False
                self.still_since = self.together_since = None
                with self.lock:
                    self.stop(now) if self.recording else self.start(now)
                return
        else:
            self.still_since = None
        if apart > GESTURE_APART:
            self.armed = True
        if self.armed and above and apart < GESTURE_NEAR:
            self.together_since = self.together_since or now
            if now - self.together_since >= GESTURE_HOLD:
                self.armed = self.still_armed = False
                self.together_since = self.still_since = None
                with self.lock:
                    self.stop(now) if self.recording else self.start(now)
        else:
            self.together_since = None

    def next_name(self):
        taken = [int(m.group(1)) for f in os.listdir(self.out)
                 for m in [re.fullmatch(re.escape(self.clip) + r'_(\d+)\.csv', f)] if m]
        return '%s_%02d' % (self.clip, max(taken, default=0) + 1)

    def start(self, now):
        self.find_devices()
        self.recording = True
        self.started = now
        self.rows = []
        self.said_go = False
        self.lost_since, self.lost_said = {}, {}
        self.name = self.next_name()
        say('%s, rest' % self.name.replace('_', ' '))

    def stop(self, now):
        self.recording = False
        length = now - self.started
        # Where the arms began rising for the stop gesture: the importer cuts the take
        # there. Stopped from the keyboard, nothing is cut.
        end = self.above_since - RAISE_TIME - self.started if self.above_since else length
        if length < REST_TO + 1.0:
            say('too short, dropped')
            return
        rest = self.rest_places(self.rows)
        trackers = {s: p for s, (p, q, kind) in rest.items() if kind == 'tracker'}
        wands = {s: p for s, (p, q, kind) in rest.items() if kind == 'wand'}
        parts, problems = assign(trackers, wands, {s: q for s, (p, q, kind) in rest.items() if kind == 'wand'})
        self.write(parts, problems, max(end, REST_TO), length)
        say('saved %s, %d seconds' % (self.name.replace('_', ' '), round(length)))
        for p in problems:
            say(p)

    def rest_places(self, rows):
        """The mean place of each device over the rest window, tracked samples only, with its last turn there."""
        kinds = {serial: kind for serial, kind in self.devices.values()}
        sums = {}
        for t, serial, xyz, q, ok in rows:
            if ok and REST_FROM <= t <= REST_TO:
                s = sums.setdefault(serial, [np.zeros(3), 0, None])
                s[0] += xyz
                s[1] += 1
                s[2] = q
        return {s: (v[0] / v[1], v[2], kinds.get(s, 'tracker')) for s, v in sums.items() if v[1]}

    def write(self, parts, problems, end, length):
        path = os.path.join(self.out, self.name + '.csv')
        lost = {}
        for t, serial, xyz, q, ok in self.rows:
            if not ok:
                lost[serial] = lost.get(serial, 0) + 1
        per_device = max(1, len(self.rows) // max(1, len(self.devices)))
        with open(path, 'w') as f:
            f.write('# clip %s\n' % self.clip)
            f.write('# recorded %s\n' % datetime.datetime.now().isoformat(timespec='seconds'))
            f.write('# rate %d\n' % RATE)
            for serial, kind in sorted(self.devices.values()):
                f.write('# device %s %s %s lost %.1f%%\n' % (
                    serial, kind, parts.get(serial, '?'), 100.0 * lost.get(serial, 0) / per_device))
            f.write('# rest %.2f %.2f\n' % (REST_FROM, REST_TO))
            f.write('# end %.2f of %.2f\n' % (end, length))
            for p in problems:
                f.write('# problem %s\n' % p)
            f.write('t,part,x,y,z,qw,qx,qy,qz,ok\n')
            for t, serial, xyz, q, ok in self.rows:
                f.write('%.4f,%s,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%d\n' % (
                    t, parts.get(serial, serial), xyz[0], xyz[1], xyz[2], q[0], q[1], q[2], q[3], ok))
        print('  wrote %s (%d s, %d rows)' % (path, round(length), len(self.rows)))
        self.parts = parts

    def call_lost(self, now, seen):
        """Says which part has lost tracking, so a take can be done again."""
        for serial, kind, ok, xyz, q in seen:
            if ok:
                self.lost_since.pop(serial, None)
                continue
            since = self.lost_since.setdefault(serial, now)
            if now - since >= LOST_AFTER and now - self.lost_said.get(serial, -99) >= LOST_REPEAT:
                self.lost_said[serial] = now
                # A tracker not yet named is called by the last digits of its serial.
                name = self.parts.get(serial) or '%s %s' % (kind, ' '.join(serial[-4:]))
                say('lost ' + name.replace('_L', ' left').replace('_R', ' right'))

    def check(self, now, seen):
        if now < self.check_until - 2.0:
            return
        for serial, kind, ok, xyz, q in seen:
            if ok:
                s = self.check_samples.setdefault(serial, [np.zeros(3), 0, kind, None])
                s[0] += xyz
                s[1] += 1
                s[3] = q
        if now >= self.check_until:
            self.check_until = None
            places = {s: v[0] / v[1] for s, v in self.check_samples.items()}
            trackers = {s: p for s, p in places.items() if self.check_samples[s][2] == 'tracker'}
            wands = {s: p for s, p in places.items() if self.check_samples[s][2] == 'wand'}
            turns = {s: v[3] for s, v in self.check_samples.items() if v[2] == 'wand'}
            parts, problems = assign(trackers, wands, turns)
            self.parts = parts
            for serial, part in sorted(parts.items(), key=lambda kv: PARTS.index(kv[1])):
                p = places[serial]
                print('  %-8s %s  height %.2f m' % (part, serial, p[1]))
            say('all nine found' if not problems else '. '.join(problems))

    def status(self):
        for i, (serial, kind) in sorted(self.devices.items()):
            xyz, ok = self.last.get(serial, (np.zeros(3), False))
            print('  %-7s %s %-8s %s  height %.2f m' % (
                kind, serial, self.parts.get(serial, ''), 'ok  ' if ok else 'LOST', xyz[1]))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        'animDLs', 'mocap', datetime.date.today().isoformat())
    os.makedirs(out, exist_ok=True)
    rec = Recorder(out)
    worker = threading.Thread(target=rec.loop, daemon=True)
    worker.start()
    time.sleep(0.3)     # a few samples in, so the status shows what is tracked
    print('Recording into %s. Clip name: %s. Type n <clip> to name it.' % (out, rec.clip))
    rec.status()
    try:
        while True:
            line = input('> ').strip()
            if line == 'q':
                break
            if line.startswith('n '):
                rec.clip = re.sub(r'\W+', '_', line[2:].strip()) or 'take'
                print('  clip: %s, next take %s' % (rec.clip, rec.next_name()))
            elif line == 'c':
                say('rest pose in 3')
                time.sleep(1.0)
                rec.check_samples = {}
                rec.check_until = time.perf_counter() + 4.0
            elif line == 's':
                rec.status()
            elif line == '':
                with rec.lock:
                    rec.toggle = True
    except (EOFError, KeyboardInterrupt):
        pass
    rec.running = False
    worker.join(timeout=1.0)
    if rec.recording:
        print('  a take was running when you quit; it was not saved')
    openvr.shutdown()


if __name__ == '__main__':
    main()
