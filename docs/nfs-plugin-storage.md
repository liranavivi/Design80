# NFS Plugin Storage Implementation

This document describes the NFS-based storage solution for the PluginLoader Processor, enabling bidirectional file access between the host machine and Kubernetes pods.

## Overview

The implementation provides:
- **Three-directory bidirectional access** between host and PluginLoader pods
- **Plugin Libraries**: Host Libraries folder ↔ Pod `/app/plugins`
- **Input Processing**: Host `C:\temp\input` → Pod `/app/input` (files to process)
- **Output Processing**: Pod `/app/output` → Host `C:\temp\output` (processed files)
- **Real-time synchronization** of all files without container rebuilds
- **Multi-pod support** with ReadWriteMany access mode
- **Complete workflow support** for FileReader/FileWriter plugins

## Architecture

```
Host Machine (Windows)
├── C:\Users\Administrator\source\repos\Design80\Libraries\
│   ├── Plugin.FileReader\v1.0.0.0\      # Plugin assemblies
│   └── Plugin.FileWriter\v1.0.0.0\      # Plugin assemblies
├── C:\temp\input\                        # Files TO process (Host → Pod)
│   ├── document1.pdf
│   ├── archive.zip
│   └── data.csv
└── C:\temp\output\                       # Files AFTER process (Pod → Host)
    ├── processed_document1.txt
    ├── extracted_files\
    └── processed_data.json

Kubernetes Cluster
├── NFS Server Pod
│   ├── Mounts host Libraries → /exports/plugins
│   ├── Mounts host input → /exports/input
│   ├── Mounts host output → /exports/output
│   └── Shares all via NFS protocol
│
└── PluginLoader Processor Pods
    ├── Mount plugins at /app/plugins
    ├── Mount input at /app/input
    └── Mount output at /app/output
```

## Components

### 1. Storage Configuration

**Files:**
- `k8s/02-storage/persistent-volumes.yaml` - Updated with NFS server PV/PVC
- `k8s/02-storage/nfs-server.yaml` - NFS server deployment
- `k8s/02-storage/plugin-storage.yaml` - Plugin storage PV/PVC

**Key Features:**
- hostPath volume maps to host Libraries folder
- NFS server shares the mounted volume
- ReadWriteMany access for multiple pods

### 2. NFS Server

**Image:** `itsthenetwork/nfs-server-alpine:latest`
**Service:** `design79-nfs-server.design79-infrastructure.svc.cluster.local:2049`

**Configuration:**
- Privileged container for NFS operations
- Exports `/exports` directory (mapped to host Libraries)
- Allows Kubernetes pod network access

### 3. PluginLoader Integration

**Updated Files:**
- `k8s/08-processors/pluginloader-processor/deployment.yaml`
- `k8s/08-processors/pluginloader-processor/configmap.yaml`

**Changes:**
- Added volume mount at `/app/plugins`
- Set `PluginLoader__AssemblyBasePath: "/app/plugins"`
- Bidirectional read/write access

## File Path Mapping

| Location | Path | Usage |
|----------|------|-------|
| **Host** | `C:\Users\Administrator\source\repos\Design80\Libraries\Plugin.FileReader\v1.0.0.0\Plugin.FileReader.dll` | Plugin assembly location |
| **Pod** | `/app/plugins/Plugin.FileReader/v1.0.0.0/Plugin.FileReader.dll` | PluginManager loads from here |
| **Config** | `AssemblyBasePath: "/app/plugins"` | Configuration sent to PluginLoader |

## Plugin Configuration

When configuring plugins, use **pod paths** (not host paths):

```json
{
  "AssemblyBasePath": "/app/plugins",
  "AssemblyName": "Plugin.FileReader",
  "Version": "1.0.0.0",
  "TypeName": "Plugin.FileReader.FileReaderPlugin"
}
```

For plugin-specific file operations:
```json
{
  "FolderPath": "/app/plugins/input",
  "OutputPath": "/app/plugins/output"
}
```

## Deployment

### Prerequisites
- Kubernetes cluster running
- Host Libraries folder exists: `C:\Users\Administrator\source\repos\Design80\Libraries`
- Existing PluginLoader processor deployment

### Deploy NFS Storage

```powershell
# Deploy all components
.\scripts\deploy-nfs-plugin-storage.ps1

# Deploy with verbose output
.\scripts\deploy-nfs-plugin-storage.ps1 -Verbose

# Deploy without waiting for readiness
.\scripts\deploy-nfs-plugin-storage.ps1 -WaitForReady:$false
```

### Manual Deployment Steps

```bash
# 1. Deploy storage configurations
kubectl apply -f k8s/02-storage/persistent-volumes.yaml
kubectl apply -f k8s/02-storage/nfs-server.yaml

# 2. Wait for NFS server
kubectl wait --for=condition=ready pod -l app=design79-nfs-server -n design79-infrastructure

# 3. Deploy plugin storage
kubectl apply -f k8s/02-storage/plugin-storage.yaml

# 4. Update PluginLoader
kubectl apply -f k8s/08-processors/pluginloader-processor/configmap.yaml
kubectl apply -f k8s/08-processors/pluginloader-processor/deployment.yaml
```

## Testing

### Test Bidirectional Access

```powershell
# Run comprehensive tests
.\scripts\test-nfs-access.ps1

# Run tests and cleanup
.\scripts\test-nfs-access.ps1 -Cleanup
```

### Manual Testing

```bash
# Get PluginLoader pod name
POD_NAME=$(kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure -o jsonpath="{.items[0].metadata.name}")

# Test host → pod access
echo "Test from host" > C:\Users\Administrator\source\repos\Design80\Libraries\test.txt
kubectl exec $POD_NAME -n design79-infrastructure -- cat /app/plugins/test.txt

# Test pod → host access
kubectl exec $POD_NAME -n design79-infrastructure -- sh -c "echo 'Test from pod' > /app/plugins/test-pod.txt"
cat C:\Users\Administrator\source\repos\Design80\Libraries\test-pod.txt
```

## Troubleshooting

### Common Issues

1. **NFS Server Not Starting**
   ```bash
   kubectl logs -l app=design79-nfs-server -n design79-infrastructure
   kubectl describe pod -l app=design79-nfs-server -n design79-infrastructure
   ```

2. **Mount Failures**
   ```bash
   kubectl describe pod -l app=design79-pluginloader-processor -n design79-infrastructure
   kubectl get events -n design79-infrastructure --sort-by='.lastTimestamp'
   ```

3. **Permission Issues**
   ```bash
   kubectl exec <pod-name> -n design79-infrastructure -- ls -la /app/plugins
   ```

### Verification Commands

```bash
# Check all components
kubectl get pods,svc,pvc -l project=design79 -n design79-infrastructure

# Check NFS server status
kubectl get pods -l app=design79-nfs-server -n design79-infrastructure

# Check PluginLoader status
kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure

# Check storage
kubectl get pv,pvc -n design79-infrastructure | grep plugin
```

## Benefits

- ✅ **Real-time plugin updates** without container rebuilds
- ✅ **Bidirectional file access** for FileReader/FileWriter plugins
- ✅ **Multi-pod support** with shared storage
- ✅ **Kubernetes native** solution
- ✅ **Development efficiency** for plugin development and testing

## Security Considerations

- NFS server runs with privileged access
- Network traffic stays within Kubernetes cluster
- File permissions may need adjustment for Windows/Linux compatibility
- Consider backup strategy for plugin files
