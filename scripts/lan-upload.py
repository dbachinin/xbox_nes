#!/usr/bin/env python3
"""Send .nes files from Ubuntu to the Xbox app's LAN Import page.

usage: scripts/lan-upload.py XBOX_ADDRESS CODE FILE.nes [...]
  XBOX_ADDRESS  as shown on the LAN Import page, e.g. 192.168.1.50 or 192.168.1.50:8765
  CODE          the 6-digit code shown on the TV

Files are validated locally first (scripts/validate-rom.py rules); invalid ones are skipped.
"""
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location("validate_rom", os.path.join(os.path.dirname(os.path.abspath(__file__)), "validate-rom.py"))
validate_rom = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(validate_rom)


def upload(base, code, path):
    with open(path, "rb") as f:
        data = f.read(validate_rom.MAX_ROM_BYTES + 1)
    status, info = validate_rom.check(data)
    if status != "ok":
        return f"skipped ({status} {info.get('reason', '')}{' mapper ' + str(info['mapper']) if 'mapper' in info else ''})"
    query = urllib.parse.urlencode({"code": code, "name": os.path.basename(path)})
    req = urllib.request.Request(f"{base}/upload?{query}", data=data, method="PUT",
                                 headers={"Content-Type": "application/octet-stream"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        try:
            body = json.loads(e.read().decode())
        except ValueError:
            return f"HTTP {e.code}"
    except urllib.error.URLError as e:
        return f"cannot reach the Xbox ({e.reason}); is the LAN Import page open?"
    return body.get("message", body.get("status", "?"))


def main(argv):
    if len(argv) < 4:
        print(__doc__.strip())
        return 2
    addr, code = argv[1], argv[2]
    if ":" not in addr:
        addr += ":8765"
    base = "http://" + addr
    ok = True
    for path in argv[3:]:
        result = upload(base, code, path)
        print(f"{path}: {result}")
        ok &= result in ("Imported", "Already in library")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
