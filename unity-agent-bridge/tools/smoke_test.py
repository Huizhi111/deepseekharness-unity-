#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""End-to-end smoke test driven from Python (urllib)."""

import json
import sys
import urllib.error
import urllib.request

PORT = int(sys.argv[1])
TOKEN = sys.argv[2] if len(sys.argv) > 2 else ""

passed = 0
failed = 0
failures = []


def call(cmd, args=None, timeout=30.0, retries=3):
    last = (0, {"error": "no attempt"})
    for _ in range(retries):
        last = _call_once(cmd, args, timeout)
        if last[0] != 0:
            return last
    return last


def _call_once(cmd, args=None, timeout=30.0):
    payload = {"cmd": cmd, "timeoutMs": int(timeout * 1000)}
    if args is not None:
        payload["args"] = args
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request("http://127.0.0.1:%d/cmd" % PORT, data=data, method="POST")
    request.add_header("Content-Type", "application/json")
    if TOKEN:
        request.add_header("X-Bridge-Token", TOKEN)
    try:
        with urllib.request.urlopen(request, timeout=timeout + 10) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", "replace")
        try:
            return error.code, json.loads(body)
        except ValueError:
            return error.code, {"raw": body}
    except Exception as error:  # noqa: BLE001
        return 0, {"error": str(error)}


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print("  [OK]   %s" % name)
    else:
        failed += 1
        failures.append("%s %s" % (name, detail))
        print("  [FAIL] %s %s" % (name, detail))


def main():
    print("Unity Agent Bridge smoke test (python)  port=%d" % PORT)

    status, data = call("ping")
    check("ping", status == 200 and data.get("ok"), "status=%s %s" % (status, data))

    status, data = call("status")
    check("status", status == 200 and data.get("ok") and data["result"].get("unity"), "")

    status, data = call("commands.list")
    count = len(data.get("result") or [])
    check("commands.list", count >= 40, "count=%d" % count)

    status, data = call("scene.info")
    check("scene.info", data.get("ok"), "")

    status, data = call("go.create", {"type": "cube", "name": "__py_smoke"})
    check("go.create", data.get("ok"), "")
    object_id = (data.get("result") or {}).get("id")

    if object_id is None:
        print("cannot continue: no object id")
        return 1

    status, data = call("go.set", {"target": "#%s" % object_id, "position": [5, 1.5, -2]})
    check("go.set", data.get("ok"), "")

    status, data = call("go.get", {"target": "#%s" % object_id})
    position = ((data.get("result") or {}).get("transform") or {}).get("position") or []
    ok = len(position) == 3 and abs(position[0] - 5) < 0.01 and abs(position[1] - 1.5) < 0.01 and abs(position[2] + 2) < 0.01
    check("go.get position", ok, str(position))

    status, data = call("comp.add", {"target": "#%s" % object_id, "type": "Rigidbody"})
    check("comp.add", data.get("ok"), "")

    status, data = call("comp.set", {"target": "#%s" % object_id, "type": "Rigidbody", "field": "m_Mass", "value": 3.5})
    check("comp.set", data.get("ok"), str(data.get("error") or data)[:300])

    status, data = call("comp.get", {"target": "#%s" % object_id, "type": "Rigidbody"})
    properties = (data.get("result") or {}).get("properties") or []
    mass = next((p for p in properties if p.get("path") == "m_Mass"), None)
    check("comp.get m_Mass", mass is not None and abs(float(mass["value"]) - 3.5) < 0.001,
          "value=%s props=%d" % (mass and mass.get("value"), len(properties)))

    status, data = call("hierarchy", {"depth": 1})
    check("hierarchy", data.get("ok"), "")

    status, data = call("console.get", {"count": 5})
    check("console.get", data.get("ok"), str(data.get("error") or data)[:300])

    status, data = call("asset.createMaterial", {"path": "Assets/__PySmoke/Smoke.mat", "color": "#FF5500"})
    check("asset.createMaterial", data.get("ok"), str(data.get("error")))

    status, data = call("asset.info", {"path": "Assets/__PySmoke/Smoke.mat"})
    check("asset.info", data.get("ok"), str(data.get("error") or data)[:300])

    status, data = call("batch", {"calls": [{"cmd": "ping"}, {"cmd": "scene.info"}]})
    check("batch", data.get("ok") and len((data.get("result") or {}).get("results") or []) == 2,
          str(data.get("error") or data)[:300])

    status, data = call("unknown.command")
    check("unknown command", data.get("code") == "unknown_command", str(data))

    status, data = call("go.delete", {"target": "#%s" % object_id})
    check("go.delete", data.get("ok"), "")

    call("asset.delete", {"path": "Assets/__PySmoke/Smoke.mat"})

    print("")
    print("PASSED %d / %d" % (passed, passed + failed))
    for item in failures:
        print("  FAILED: %s" % item)
    print("SMOKE OK" if failed == 0 else "SMOKE FAILED")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
