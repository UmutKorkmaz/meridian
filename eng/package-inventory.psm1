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
    return $selected
}
Export-ModuleMember -Function Select-ReleasePackages
