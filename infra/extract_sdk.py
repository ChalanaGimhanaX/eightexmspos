import zipfile
import os
import shutil

zip_path = "/tmp/dotnet-win.zip"
dotnet_root = "/usr/lib/dotnet"

with zipfile.ZipFile(zip_path) as z:
    for member in z.infolist():
        # Extract Sdks/Microsoft.NET.Sdk.WindowsDesktop
        if "sdk/8.0.401/Sdks/Microsoft.NET.Sdk.WindowsDesktop" in member.filename:
            rel_path = member.filename.replace("sdk/8.0.401/Sdks/Microsoft.NET.Sdk.WindowsDesktop", "")
            if rel_path.startswith("/"):
                rel_path = rel_path[1:]
            dest = os.path.join(dotnet_root, "sdk", "8.0.131", "Sdks", "Microsoft.NET.Sdk.WindowsDesktop", rel_path)
            if member.is_dir():
                os.makedirs(dest, exist_ok=True)
            else:
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with z.open(member) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)
        
        # Extract packs/Microsoft.WindowsDesktop.App.Ref
        elif member.filename.startswith("packs/Microsoft.WindowsDesktop.App.Ref"):
            dest = os.path.join(dotnet_root, member.filename)
            if member.is_dir():
                os.makedirs(dest, exist_ok=True)
            else:
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with z.open(member) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)

        # Extract shared/Microsoft.WindowsDesktop.App
        elif member.filename.startswith("shared/Microsoft.WindowsDesktop.App"):
            dest = os.path.join(dotnet_root, member.filename)
            if member.is_dir():
                os.makedirs(dest, exist_ok=True)
            else:
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with z.open(member) as src, open(dest, "wb") as dst:
                    shutil.copyfileobj(src, dst)

print("Extraction completed successfully!")

