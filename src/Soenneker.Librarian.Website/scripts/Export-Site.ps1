param([int]$Port = 5193)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $projectRoot 'artifacts/publish'
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'out'))
# Always export into a clean, project-owned directory.
if ($outputRoot -ne (Join-Path $projectRoot 'out')) { throw 'Unexpected output directory.' }
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
dotnet publish (Join-Path $projectRoot 'Soenneker.Librarian.Website.csproj') -c Release -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Website publish failed.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
# Quark renders the HTML at build time. This site only needs its own CSS and JS at runtime.
Copy-Item -Path (Join-Path $projectRoot 'wwwroot/*') -Destination $outputRoot -Recurse -Force
$startOptions = @{
    FilePath = 'dotnet'
    ArgumentList = @(('"' + (Join-Path $publishRoot 'Soenneker.Librarian.Website.dll') + '"'), '--urls', "http://127.0.0.1:$Port")
    WorkingDirectory = $publishRoot
    PassThru = $true
    RedirectStandardOutput = (Join-Path $projectRoot 'artifacts/export.log')
    RedirectStandardError = (Join-Path $projectRoot 'artifacts/export-error.log')
}
if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
$server = Start-Process @startOptions
try {
    $response = $null
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($server.HasExited) { throw 'The export server exited before rendering. See artifacts/export-error.log.' }
        try { $response = Invoke-WebRequest "http://127.0.0.1:$Port/"; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $response) { throw 'The export server did not start.' }
    foreach ($route in @('', 'getting-started/', 'providers/', '404/')) {
        $page = Invoke-WebRequest "http://127.0.0.1:$Port/$route"
        if ($page.StatusCode -ne 200 -or $page.Content -notmatch '<h1' -or $page.Content -notmatch '<title>') { throw "Incomplete page: /$route" }
        $pageRoot = Join-Path $outputRoot $route
        New-Item -ItemType Directory -Path $pageRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $pageRoot 'index.html'), $page.Content)
        if ($route -eq '404/') { [IO.File]::WriteAllText((Join-Path $outputRoot '404.html'), $page.Content) }
    }
    foreach ($asset in @('css/site.css','js/site.js','favicon.svg','sitemap.xml','robots.txt','_headers')) {
        if (-not (Test-Path (Join-Path $outputRoot $asset))) { throw "Missing asset: $asset" }
    }
    # Check every local href/src in exported pages, including fragment targets.
    foreach ($file in Get-ChildItem -LiteralPath $outputRoot -Recurse -Filter '*.html') {
        $html = [IO.File]::ReadAllText($file.FullName)
        foreach ($match in [regex]::Matches($html, '(?:href|src)="([/#][^"]*)"')) {
            $url = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
            $parts = $url.Split('#', 2)
            $target = if ($parts[0] -eq '') { $file.FullName } else { Join-Path $outputRoot $parts[0].TrimStart('/') }
            if (Test-Path -LiteralPath $target -PathType Container) { $target = Join-Path $target 'index.html' }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Broken local link: $url in $($file.Name)" }
            if ($parts.Length -eq 2 -and $parts[1] -ne '') {
                $targetHtml = [IO.File]::ReadAllText($target)
                if ($targetHtml -notmatch ('id="' + [regex]::Escape($parts[1]) + '"')) { throw "Missing anchor: $url" }
            }
        }
    }
    Write-Output "Exported and verified website: $outputRoot"
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
