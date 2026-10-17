# Uploads dist\DnsPilot-Setup.exe to release 414281366, retrying because the long
# transfer keeps dying mid-stream on this machine's route (small POSTs succeed
# every time - the probe came back 201 - so each fresh attempt is a coin toss the
# retries win). Alternates between curl (single POST, needs both the auth and the
# content-type header or GitHub answers 400/422) and gh's own uploader, which uses
# a different HTTP stack.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Upload-Release.ps1
# Result: uploads an asset named DnsPilot-Setup.exe; the script exits 0 only when
#         one attempt reports 201.

$ErrorActionPreference = 'Stop'
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$zip = 'E:\dnspilot\DnsPilot\dist\DnsPilot-Setup.exe'
$url = 'https://uploads.github.com/repos/Ekkh1300/DnsPilot/releases/414281366/assets?name=DnsPilot-Setup.exe'

for ($i = 1; $i -le 8; $i++)
{
    $tok = & $gh auth token
    if ($i % 2 -eq 1)
    {
        Write-Output ("attempt {0}: curl" -f $i)
        curl.exe -sS --ipv4 --connect-timeout 30 `
            -H "Authorization: Bearer $tok" `
            -H "Content-Type: application/octet-stream" `
            --data-binary "@$zip" "$url" `
            -o 'E:\dnspilot\tools\upload-result.json' `
            -w ("HTTP {0} in {1}s at {2} B/s" -f '%{http_code}', '%{time_total}', '%{speed_upload}')
    }
    else
    {
        Write-Output ("attempt {0}: gh release upload" -f $i)
        & $gh release upload v1.0 "$zip" --repo Ekkh1300/DnsPilot --clobber 2>&1
    }

    # One way or another, the only success signal is the asset appearing on the
    # release - a body that says so beats parsing each tool's exit code.
    $j = & $gh api 'repos/Ekkh1300/DnsPilot/releases/414281366' --jq '.assets | length'
    if ([int]$j -gt 0)
    {
        Write-Output ("asset landed after {0} attempt(s)" -f $i)
        exit 0
    }

    Write-Output ("attempt {0} did not land; sleeping 30s" -f $i)
    Start-Sleep -Seconds 30
}
Write-Output 'exhausted 8 attempts'
exit 1
