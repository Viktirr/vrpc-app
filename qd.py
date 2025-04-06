# qd as in Quick Download (as opposed to quick install because I want to be able to type this quickly)
# Used Gemini for this script

import os
import shutil
from pathlib import Path
import sys

def copy_files_to_appdata():
    """
    Copies all files from ./bin/Debug/net6.0 to %APPDATA%/VRPCApp.
    """
    try:
        # 1. Define the source directory relative to the script's location
        #    Use resolve() to get an absolute path, which is generally safer.
        source_dir = Path("./bin/Debug/net6.0").resolve()

        # 2. Define the destination directory using %APPDATA%
        appdata_path = os.getenv('APPDATA')
        if not appdata_path:
            print("Error: APPDATA environment variable not found.")
            sys.exit(1) # Exit if APPDATA is not set

        destination_dir = Path(appdata_path) / "VRPCApp"

        # 3. Check if the source directory exists
        if not source_dir.is_dir():
            print(f"Error: Source directory not found: {source_dir}")
            sys.exit(1) # Exit if source doesn't exist

        # 4. Create the destination directory if it doesn't exist
        #    parents=True creates any necessary parent directories.
        #    exist_ok=True prevents an error if the directory already exists.
        print(f"Ensuring destination directory exists: {destination_dir}")
        destination_dir.mkdir(parents=True, exist_ok=True)

        # 5. Iterate through items in the source directory and copy files
        print(f"Starting copy from {source_dir} to {destination_dir}...")
        file_copied_count = 0
        for item in source_dir.iterdir():
            if item.is_file(): # Only copy files, ignore subdirectories
                destination_file_path = destination_dir / item.name
                try:
                    print(f"  Copying {item.name}...")
                    # shutil.copy2 attempts to preserve metadata (like modification time)
                    shutil.copy2(item, destination_file_path)
                    file_copied_count += 1
                except Exception as copy_e:
                    print(f"    Error copying {item.name}: {copy_e}")
            # else:
            #     print(f"  Skipping directory: {item.name}") # Optional: Log skipped dirs

        print(f"\nCopy complete. {file_copied_count} file(s) copied.")

    except Exception as e:
        print(f"\nAn unexpected error occurred: {e}")
        sys.exit(1)

# --- Run the function ---
if __name__ == "__main__":
    copy_files_to_appdata()