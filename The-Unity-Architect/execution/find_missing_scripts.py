import os
import sys

def find_missing_scripts(directory):
    print(f"--- Scanning project for Missing Scripts: {directory} ---\n")
    
    # Unity saves scenes and prefabs in YAML text format
    target_extensions = ('.prefab', '.unity')
    missing_script_pattern = "m_Script: {fileID: 0}"
    
    files_with_missing_scripts = {}

    # Traverse all project folders
    for root, _, files in os.walk(directory):
        # Ignore hidden or temporary Unity folders for speed
        if '.git' in root or 'Library' in root or 'Temp' in root or 'Logs' in root:
            continue
            
        for file in files:
            if file.endswith(target_extensions):
                file_path = os.path.join(root, file)
                missing_count = 0
                
                try:
                    # Read the file line by line to avoid memory issues on giant scenes
                    with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                        for line in f:
                            if missing_script_pattern in line:
                                missing_count += 1
                                
                    if missing_count > 0:
                        # Save relative path for clean output
                        rel_path = os.path.relpath(file_path, directory)
                        files_with_missing_scripts[rel_path] = missing_count
                except Exception as e:
                    print(f"Error reading: {file_path} - {e}")

    # Final report
    if not files_with_missing_scripts:
        print("✅ Excellent! No 'Missing Scripts' found in the analyzed Prefabs or Scenes.")
    else:
        print(f"⚠️ ALERT! Broken dependencies found in {len(files_with_missing_scripts)} files:\n")
        
        # Sort alphabetically for better readability
        for file, count in sorted(files_with_missing_scripts.items()):
            print(f"  - {file} ({count} missing scripts)")
            
        print("\n💡 AI Directive: Instruct the user to open these specific files in the Unity Editor and clean up orphaned components before proceeding with code debugging.")

if __name__ == "__main__":
    # If an argument is provided, use that path. Otherwise, use current directory (Assets).
    target_dir = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    find_missing_scripts(target_dir)