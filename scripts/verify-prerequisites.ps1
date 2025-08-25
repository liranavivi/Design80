# Verify Prerequisites for NFS Plugin Storage Implementation
# This script checks if all prerequisites are met before deploying NFS storage

Write-Host "🔍 Verifying Prerequisites for NFS Plugin Storage..." -ForegroundColor Green

$ErrorActionPreference = "Continue"
$allGood = $true

# Check 1: Kubernetes cluster connectivity
Write-Host ""
Write-Host "📡 Checking Kubernetes cluster connectivity..." -ForegroundColor Yellow
try {
    $clusterInfo = kubectl cluster-info 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✅ Kubernetes cluster is accessible" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Cannot connect to Kubernetes cluster" -ForegroundColor Red
        $allGood = $false
    }
} catch {
    Write-Host "   ❌ kubectl command failed" -ForegroundColor Red
    $allGood = $false
}

# Check 2: Namespace exists
Write-Host ""
Write-Host "🏷️  Checking namespace..." -ForegroundColor Yellow
try {
    $namespace = kubectl get namespace design79-infrastructure 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✅ Namespace 'design79-infrastructure' exists" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Namespace 'design79-infrastructure' not found" -ForegroundColor Red
        Write-Host "   💡 Create it with: kubectl apply -f k8s/01-namespace/namespace.yaml" -ForegroundColor Cyan
        $allGood = $false
    }
} catch {
    Write-Host "   ❌ Failed to check namespace" -ForegroundColor Red
    $allGood = $false
}

# Check 3: Host Libraries folder exists
Write-Host ""
Write-Host "📁 Checking host Libraries folder..." -ForegroundColor Yellow
$librariesPath = "C:\Users\Administrator\source\repos\Design80\Libraries"
if (Test-Path $librariesPath -PathType Container) {
    Write-Host "   ✅ Libraries folder exists: $librariesPath" -ForegroundColor Green
    
    # Check for existing plugins
    $plugins = Get-ChildItem $librariesPath -Directory -ErrorAction SilentlyContinue
    if ($plugins.Count -gt 0) {
        Write-Host "   📦 Found plugins:" -ForegroundColor Cyan
        $plugins | ForEach-Object { Write-Host "     - $($_.Name)" -ForegroundColor White }
    } else {
        Write-Host "   ⚠️  No plugins found (this is normal for initial setup)" -ForegroundColor Yellow
    }
} else {
    Write-Host "   ❌ Libraries folder not found: $librariesPath" -ForegroundColor Red
    Write-Host "   💡 Create the folder or adjust the path in the configuration" -ForegroundColor Cyan
    $allGood = $false
}

# Check 4: PluginLoader processor exists
Write-Host ""
Write-Host "🔧 Checking PluginLoader processor..." -ForegroundColor Yellow
try {
    $pluginLoader = kubectl get deployment design79-pluginloader-processor -n design79-infrastructure 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✅ PluginLoader processor deployment exists" -ForegroundColor Green
        
        # Check if it's running
        $pods = kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure -o jsonpath="{.items[*].status.phase}" 2>$null
        if ($pods -like "*Running*") {
            Write-Host "   ✅ PluginLoader processor is running" -ForegroundColor Green
        } else {
            Write-Host "   ⚠️  PluginLoader processor is not running" -ForegroundColor Yellow
        }
    } else {
        Write-Host "   ❌ PluginLoader processor deployment not found" -ForegroundColor Red
        Write-Host "   💡 Deploy it first with the existing deployment files" -ForegroundColor Cyan
        $allGood = $false
    }
} catch {
    Write-Host "   ❌ Failed to check PluginLoader processor" -ForegroundColor Red
    $allGood = $false
}

# Check 5: Storage class availability
Write-Host ""
Write-Host "💾 Checking storage class..." -ForegroundColor Yellow
try {
    $storageClass = kubectl get storageclass hostpath 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✅ Storage class 'hostpath' is available" -ForegroundColor Green
    } else {
        Write-Host "   ⚠️  Storage class 'hostpath' not found" -ForegroundColor Yellow
        Write-Host "   💡 This might be normal depending on your Kubernetes setup" -ForegroundColor Cyan
    }
} catch {
    Write-Host "   ❌ Failed to check storage class" -ForegroundColor Red
}

# Check 6: Required files exist
Write-Host ""
Write-Host "📄 Checking required configuration files..." -ForegroundColor Yellow

$requiredFiles = @(
    "k8s/02-storage/persistent-volumes.yaml",
    "k8s/02-storage/nfs-server.yaml", 
    "k8s/02-storage/plugin-storage.yaml",
    "k8s/08-processors/pluginloader-processor/deployment.yaml",
    "k8s/08-processors/pluginloader-processor/configmap.yaml"
)

$missingFiles = @()
foreach ($file in $requiredFiles) {
    if (Test-Path $file) {
        Write-Host "   ✅ $file" -ForegroundColor Green
    } else {
        Write-Host "   ❌ $file" -ForegroundColor Red
        $missingFiles += $file
        $allGood = $false
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host "   💡 Missing files need to be created before deployment" -ForegroundColor Cyan
}

# Summary
Write-Host ""
Write-Host "📊 Prerequisites Summary:" -ForegroundColor Cyan
if ($allGood) {
    Write-Host "   🎉 All prerequisites met! Ready for deployment." -ForegroundColor Green
    Write-Host ""
    Write-Host "🚀 Next Steps:" -ForegroundColor Cyan
    Write-Host "   1. Run: .\scripts\deploy-nfs-plugin-storage.ps1" -ForegroundColor White
    Write-Host "   2. Test: .\scripts\test-nfs-access.ps1" -ForegroundColor White
    Write-Host "   3. Configure plugins to use AssemblyBasePath: '/app/plugins'" -ForegroundColor White
} else {
    Write-Host "   ⚠️  Some prerequisites are missing. Please address the issues above." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "🔧 Common Solutions:" -ForegroundColor Cyan
    Write-Host "   - Ensure kubectl is configured and connected to your cluster" -ForegroundColor White
    Write-Host "   - Create the design79-infrastructure namespace if missing" -ForegroundColor White
    Write-Host "   - Ensure the Libraries folder exists on the host" -ForegroundColor White
    Write-Host "   - Deploy the PluginLoader processor if missing" -ForegroundColor White
}

Write-Host ""
exit $(if ($allGood) { 0 } else { 1 })
