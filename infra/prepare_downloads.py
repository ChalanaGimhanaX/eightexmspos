import os
import shutil
import zipfile

output_dir = "/root/workspace/downloads"
os.makedirs(output_dir, exist_ok=True)

# 1. Single-file executables
single_file_src = "/root/workspace/enightx Pos System/apps/desktop/src/Enightx.Pos.Wpf/bin/Release/net8.0-windows/win-x64/publish/Enightx.Pos.Wpf.exe"
single_file_dst1 = os.path.join(output_dir, "EnightxPos.exe")
single_file_dst2 = os.path.join(output_dir, "Enightx.Pos.Wpf.exe")

shutil.copy2(single_file_src, single_file_dst1)
shutil.copy2(single_file_src, single_file_dst2)
print("Copied single-file executables:", single_file_dst1, single_file_dst2)

# 2. Bundled zip containing unbundled publish (Enightx.Pos.Wpf.exe + EnightxPos.exe copy + all DLLs)
unbundled_src = "/tmp/wpf-publish"
zip_path = os.path.join(output_dir, "EnightxPos-win-x64.zip")

print("Creating zip bundle at", zip_path)
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(unbundled_src):
        for f in files:
            full_path = os.path.join(root, f)
            rel_path = os.path.relpath(full_path, unbundled_src)
            z.write(full_path, arcname=rel_path)
    # Also add an alias EnightxPos.exe in root of zip for convenience
    z.write(os.path.join(unbundled_src, "Enightx.Pos.Wpf.exe"), arcname="EnightxPos.exe")

print("Zip bundle created successfully! Size:", os.path.getsize(zip_path))
for f in os.listdir(output_dir):
    p = os.path.join(output_dir, f)
    print(f"{f}: {os.path.getsize(p):,} bytes")

