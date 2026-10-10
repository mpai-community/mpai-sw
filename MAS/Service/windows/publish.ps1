<#
.SYNOPSIS
  Publishes MAS-App for the public instance on Windows (M3248 3.2): the MAS Service
  and the browser client's host, Release, into the deploy folder of the repository - not the build folders,
  which stay for development.

.DESCRIPTION
  The public reaches a Release client, in Production (the host is published: its
  own folder its content root, Production unless told otherwise). Both programs are
  published inside the repository, which they find by going up from their folder:
  the Service its AIMs, L3s, schemas and models; the host the avatar and the
  client's workflow.

  After publishing, the public Service and host are restarted from deploy - with the
  author's agreement - on the ports they had. The script prints their command lines;
  it starts and stops nothing.

.PARAMETER Config
  The Service's configuration (git-ignored: it may hold a bearer token).

.EXAMPLE
  powershell -File MAS\Service\windows\publish.ps1
#>
param([string]$Config = 'MAS\Service\mas-server.json',
      [string]$ServiceUrl = 'https://localhost:5105/',
      [string]$Urls = 'https://localhost:5010')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$out  = Join-Path $root 'deploy'

foreach ($p in @(@('service', 'MAS\Service\src\MasService.csproj'),
                 @('client',  'UserAgent\Clients\Browser\Host\RcaWeb.Host.csproj'))) {
    $to = Join-Path $out $p[0]
    Write-Host "== $($p[1]) -> $to"
    & dotnet publish (Join-Path $root $p[1]) -c Release -o $to -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$($p[1]) did not publish." }
}
$pdb = @(Get-ChildItem (Join-Path $out 'client\wwwroot\_framework') -Filter *.pdb -ErrorAction SilentlyContinue).Count
Write-Host "The client's files: $(if ($pdb) { "$pdb with symbols" } else { 'no symbols' })."

Write-Host ""
Write-Host "Published in $out. Restart the public instance from it (with the author's agreement):"
Write-Host "  Service (in $root):"
Write-Host "    `"$out\service\MasService.exe`" `"$(Join-Path $root $Config)`""
Write-Host "  Host (in $out\client):"
Write-Host "    `"$out\client\RcaWeb.Host.exe`" --Service $ServiceUrl --Urls $Urls"
