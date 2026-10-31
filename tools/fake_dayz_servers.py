"""
Fake DayZ servers for testing MM Launcher without real servers.

Answers Steam A2S_INFO and A2S_RULES queries on localhost the way a DayZ server does,
including the challenge handshake, split (multi-packet) replies and the escaped binary
mod list inside the rules. Run it, then point the launcher's settings at "localhost".

    python fake_dayz_servers.py            # serve until Ctrl+C
    python fake_dayz_servers.py --check    # also decode our own rules with dayzquery (reference)
"""
import random
import select
import socket
import struct
import sys

HOST = "127.0.0.1"
MAX_PACKET = 1248  # Source engine split size

# workshop id, mod name
MODS_DEER_ISLE = [
    (1559212036, "CF"), (2545327648, "Dabs Framework"), (2572331007, "DayZ-Expansion-Bundle"),
    (2116157322, "DayZ-Expansion-Licensed"), (1828439124, "VPPAdminTools"), (2792982069, "TerjeCore"),
    (3359676785, "TerjeSkills"), (1797720064, "WindstridesClothingPack"), (1564026768, "Community-Online-Tools"),
    (1646187754, "Code Lock"), (2443122116, "SNAFU Weapons"), (1991570984, "Mass'sManyItemOverhaul"),
]
MODS_SMALL = [(1559212036, "CF"), (2545327648, "Dabs Framework"), (1828439124, "VPPAdminTools")]


def escape(data: bytes) -> bytes:
    out = bytearray()
    for b in data:
        if b == 0x01: out += b"\x01\x01"
        elif b == 0x00: out += b"\x01\x02"
        elif b == 0xFF: out += b"\x01\x03"
        else: out.append(b)
    return bytes(out)


def mod_blob(mods, dlc_flags=0, overflow=0, description="Misfit Mercenaries test server"):
    b = bytearray([2, overflow]) + struct.pack("<H", dlc_flags)
    for _ in range(bin(dlc_flags).count("1")):
        b += struct.pack("<I", random.getrandbits(32))
    b.append(len(mods))
    for wid, name in mods:
        idb = wid.to_bytes(8, "little").rstrip(b"\x00") or b"\x00"
        idb = idb.ljust(4, b"\x00")
        nb = name.encode()
        b += struct.pack("<I", random.getrandbits(32)) + bytes([len(idb)]) + idb + bytes([len(nb)]) + nb
    b.append(0)  # signatures
    d = description.encode()
    b += bytes([len(d)]) + d
    return bytes(b)


def rules_dict(mods, dlc_flags=0):
    blob = escape(mod_blob(mods, dlc_flags))
    chunk = 120
    parts = [blob[i:i + chunk] for i in range(0, len(blob), chunk)]
    rules = {}
    for i, p in enumerate(parts, start=1):
        rules[bytes([i, len(parts)])] = p
    rules.update({b"allowedBuild": b"0", b"dedicated": b"1", b"island": b"deerisle", b"language": b"65545",
                  b"platform": b"win", b"requiredBuild": b"0", b"requiredVersion": b"129", b"timeLeft": b"15"})
    return rules


