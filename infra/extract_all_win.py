import zipfile
import os

zip_path = "/tmp/dotnet-win.zip"
dest_dir = "/opt/dotnet-win"
os.makedirs(dest_dir, exist_ok=True)

print("Extracting", zip_path, "to", dest_dir)
with zipfile.ZipFile(zip_path) as z:
    z.extractall(dest_dir)
print("Done extracting Windows .NET SDK!")

