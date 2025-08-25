# Deploy NFS Plugin Storage for PluginLoader Processor
# This script deploys the NFS server and configures PluginLoader for bidirectional file access

param(
    [switch]$WaitForReady = $true,
    [switch]$Verbose = $false
)

Write-Host "🚀 Deploying NFS Plugin Storage for PluginLoader Processor..." -ForegroundColor Green

# Set error action preference
$ErrorActionPreference = "Stop"

try {
    # Step 1: Deploy storage configurations
    Write-Host "📦 Step 1: Deploying storage configurations..." -ForegroundColor Yellow
    
    Write-Host "   - Deploying persistent volumes..." -ForegroundColor Cyan
    kubectl apply -f k8s/02-storage/persistent-volumes.yaml
    if ($LASTEXITCODE -ne 0) { throw "Failed to deploy persistent volumes" }
    
    Write-Host "   - Deploying NFS server..." -ForegroundColor Cyan
    kubectl apply -f k8s/02-storage/nfs-server.yaml
    if ($LASTEXITCODE -ne 0) { throw "Failed to deploy NFS server" }
    
    # Step 2: Wait for NFS server to be ready
    if ($WaitForReady) {
        Write-Host "⏳ Step 2: Waiting for NFS server to be ready..." -ForegroundColor Yellow
        kubectl wait --for=condition=ready pod -l app=design79-nfs-server -n design79-infrastructure --timeout=300s
        if ($LASTEXITCODE -ne 0) { throw "NFS server failed to become ready" }
        Write-Host "   ✅ NFS server is ready" -ForegroundColor Green
    }
    
    # Step 3: Deploy plugin storage
    Write-Host "📁 Step 3: Deploying plugin storage..." -ForegroundColor Yellow
    kubectl apply -f k8s/02-storage/plugin-storage.yaml
    if ($LASTEXITCODE -ne 0) { throw "Failed to deploy plugin storage" }
    
    # Step 4: Update PluginLoader configuration
    Write-Host "⚙️  Step 4: Updating PluginLoader configuration..." -ForegroundColor Yellow
    kubectl apply -f k8s/08-processors/pluginloader-processor/configmap.yaml
    if ($LASTEXITCODE -ne 0) { throw "Failed to update PluginLoader configuration" }
    
    # Step 5: Update PluginLoader deployment
    Write-Host "🔄 Step 5: Updating PluginLoader deployment..." -ForegroundColor Yellow
    kubectl apply -f k8s/08-processors/pluginloader-processor/deployment.yaml
    if ($LASTEXITCODE -ne 0) { throw "Failed to update PluginLoader deployment" }
    
    # Step 6: Wait for PluginLoader to be ready
    if ($WaitForReady) {
        Write-Host "⏳ Step 6: Waiting for PluginLoader to be ready..." -ForegroundColor Yellow
        kubectl wait --for=condition=ready pod -l app=design79-pluginloader-processor -n design79-infrastructure --timeout=300s
        if ($LASTEXITCODE -ne 0) { throw "PluginLoader failed to become ready" }
        Write-Host "   ✅ PluginLoader is ready" -ForegroundColor Green
    }
    
    Write-Host ""
    Write-Host "🎉 NFS Plugin Storage deployment completed successfully!" -ForegroundColor Green
    Write-Host ""
    
    # Display status information
    Write-Host "📊 Deployment Status:" -ForegroundColor Cyan
    Write-Host "   NFS Server:" -ForegroundColor White
    kubectl get pods -l app=design79-nfs-server -n design79-infrastructure -o wide
    
    Write-Host ""
    Write-Host "   PluginLoader Processor:" -ForegroundColor White
    kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure -o wide
    
    Write-Host ""
    Write-Host "   Storage:" -ForegroundColor White
    kubectl get pvc -l project=design79 -n design79-infrastructure
    
    Write-Host ""
    Write-Host "📁 File Access Information:" -ForegroundColor Cyan
    Write-Host "   Host Libraries Folder: C:\Users\Administrator\source\repos\Design80\Libraries" -ForegroundColor White
    Write-Host "   Pod Mount Point: /app/plugins" -ForegroundColor White
    Write-Host "   Access Mode: Bidirectional (Read/Write)" -ForegroundColor White
    
    Write-Host ""
    Write-Host "🔧 Next Steps:" -ForegroundColor Cyan
    Write-Host "   1. Test file access with: .\scripts\test-nfs-access.ps1" -ForegroundColor White
    Write-Host "   2. Configure plugins to use AssemblyBasePath: '/app/plugins'" -ForegroundColor White
    Write-Host "   3. FileReader/FileWriter plugins can now access host files bidirectionally" -ForegroundColor White

} catch {
    Write-Host ""
    Write-Host "❌ Deployment failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "🔍 Troubleshooting:" -ForegroundColor Yellow
    Write-Host "   - Check pod logs: kubectl logs -l app=design79-nfs-server -n design79-infrastructure" -ForegroundColor White
    Write-Host "   - Check events: kubectl get events -n design79-infrastructure --sort-by='.lastTimestamp'" -ForegroundColor White
    Write-Host "   - Check storage: kubectl get pv,pvc -n design79-infrastructure" -ForegroundColor White
    exit 1
}
