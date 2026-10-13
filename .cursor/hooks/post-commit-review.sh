#!/bin/bash
# Post-commit: Show review hint
input=$(cat)
exit_code=$(echo "$input" | jq -r '.exit_code // empty')

if [[ "$exit_code" == "0" ]]; then
    commit=$(git rev-parse HEAD 2>/dev/null | head -c 8)
    echo ""
    echo "✅ 提交成功: $commit"
fi

echo '{"continue": true}'
exit 0
