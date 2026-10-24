$ErrorActionPreference = "Stop"

cargo build --release --locked --manifest-path (Join-Path $PSScriptRoot "Cargo.toml")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$readerPath = Join-Path $PSScriptRoot "target\release\xrk-reader.exe"
$assetPath = Join-Path $projectRoot "Assets\XrkReader.exe"

$metadataOutput = & cargo metadata --manifest-path (Join-Path $PSScriptRoot "Cargo.toml") --locked --format-version 1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$metadata = ($metadataOutput -join "`n") | ConvertFrom-Json
$activePackageIds = @{}
foreach ($node in $metadata.resolve.nodes) {
    $activePackageIds[$node.id] = $true
}

$noticeSections = [System.Collections.Generic.List[string]]::new()
$libXrkLicensePath = Join-Path $PSScriptRoot "LIBXRK-LICENSE.txt"
$noticeSections.Add("libxrk (MIT)`r`n$([System.IO.File]::ReadAllText($libXrkLicensePath))")
foreach ($package in $metadata.packages) {
    if (-not $package.source -or
        -not $activePackageIds.ContainsKey($package.id) -or
        $package.name -eq "libxrk") {
        continue
    }

    $packageDirectory = Split-Path $package.manifest_path -Parent
    $licenseNames = if ($package.license -match "\bMIT\b") {
        @("LICENSE-MIT", "LICENSE-MIT.txt", "LICENSE", "LICENSE.md")
    }
    elseif ($package.license -match "0BSD") {
        @("LICENSE-0BSD", "LICENSE-0BSD.txt", "LICENSE")
    }
    elseif ($package.license -match "Zlib") {
        @("LICENSE-ZLIB", "LICENSE-ZLIB.md", "LICENSE")
    }
    else {
        @("LICENSE", "LICENSE.txt", "LICENSE.md")
    }

    $licensePath = $null
    if ($package.name -eq "binrw" -or $package.name -eq "binrw_derive") {
        $licensePath = Join-Path $PSScriptRoot "BINRW-LICENSE.txt"
    }
    foreach ($licenseName in $licenseNames) {
        if ($licensePath) {
            break
        }

        $candidatePath = Join-Path $packageDirectory $licenseName
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $licensePath = $candidatePath
            break
        }
    }
    if (-not $licensePath -and $package.license_file) {
        $candidatePath = Join-Path $packageDirectory $package.license_file
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $licensePath = $candidatePath
        }
    }
    if (-not $licensePath) {
        throw "Could not find a license file for Rust dependency $($package.name) $($package.version)."
    }

    $noticeSections.Add(
        "$($package.name) $($package.version) ($($package.license))`r`n$([System.IO.File]::ReadAllText($licensePath))")
    if ($package.license -match "Unicode-3\.0") {
        $unicodeLicensePath = Join-Path $packageDirectory "LICENSE-UNICODE"
        if (-not (Test-Path -LiteralPath $unicodeLicensePath -PathType Leaf)) {
            throw "Could not find the Unicode-3.0 license for Rust dependency $($package.name)."
        }
        $noticeSections.Add(
            "$($package.name) $($package.version) (Unicode-3.0)`r`n$([System.IO.File]::ReadAllText($unicodeLicensePath))")
    }
}

$noticesPath = Join-Path $projectRoot "Assets\XrkReader-Third-Party-Notices.txt"
[System.IO.File]::WriteAllText(
    $noticesPath,
    ($noticeSections -join "`r`n`r`n"),
    [System.Text.Encoding]::UTF8)
Copy-Item -LiteralPath $readerPath -Destination $assetPath -Force
Write-Output "Updated $assetPath"
Write-Output "Updated $noticesPath"
