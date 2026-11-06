import os
import sys
import platform
import re

def get_default_log_path():
    """Gets the default Editor.log path based on the operating system."""
    system = platform.system()
    if system == "Windows":
        return os.path.expandvars(r"%LOCALAPPDATA%\Unity\Editor\Editor.log")
    elif system == "Darwin": # macOS
        return os.path.expanduser("~/Library/Logs/Unity/Editor.log")
    elif system == "Linux":
        return os.path.expanduser("~/.config/unity3d/Editor.log")
    else:
        return None

def parse_log(file_path, output_limit=50):
    """Extracts errors and exceptions from the log."""
    if not os.path.exists(file_path):
        print(f"Error: Log file not found at: {file_path}")
        return

    print(f"--- Analyzing Unity Editor Log: {file_path} ---\n")
    
    error_pattern = re.compile(r"(Exception|Error|NullReferenceException|Assertion failed)", re.IGNORECASE)
    
    extracted_errors = []
    current_error = []
    capturing_stack = False

    with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()

    for line in lines:
        # Clean newline
        line = line.rstrip()

        # If an error keyword is found, start capturing
        if error_pattern.search(line):
            if current_error: # Save previous error if exists
                extracted_errors.append("\n".join(current_error))
                current_error = []
            
            current_error.append(line)
            capturing_stack = True
            continue
        
        # If capturing stack trace
        if capturing_stack:
            # Unity stack traces usually start with "at " or file paths
            if line.strip().startswith("at ") or ".cs:" in line or "UnityEngine." in line:
                current_error.append(line)
            elif line.strip() == "":
                # Empty line often means end of error block
                capturing_stack = False
            else:
                # If the line doesn't seem part of the stack trace, stop capturing
                capturing_stack = False

    # Add the last captured error
    if current_error:
        extracted_errors.append("\n".join(current_error))

    # Remove duplicates (common in Unity logs)
    unique_errors = list(dict.fromkeys(extracted_errors))

    if not unique_errors:
        print("✅ No critical exceptions or errors found in the analyzed log.")
        return

    print(f"🔥 Found {len(unique_errors)} unique errors. Showing the last {output_limit}:\n")
    
    # Show only the last 'output_limit' errors to save tokens
    for i, error in enumerate(unique_errors[-output_limit:], 1):
        print(f"--- ERROR {i} ---")
        print(f"{error}\n")

if __name__ == "__main__":
    # Allows passing the log path as an argument, or use the default
    log_path = sys.argv[1] if len(sys.argv) > 1 else get_default_log_path()
    
    if log_path:
        parse_log(log_path)
    else:
        print("Could not determine the default log path for this operating system.")
        print("Usage: python parse_editor_log.py <path_to_log_file>")