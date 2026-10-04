$ErrorActionPreference = 'Stop'

function Resolve-RepositoryPath([string] $Repo, [string] $Path) {
    $root = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($Path -notmatch '^(?!/)(?!.*(?:^|/)\.\.(?:/|$))[A-Za-z0-9_.\-/]+$') { throw "Unsafe repository path: $Path" }
    $resolved = $root
    foreach ($part in $Path.Split('/')) {
        $resolved = [IO.Path]::GetFullPath((Join-Path $resolved $part))
        $info = Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
        if ($info -and $info.LinkType) {
            $target = $info.ResolveLinkTarget($true)
            if ($target) { $resolved = $target.FullName } else { throw "Repository path escapes or has a broken link: $Path" }
        }
        if (-not $resolved.StartsWith("$root$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -and $resolved -ne $root) { throw "Repository path escapes: $Path" }
    }
    return $resolved
}

function Get-PackageClosure($Inventory, [string[]] $Ids) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($id in $Ids) { $queue.Enqueue($id) }
    while ($queue.Count) {
        $id = $queue.Dequeue()
        if (-not $seen.Add($id)) { continue }
        $package = @($Inventory.packages | Where-Object id -EQ $id)
        if ($package.Count -ne 1) { throw "Unknown inventory dependency: $id" }
        foreach ($dependency in $package[0].projectDependencies) { $queue.Enqueue($dependency) }
    }
    return @($Inventory.packages | Where-Object { $seen.Contains($_.id) })
}

function Read-PackageInventory([string] $Repo, [string] $Manifest) {
    $path = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $Repo $Manifest }
    $json = Get-Content -Raw -LiteralPath $path
    if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'packages.schema.json') -ErrorAction SilentlyContinue)) { throw 'Invalid package manifest' }
    $inventory = $json | ConvertFrom-Json
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $inventory.packages) {
        if (-not $seen.Add($package.id)) { throw "Duplicate package $($package.id)" }
        if (-not $package.PSObject.Properties['projectDependencies']) { $package | Add-Member projectDependencies @() }
        if (-not $package.PSObject.Properties['licenseExpression']) { $package | Add-Member licenseExpression 'MIT' }
    }
    $preceding = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $inventory.packages) {
        foreach ($external in $package.directPackageDependencies) {
            if ($external.id -in $inventory.packages.id) { throw "Inventory dependency must use projectDependencies: $($external.id)" }
        }
        foreach ($dependency in $package.projectDependencies) {
            if (-not $preceding.Contains($dependency)) { throw "Project dependency $dependency must precede $($package.id)" }
        }
        $null = $preceding.Add($package.id)
        $project = Resolve-RepositoryPath $Repo $package.project
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project $project" }
        foreach ($asset in $package.analyzerPaths) {
            $producer = Resolve-RepositoryPath $Repo $asset.project
            if (-not (Test-Path -LiteralPath $producer -PathType Leaf)) { throw "Missing analyzer producer $producer" }
            if ($asset.packagePath -notmatch '^analyzers/dotnet/cs/[A-Za-z0-9_.-]+\.(dll|pdb)$') { throw "Unsafe analyzer archive path: $($asset.packagePath)" }
        }
        if ($package.id -in @('Meridian.Mapping', 'Meridian.Mediator')) {
            $allowed = @('Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Extensions.Logging.Abstractions','Microsoft.Extensions.Localization.Abstractions')
            if ($package.projectDependencies.Count -or @($package.directPackageDependencies | Where-Object { $_.id -notin $allowed }).Count) { throw "Core dependency boundary violated: $($package.id)" }
            if ($package.baselineVersion -ne '2.1.3' -or $package.kind -ne 'runtime') { throw "Core baseline/kind mismatch: $($package.id)" }
        }
    }
    if (-not $inventory.PSObject.Properties['consumers']) { throw 'Manifest must declare consumers explicitly' }
    $consumerIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($consumer in $inventory.consumers) {
        if (-not $consumerIds.Add($consumer.id)) { throw "Duplicate consumer $($consumer.id)" }
        $project = Resolve-RepositoryPath $Repo $consumer.project
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing consumer project $project" }
        if ($consumer.project -notin $consumer.sourceFiles) { throw "Consumer project must be a declared source input: $($consumer.id)" }
        foreach ($source in $consumer.sourceFiles) {
            $full = Resolve-RepositoryPath $Repo $source
            if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or -not $full.StartsWith("$(Split-Path $project)$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) { throw "Invalid consumer source input: $source" }
        }
        $closure = @(Get-PackageClosure $inventory $consumer.packageIds)
        if ($consumer.baselineComparison -and @($closure | Where-Object { -not $_.baselineVersion }).Count) { throw "Baseline closure has no stable baseline: $($consumer.id)" }
    }
    return $inventory
}

