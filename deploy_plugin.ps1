$pluginDir = "D:\The_Sapling\The Sapling\BepInEx\plugins"
$targetDll = Join-Path $pluginDir "SaplingRustBridge.dll"
$oldDll = Join-Path $pluginDir "SaplingRustBridge.dll.old"
$newDll = "d:\put gitrepos here\sapling-rust\csharp\SaplingRustBridge\bin\Release\net35\SaplingRustBridge.dll"

if (Test-Path $oldDll) {
    try { Remove-Item -Force $oldDll -ErrorAction SilentlyContinue } catch {}
}
if (Test-Path $targetDll) {
    try {
        Move-Item -Force $targetDll $oldDll
        Write-Output "Renamed locked DLL to .old"
    } catch {
        Write-Output ("Move failed: " + $_.Exception.Message)
    }
}
Copy-Item -Force $newDll $targetDll
Write-Output "Copied new SaplingRustBridge.dll successfully!"
Get-Item $targetDll | Select-Object Name, Length, LastWriteTime
