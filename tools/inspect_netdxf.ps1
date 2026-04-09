$dll = "C:\Users\Alex\.nuget\packages\netdxf\2023.11.10\lib\netstandard2.0\netDxf.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$type = $asm.GetType("netDxf.DxfDocument")
"=== Properties ==="
$type.GetProperties() | ForEach-Object { $_.Name }
"=== Fields ==="
$type.GetFields([System.Reflection.BindingFlags] "Public,NonPublic,Instance,Static") | ForEach-Object { $_.Name }
