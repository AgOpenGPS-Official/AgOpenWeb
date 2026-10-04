"""RTCM 3 framing shared by the bench scripts (same rules as RtcmFramer in the app)."""

_TABLE = []
for _i in range(256):
    _c = _i << 16
    for _ in range(8):
        _c <<= 1
        if _c & 0x1000000:
            _c ^= 0x1864CFB
    _TABLE.append(_c & 0xFFFFFF)


def crc24q(data):
    c = 0
    for b in data:
        c = ((c << 8) & 0xFFFFFF) ^ _TABLE[(c >> 16) ^ b]
    return c


def is_observation(t):
    return 1001 <= t <= 1004 or 1009 <= t <= 1012 or (1071 <= t <= 1137 and 1 <= t % 10 <= 7)


def frame(stream, times=None):
    """Split a byte stream into RTCM messages.

    Returns (messages, bad, skipped): messages is a list of (time, type, length); bad the
    times of failed checksums; skipped the times of bytes outside any whole message. `times`
    gives the arrival time of each byte (optional).
    """
    msgs, bad, skipped = [], [], []
    p, n = 0, len(stream)
    at = (lambda i: times[i]) if times is not None else (lambda i: 0.0)
    while p < n:
        if stream[p] != 0xD3 or (n - p >= 2 and stream[p + 1] & 0xFC):
            skipped.append(at(p)); p += 1; continue
        if n - p < 3:
            break
        length = ((stream[p + 1] & 3) << 8) | stream[p + 2]
        total = length + 6
        if n - p < total:
            break
        if crc24q(stream[p:p + total - 3]) == int.from_bytes(stream[p + total - 3:p + total], "big"):
            mtype = (stream[p + 3] << 4) | (stream[p + 4] >> 4) if length >= 2 else -1
            msgs.append((at(p + total - 1), mtype, total)); p += total
        else:
            bad.append(at(p)); skipped.append(at(p)); p += 1
    return msgs, bad, skipped


class FixWatch:
    """Listens to the module's position sentences on UDP 9999 beside the app.

    `fixed_for()` is how long the receiver has reported RTK Fixed (quality 4) without a
    break, in seconds; 0 when it is not Fixed. The bench waits on it so that episodes start
    from a converged receiver: time to Fixed varies a lot from run to run.
    """

    def __init__(self):
        import socket, threading
        self.fix, self.age, self._since = 0, 0.0, None
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        for opt in (socket.SO_REUSEADDR, socket.SO_REUSEPORT, socket.SO_BROADCAST):
            s.setsockopt(socket.SOL_SOCKET, opt, 1)
        s.bind(("", 9999))
        self._sock = s
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        import time
        while True:
            d, _ = self._sock.recvfrom(2048)
            if d[:6] not in (b"$PAOGI", b"$PANDA"):
                continue
            f = d.decode("ascii", "replace").split(",")
            try:
                self.fix, self.age = int(f[6]), float(f[10] or 0)
            except (ValueError, IndexError):
                continue
            if self.fix != 4:
                self._since = None
            elif self._since is None:
                self._since = time.time()

    def fixed_for(self):
        import time
        return 0.0 if self._since is None else time.time() - self._since
