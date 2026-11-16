[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][ValidatePattern('\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z')][string] $CandidateVersion,
    # Intent for a stable local candidate; never authorization to publish.
    [switch] $ReleasePreparation,
    [string] $Framework,
    [string] $SdkVersion,
    [string] $Manifest = 'eng/packages.json',
    [switch] $CurrentServicing,
    [string] $RequiredChecksPolicy = 'eng/required-checks.json',
    # Lists the approved compiler/reference plan without inspecting feeds, restoring, building or running a consumer.
    [switch] $PlanOnly,
    [string] $BaselineFeed,
    [string[]] $ExcludeConsumerIds = @(),
    [string] $OutputDirectory = 'artifacts/package-consumers'
)
$ErrorActionPreference = 'Stop'
# PowerShell -File binds a string[] parameter from one argv token. Keep the
# process boundary unambiguous while preserving normal in-process array calls.
$ExcludeConsumerIds = @($ExcludeConsumerIds | ForEach-Object { $_.Split(',') | ForEach-Object { $_.Trim() } })
function Select-PackageConsumers([object[]] $Consumers, [string[]] $Excluded) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $Excluded) {
        if (-not $seen.Add($id)) { throw "Duplicate excluded consumer ID: $id" }
        if (@($Consumers | Where-Object { $_.id -ceq $id }).Count -ne 1) { throw "Unknown excluded consumer ID: $id" }
    }
    $selected = @($Consumers | Where-Object { -not $seen.Contains($_.id) })
    if ($Consumers.Count -and -not $selected.Count) { throw 'Consumer exclusion cannot empty the selected inventory' }
    return [pscustomobject]@{ Consumers=$selected; SelectedIds=@($selected.id); OmittedIds=@($Consumers | Where-Object { $seen.Contains($_.id) } | ForEach-Object id); Scope=$(if ($seen.Count) { 'partial' } else { 'all' }) }
}
function Get-ConsumerCompilerPlan($Inventory, $Selection, [string] $Framework, [string] $SdkVersion, [bool] $Servicing, $PolicyDocument) {
    if ([bool]$Framework -ne [bool]$SdkVersion) { throw 'Framework and SdkVersion must be specified together' }
    $contract = if ($Servicing) { 'currentServicing' } else { 'pinnedCompatibility' }
    $approved = if ($Servicing) {
        @($PolicyDocument.toolchains.currentServicing.lanes | ForEach-Object { [pscustomobject]@{framework=$_.framework;sdkVersion=$_.sdk} })
    } else { @($Inventory.compilerHosts) }
    if (-not $approved.Count) { throw "No approved compiler hosts in $contract" }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $approved) {
        if (-not $seen.Add($entry.framework) -or $entry.framework -notmatch '^net(8|9|10)\.0$' -or
            $entry.sdkVersion -notmatch '^\d+\.\d+\.\d+$' -or $entry.sdkVersion.Split('.')[0] -ne $entry.framework.Substring(3).Split('.')[0]) {
            throw "Invalid approved compiler contract: $contract"
        }
    }
    $candidate = if ($Framework) { @($approved | Where-Object { $_.framework -ceq $Framework -and $_.sdkVersion -ceq $SdkVersion }) } else { $approved }
    if (-not $candidate.Count) { throw "Compiler SDK/framework pair is not approved by $contract" }
    $reference = @($approved | Where-Object framework -EQ 'net10.0')
    $lanes = @()
    if (@($Selection.Consumers | Where-Object baselineComparison).Count) {
        if ($reference.Count -ne 1) { throw 'Exactly one approved SDK 10 reference lane is required' }
        # Compare the published stable API under the same operational host family. Historical lanes retain their
        # historical reference; servicing lanes never require or inject an old runtime into the current-only root.
        $lanes += [pscustomobject]@{framework=$reference[0].framework;sdkVersion=$reference[0].sdkVersion;baselineOnly=$true}
    }
    $lanes += @($candidate | ForEach-Object { [pscustomobject]@{framework=$_.framework;sdkVersion=$_.sdkVersion;baselineOnly=$false} })
    return [pscustomobject]@{compilerContract=$contract;executionProven=$false;lanes=$lanes}
}
Import-Module (Join-Path $PSScriptRoot 'package-inventory.psm1') -Force -DisableNameChecking
Assert-CandidateVersion $CandidateVersion ([bool] $ReleasePreparation)
$repo = Split-Path -Parent $PSScriptRoot
$Feed = [IO.Path]::GetFullPath($Feed)
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $repo $Manifest }
$inventory = Read-PackageInventory $repo $manifestPath
# Published 2.1.3 old-host failures remain A4 historical evidence. The baseline
# operational comparison runs on SDK 10; all candidate compiler hosts must pass.
$selection = Select-PackageConsumers @($inventory.consumers) $ExcludeConsumerIds
$policyPath = Resolve-OutputDirectory $repo $RequiredChecksPolicy
$compilerPolicy = if ($CurrentServicing) { Get-Content -Raw -LiteralPath $policyPath | ConvertFrom-Json } else { $null }
$compilerPlan = Get-ConsumerCompilerPlan $inventory $selection $Framework $SdkVersion ([bool]$CurrentServicing) $compilerPolicy
if ($PlanOnly) {
    $compilerPlan | Add-Member -NotePropertyName selectedIds -NotePropertyValue @($selection.SelectedIds)
    $compilerPlan | Add-Member -NotePropertyName omittedIds -NotePropertyValue @($selection.OmittedIds)
    $compilerPlan | Add-Member -NotePropertyName consumerScope -NotePropertyValue $selection.Scope
    $compilerPlan | ConvertTo-Json -Depth 8
    return
}
& (Join-Path $PSScriptRoot 'pack-packages.ps1') -OutputDirectory $Feed -CandidateVersion $CandidateVersion -Manifest $manifestPath -InspectOnly -ReleasePreparation:$ReleasePreparation
$baselineConsumers = @($selection.Consumers | Where-Object baselineComparison)
$baselinePackages = @(Get-PackageClosure $inventory @($baselineConsumers.packageIds | Select-Object -Unique))
$lanes = @($compilerPlan.lanes)
$common = @{}
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$CandidateVersion/$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))"
$null = New-Item -ItemType Directory -Force $run
[ordered]@{ scope=$selection.Scope; selectedIds=$selection.SelectedIds; omittedIds=$selection.OmittedIds; compilerContract=$compilerPlan.compilerContract; archiveInspection='all-manifest-packages'; toolingInspection='all-manifest-producers' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'consumer-selection.json')
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $run 'Directory.Build.targets')
if ($baselinePackages.Count -and -not $BaselineFeed) {
    $BaselineFeed = Join-Path $run 'baseline-feed'
    $null = New-Item -ItemType Directory -Force $BaselineFeed
    foreach ($package in $baselinePackages) {
        $id = $package.id.ToLowerInvariant()
        $version = $package.baselineVersion
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile (Join-Path $BaselineFeed "$id.$version.nupkg")
    }
}
if ($BaselineFeed) { $BaselineFeed = [IO.Path]::GetFullPath($BaselineFeed) }
$baselineHashes = @($baselinePackages | ForEach-Object {
    $path = Join-Path $BaselineFeed "$($_.id.ToLowerInvariant()).$($_.baselineVersion).nupkg"
    $artifact = "$($_.id).$($_.baselineVersion).baseline-nupkg"
    Copy-Item -LiteralPath $path -Destination (Join-Path $run $artifact)
    [ordered]@{ id=$_.id; version=$_.baselineVersion; file=[IO.Path]::GetFileName($path); artifact=$artifact; sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
ConvertTo-Json -InputObject $baselineHashes -Depth 5 | Set-Content (Join-Path $run 'baseline-hashes.json')
function Execute([string[]] $Arguments, [string] $Log) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $process.StartInfo.WorkingDirectory = (Get-Location).ProviderPath
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.Environment['DOTNET_ROLL_FORWARD'] = 'LatestPatch'
    $process.StartInfo.Environment['DOTNET_ROLL_FORWARD_TO_PRERELEASE'] = '0'
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $process.StartInfo.ArgumentList.Add($arg) }
    try {
        $null = $process.Start()
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, "command=dotnet $($Arguments -join ' ')`nexit=$($process.ExitCode)`n$output")
        Write-Host $output
        if ($process.ExitCode -ne 0) { throw "Consumer command failed ($($process.ExitCode)); $Log" }
        return $output
    } finally { $process.Dispose() }
}
$results = @()
$components = @{}
$graphs = @()
foreach ($lane in $lanes) {
    if (@($compilerPlan.lanes | Where-Object { $_.framework -ceq $lane.framework -and $_.sdkVersion -ceq $lane.sdkVersion -and $_.baselineOnly -eq $lane.baselineOnly }).Count -ne 1) { throw 'Invalid compiler lane: must match exactly one approved compiler plan row' }
    $dir = Join-Path $run "$($lane.framework)-$($lane.sdkVersion)-$($lane.baselineOnly)"
    $null = New-Item -ItemType Directory -Force $dir
    @{ sdk=@{ version=$lane.sdkVersion; rollForward='disable'; allowPrerelease=$false } } | ConvertTo-Json | Set-Content (Join-Path $dir 'global.json')
    Push-Location $dir
    try {
        $sdk = (Execute @('--version') (Join-Path $dir 'sdk.log')).Trim()
        if ($sdk -ne $lane.sdkVersion) { throw "Wrong actual SDK: $sdk" }
        $null = Execute @('--info') (Join-Path $dir 'host.log')
        $installed = Execute @('--list-sdks') (Join-Path $dir 'installed-sdks.log')
        $sdkLine = @($installed -split "`r?`n" | Where-Object { $_.StartsWith("$sdk ") })
        if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]$') { throw 'Cannot locate selected SDK compiler' }
        $compiler = Join-Path $Matches[1] "$sdk/Roslyn/bincore/csc.dll"
        $compilerVersion = (Execute @('exec',$compiler,'-version') (Join-Path $dir 'compiler.log')).Trim()
        $consumers = if ($lane.baselineOnly) { $baselineConsumers } else { @($selection.Consumers) }
        foreach ($consumer in $consumers) {
            $closure = @(Get-PackageClosure $inventory $consumer.packageIds)
            $variants = if ($lane.baselineOnly) { @('baseline') } else { @($consumer.variants) }
            foreach ($variant in $variants) {
                $versions = @(if ($variant -eq 'baseline') { $closure.baselineVersion | Select-Object -Unique } else { $CandidateVersion })
                if ($versions.Count -ne 1) { throw "Consumer closure needs one shared package version: $($consumer.id)" }
                $version = $versions[0]
                $sourceFeed = if ($variant -eq 'baseline') { $BaselineFeed } else { $Feed }
                $work = Join-Path $dir "$variant-$($consumer.id)"
                $null = New-Item -ItemType Directory -Force $work
                @{ sdk=@{ version=$lane.sdkVersion; rollForward='disable'; allowPrerelease=$false } } | ConvertTo-Json | Set-Content (Join-Path $work 'global.json')
                $sourceDir = Split-Path (Resolve-RepositoryPath $repo $consumer.project)
                $fixture = @()
                foreach ($input in $consumer.sourceFiles) {
                    $source = Resolve-RepositoryPath $repo $input
                    $relative = [IO.Path]::GetRelativePath($sourceDir,$source)
                    $destination = Join-Path $work $relative
                    $null = New-Item -ItemType Directory -Force (Split-Path $destination)
                    Copy-Item -LiteralPath $source -Destination $destination
                    $fixture += [ordered]@{ source=$input; file=$relative; sha256=(Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
                }
                ConvertTo-Json -InputObject $fixture -Depth 5 | Set-Content (Join-Path $work 'fixture-hashes.json')
                $escapedFeed = [Security.SecurityElement]::Escape($sourceFeed)
                $patterns = ($inventory.packages | ForEach-Object { "<package pattern=`"$($_.id)`" />" }) -join ''
                "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$escapedFeed`"/><add key=`"dependencies`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"candidate`">$patterns</packageSource><packageSource key=`"dependencies`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>" | Set-Content (Join-Path $work 'NuGet.Config')
                $packages = Join-Path $work 'packages'
                $project = Join-Path $work ([IO.Path]::GetFileName($consumer.project))
                $properties = @("-p:ConsumerFramework=$($lane.framework)","-p:MeridianPackageVersion=$version","-p:BaselineConsumer=$($variant -eq 'baseline')","-p:MigratedConsumer=$($variant -eq 'migrated')","-p:RestorePackagesPath=$packages")
                $evaluated = Invoke-ProjectEvaluation $dotnet $project @('LangVersion','TreatWarningsAsErrors','AssemblyName') @('PackageReference','ProjectReference') $properties
                if (@($evaluated.Items.ProjectReference).Count) { throw "NuGet consumer cannot load source ProjectReference: $($consumer.id)" }
                if ($evaluated.Properties.LangVersion -notin @('12','12.0') -or $evaluated.Properties.TreatWarningsAsErrors -ne 'true') { throw 'Consumer must enforce CSharp12 and warnings as errors' }
                $roots = @($evaluated.Items.PackageReference | Where-Object { $_.Identity -in $inventory.packages.id })
                Assert-SameSet @($roots.Identity) @($consumer.packageIds) "Consumer root dependency mismatch: $($consumer.id)"
                foreach ($root in $roots) { if ($root.Version -ne "[$version]") { throw "Consumer package version must be exact: $($root.Identity)" } }
                $null = Execute (@('restore',$project,'--configfile',(Join-Path $work 'NuGet.Config'),'--no-cache') + $properties) (Join-Path $work 'restore.log')
                $assetsPath = Join-Path $work 'obj/project.assets.json'
                $assets = Get-Content -Raw $assetsPath | ConvertFrom-Json
                $actualIds = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name.Split('/')[0] -in $inventory.packages.id } | ForEach-Object { $_.Name.Split('/')[0] })
                Assert-SameSet $actualIds @($closure.id) "Restored inventory closure mismatch: $($consumer.id)"
                foreach ($package in $closure) {
                    $id = $package.id.ToLowerInvariant()
                    $restored = Join-Path $packages "$id/$version/$id.$version.nupkg"
                    $expectedPath = Join-Path $sourceFeed "$($package.id).$version.nupkg"
                    if (-not (Test-Path $expectedPath)) { $expectedPath = Join-Path $sourceFeed "$id.$version.nupkg" }
                    if ((Get-FileHash $restored -Algorithm SHA256).Hash -ne (Get-FileHash $expectedPath -Algorithm SHA256).Hash) { throw "Restored package hash differs: $id" }
                }
                foreach ($component in (Read-RestoredComponents $assets $packages)) { $components["$($component.id)/$($component.version)"] = $component }
                $graph = @($assets.targets.PSObject.Properties | ForEach-Object { $target=$_; @($target.Value.PSObject.Properties | ForEach-Object { [ordered]@{ identity=$_.Name; dependencies=$_.Value.dependencies } }) })
                $graphs += [ordered]@{consumer=$consumer.id;variant=$variant;framework=$lane.framework;assetsSha256=(Get-FileHash $assetsPath -Algorithm SHA256).Hash.ToLowerInvariant();dependencies=$graph}
                $null = Execute (@('build',$project,'-c','Release','--no-restore','-warnaserror') + $properties) (Join-Path $work 'build.log')
                $binary = Join-Path $work "bin/Release/$($lane.framework)/$($evaluated.Properties.AssemblyName).dll"
                $binaryBefore = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
                $stem = "$($consumer.id)-$variant-$($lane.framework)"
                $binaryArtifact = "$stem.consumer-binary"
                $depsArtifact = "$stem.consumer-deps.json"
                $assetsArtifact = "$stem.consumer-assets.json"
                Copy-Item -LiteralPath $binary -Destination (Join-Path $run $binaryArtifact)
                Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($binary, '.deps.json')) -Destination (Join-Path $run $depsArtifact)
                Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $run $assetsArtifact)
                $output = Execute @($binary) (Join-Path $work 'run.log')
                if ((Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binaryBefore) { throw 'The consumer executable changed while it ran.' }
                $runtimeLine = @($output -split "`r?`n" | Where-Object { $_.StartsWith('HOST runtime=') })
                $family = $lane.framework.Substring(3).Split('.')[0]
                if ($runtimeLine.Count -ne 1 -or $runtimeLine[0] -notmatch "^HOST runtime=$family\.") { throw "Wrong actual consumer runtime: $runtimeLine" }
                $lines = @($output -split "`r?`n" | Where-Object { $_.StartsWith('COMMON ') })
                if (-not $lines.Count) { throw "Consumer has no asserted COMMON output: $($consumer.id)" }
                if ($consumer.baselineComparison) {
                    if ($variant -eq 'baseline') { $common[$consumer.id] = $lines } else { Assert-SameSet @($common[$consumer.id]) $lines "Old/new common output mismatch: $($consumer.id)" }
                }
                $results += [ordered]@{ framework=$lane.framework; sdkVersion=$sdk; compilerVersion=$compilerVersion; runtime=$runtimeLine[0]; consumer=$consumer.id; variant=$variant; baselineComparison=$consumer.baselineComparison; packageVersion=$version; packageIds=@($closure.id); exit=0; commonOutput=$lines; binarySha256=$binaryBefore; binaryArtifact=$binaryArtifact; depsArtifact=$depsArtifact; assetsArtifact=$assetsArtifact; sourceFiles=@($fixture); directory=$work }
                ConvertTo-Json -InputObject $results -Depth 10 | Set-Content (Join-Path $run 'consumer-results.json')
                Write-Host "PASS $variant $($consumer.id) SDK=$sdk framework=$($lane.framework) CSharp=12 warningsAsErrors=true hashes=matched"
            }
        }
    } finally { Pop-Location }
}
# Pack-time producer evidence was validated by InspectOnly above; never restamp live obj inputs.
$packageRecord = Get-Content -Raw (Join-Path $Feed "package-manifest.$CandidateVersion.json") | ConvertFrom-Json
$buildTooling = @(Test-RecordedBuildTooling $inventory $packageRecord $CandidateVersion)
# This is a Meridian schemaVersion1 SBOM inventory, not a claimed CycloneDX/SPDX document.
$sbom = [ordered]@{consumerScope=$selection.Scope;selectedConsumerIds=$selection.SelectedIds;omittedConsumerIds=$selection.OmittedIds;schemaVersion=1;format='meridian-restored-package-inventory';manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();candidateVersion=$CandidateVersion;components=@($components.Values | Sort-Object id,version);consumerGraphs=$graphs;distributedPackages=$packageRecord.packages;buildTooling=$buildTooling;buildToolingScope='Private build tools are recorded separately from consumer distribution graphs'}
$sbom | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'sbom-inventory.json')
Write-Host "PASS packaged compiler consumers: $($results.Count); evidence=$run"
