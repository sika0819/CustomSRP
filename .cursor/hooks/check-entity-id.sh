#!/bin/bash
# Pre-commit check: Ensure no deprecated GetInstanceID() usage
input=$(cat)

if git diff --cached --staged 2>/dev/null | grep -E "GetInstanceID\(\)" > /dev/null 2>&1; then
    echo '{"permission": "ask", "user_message": "检测到使用废弃的 GetInstanceID()。请改用 GetEntityId()。是否强制提交？", "agent_message": "警告: 暂存代码使用了废弃 API"}'
    exit 0
fi

echo '{"permission": "allow"}'
exit 0
