<#
.SYNOPSIS
    Installs or updates TaskTracker for the current user from a GitHub release.
.DESCRIPTION
    Downloads the public standalone release without GitHub CLI or authentication.
    Installs the standalone exe and adds its directory to the user's PATH.
.PARAMETER TaskTrackerVersion
    The release to install, such as v1.6.0 or 1.6.0. Also accepted as -Version. Without it, the latest release.
#>
[CmdletBinding()]
param(
    # Not named $Version: under 'irm | iex' param() runs in the user's own session and would overwrite theirs.
    [Alias('Version')]
    [string] $TaskTrackerVersion
)

# Under 'irm | iex' the script runs in the user's own session. The child scope keeps settings and the
# script's variables from staying behind after the install.
& {
    # param() declared this in the parent scope, which under 'irm | iex' is the user's session.
    $requested = $TaskTrackerVersion
    Remove-Variable -Name TaskTrackerVersion -Scope 1 -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Stop'

    # The app prints its version without the 'v' of the release tag, so accept that form too.
    $tag = $requested
    if ($tag -cmatch '^\d') { $tag = "v$tag" }
    # Checked here, not with [ValidatePattern]: under 'irm | iex' param() is an ordinary variable declaration,
    # and the attribute would reject the empty default. -cnotmatch because release tag URLs are case-sensitive,
    # and \z because $ also matches before a trailing newline, which would end up in the URL.
    if ($tag -and $tag -cnotmatch '^v\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?\z') {
        throw "Version must be a release tag such as v1.6.0 (got '$requested')."
    }

    $releaseUrl = 'https://github.com/vlkozl/tasktracker/releases'
    if ($tag) { $downloadUrl = "$releaseUrl/download/$tag/TaskTracker.exe" }
    else { $downloadUrl = "$releaseUrl/latest/download/TaskTracker.exe" }

    $installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\TaskTracker'
    $destination = Join-Path $installDirectory 'TaskTracker.exe'
    $backup = "$destination.bak"

    # Windows lets a running exe be renamed, so the replace below would succeed under a running TaskTracker
    # and leave it running the old version. Checked by name only: the path of a process isn't always readable.
    if (Get-Process -Name TaskTracker -ErrorAction SilentlyContinue) {
        throw 'TaskTracker is running. Close it, then run the installer again.'
    }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    $download = Join-Path $installDirectory ('download-' + [Guid]::NewGuid().ToString('N') + '.tmp')

    try {
        Write-Host "Downloading $downloadUrl"
        # The progress bar slows the download a lot in Windows PowerShell 5.1. The script's own
        # scope ends after the install, so the user's setting doesn't need restoring.
        $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $download }
        catch { throw "Download failed. Check your internet connection and that the release includes TaskTracker.exe. $($_.Exception.Message)" }

        # A proxy or captive portal can answer with an HTML page. A Windows exe starts with 'MZ'.
        $header = New-Object byte[] 2
        $stream = [IO.File]::OpenRead($download)
        try { $read = $stream.Read($header, 0, 2) }
        finally { $stream.Dispose() }
        if ($read -lt 2 -or $header[0] -ne 0x4D -or $header[1] -ne 0x5A) {
            throw 'The download is not a Windows program, so nothing was installed. A proxy or sign-in page may have answered instead of GitHub.'
        }

        # The download is staged in the install folder, so the replace stays on one volume. Replace isn't
        # atomic: it can fail after it moved the old exe away. With a backup name the old exe is kept and
        # put back, so a failed update doesn't leave the user without TaskTracker.exe.
        if (Test-Path -LiteralPath $destination) {
            try { [IO.File]::Replace($download, $destination, $backup) }
            catch {
                $message = "Could not replace $destination. $($_.Exception.Message)"
                # A failed restore must not hide why the replace failed, so say where the old exe is instead.
                if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) {
                    try { [IO.File]::Move($backup, $destination) }
                    catch { $message += " The previous version is at $backup; rename it to TaskTracker.exe." }
                }
                throw $message
            }
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        else {
            [IO.File]::Move($download, $destination)
        }
    }
    finally {
        Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
    }

    # Whether a PATH value has the install folder, written with %VAR%, in quotes or with a trailing '\'.
    $hasInstallDirectory = { param($path)
        [bool]($path -split ';' | Where-Object {
            [Environment]::ExpandEnvironmentVariables($_).Trim().Trim('"').TrimEnd('\') -ieq $installDirectory
        })
    }

    # Read and write the registry, not [Environment]::SetEnvironmentVariable: that one returns PATH with
    # %VAR% entries expanded and writes it back as REG_SZ, hard-coding the user's other entries.
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try {
        $userPath = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $present = & $hasInstallDirectory $userPath
        if (-not $present) {
            # Keep the value's type; a new value gets REG_EXPAND_SZ, like the one Windows creates.
            $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
            if ($key.GetValueNames() -contains 'Path' -and $key.GetValueKind('Path') -eq 'String') { $kind = 'String' }
            $newPath = $installDirectory
            if ($userPath) { $newPath = $userPath.TrimEnd(';') + ';' + $installDirectory }
            $key.SetValue('Path', $newPath, $kind)
        }
    }
    finally { $key.Dispose() }

    if (-not $present) {
        # Writing the registry doesn't tell Explorer that the environment changed, so new terminals wouldn't
        # see the new PATH until the user signs out. SetEnvironmentVariable sends that notice, so set and clear
        # a dummy variable. NullString, because PowerShell passes $null as "", which PowerShell 7 stores
        # instead of deleting the variable.
        [Environment]::SetEnvironmentVariable('TaskTrackerInstall', '1', 'User')
        [Environment]::SetEnvironmentVariable('TaskTrackerInstall', [System.Management.Automation.Language.NullString]::Value, 'User')
    }

    # So that TaskTracker runs in this window straight away, not only in new ones.
    if (-not (& $hasInstallDirectory $env:Path)) {
        $env:Path = $env:Path.TrimEnd(';') + ';' + $installDirectory
    }

    # The version makes a downgrade with -Version visible, too.
    $installed = ((Get-Item -LiteralPath $destination).VersionInfo.ProductVersion -split '\+')[0]
    Write-Host "Installed TaskTracker $installed to $destination"
    # Run with -File, the script's process ends with the PATH change for this window, so don't promise it works here.
    Write-Host "Run TaskTracker to start it. If this window doesn't find it, open a new terminal."
}
