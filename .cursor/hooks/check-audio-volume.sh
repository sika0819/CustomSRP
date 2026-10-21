#!/bin/bash
# Pre-commit check: block the WIAEngine volume mistakes that fail *silently*.
#
# 1. Passing "SFX_Volume" to a parameter setter — wrong RTPC name. The engine
#    silently drops unknown RTPC names, so the volume knob does nothing and
#    nothing is logged. The correct name is Fx_Volume.
# 2. Writing a volume through the raw WanosNative.SetParameter channel — that
#    channel speaks the engine's 0-100 scale, while the UI speaks 0-1. Volume
#    must go through WanosEngine.SetVolumeParameter (it does the x100).
#
# Only call sites are matched, so comments and log messages may still mention
# the wrong name.
#
# See .cursor/rules/wanos-audio.mdc and Assets/WanosAudio/README.md.
input=$(cat)

# Keep only added/changed code lines, dropping line comments and block-comment bodies.
code_only() {
    git diff --cached --staged 2>/dev/null \
        | grep -E "^\+" \
        | grep -v "^+++" \
        | sed -E 's@//.*$@@' \
        | grep -vE "^\+\s*(\*|/\*|///)"
}

if code_only | grep -E "(SetVolumeParameter|SetParameter|SetGlobalParameter|SetParameterEngineValue)\(\s*\"[A-Za-z_]*SFX_Volume\"" > /dev/null 2>&1; then
    echo '{"permission": "ask", "user_message": "检测到 SFX_Volume：引擎里的音效音量 RTPC 正确名字是 Fx_Volume（不是 SFX_Volume）。写错名字引擎会静默丢弃，音量不会有任何变化。是否强制提交？", "agent_message": "警告: 暂存代码使用了错误的 RTPC 名 SFX_Volume"}'
    exit 0
fi

if code_only | grep -E "(^|[^A-Za-z])SetParameter\(\s*\"[A-Za-z_]*[Vv]olume\"" > /dev/null 2>&1; then
    echo '{"permission": "ask", "user_message": "检测到直接用 WanosNative.SetParameter 写音量：引擎 RTPC 量程是 0-100，而 UI 是 0-1。请改用 WanosEngine.SetVolumeParameter(name, ui0to1)（内部会 x100）。是否强制提交？", "agent_message": "警告: 暂存代码绕过了音量量纲换算"}'
    exit 0
fi

echo '{"permission": "allow"}'
exit 0