function Invoke-ProjectEvaluation([string] $Dotnet, [string] $Project, [string[]] $Properties, [string[]] $Items = @(), [string[]] $Extra = @()) {
    $arguments = @('msbuild', $Project, '-nologo', "-getProperty:$($Properties -join ',')") + $Extra
    if ($Items.Count) { $arguments += "-getItem:$($Items -join ',')" }
    $output = & $Dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not evaluate $Project (exit=$LASTEXITCODE)" }
    return ($output -join "`n" | ConvertFrom-Json)
}

function Assert-SameSet([object[]] $Actual, [object[]] $Expected, [string] $Message) {
    $Actual = @($Actual | Where-Object { $null -ne $_ })
    $Expected = @($Expected | Where-Object { $null -ne $_ })
    if ($Actual.Count -ne $Expected.Count -or ($Actual.Count -and (Compare-Object $Actual $Expected))) { throw $Message }
}

function Get-DeclaredDependencies($Package, [string] $CandidateVersion) {
    return @(@($Package.directPackageDependencies | ForEach-Object { [ordered]@{id=$_.id;version=$_.version} }) + @($Package.projectDependencies | ForEach-Object { [ordered]@{id=$_;version=$CandidateVersion} }))
}

function Test-EvaluatedInventory($Inventory, [string] $Repo, [string] $Dotnet, [string] $Configuration) {
    $evaluations = @{}
    foreach ($package in $Inventory.packages) {
        $project = Resolve-RepositoryPath $Repo $package.project
        $properties = @('PackageId','AssemblyName','IsPackable','TargetFrameworks','TargetFramework','TargetPath','PackageValidationBaselineVersion','EnablePackageValidation','PackageLicenseExpression',$package.versionSource)
        $evaluated = Invoke-ProjectEvaluation $Dotnet $project $properties @('PackageReference','ProjectReference') @("-p:Configuration=$Configuration")
        $p = $evaluated.Properties
        if ($p.PackageId -ne $package.id -or $p.IsPackable -ne 'true') { throw "Package identity mismatch: $project" }
        if (-not $p.($package.versionSource)) { throw "Missing version property: $($package.versionSource)" }
        $tfms = if ($p.TargetFrameworks) { @($p.TargetFrameworks -split ';') } else { @($p.TargetFramework) }
        Assert-SameSet $tfms @($package.targetFrameworks) "TargetFramework mismatch: $project"
        if ([string]$p.PackageValidationBaselineVersion -ne [string]$package.baselineVersion -or ($package.baselineVersion -and $p.EnablePackageValidation -ne 'true')) { throw "API baseline mismatch: $project" }
        if ($p.PackageLicenseExpression -ne $package.licenseExpression) { throw "License expression mismatch: $project" }
        $analyzerTargets = @{}
        foreach ($tfm in $tfms) {
            $inner = Invoke-ProjectEvaluation $Dotnet $project $properties @('PackageReference','ProjectReference') @("-p:Configuration=$Configuration","-p:TargetFramework=$tfm")
            $actualDeps = @($inner.Items.PackageReference | Where-Object { $_.PrivateAssets -ne 'all' } | ForEach-Object { "$($_.Identity)=$($_.Version)" })
            Assert-SameSet $actualDeps @($package.directPackageDependencies | ForEach-Object { "$($_.id)=$($_.version)" }) "Direct dependency mismatch: $project ($tfm)"
            $runtimeReferences = @($inner.Items.ProjectReference | Where-Object { $_.ReferenceOutputAssembly -ne 'false' -and $_.PrivateAssets -ne 'all' })
            $actualProjects = @($runtimeReferences | ForEach-Object {
                $reference = $_
                $entry = @($Inventory.packages | Where-Object { (Resolve-RepositoryPath $Repo $_.project) -eq [IO.Path]::GetFullPath($reference.FullPath) })
                if ($entry.Count -ne 1) { throw "Project dependency mismatch: unlisted $($reference.FullPath)" }
                $entry[0].id
            })
            Assert-SameSet $actualProjects @($package.projectDependencies) "Project dependency mismatch: $project ($tfm)"
            $analyzerRefs = @($inner.Items.ProjectReference | Where-Object { $_.ReferenceOutputAssembly -eq 'false' -and $_.OutputItemType -eq 'Analyzer' })
            foreach ($reference in $analyzerRefs) {
                if (-not @($package.analyzerPaths | Where-Object { (Resolve-RepositoryPath $Repo $_.project) -eq [IO.Path]::GetFullPath($reference.FullPath) }).Count) { throw "Unlisted analyzer reference: $($reference.FullPath)" }
            }
            foreach ($analyzer in $package.analyzerPaths) {
                $producer = Resolve-RepositoryPath $Repo $analyzer.project
                if ($package.kind -eq 'analyzer' -and $producer -eq $project) {
                    $target = $inner
                    if ($target.Properties.TargetFramework -ne 'netstandard2.0') { throw "Analyzer target framework mismatch: $producer" }
                } else {
                    if (@($analyzerRefs | Where-Object { [IO.Path]::GetFullPath($_.FullPath) -eq $producer }).Count -ne 1) { throw "Analyzer producer is not an evaluated analyzer reference: $producer" }
                    $target = Invoke-ProjectEvaluation $Dotnet $producer @('TargetPath','TargetFramework') @() @("-p:Configuration=$Configuration")
                    if ($target.Properties.TargetFramework -ne 'netstandard2.0') { throw "Analyzer target framework mismatch: $producer" }
                }
                $output = [IO.Path]::ChangeExtension($target.Properties.TargetPath, [IO.Path]::GetExtension($analyzer.packagePath))
                if ([IO.Path]::GetFileName($output) -ne [IO.Path]::GetFileName($analyzer.packagePath)) { throw 'Analyzer target mismatch' }
                $analyzerTargets[$analyzer.packagePath] = $output
            }
        }
        if ($package.kind -eq 'analyzer' -and -not $package.analyzerPaths.Count) { throw "Analyzer package has no declared output: $($package.id)" }
        $evaluations[$package.id] = @{ properties=$p; analyzers=$analyzerTargets }
    }
    return $evaluations
}

