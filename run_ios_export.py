#!/usr/bin/env python3
"""
启动 Unity batchmode，导出 iOS Xcode 工程到宿主。

输出：BoluoProjectNew/IOSProject/Boluo_Unity
入口：Game.Editor.BuildTool.CIBuild.BuildiOS

须带 -buildTarget iOS，否则部分 PostProcessBuild 不会编译进 Editor。

用法：
    python3 run_ios_export.py
"""
import os
import sys
import subprocess

PROJECT = os.path.dirname(os.path.abspath(__file__))
UNITY = "/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity"
OUTPUT_DIR = os.path.join(os.path.dirname(PROJECT), "IOSProject")
LOG_FILE = os.path.join(PROJECT, "ios_export.log")

BOLUO_ENV = os.environ.get("BOLUO_CI_ENV") or os.environ.get("BOLUO_ENV") or "dev"
BOLUO_VERSION_TYPE = os.environ.get("BOLUO_CI_VERSION_TYPE") or "release"
BOLUO_VERSION = os.environ.get("BOLUO_CI_VERSION") or "1.1.60"

cmd = [
    UNITY,
    "-quit",
    "-batchmode",
    "-nographics",
    "-buildTarget", "iOS",
    "-projectPath", PROJECT,
    "-executeMethod", "Game.Editor.BuildTool.CIBuild.BuildiOS",
    "-boluoPlatform", "ios",
    "-boluoEnv", BOLUO_ENV,
    "-boluoVersionType", BOLUO_VERSION_TYPE,
    "-boluoVersion", BOLUO_VERSION,
    "-boluoOutput", OUTPUT_DIR,
    "-logFile", LOG_FILE,
]

print("[runner] launching Unity iOS export...", flush=True)
print("[runner] Unity:", UNITY, flush=True)
print("[runner] output:", os.path.join(OUTPUT_DIR, "Boluo_Unity"), flush=True)
print("[runner] log:", LOG_FILE, flush=True)

clean_env = {}
for k, v in os.environ.items():
    try:
        k.encode("utf-8")
        v.encode("utf-8")
    except Exception:
        print(f"[runner] dropping non-utf8 env var: {k}", flush=True)
        continue
    clean_env[k] = v

try:
    open(LOG_FILE, "w").close()
except Exception:
    pass

proc = subprocess.Popen(
    cmd,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
    stdin=subprocess.DEVNULL,
    start_new_session=True,
    close_fds=True,
    env=clean_env,
)
print(f"[runner] Unity pid={proc.pid}，等待导出完成...", flush=True)
exit_code = proc.wait()
if exit_code != 0:
    print(f"[runner] Unity 导出失败 exit={exit_code}，详见 {LOG_FILE}", flush=True)
    sys.exit(exit_code)

export_dir = os.path.join(OUTPUT_DIR, "Boluo_Unity")
if not os.path.isdir(export_dir):
    print(f"[runner] 导出目录不存在: {export_dir}", flush=True)
    sys.exit(1)

strip_script = os.path.join(OUTPUT_DIR, "scripts", "strip_boluo_kit_unity_export.py")
if os.path.isfile(strip_script):
    print(f"[runner] post-process: {strip_script}", flush=True)
    strip_rc = subprocess.call([sys.executable, strip_script], env=clean_env)
    if strip_rc != 0:
        print(f"[runner] strip_boluo_kit 失败 exit={strip_rc}", flush=True)
        sys.exit(strip_rc)
else:
    print(f"[runner] warn: 未找到 strip 脚本 {strip_script}", flush=True)

print(f"[runner] OK: {export_dir}", flush=True)
