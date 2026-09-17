param([string]$App, [string]$Zip, [string[]]$Amds)
$stage = "D:\CI\_staging"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
function Closure($proj, $seen) {
  $p = (Resolve-Path $proj).Path
  if ($seen.Contains($p)) { return }
  $seen.Add($p) | Out-Null
  $dir = Split-Path $p -Parent
  ([xml](Get-Content $p)).Project.ItemGroup.ProjectReference | Where-Object { $_ } | ForEach-Object {
    $r = Join-Path $dir $_.Include
    if (Test-Path $r) { Closure $r $seen }
  }
}
$seen = New-Object "System.Collections.Generic.HashSet[string]"
Closure "D:\CI\MPAIApps\MmcApps\$App\src\$App.csproj" $seen
Write-Host "projects: $($seen.Count)"
foreach ($p in $seen) { $d = Split-Path $p -Parent; robocopy $d (Join-Path $stage $d.Substring(6)) /E /XD bin obj /NFL /NDL /NJH /NJS | Out-Null }
New-Item -ItemType Directory -Force -Path "$stage\AIMs\AMDs" | Out-Null
Get-ChildItem "D:\CI\AIMs\AMDs" -Filter "*.json" | Where-Object { $n = $_.Name; $Amds | Where-Object { $n -match $_ } } | Copy-Item -Destination "$stage\AIMs\AMDs"
Copy-Item "D:\CI\AIMs\aim-settings.json" "$stage\AIMs\" -Force
robocopy "D:\CI\schemas" "$stage\schemas" /E /NFL /NDL /NJH /NJS | Out-Null
robocopy "D:\CI\UAs\Assets" "$stage\UAs\Assets" /E /NFL /NDL /NJH /NJS | Out-Null
robocopy "D:\CI\MPAIApps\MmcApps\$App\docs" "$stage\MPAIApps\MmcApps\$App\docs" /E /NFL /NDL /NJH /NJS | Out-Null
Copy-Item "D:\CI\MPAIApps\MmcApps\$App\${App}Build.bat" "$stage\MPAIApps\MmcApps\$App\" -Force -ErrorAction SilentlyContinue
Copy-Item "D:\CI\LICENSE" $stage -Force -ErrorAction SilentlyContinue
Write-Host ("AMDs:   " + (Get-ChildItem "$stage\AIMs\AMDs").Count)
Write-Host ("csproj: " + (Get-ChildItem $stage -Recurse -Filter "*.csproj").Count)
Compress-Archive -Path "$stage\*" -DestinationPath $Zip -Force
Remove-Item $stage -Recurse -Force
Get-Item $Zip | Select-Object FullName, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}