function Read-ArchiveText($Archive, [string] $Path) {
    $entry = $Archive.GetEntry($Path)
    if (-not $entry) { throw "Missing archive asset $Path" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-ArchiveHash($Entry) {
    $stream = $Entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
}

Export-ModuleMember -Function Resolve-RepositoryPath,Get-PackageClosure,Read-PackageInventory,Invoke-ProjectEvaluation,Assert-SameSet,Get-DeclaredDependencies,Test-EvaluatedInventory,Read-ArchiveText,Get-ArchiveHash

function Select-ReleasePackages($Inventory, [string] $Repo, [string] $Dotnet, [string] $TagVersion) {
    if ($TagVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release tag version' }
    $evaluated = Test-EvaluatedInventory $Inventory $Repo $Dotnet 'Release'
    $selected = @($Inventory.packages | Where-Object { $evaluated[$_.id].properties.($_.versionSource) -eq $TagVersion })
    if (-not $selected.Count) { throw "No inventory package version matches tag $TagVersion" }
    $closure = @(Get-PackageClosure $Inventory @($selected.id))
    foreach ($dependency in $closure) {
        $version = $evaluated[$dependency.id].properties.($dependency.versionSource)
        if ($version -ne $TagVersion) { throw "Release dependency version mismatch: $($dependency.id) evaluated $version, tag $TagVersion" }
        if ($dependency.id -notin $selected.id) { throw "Release dependency closure omitted: $($dependency.id)" }
    }
    return $closure
}
Export-ModuleMember -Function Select-ReleasePackages

function Initialize-NuGetVersioning {
    if ('NuGet.Versioning.NuGetVersion' -as [type]) { return }
    # Use the active SDK's NuGet parser/comparer, matching restore identity semantics.
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $sdk = (& $dotnet --version) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve NuGet version identity SDK' }
    $installed = & $dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate NuGet version identity SDK' }
    $sdkLine = @($installed | Where-Object { $_.StartsWith("$sdk ") })
    if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]$') { throw 'Cannot locate active SDK NuGet.Versioning assembly' }
    $assembly = Join-Path $Matches[1] "$sdk/NuGet.Versioning.dll"
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Active SDK has no NuGet.Versioning assembly' }
    $null = [Reflection.Assembly]::LoadFrom($assembly)
}

function Read-RestoredComponents($Assets, [string] $Packages) {
    Initialize-NuGetVersioning
    $components = @()
    foreach ($library in $Assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $identity = $library.Name.Split('/')
        $id = $identity[0]; $version = $identity[1]
        $path = Join-Path $Packages "$($library.Value.path)/$($id.ToLowerInvariant()).$version.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
            if ($entry.Count -ne 1) { throw "Missing restored nuspec: $id" }
            [xml] $nuspec = Read-ArchiveText $archive $entry[0].FullName
            $nuspecVersion = [NuGet.Versioning.NuGetVersion]::Parse([string]$nuspec.package.metadata.version)
            $restoredVersion = [NuGet.Versioning.NuGetVersion]::Parse($version)
            if ($nuspec.package.metadata.id -ne $id -or -not [NuGet.Versioning.VersionComparer]::VersionRelease.Equals($nuspecVersion, $restoredVersion)) { throw "Restored nuspec identity mismatch: $id/$version" }
            $license = $nuspec.package.metadata.license
            $licenseFileHash = if ($license -and $license.type -eq 'file') { Get-ArchiveHash $archive.GetEntry($license.InnerText) } else { $null }
            $components += [ordered]@{ id=$id; version=$version; sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant(); nuspecSha256=(Get-ArchiveHash $entry[0]); restoreSha512=$library.Value.sha512; licenseType=if ($license) {$license.type} else {$null}; license=if ($license) {$license.InnerText} else {$null}; licenseFileSha256=$licenseFileHash; path=$path }
        } finally { $archive.Dispose() }
    }
    return $components
}

function Get-BytesHash([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function New-AnalyzerBuildEvidence([string] $Repo, [string] $Producer, [string] $Dotnet, [string] $Configuration, [string] $CandidateVersion, $Outputs, [string[]] $Extra) {
    $project = Resolve-RepositoryPath $Repo $Producer
    $assetsPath = Join-Path (Split-Path $project) 'obj/project.assets.json'
    if (-not (Test-Path $assetsPath)) { throw "Missing analyzer build dependency evidence: $Producer" }
    $bytes = [IO.File]::ReadAllBytes($assetsPath)
    $assets = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($folders.Count -ne 1) { throw "Analyzer build evidence needs one unambiguous package folder: $Producer" }
    $evaluated = Invoke-ProjectEvaluation $Dotnet $project @('TargetFramework','AssemblyName') @('PackageReference') (@("-p:Configuration=$Configuration") + $Extra)
    $roots = @($evaluated.Items.PackageReference | Where-Object { $_.PrivateAssets -eq 'all' } | Sort-Object Identity | ForEach-Object { [ordered]@{id=$_.Identity;version=$_.Version;privateAssets=$_.PrivateAssets} })
    # Keep portable identities/hashes, rather than the machine-specific cache path.
    $components = @(Read-RestoredComponents $assets $folders[0] | Sort-Object id,version | ForEach-Object {
        [ordered]@{id=$_.id;version=$_.version;sha256=$_.sha256;nuspecSha256=$_.nuspecSha256;restoreSha512=$_.restoreSha512;licenseType=$_.licenseType;license=$_.license;licenseFileSha256=$_.licenseFileSha256}
    })
    $projectBytes = [IO.File]::ReadAllBytes($project)
    $outputHashes = @($Outputs.Keys | Sort-Object | ForEach-Object { [ordered]@{packagePath=$_;sha256=(Get-FileHash $Outputs[$_] -Algorithm SHA256).Hash.ToLowerInvariant()} })
    $sdk = (& $Dotnet --version) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record analyzer build SDK' }
    $payload = [ordered]@{producer=$Producer;scope='private-analyzer-build';candidateVersion=$CandidateVersion;projectSha256=(Get-BytesHash $projectBytes);projectBase64=[Convert]::ToBase64String($projectBytes);framework=$evaluated.Properties.TargetFramework;assemblyName=$evaluated.Properties.AssemblyName;sdkVersion=$sdk;assetsSha256=(Get-BytesHash $bytes);assetsBase64=[Convert]::ToBase64String($bytes);privateRoots=$roots;components=$components;outputs=$outputHashes}
    # Hash exactly these serialized UTF-8 bytes. Inspection never reserializes to verify a digest.
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 30 -Compress))
    return [ordered]@{producer=$Producer;scope='private-analyzer-build';evidenceSha256=(Get-BytesHash $payloadBytes);evidenceBase64=[Convert]::ToBase64String($payloadBytes)}
}

function Test-RecordedBuildTooling($Inventory, $Record, [string] $CandidateVersion) {
    $producers = @($Inventory.packages.analyzerPaths.project | Select-Object -Unique)
    if ($producers.Count -and -not $Record.buildTooling) { throw 'Missing analyzer build provenance' }
    Assert-SameSet @($Record.buildTooling.producer) $producers 'Analyzer build provenance producer set mismatch'
    if ($Record.candidateVersion -ne $CandidateVersion) { throw 'Analyzer build provenance candidate mismatch' }
    $decoded = @()
    foreach ($snapshot in $Record.buildTooling) {
        if (-not $snapshot.evidenceBase64) { throw 'Missing analyzer build provenance payload' }
        $bytes = [Convert]::FromBase64String($snapshot.evidenceBase64)
        if ((Get-BytesHash $bytes) -ne $snapshot.evidenceSha256) { throw 'Analyzer build provenance digest mismatch' }
        $payload = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        if ($payload.producer -ne $snapshot.producer -or $payload.candidateVersion -ne $CandidateVersion -or $payload.scope -ne 'private-analyzer-build' -or $snapshot.scope -ne $payload.scope -or $payload.framework -ne 'netstandard2.0' -or -not $payload.assemblyName -or -not $payload.sdkVersion) { throw 'Analyzer build provenance identity mismatch' }
        if (-not $payload.assetsBase64 -or -not $payload.projectBase64) { throw 'Missing analyzer build provenance source inputs' }
        $assetsBytes = [Convert]::FromBase64String($payload.assetsBase64)
        if ((Get-BytesHash $assetsBytes) -ne $payload.assetsSha256 -or (Get-BytesHash ([Convert]::FromBase64String($payload.projectBase64))) -ne $payload.projectSha256) { throw 'Analyzer build provenance input hash mismatch' }
        $assets = [Text.Encoding]::UTF8.GetString($assetsBytes) | ConvertFrom-Json
        $assetProject = $assets.project.restore.projectPath.Replace('\','/')
        if (-not $assetProject.EndsWith("/$($payload.producer)", [StringComparison]::Ordinal)) { throw 'Analyzer build provenance project binding mismatch' }
        $libraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
        Assert-SameSet @($payload.components | ForEach-Object { "$($_.id)/$($_.version)" }) @($libraries.Name) 'Analyzer build provenance component closure mismatch'
        foreach ($component in $payload.components) {
            $identity = "$($component.id)/$($component.version)"
            if ($component.restoreSha512 -ne $assets.libraries.$identity.sha512 -or $component.sha256 -notmatch '^[0-9a-f]{64}$' -or $component.nuspecSha256 -notmatch '^[0-9a-f]{64}$' -or ($component.licenseType -and $component.licenseType -notin @('expression','file')) -or ($component.licenseType -and -not $component.license) -or ($component.licenseType -eq 'file' -and $component.licenseFileSha256 -notmatch '^[0-9a-f]{64}$')) { throw "Analyzer build provenance component metadata mismatch: $identity" }
        }
        $frameworks = @($assets.project.frameworks.PSObject.Properties)
        if ($frameworks.Count -ne 1 -or $frameworks[0].Name -ne $payload.framework) { throw 'Analyzer build provenance framework mismatch' }
        $privateIds = @($frameworks[0].Value.dependencies.PSObject.Properties | Where-Object { $_.Value.suppressParent -eq 'All' } | ForEach-Object Name)
        Assert-SameSet @($payload.privateRoots.id) $privateIds 'Analyzer build provenance private root closure mismatch'
        foreach ($root in $payload.privateRoots) {
            if ($root.privateAssets -ne 'all' -or -not $root.version) { throw 'Analyzer build provenance private root metadata mismatch' }
        }
        $declared = @($Inventory.packages.analyzerPaths | Where-Object project -EQ $snapshot.producer)
        Assert-SameSet @($payload.outputs.packagePath) @($declared.packagePath | Select-Object -Unique) 'Analyzer build provenance output closure mismatch'
        foreach ($package in $Inventory.packages) {
            $recordedPackage = @($Record.packages | Where-Object id -EQ $package.id)
            if ($recordedPackage.Count -ne 1) { throw 'Analyzer build provenance package identity mismatch' }
            Assert-SameSet @($recordedPackage[0].analyzers.packagePath) @($package.analyzerPaths.packagePath) 'Analyzer build provenance analyzer set mismatch'
            foreach ($analyzer in @($package.analyzerPaths | Where-Object project -EQ $snapshot.producer)) {
                $binding = @($recordedPackage[0].analyzers | Where-Object packagePath -EQ $analyzer.packagePath)
                if ($binding.Count -ne 1 -or $binding[0].project -ne $analyzer.project -or $binding[0].toolingEvidenceSha256 -ne $snapshot.evidenceSha256) { throw 'Analyzer build provenance identity mismatch' }
                $output = @($payload.outputs | Where-Object packagePath -EQ $analyzer.packagePath)
                if ($output.Count -ne 1 -or $output[0].sha256 -notmatch '^[0-9a-f]{64}$' -or $output[0].sha256 -ne $binding[0].sha256 -or $output[0].sha256 -ne $binding[0].producerSha256 -or [IO.Path]::GetFileNameWithoutExtension($analyzer.packagePath) -ne $payload.assemblyName) { throw 'Analyzer build provenance binary binding mismatch' }
            }
        }
        $payload | Add-Member evidenceSha256 $snapshot.evidenceSha256
        $decoded += $payload
    }
    return $decoded
}
Export-ModuleMember -Function Read-RestoredComponents,Get-BytesHash,New-AnalyzerBuildEvidence,Test-RecordedBuildTooling
