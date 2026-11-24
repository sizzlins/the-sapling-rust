$ErrorActionPreference = "Stop"

$srcRust = "D:\put gitrepos here\sapling-rust\target\x86_64-pc-windows-gnu\release\sapling_rust.dll"
$srcCs = "D:\put gitrepos here\sapling-rust\csharp\SaplingRustBridge\bin\Release\net35\SaplingRustBridge.dll"

$gameRoot = "D:\The_Sapling\The Sapling"
$pluginDir = Join-Path $gameRoot "BepInEx\plugins"
$nativePluginDir = Join-Path $gameRoot "the_sapling_Data\Plugins"

function Deploy-Safe($src, $dst) {
    $old = "$dst.old"
    if (Test-Path $old) {
        Remove-Item -Force $old -ErrorAction SilentlyContinue
    }
    try {
        Copy-Item -Force $src $dst
    } catch {
        # If the game currently holds the DLL open, rename it out of the way (Windows allows
        # renaming loaded DLLs) and copy the new one in place for the next launch.
        Move-Item -Force $dst $old
        Copy-Item -Force $src $dst
        Write-Output "Staged locked DLL via .old rename: $dst (restart game to load)"
    }
}

$strayNative = Join-Path $pluginDir "sapling_rust.dll"
if (Test-Path $strayNative) {
    Remove-Item -Force $strayNative -ErrorAction SilentlyContinue
}

Deploy-Safe $srcCs (Join-Path $pluginDir "SaplingRustBridge.dll")
Deploy-Safe $srcRust (Join-Path $gameRoot "sapling_rust.dll")
Deploy-Safe $srcRust (Join-Path $nativePluginDir "sapling_rust.dll")

Write-Output "Successfully deployed DLLs!"
Get-Item (Join-Path $pluginDir "SaplingRustBridge.dll"), (Join-Path $gameRoot "sapling_rust.dll"), (Join-Path $nativePluginDir "sapling_rust.dll") |
    Format-Table FullName, Length, LastWriteTime -AutoSize
