#!/usr/bin/env python3
"""
启动 Unity batchmode，导出 Android Gradle（unityLibrary）到宿主。

输出：BoluoProjectNew/AndroidProject/UnityExport/{Boluo_Android|YiShu}/unityLibrary
入口：Game.Editor.BuildTool.CIBuild.ExportAndroidGradle

用法：
    python3 run_android_export.py
环境变量：
    BOLUO_ANDROID_FLAVOR / BOLUO_CI_ANDROID_FLAVOR  skyworthUltra | yishu（默认 skyworthUltra）
    BOLUO_CI_ENV / 等见 -boluo* CLI
"""
import os
import sys
import subprocess

PROJECT = os.path.dirname(os.path.abspath(__file__))
UNITY = "/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity"
OUTPUT_DIR = os.path.join(os.path.dirname(PROJECT), "AndroidProject", "UnityExport")
LOG_FILE = os.path.join(PROJECT, "android_export.log")

BOLUO_ENV = os.environ.get("BOLUO_CI_ENV") or os.environ.get("BOLUO_ENV") or "dev"
BOLUO_VERSION_TYPE = os.environ.get("BOLUO_CI_VERSION_TYPE") or "release"
BOLUO_VERSION = os.environ.get("BOLUO_CI_VERSION") or "1.1.60"
BOLUO_ORIENTATION = os.environ.get("BOLUO_CI_ORIENTATION") or "sensor"

_FLAVOR = (
    os.environ.get("BOLUO_ANDROID_FLAVOR")
    or os.environ.get("BOLUO_CI_ANDROID_FLAVOR")
    or "skyworthUltra"
).strip()
_FLAVOR_KEY = _FLAVOR.lower().replace("_", "").replace("-", "")
if _FLAVOR_KEY in ("yishu", "yishuprojection"):
    _EXPORT_FOLDER = "YiShu"
    _FLAVOR = "yishu"
else:
    _EXPORT_FOLDER = "Boluo_Android"
    _FLAVOR = "skyworthUltra"

UNITY_LIBRARY = os.path.join(OUTPUT_DIR, _EXPORT_FOLDER, "unityLibrary")

cmd = [
    UNITY,
    "-quit",
    "-batchmode",
    "-nographics",
    "-projectPath", PROJECT,
    "-buildTarget", "Android",
    "-executeMethod", "Game.Editor.BuildTool.CIBuild.ExportAndroidGradle",
    "-boluoPlatform", "android",
    "-boluoEnv", BOLUO_ENV,
    "-boluoVersionType", BOLUO_VERSION_TYPE,
    "-boluoVersion", BOLUO_VERSION,
    "-boluoOrientation", BOLUO_ORIENTATION,
    "-boluoAndroidFlavor", _FLAVOR,
    "-boluoOutput", OUTPUT_DIR,
    "-logFile", LOG_FILE,
]

print("[runner] launching Unity Android export...", flush=True)
print("[runner] Unity:", UNITY, flush=True)
print("[runner] flavor:", _FLAVOR, "→", UNITY_LIBRARY, flush=True)
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

if not os.path.isdir(UNITY_LIBRARY):
    print(f"[runner] 导出目录不存在: {UNITY_LIBRARY}", flush=True)
    sys.exit(1)

# 导出覆盖 Gradle/CMake → 对齐宿主模块名 + CMake≥3.22.1（幂等；Unity 侧也会打）
_patch = os.path.join(
    os.path.dirname(OUTPUT_DIR), "scripts", "patch_unity_export_for_host.py"
)
if os.path.isfile(_patch):
    print(f"[runner] patch host: {_patch}", flush=True)
    patch_rc = subprocess.call([sys.executable, _patch, UNITY_LIBRARY], env=clean_env)
    if patch_rc != 0:
        print(f"[runner] host patch 失败 exit={patch_rc}", flush=True)
        sys.exit(patch_rc)
else:
    print(f"[runner] warn: 缺少 host patch 脚本: {_patch}", flush=True)

print(f"[runner] OK: {UNITY_LIBRARY}", flush=True)
