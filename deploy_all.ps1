$srcRust = "D:\put gitrepos here\sapling-rust\target\x86_64-pc-windows-gnu\release\sapling_rust.dll"
$srcCs = "D:\put gitrepos here\sapling-rust\csharp\SaplingRustBridge\bin\Release\net35\SaplingRustBridge.dll"

Copy-Item -Force $srcCs "D:\The_Sapling\The Sapling\BepInEx\plugins\SaplingRustBridge.dll"
Copy-Item -Force $srcRust "D:\The_Sapling\The Sapling\sapling_rust.dll"
Copy-Item -Force $srcRust "D:\The_Sapling\The Sapling\the_sapling_Data\Plugins\sapling_rust.dll"
Copy-Item -Force $srcRust "D:\The_Sapling\The Sapling\BepInEx\plugins\sapling_rust.dll"

Write-Output "Successfully deployed new DLLs!"
Get-Item "D:\The_Sapling\The Sapling\BepInEx\plugins\SaplingRustBridge.dll", "D:\The_Sapling\The Sapling\the_sapling_Data\Plugins\sapling_rust.dll" | Format-Table Name, Length, LastWriteTime
