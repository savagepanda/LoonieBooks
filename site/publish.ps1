<#
.SYNOPSIS
Builds LoonieBooks' static site: the app as plain files for any static web host.

.DESCRIPTION
Publishes MapleLedger.Web.Client on its own (-p:StaticSite=true), adds the web host's shared files
(styles, icons, Bootstrap, help clips) and site\index.html, and writes the result to artifacts\site.

The site has no server side and keeps nothing: everyone who opens it keeps their ledger in their own
browser, as on any LoonieBooks site when they aren't signed in. Upload the folder's contents as they are
(GitHub Pages, Cloudflare Pages, any web server).

.PARAMETER BaseHref
The path the site is served under: / at its own domain, /LoonieBooks/ on a github.io project page.

.EXAMPLE
pwsh site\publish.ps1
pwsh site\publish.ps1 -BaseHref /LoonieBooks/
#>
param(
    [string]$BaseHref = '/',
    [string]$Out
)

$ErrorActionPreference = 'Stop'
# Runs on Windows and on Linux (the GitHub Pages workflow), so paths are joined, never written with \.
$root = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $root 'artifacts' 'site' }
if (-not ($BaseHref.StartsWith('/') -and $BaseHref.EndsWith('/'))) { throw '-BaseHref must start and end with /, e.g. / or /LoonieBooks/.' }

# Copies a folder's contents into another, merging subfolders that are already there.
function Copy-Folder([string]$From, [string]$To) {
    $from = (Resolve-Path -LiteralPath $From).Path
    foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File -Force) {
        $target = Join-Path $To $file.FullName.Substring($from.Length).TrimStart('\', '/')
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

$publish = Join-Path $root 'artifacts' 'site-publish'
foreach ($folder in $Out, $publish) {
    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}

Write-Host 'Publishing the app on its own (the WebAssembly build takes a few minutes)...'
# Its own bin and obj folders, so this build and the desktop app's (desktopbuild.bat) never reuse each
# other's compiled output.
dotnet publish (Join-Path $root 'MapleLedger.Web.Client') -c Release -p:StaticSite=true -o $publish -nologo `
    '-p:IntermediateOutputPath=obj/static-site/' '-p:OutputPath=bin/static-site/'
if ($LASTEXITCODE -ne 0) { throw 'Publishing MapleLedger.Web.Client failed.' }

Copy-Folder (Join-Path $publish 'wwwroot') $Out
# The web host's own files, which its pages share with the app.
Copy-Folder (Join-Path $root 'MapleLedger.Web' 'wwwroot') $Out
Remove-Item -LiteralPath $publish -Recurse -Force

# Static hosts don't use the build's precompressed copies (they compress as they serve), so drop them.
# Only copies of a file that's also there: Tesseract's language data is gzipped on purpose.
Get-ChildItem -LiteralPath $Out -Recurse -File -Include *.br, *.gz |
    Where-Object { Test-Path -LiteralPath ($_.FullName.Substring(0, $_.FullName.Length - $_.Extension.Length)) } |
    Remove-Item
Remove-Item -LiteralPath (Join-Path $Out 'appsettings.Development.json') -ErrorAction SilentlyContinue

$page = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'index.html') -Raw).Replace('<base href="/" />', "<base href=`"$BaseHref`" />")
Set-Content -LiteralPath (Join-Path $Out 'index.html') -Value $page -NoNewline
# A link straight to a page (say /transactions) isn't a file: static hosts answer it with 404.html,
# which is the same app, and the app then shows that page.
Set-Content -LiteralPath (Join-Path $Out '404.html') -Value $page -NoNewline

# Option prices are looked up through the web host, which this site doesn't have.
Set-Content -LiteralPath (Join-Path $Out 'appsettings.Production.json') -Value '{ "Quotes": { "OptionsUrl": "" } }'

# GitHub Pages runs sites through Jekyll, which leaves out folders starting with _ (like _framework).
New-Item -ItemType File -Path (Join-Path $Out '.nojekyll') -Force | Out-Null

$files = Get-ChildItem -LiteralPath $Out -Recurse -File
$largest = $files | Sort-Object Length -Descending | Select-Object -First 1
Write-Host ("Built {0}: {1} files, {2:N1} MB (largest: {3}, {4:N1} MB)." -f $Out, $files.Count,
    (($files | Measure-Object Length -Sum).Sum / 1MB), $largest.Name, ($largest.Length / 1MB))
