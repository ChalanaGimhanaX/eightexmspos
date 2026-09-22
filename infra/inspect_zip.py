import zipfile

zip_path = "/tmp/dotnet-win.zip"
with zipfile.ZipFile(zip_path) as z:
    for n in z.namelist():
        if "sdk/8.0.401/Sdks/Microsoft.NET.Sdk.WindowsDesktop" in n:
            print(n)