class FakeServer:
    def __init__(self, name, query_port, game_port, map_name, players, max_players, keywords,
                 mods, dlc_flags=0, challenge=True, password=False):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind((HOST, query_port))
        self.name, self.game_port, self.map = name, game_port, map_name
        self.players, self.max_players, self.keywords = players, max_players, keywords
        self.rules = rules_dict(mods, dlc_flags)
        self.challenge = challenge
        self.password = password
        self.token = struct.pack("<I", random.getrandbits(32))
        self.split_id = 1000 + query_port

    def info(self):
        p = bytearray(b"\xFF\xFF\xFF\xFFI\x11")
        for s in (self.name, self.map, "dayz", "DayZ"):
            p += s.encode() + b"\x00"
        p += struct.pack("<h", 0) + bytes([self.players, self.max_players, 0]) + b"dw"
        p += bytes([1 if self.password else 0, 0]) + b"1.29.162510\x00"
        p += bytes([0x80 | 0x10 | 0x20 | 0x01]) + struct.pack("<H", self.game_port)
        p += struct.pack("<Q", 90000000000000000 + self.game_port) + self.keywords.encode() + b"\x00"
        p += struct.pack("<Q", 221100)
        return bytes(p)

    def rules_payload(self):
        p = bytearray(b"\xFF\xFF\xFF\xFFE") + struct.pack("<H", len(self.rules))
        for k, v in self.rules.items():
            p += k + b"\x00" + v + b"\x00"
        return bytes(p)

    def send(self, payload, addr):
        if len(payload) <= 1400:
            self.sock.sendto(payload, addr)
            return
        chunks = [payload[i:i + MAX_PACKET] for i in range(0, len(payload), MAX_PACKET)]
        order = list(range(len(chunks)))
        random.shuffle(order)  # arrive out of order on purpose
        for n in order:
            hdr = b"\xFE\xFF\xFF\xFF" + struct.pack("<iBBH", self.split_id, len(chunks), n, MAX_PACKET)
            self.sock.sendto(hdr + chunks[n], addr)

    def handle(self):
        data, addr = self.sock.recvfrom(2048)
        if data.startswith(b"\xFF\xFF\xFF\xFFTSource Engine Query\x00"):
            given = data[25:29]
            if self.challenge and given != self.token:
                self.sock.sendto(b"\xFF\xFF\xFF\xFFA" + self.token, addr)
            else:
                self.sock.sendto(self.info(), addr)
        elif data.startswith(b"\xFF\xFF\xFF\xFFV"):
            if data[5:9] != self.token:
                self.sock.sendto(b"\xFF\xFF\xFF\xFFA" + self.token, addr)
            else:
                self.send(self.rules_payload(), addr)


class JunkResponder:
    """Something on a port in the range that isn't a game server."""
    def __init__(self, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind((HOST, port))

    def handle(self):
        _, addr = self.sock.recvfrom(2048)
        self.sock.sendto(b"HTTP/1.1 400 nope\r\n", addr)


def build():
    many = MODS_DEER_ISLE * 7  # 84 mods: big enough to force a split reply
    many = many[:80]
    return [
        FakeServer("Misfit Mercenaries | Deer Isle | PvE | Expansion | Trader", 2305, 2302, "deerisle", 37, 60,
                   "battleye,external,privHive,shard,lqs2,etm4.000000,entm8.000000,mod,14:32",
                   MODS_DEER_ISLE, challenge=True),
        FakeServer("Misfit Mercenaries | Badlands | Nasdara | PvE", 2315, 2312, "nasdara", 12, 50,
                   "battleye,no3rd,external,privHive,shard,lqs0,etm6.000000,entm12.000000,mod,22:05",
                   MODS_SMALL, dlc_flags=4, challenge=False, password=True),
        FakeServer("Misfit Mercenaries | Sakhal | Hardcore Survival", 2325, 2322, "sakhal", 0, 40,
                   "battleye,no3rd,external,privHive,shard,lqs0,etm2.000000,entm4.000000,mod,07:41",
                   many, dlc_flags=2, challenge=True, password=True),
    ], [JunkResponder(2330)]


def check(servers):
    sys.path.insert(0, sys.argv[sys.argv.index("--check") + 1]) if len(sys.argv) > sys.argv.index("--check") + 1 else None
    import dayzquery
    for s in servers:
        r = dayzquery.dayz_rules_decode(s.rules, "utf-8")
        print(f"[reference decoder] {s.name}: {len(r.mods)} mods, dlc={r.dlc_flags}, first={r.mods[0].workshop_id} {r.mods[0].name}")


if __name__ == "__main__":
    servers, junk = build()
    if "--check" in sys.argv:
        check(servers)
    socks = {s.sock: s for s in servers + junk}
    print(f"Fake DayZ servers listening on {HOST}: " + ", ".join(str(s.sock.getsockname()[1]) for s in servers), flush=True)
    while True:
        ready, _, _ = select.select(list(socks), [], [])
        for r in ready:
            try:
                socks[r].handle()
            except Exception as e:
                print("error:", e, flush=True)
