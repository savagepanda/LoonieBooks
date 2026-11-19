<#
.SYNOPSIS
Publishes the website (looniebooks.ca, the files in public/) to Cloudflare.

.DESCRIPTION
A Worker with static assets only, configured in wrangler.jsonc next to this script. Needs Node.js and
Wrangler signed in to the Cloudflare account (`npx wrangler login`, once).
#>
$ErrorActionPreference = 'Stop'
$npx = (Get-Command npx -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\nodejs\npx.cmd'

Push-Location $PSScriptRoot
try {
    & $npx --yes wrangler@4 deploy
    if ($LASTEXITCODE) { throw 'Deploy failed.' }
}
finally {
    Pop-Location
}
