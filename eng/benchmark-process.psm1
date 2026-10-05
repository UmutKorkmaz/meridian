# Starts a child process with an exact argument array and streams its output to files.
#
# Start-Process -ArgumentList joins the array with spaces and does not quote, so an element that contains a space (a filter
# such as '*Foo Bar*', or a path with a space) reaches the child as several arguments. ProcessStartInfo.ArgumentList passes
# every element verbatim, which keeps the timed run identical to the invocation that was listed and preflighted.

Set-StrictMode -Version Latest

function Start-LoggedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [Parameter(Mandatory)][string] $StandardOutputPath,
        [Parameter(Mandatory)][string] $StandardErrorPath
    )

    $info = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }

    $outStream = [IO.File]::Create($StandardOutputPath)
    $errStream = [IO.File]::Create($StandardErrorPath)
    try {
        $process = [Diagnostics.Process]::Start($info)
    } catch {
        $outStream.Dispose()
        $errStream.Dispose()
        throw
    }

    return [pscustomobject]@{
        Process = $process
        Streams = @($outStream, $errStream)
        Copies = @($process.StandardOutput.BaseStream.CopyToAsync($outStream), $process.StandardError.BaseStream.CopyToAsync($errStream))
    }
}

# Call after the process has exited: waits until all of its output reached the files, then closes them.
function Complete-LoggedProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $LoggedProcess)

    try {
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]] $LoggedProcess.Copies, 30000) | Out-Null
    } finally {
        foreach ($stream in $LoggedProcess.Streams) { $stream.Flush(); $stream.Dispose() }
    }
}

Export-ModuleMember -Function Start-LoggedProcess, Complete-LoggedProcess
