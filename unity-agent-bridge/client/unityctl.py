#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unity Agent Bridge 命令行客户端（Python 3.7+，仅标准库）。

示例:
    python unityctl.py ping
    python unityctl.py hierarchy --depth 2
    python unityctl.py go.create '{"type": "cube", "name": "Box"}'
    python unityctl.py call go.set '{"target": "Box", "position": [0, 2, 0]}'
    python unityctl.py exec "return Selection.activeGameObject ? Selection.activeGameObject.name : null;" --wait
    python unityctl.py commands          # 列出全部命令

令牌自动从 <项目>/Library/UnityAgentBridge/token.txt 读取；
也可以用 --token 或环境变量 UNITY_BRIDGE_TOKEN 指定。
"""

import argparse
import http.client
import json
import os
import sys
import time
import urllib.error
import urllib.request

DEFAULT_PORT = 17899


def _utf8_stdout():
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass


def find_project(explicit):
    if explicit:
        return os.path.abspath(explicit)
    candidates = []
    env = os.environ.get("UNITY_BRIDGE_PROJECT")
    if env:
        candidates.append(env)
    candidates.append(os.getcwd())
    for candidate in candidates:
        if candidate and os.path.isdir(os.path.join(candidate, "Library", "UnityAgentBridge")):
            return candidate
    return None


def load_settings(project):
    port, token = DEFAULT_PORT, ""
    if not project:
        return port, token

    base = os.path.join(project, "Library", "UnityAgentBridge")
    settings_file = os.path.join(base, "settings.json")
    if os.path.isfile(settings_file):
        try:
            with open(settings_file, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            port = int(data.get("port") or port)
            token = data.get("token") or token
        except Exception:
            pass

    token_file = os.path.join(base, "token.txt")
    if os.path.isfile(token_file):
        try:
            with open(token_file, "r", encoding="utf-8") as handle:
                file_token = handle.read().strip()
            if file_token:
                token = file_token
        except Exception:
            pass
    return port, token


def call(port, token, command, args=None, timeout=120.0, retries=3):
    """POST /cmd and return (status, body). Retries transient loopback hiccups."""
    last = None
    for attempt in range(retries):
        try:
            return _call_once(port, token, command, args, timeout)
        except ConnectionError as error:
            last = error
            if attempt < retries - 1:
                time.sleep(0.3)
    raise last


def _call_once(port, token, command, args=None, timeout=120.0):
    """POST /cmd and return (status, body). Never raises for HTTP errors."""
    payload = {"cmd": command, "timeoutMs": int(timeout * 1000)}
    if args is not None:
        payload["args"] = args
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    request = urllib.request.Request("http://127.0.0.1:%d/cmd" % port, data=data, method="POST")
    request.add_header("Content-Type", "application/json; charset=utf-8")
    if token:
        request.add_header("X-Bridge-Token", token)

    try:
        with urllib.request.urlopen(request, timeout=timeout + 30) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")
    except http.client.RemoteDisconnected as error:
        raise ConnectionError("连接被对端关闭: %s" % error)
    except urllib.error.URLError as error:
        raise ConnectionError(str(error.reason))
    except OSError as error:
        raise ConnectionError(str(error))


def print_json(text, compact=False):
    try:
        data = json.loads(text)
    except ValueError:
        print(text)
        return None
    if compact:
        print(json.dumps(data, ensure_ascii=False, separators=(",", ":")))
    else:
        print(json.dumps(data, ensure_ascii=False, indent=2))
    return data


def wait_for_job(port, token, job_id, timeout):
    deadline = time.time() + timeout
    print("# 等待编译并执行任务 %s ..." % job_id, file=sys.stderr)
    while time.time() < deadline:
        time.sleep(0.8)
        try:
            status, body = call(port, token, "cs.status", {"jobId": job_id}, 30)
        except ConnectionError:
            print("# Unity 正在重载程序集，继续等待 ...", file=sys.stderr)
            continue
        if status >= 400:
            continue
        try:
            data = json.loads(body)
        except ValueError:
            continue
        result = data.get("result") or {}
        if result.get("status") == "pending":
            continue
        print_json(body)
        return 0 if result.get("ok") else 1
    print("# 超时：任务 %s 未在 %.0f 秒内完成" % (job_id, timeout), file=sys.stderr)
    return 1


def main(argv=None):
    _utf8_stdout()

    parser = argparse.ArgumentParser(
        prog="unityctl",
        description="Unity Agent Bridge 命令行客户端",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("command", help="命令名，例如 ping / hierarchy / go.create / exec / commands")
    parser.add_argument("args", nargs="?", help="参数 JSON（exec 时是 C# 代码）")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--token", default="")
    parser.add_argument("--project", default="")
    parser.add_argument("--args-file", default="", help="从文件读取参数 JSON（避免命令行转义）")
    parser.add_argument("--timeout", type=float, default=120.0, help="秒")
    parser.add_argument("--depth", type=int, default=None, help="hierarchy 的简写参数")
    parser.add_argument("--wait", action="store_true", help="cs.execute/exec 后轮询结果")
    parser.add_argument("--compact", action="store_true")
    parser.add_argument("--raw", action="store_true")

    options = parser.parse_args(argv)

    project = find_project(options.project)
    port, token = load_settings(project)
    if options.port:
        port = options.port
    if options.token:
        token = options.token
    elif os.environ.get("UNITY_BRIDGE_TOKEN"):
        token = os.environ["UNITY_BRIDGE_TOKEN"]

    command = options.command
    if command in ("help", "commands"):
        command = "commands.list"

    raw_args = options.args
    if options.args_file:
        with open(options.args_file, "r", encoding="utf-8") as handle:
            raw_args = handle.read()
    if raw_args is None and not sys.stdin.isatty():
        piped = sys.stdin.read().strip()
        if piped:
            raw_args = piped

    args = None
    is_exec = False

    if command == "exec":
        if not raw_args:
            parser.error("exec 需要一段 C# 代码作为参数")
        args = {"code": raw_args}
        command = "cs.execute"
        options.wait = True
        is_exec = True
    elif command == "hierarchy" and options.depth is not None and not raw_args:
        args = {"depth": options.depth}
    elif raw_args:
        try:
            args = json.loads(raw_args)
        except ValueError as error:
            parser.error("参数不是合法 JSON: %s" % error)

    try:
        status, body = call(port, token, command, args, options.timeout)
    except ConnectionError as error:
        print("[连接失败] %s" % error, file=sys.stderr)
        print("确认 Unity 已打开、已启用 Tools/Unity Agent Bridge，且端口为 %d。" % port, file=sys.stderr)
        return 2

    exit_code = 0
    if options.raw:
        print(body)
        data = None
    else:
        data = print_json(body, options.compact)

    if data and data.get("ok") is False:
        exit_code = 1
        print("[失败] %s (%s)" % (data.get("error"), data.get("code")), file=sys.stderr)

    if options.wait and data and data.get("result", {}).get("jobId"):
        return wait_for_job(port, token, data["result"]["jobId"], options.timeout)

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
