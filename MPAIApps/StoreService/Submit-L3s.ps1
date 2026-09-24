<#
  Submit-L3s.ps1 - submit a set of L3s to the MPAI Store, as their Implementer.

  Reads the L3s named (or every L3 in -Folder), and submits them to the Store
  Service in an order that lets each composite find its Sub-AIMs: an L3 is
  submitted once every Sub-AIM it names has been. For an AIM with a package, the
  copy submitted names where the package is - -Packages\<ImplementationID>\ -
  in its ImplementationURI; the L3 files themselves are not changed.

  Prints, for each L3, the Store's verdict and what it found.

  -Folder    where the L3s are (e.g. D:\BI\AIMs\AMDs). Required.
  -Ids       which L3s, by AIM Instance id; default: every *.json in -Folder
  -Store     the Store Service (default https://localhost:5020/)
  -Packages  the folder holding all packages (default: none - URIs left as they are)
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [string[]]$Ids,
    [string]$Store = 'https://localhost:5020/',
    [string]$Packages
)
$ErrorActionPreference = 'Stop'
$Store = $Store.TrimEnd('/') + '/'
# -Ids a,b,c arrives as one string under powershell -File: split it here.
$Ids = @($Ids | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Ids.Count -eq 0) { $Ids = Get-ChildItem $Folder -Filter *.json | ForEach-Object { $_.BaseName } }

$l3 = @{}
foreach ($id in $Ids) {
    $path = Join-Path $Folder "$id.json"
    if (-not (Test-Path $path)) { Write-Host "  NOT FOUND  $path" -ForegroundColor Red; exit 1 }
    $l3[$id] = Get-Content $path -Raw | ConvertFrom-Json
}
function SubAims($doc) { @($doc.SubAIMs | Where-Object { $_ } | ForEach-Object { $_.Identifier.AIMName }) }

# The order: an L3 goes once all its Sub-AIMs named in this set have gone.
$done = @{}; $order = New-Object System.Collections.Generic.List[string]
while ($order.Count -lt $l3.Count) {
    $ready = $l3.Keys | Where-Object { -not $done.ContainsKey($_) -and
             -not (SubAims $l3[$_] | Where-Object { $l3.ContainsKey($_) -and -not $done.ContainsKey($_) }) } | Sort-Object
    if (-not $ready) { Write-Host "  A cycle among: $(($l3.Keys | Where-Object { -not $done.ContainsKey($_) }) -join ', ')" -ForegroundColor Red; exit 1 }
    foreach ($id in $ready) { $done[$id] = $true; $order.Add($id) }
}

$published = 0; $refused = 0
foreach ($id in $order) {
    $doc = $l3[$id]
    if ($Packages -and -not (SubAims $doc)) {
        foreach ($impl in @($doc.Implementations)) {
            $impl.ImplementationURI = [Uri]::new([IO.Path]::GetFullPath((Join-Path $Packages $doc.Identifier.ImplementationID)), [UriKind]::Absolute).AbsoluteUri + '/'
        }
    }
    $body = $doc | ConvertTo-Json -Depth 32
    try {
        $answer = Invoke-WebRequest -Uri ($Store + 'MPAI/Store/L3') -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
                                    -ContentType 'application/json' -UseBasicParsing
        $r = $answer.Content | ConvertFrom-Json
    } catch {
        # A refusal (4xx) carries the Store's reasons in its body; both Windows
        # PowerShell and PowerShell 7 give it as ErrorDetails.Message.
        if (-not $_.Exception.Response -or -not $_.ErrorDetails.Message) {
            Write-Host "  Could not submit $id to the Store at $Store : $($_.Exception.Message)" -ForegroundColor Red; exit 1
        }
        $r = $_.ErrorDetails.Message | ConvertFrom-Json
    }
    if ($r.published) {
        $published++
        $errors = @($r.findings | Where-Object { $_.severity -eq 'error' }).Count
        $warnings = @($r.findings | Where-Object { $_.severity -ne 'error' }).Count
        Write-Host ("  published  {0,-22} v{1}   {2} error(s), {3} warning(s) found" -f $id, $r.version, $errors, $warnings) -ForegroundColor Green
    } else {
        $refused++
        Write-Host ("  REFUSED    {0,-22} {1}" -f $id, $r.refused) -ForegroundColor Red
    }
}
Write-Host "$published published, $refused refused. What the Store found: GET $($Store)MPAI/Store/L3/<id>/findings"
