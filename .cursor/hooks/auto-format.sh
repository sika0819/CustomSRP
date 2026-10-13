#!/bin/bash
# Auto-format C# files after edit
input=$(cat)
file=$(echo "$input" | jq -r '.file // empty')

if [[ -n "$file" && "$file" == *.cs ]] && command -v dotnet &> /dev/null; then
    dotnet format --severity warn "$file" 2>/dev/null &
fi

echo '{"continue": true}'
exit 0
