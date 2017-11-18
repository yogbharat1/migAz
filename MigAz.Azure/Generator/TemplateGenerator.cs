﻿using System;
using System.Collections.Generic;
using System.IO;
using MigAz.Core.Interface;
using MigAz.Core.ArmTemplate;
using System.Threading.Tasks;
using System.Text;
using System.Windows.Forms;
using MigAz.Core.Generator;
using System.Collections;
using MigAz.Azure.Models;
using Newtonsoft.Json;
using System.Reflection;
using System.Linq;

namespace MigAz.Azure.Generator
{
    public class TemplateGenerator
    {
        private Guid _ExecutionGuid = Guid.NewGuid();
        private List<ArmResource> _Resources = new List<ArmResource>();
        private Dictionary<string, Core.ArmTemplate.Parameter> _Parameters = new Dictionary<string, Core.ArmTemplate.Parameter>();
        private List<MigAzGeneratorAlert> _Alerts = new List<MigAzGeneratorAlert>();
        private List<MigrationTarget.StorageAccount> _TemporaryStorageAccounts = new List<MigrationTarget.StorageAccount>();
        private ILogProvider _logProvider;
        private IStatusProvider _statusProvider;
        private Dictionary<string, MemoryStream> _TemplateStreams = new Dictionary<string, MemoryStream>();
        private string _OutputDirectory = String.Empty;
        private ISubscription _SourceSubscription;
        private ISubscription _TargetSubscription;
        private ExportArtifacts _ExportArtifacts;
        private List<CopyBlobDetail> _CopyBlobDetails = new List<CopyBlobDetail>();
        private ITelemetryProvider _telemetryProvider;
        private ISettingsProvider _settingsProvider;

        public delegate Task AfterTemplateChangedHandler(TemplateGenerator sender);
        public event EventHandler AfterTemplateChanged;

        private TemplateGenerator() { }

        public TemplateGenerator(ILogProvider logProvider, IStatusProvider statusProvider, ISubscription sourceSubscription, ISubscription targetSubscription, ITelemetryProvider telemetryProvider, ISettingsProvider settingsProvider)
        {
            _logProvider = logProvider;
            _statusProvider = statusProvider;
            _SourceSubscription = sourceSubscription;
            _TargetSubscription = targetSubscription;
            _telemetryProvider = telemetryProvider;
            _settingsProvider = settingsProvider;
        }

        public ILogProvider LogProvider
        {
            get { return _logProvider; }
        }

        public IStatusProvider StatusProvider
        {
            get { return _statusProvider; }
        }

        public ISubscription SourceSubscription { get { return _SourceSubscription; } set { _SourceSubscription = value; } }
        public ISubscription TargetSubscription { get { return _TargetSubscription; } set { _TargetSubscription = value; } }

        public Guid ExecutionGuid
        {
            get { return _ExecutionGuid; }
        }

        public List<MigAzGeneratorAlert> Alerts
        {
            get { return _Alerts; }
            set { _Alerts = value; }
        }
        public String OutputDirectory
        {
            get { return _OutputDirectory; }
            set
            {
                if (value == null)
                    throw new ArgumentException("OutputDirectory cannot be null.");

                if (value.EndsWith(@"\"))
                    _OutputDirectory = value;
                else
                    _OutputDirectory = value + @"\";
            }
        }

        public List<ArmResource> Resources { get { return _Resources; } }
        public Dictionary<string, Core.ArmTemplate.Parameter> Parameters { get { return _Parameters; } }
        public Dictionary<string, MemoryStream> TemplateStreams { get { return _TemplateStreams; } }

        public bool HasErrors
        {
            get
            {
                foreach (MigAzGeneratorAlert alert in this.Alerts)
                {
                    if (alert.AlertType == AlertType.Error)
                        return true;
                }

                return false;
            }
        }

        public MigAzGeneratorAlert SeekAlert(string alertMessage)
        {
            foreach (MigAzGeneratorAlert alert in this.Alerts)
            {
                if (alert.Message.Contains(alertMessage))
                    return alert;
            }

            return null;
        }

        public bool IsProcessed(ArmResource resource)
        {
            return this.Resources.Contains(resource);
        }

        private void AddResource(ArmResource resource)
        {
            if (!IsProcessed(resource))
            {
                this.Resources.Add(resource);
                _logProvider.WriteLog("TemplateResult.AddResource", resource.type + resource.name + " added.");
            }
            else
                _logProvider.WriteLog("TemplateResult.AddResource", resource.type + resource.name + " already exists.");

        }

        public void AddAlert(AlertType alertType, string message, object sourceObject)
        {
            this.Alerts.Add(new MigAzGeneratorAlert(alertType, message, sourceObject));
        }

        public bool ResourceExists(Type type, string objectName)
        {
            object resource = GetResource(type, objectName);
            return resource != null;
        }

        public object GetResource(Type type, string objectName)
        {
            foreach (ArmResource armResource in this.Resources)
            {
                if (armResource.GetType() == type && armResource.name == objectName)
                    return armResource;
            }

            return null;
        }

        // Use of Treeview has been added here with aspect of transitioning full output towards this as authoritative source
        // Thought is that ExportArtifacts phases out, as it is providing limited context availability.
        public new async Task UpdateArtifacts(IExportArtifacts artifacts)
        {
            LogProvider.WriteLog("UpdateArtifacts", "Start - Execution " + this.ExecutionGuid.ToString());

            Alerts.Clear();
            _TemporaryStorageAccounts.Clear();

            _ExportArtifacts = (ExportArtifacts)artifacts;

            if (_ExportArtifacts.ResourceGroup == null)
            {
                this.AddAlert(AlertType.Error, "Target Resource Group must be provided for template generation.", _ExportArtifacts.ResourceGroup);
            }
            else
            {
                if (_ExportArtifacts.ResourceGroup.TargetLocation == null)
                {
                    this.AddAlert(AlertType.Error, "Target Resource Group Location must be provided for template generation.", _ExportArtifacts.ResourceGroup);
                }
            }

            foreach (MigrationTarget.NetworkSecurityGroup targetNetworkSecurityGroup in _ExportArtifacts.NetworkSecurityGroups)
            {
                if (targetNetworkSecurityGroup.TargetName == string.Empty)
                    this.AddAlert(AlertType.Error, "Target Name for Network Security Group must be specified.", targetNetworkSecurityGroup);
            }

            foreach (MigrationTarget.LoadBalancer targetLoadBalancer in _ExportArtifacts.LoadBalancers)
            {
                if (targetLoadBalancer.Name == string.Empty)
                    this.AddAlert(AlertType.Error, "Target Name for Load Balancer must be specified.", targetLoadBalancer);

                if (targetLoadBalancer.FrontEndIpConfigurations.Count == 0)
                {
                    this.AddAlert(AlertType.Error, "Load Balancer must have a FrontEndIpConfiguration.", targetLoadBalancer);
                }
                else
                {
                    if (targetLoadBalancer.LoadBalancerType == MigrationTarget.LoadBalancerType.Internal)
                    {
                        if (targetLoadBalancer.FrontEndIpConfigurations[0].TargetSubnet == null)
                        {
                            this.AddAlert(AlertType.Error, "Internal Load Balancer must have an internal Subnet association.", targetLoadBalancer);
                        }
                    }
                    else
                    {
                        if (targetLoadBalancer.FrontEndIpConfigurations[0].PublicIp == null)
                        {
                            this.AddAlert(AlertType.Error, "Public Load Balancer must have either a Public IP association.", targetLoadBalancer);
                        }
                        else
                        {
                            // Ensure the selected Public IP Address is "in the migration" as a target new Public IP Object
                            bool publicIpExistsInMigration = false;
                            foreach (Azure.MigrationTarget.PublicIp publicIp in _ExportArtifacts.PublicIPs)
                            {
                                if (publicIp.Name == targetLoadBalancer.FrontEndIpConfigurations[0].PublicIp.Name)
                                {
                                    publicIpExistsInMigration = true;
                                    break;
                                }
                            }

                            if (!publicIpExistsInMigration)
                                this.AddAlert(AlertType.Error, "Public IP '" + targetLoadBalancer.FrontEndIpConfigurations[0].PublicIp.Name + "' specified for Load Balancer '" + targetLoadBalancer.ToString() + "' is not included in the migration template.", targetLoadBalancer);
                        }
                    }
                }
            }

            foreach (Azure.MigrationTarget.AvailabilitySet availablitySet in _ExportArtifacts.AvailablitySets)
            {
                if (!availablitySet.IsManagedDisks && !availablitySet.IsUnmanagedDisks)
                {
                    this.AddAlert(AlertType.Error, "All OS and Data Disks for Virtual Machines contained within Availablity Set '" + availablitySet.ToString() + "' should be either Unmanaged Disks or Managed Disks for consistent deployment.", availablitySet);
                }
            }

            foreach (Azure.MigrationTarget.VirtualMachine virtualMachine in _ExportArtifacts.VirtualMachines)
            {
                if (virtualMachine.TargetName == string.Empty)
                    this.AddAlert(AlertType.Error, "Target Name for Virtual Machine '" + virtualMachine.ToString() + "' must be specified.", virtualMachine);

                if (virtualMachine.TargetAvailabilitySet == null)
                {
                    if (virtualMachine.OSVirtualHardDisk.TargetStorage != null && virtualMachine.OSVirtualHardDisk.TargetStorage.StorageAccountType != StorageAccountType.Premium_LRS)
                        this.AddAlert(AlertType.Warning, "Virtual Machine '" + virtualMachine.ToString() + "' is not part of an Availability Set.  OS Disk must be migrated to Azure Premium Storage to receive an Azure SLA for single server deployments.", virtualMachine);

                    foreach (Azure.MigrationTarget.Disk dataDisk in virtualMachine.DataDisks)
                    {
                        if (dataDisk.TargetStorage != null && dataDisk.TargetStorage.StorageAccountType != StorageAccountType.Premium_LRS)
                            this.AddAlert(AlertType.Warning, "Virtual Machine '" + virtualMachine.ToString() + "' is not part of an Availability Set.  Data Disk '" + dataDisk.ToString() + "' must be migrated to Azure Premium Storage to receive an Azure SLA for single server deployments.", virtualMachine);
                    }
                }

                if (virtualMachine.TargetSize == null)
                {
                    this.AddAlert(AlertType.Error, "Target Size for Virtual Machine '" + virtualMachine.ToString() + "' must be specified.", virtualMachine);
                }
                else
                {
                    // Ensure that the selected target size is available in the target Azure Location
                    if (_ExportArtifacts.ResourceGroup != null && _ExportArtifacts.ResourceGroup.TargetLocation != null)
                    {
                        if (_ExportArtifacts.ResourceGroup.TargetLocation.VMSizes == null || _ExportArtifacts.ResourceGroup.TargetLocation.VMSizes.Count == 0)
                        {
                            this.AddAlert(AlertType.Error, "No ARM VM Sizes are available for Azure Location '" + _ExportArtifacts.ResourceGroup.TargetLocation.DisplayName + "'.", virtualMachine);
                        }
                        else
                        {
                            // Ensure selected target VM Size is available in the Target Azure Location
                            Arm.VMSize matchedVmSize = _ExportArtifacts.ResourceGroup.TargetLocation.VMSizes.Where(a => a.Name == virtualMachine.TargetSize.Name).FirstOrDefault();
                            if (matchedVmSize == null)
                                this.AddAlert(AlertType.Error, "Specified VM Size '" + virtualMachine.TargetSize.Name + "' for Virtual Machine '" + virtualMachine.ToString() + "' is invalid as it is not available in Azure Location '" + _ExportArtifacts.ResourceGroup.TargetLocation.DisplayName + "'.", virtualMachine);
                        }
                    }
                }

                foreach (Azure.MigrationTarget.NetworkInterface networkInterface in virtualMachine.NetworkInterfaces)
                {
                    // Seek the inclusion of the Network Interface in the export object
                    bool networkInterfaceExistsInExport = false;
                    foreach (Azure.MigrationTarget.NetworkInterface targetNetworkInterface in _ExportArtifacts.NetworkInterfaces)
                    {
                        if (String.Compare(networkInterface.SourceName, targetNetworkInterface.SourceName, true) == 0)
                        {
                            networkInterfaceExistsInExport = true;
                            break;
                        }
                    }
                    if (!networkInterfaceExistsInExport)
                    {
                        this.AddAlert(AlertType.Error, "Network Interface Card (NIC) '" + networkInterface.ToString() + "' is used by Virtual Machine '" + virtualMachine.ToString() + "', but is not included in the exported resources.", virtualMachine);
                    }

                    if (networkInterface.NetworkSecurityGroup != null)
                    {
                        MigrationTarget.NetworkSecurityGroup networkSecurityGroupInMigration = _ExportArtifacts.SeekNetworkSecurityGroup(networkInterface.NetworkSecurityGroup.ToString());

                        if (networkSecurityGroupInMigration == null)
                        {
                            this.AddAlert(AlertType.Error, "Network Interface Card (NIC) '" + networkInterface.ToString() + "' utilizes Network Security Group (NSG) '" + networkInterface.NetworkSecurityGroup.ToString() + "', but the NSG resource is not added into the migration template.", networkInterface);
                        }
                    }

                    foreach (Azure.MigrationTarget.NetworkInterfaceIpConfiguration ipConfiguration in networkInterface.TargetNetworkInterfaceIpConfigurations)
                    {
                        if (ipConfiguration.TargetVirtualNetwork == null)
                            this.AddAlert(AlertType.Error, "Target Virtual Network for Virtual Machine '" + virtualMachine.ToString() + "' Network Interface '" + networkInterface.ToString() + "' must be specified.", networkInterface);
                        else
                        {
                            if (ipConfiguration.TargetVirtualNetwork.GetType() == typeof(MigrationTarget.VirtualNetwork))
                            {
                                MigrationTarget.VirtualNetwork virtualMachineTargetVirtualNetwork = (MigrationTarget.VirtualNetwork)ipConfiguration.TargetVirtualNetwork;
                                bool targetVNetExists = false;

                                foreach (MigrationTarget.VirtualNetwork targetVirtualNetwork in _ExportArtifacts.VirtualNetworks)
                                {
                                    if (targetVirtualNetwork.TargetName == virtualMachineTargetVirtualNetwork.TargetName)
                                    {
                                        targetVNetExists = true;
                                        break;
                                    }
                                }

                                if (!targetVNetExists)
                                    this.AddAlert(AlertType.Error, "Target Virtual Network '" + virtualMachineTargetVirtualNetwork.ToString() + "' for Virtual Machine '" + virtualMachine.ToString() + "' Network Interface '" + networkInterface.ToString() + "' is invalid, as it is not included in the migration / template.", networkInterface);
                            }
                        }

                        if (ipConfiguration.TargetSubnet == null)
                            this.AddAlert(AlertType.Error, "Target Subnet for Virtual Machine '" + virtualMachine.ToString() + "' Network Interface '" + networkInterface.ToString() + "' must be specified.", networkInterface);

                        if (ipConfiguration.TargetPublicIp != null)
                        {
                            MigrationTarget.PublicIp publicIpInMigration = _ExportArtifacts.SeekPublicIp(ipConfiguration.TargetPublicIp.ToString());

                            if (publicIpInMigration == null)
                            {
                                this.AddAlert(AlertType.Error, "Network Interface Card (NIC) '" + networkInterface.ToString() + "' IP Configuration '" + ipConfiguration.ToString() + "' utilizes Public IP '" + ipConfiguration.TargetPublicIp.ToString() + "', but the Public IP resource is not added into the migration template.", networkInterface);
                            }
                        }
                    }
                }

                ValidateVMDisk(virtualMachine.OSVirtualHardDisk);
                foreach (MigrationTarget.Disk dataDisk in virtualMachine.DataDisks)
                {
                    ValidateVMDisk(dataDisk);
                }

                if (!virtualMachine.IsManagedDisks && !virtualMachine.IsUnmanagedDisks)
                {
                    this.AddAlert(AlertType.Error, "All OS and Data Disks for Virtual Machine '" + virtualMachine.ToString() + "' should be either Unmanaged Disks or Managed Disks for consistent deployment.", virtualMachine);
                }
            }

            foreach (Azure.MigrationTarget.Disk targetDisk in _ExportArtifacts.Disks)
            {
                ValidateDiskStandards(targetDisk);
            }

            // todo now asap - Add test for NSGs being present in Migration
            //MigrationTarget.NetworkSecurityGroup targetNetworkSecurityGroup = (MigrationTarget.NetworkSecurityGroup)_ExportArtifacts.SeekNetworkSecurityGroup(targetSubnet.NetworkSecurityGroup.ToString());
            //if (targetNetworkSecurityGroup == null)
            //{
            //    this.AddAlert(AlertType.Error, "Subnet '" + subnet.name + "' utilized ASM Network Security Group (NSG) '" + targetSubnet.NetworkSecurityGroup.ToString() + "', which has not been added to the ARM Subnet as the NSG was not included in the ARM Template (was not selected as an included resources for export).", targetNetworkSecurityGroup);
            //}

            // todo add Warning about availability set with only single VM included

            // todo add error if existing target disk storage is not in the same data center / region as vm.

            LogProvider.WriteLog("UpdateArtifacts", "Start OnTemplateChanged Event");
            OnTemplateChanged();
            LogProvider.WriteLog("UpdateArtifacts", "End OnTemplateChanged Event");

            StatusProvider.UpdateStatus("Ready");

            LogProvider.WriteLog("UpdateArtifacts", "End - Execution " + this.ExecutionGuid.ToString());
        }

        private void ValidateVMDisk(MigrationTarget.Disk targetDisk)
        {
            if (targetDisk.IsManagedDisk)
            {
                // All Managed Disks are included in the Export Artifacts, so we aren't including a call here to ValidateDiskStandards for Managed Disks.  
                // Only non Managed Disks are validated against Disk Standards below.

                // VM References a managed disk, ensure it is included in the Export Artifacts
                bool targetDiskInExport = false;
                foreach (Azure.MigrationTarget.Disk exportDisk in _ExportArtifacts.Disks)
                {
                    if (targetDisk.SourceName == exportDisk.SourceName)
                        targetDiskInExport = true;
                }

                if (!targetDiskInExport && targetDisk.ParentVirtualMachine != null)
                {
                    this.AddAlert(AlertType.Error, "Virtual Machine '" + targetDisk.ParentVirtualMachine.SourceName + "' references Managed Disk '" + targetDisk.SourceName + "' which has not been added as an export resource.", targetDisk.ParentVirtualMachine);
                }
            }
            else
            {
                // We are calling Validate Disk Standards here (only for non-managed disks, as noted above) as all Managed Disks are validated for Disk Standards through the ExportArtifacts.Disks Collection
                ValidateDiskStandards(targetDisk);
            }

        }

        private void ValidateDiskStandards(MigrationTarget.Disk targetDisk)
        {
            if (targetDisk.DiskSizeInGB == 0)
                this.AddAlert(AlertType.Error, "Disk '" + targetDisk.ToString() + "' does not have a Disk Size defined.  Disk Size (not to exceed 4095 GB) is required.", targetDisk);

            if (targetDisk.IsSmallerThanSourceDisk)
                this.AddAlert(AlertType.Error, "Disk '" + targetDisk.ToString() + "' Size of " + targetDisk.DiskSizeInGB.ToString() + " GB cannot be smaller than the source Disk Size of " + targetDisk.SourceDisk.DiskSizeGb.ToString() + " GB.", targetDisk);

            if (targetDisk.SourceDisk != null && targetDisk.SourceDisk.GetType() == typeof(Azure.Arm.ClassicDisk))
            {
                if (targetDisk.SourceDisk.IsEncrypted)
                {
                    this.AddAlert(AlertType.Error, "Disk '" + targetDisk.ToString() + "' is encrypted.  MigAz does not contain support for moving encrypted Azure Compute VMs.", targetDisk);
                }
            }

            if (targetDisk.TargetStorage == null)
                this.AddAlert(AlertType.Error, "Disk '" + targetDisk.ToString() + "' Target Storage must be specified.", targetDisk);
            else
            {
                if (targetDisk.TargetStorage.GetType() == typeof(Azure.Arm.StorageAccount))
                {
                    Arm.StorageAccount armStorageAccount = (Arm.StorageAccount)targetDisk.TargetStorage;
                    if (armStorageAccount.Location.Name != _ExportArtifacts.ResourceGroup.TargetLocation.Name)
                    {
                        this.AddAlert(AlertType.Error, "Target Storage Account '" + armStorageAccount.Name + "' is not in the same region (" + armStorageAccount.Location.Name + ") as the Target Resource Group '" + _ExportArtifacts.ResourceGroup.ToString() + "' (" + _ExportArtifacts.ResourceGroup.TargetLocation.Name + ").", targetDisk);
                    }
                }
                else if (targetDisk.TargetStorage.GetType() == typeof(Azure.MigrationTarget.StorageAccount))
                {
                    Azure.MigrationTarget.StorageAccount targetStorageAccount = (Azure.MigrationTarget.StorageAccount)targetDisk.TargetStorage;
                    bool targetStorageExists = false;

                    foreach (Azure.MigrationTarget.StorageAccount storageAccount in _ExportArtifacts.StorageAccounts)
                    {
                        if (storageAccount.ToString() == targetStorageAccount.ToString())
                        {
                            targetStorageExists = true;
                            break;
                        }
                    }

                    if (!targetStorageExists)
                        this.AddAlert(AlertType.Error, "Target Storage Account '" + targetStorageAccount.ToString() + "' for Disk '" + targetDisk.ToString() + "' is invalid, as it is not included in the migration / template.", targetDisk);
                }

                if (targetDisk.TargetStorage.GetType() == typeof(Azure.MigrationTarget.StorageAccount) ||
                    targetDisk.TargetStorage.GetType() == typeof(Azure.Arm.StorageAccount))
                {
                    if (targetDisk.TargetStorageAccountBlob == null || targetDisk.TargetStorageAccountBlob.Trim().Length == 0)
                        this.AddAlert(AlertType.Error, "Target Storage Blob Name is required.", targetDisk);
                    else if (!targetDisk.TargetStorageAccountBlob.ToLower().EndsWith(".vhd"))
                        this.AddAlert(AlertType.Error, "Target Storage Blob Name '" + targetDisk.TargetStorageAccountBlob + "' for Disk is invalid, as it must end with '.vhd'.", targetDisk);
                }

                if (targetDisk.TargetStorage.StorageAccountType == StorageAccountType.Premium_LRS)
                {
                    if (targetDisk.DiskSizeInGB > 0 && targetDisk.DiskSizeInGB < 32)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 32GB (P4), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 32 && targetDisk.DiskSizeInGB < 64)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 64GB (P6), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 64 && targetDisk.DiskSizeInGB < 128)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 128GB (P10), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 128 && targetDisk.DiskSizeInGB < 512)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 512GB (P20), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 512 && targetDisk.DiskSizeInGB < 1023)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 1023GB (P30), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 1023 && targetDisk.DiskSizeInGB < 2047)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 2047GB (P40), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                    else if (targetDisk.DiskSizeInGB > 2047 && targetDisk.DiskSizeInGB < 4095)
                        this.AddAlert(AlertType.Recommendation, "Consider using disk size 4095GB (P50), as this disk will be billed at that capacity per Azure Premium Storage billing sizes.", targetDisk);
                }
            }
        }

        public async Task GenerateStreams()
        {
            TemplateStreams.Clear();
            Resources.Clear();
            Parameters.Clear();
            _CopyBlobDetails.Clear();
            _TemporaryStorageAccounts.Clear();

            LogProvider.WriteLog("GenerateStreams", "Start - Execution " + this.ExecutionGuid.ToString());

            if (_ExportArtifacts != null)
            {
                LogProvider.WriteLog("GenerateStreams", "Start processing selected Network Security Groups");
                foreach (MigrationTarget.NetworkSecurityGroup targetNetworkSecurityGroup in _ExportArtifacts.NetworkSecurityGroups)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Network Security Group : " + targetNetworkSecurityGroup.ToString());
                    await BuildNetworkSecurityGroup(targetNetworkSecurityGroup);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Network Security Groups");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Route Tables");
                foreach (MigrationTarget.RouteTable targetRouteTable in _ExportArtifacts.RouteTables)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Route Tables : " + targetRouteTable.ToString());
                    await BuildRouteTable(targetRouteTable);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Route Tables");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Network Security Groups");
                foreach (MigrationTarget.PublicIp targetPublicIp in _ExportArtifacts.PublicIPs)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Public IP : " + targetPublicIp.ToString());
                    await BuildPublicIPAddressObject(targetPublicIp);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Network Security Groups");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Virtual Networks");
                foreach (Azure.MigrationTarget.VirtualNetwork virtualNetwork in _ExportArtifacts.VirtualNetworks)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Virtual Network : " + virtualNetwork.ToString());
                    await BuildVirtualNetworkObject(virtualNetwork);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Virtual Networks");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Load Balancers");
                foreach (Azure.MigrationTarget.LoadBalancer loadBalancer in _ExportArtifacts.LoadBalancers)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Load Balancer : " + loadBalancer.ToString());
                    await BuildLoadBalancerObject(loadBalancer);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Load Balancers");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Storage Accounts");
                foreach (MigrationTarget.StorageAccount storageAccount in _ExportArtifacts.StorageAccounts)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Storage Account : " + storageAccount.ToString());
                    BuildStorageAccountObject(storageAccount);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Storage Accounts");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Availablity Sets");
                foreach (Azure.MigrationTarget.AvailabilitySet availablitySet in _ExportArtifacts.AvailablitySets)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Availablity Set : " + availablitySet.ToString());
                    BuildAvailabilitySetObject(availablitySet);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Availablity Sets");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Network Interfaces");
                foreach (Azure.MigrationTarget.NetworkInterface networkInterface in _ExportArtifacts.NetworkInterfaces)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Network Interface : " + networkInterface.ToString());
                    BuildNetworkInterfaceObject(networkInterface);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Network Interfaces");

                LogProvider.WriteLog("GenerateStreams", "Start processing selected Cloud Services / Virtual Machines");
                foreach (Azure.MigrationTarget.VirtualMachine virtualMachine in _ExportArtifacts.VirtualMachines)
                {
                    StatusProvider.UpdateStatus("BUSY: Exporting Virtual Machine : " + virtualMachine.ToString());
                    await BuildVirtualMachineObject(virtualMachine);
                }
                LogProvider.WriteLog("GenerateStreams", "End processing selected Cloud Services / Virtual Machines");
            }
            else
                LogProvider.WriteLog("GenerateStreams", "ExportArtifacts is null, nothing to export.");

            StatusProvider.UpdateStatus("Ready");
            LogProvider.WriteLog("GenerateStreams", "End - Execution " + this.ExecutionGuid.ToString());
        }


        public void Write()
        {
            if (!Directory.Exists(_OutputDirectory))
            {
                Directory.CreateDirectory(_OutputDirectory);
            }

            foreach (string key in TemplateStreams.Keys)
            {
                MemoryStream ms = TemplateStreams[key];
                using (FileStream file = new FileStream(_OutputDirectory + key, FileMode.Create, System.IO.FileAccess.Write))
                {
                    byte[] bytes = new byte[ms.Length];
                    ms.Position = 0;
                    ms.Read(bytes, 0, (int)ms.Length);
                    file.Write(bytes, 0, bytes.Length);
                }
            }
        }

        public string BuildMigAzMessages()
        {
            if (this.Alerts.Count == 0)
                return String.Empty;

            StringBuilder sbMigAzMessageResult = new StringBuilder();

            sbMigAzMessageResult.Append("<p>MigAz has identified the following advisements during template generation for review:</p>");

            sbMigAzMessageResult.Append("<p>");
            sbMigAzMessageResult.Append("<ul>");
            foreach (MigAzGeneratorAlert migAzMessage in this.Alerts)
            {
                sbMigAzMessageResult.Append("<li>");
                sbMigAzMessageResult.Append(migAzMessage);
                sbMigAzMessageResult.Append("</li>");
            }
            sbMigAzMessageResult.Append("</ul>");
            sbMigAzMessageResult.Append("</p>");

            return sbMigAzMessageResult.ToString();
        }

        private AvailabilitySet BuildAvailabilitySetObject(Azure.MigrationTarget.AvailabilitySet targetAvailabilitySet)
        {
            LogProvider.WriteLog("BuildAvailabilitySetObject", "Start");

            AvailabilitySet availabilitySet = new AvailabilitySet(this.ExecutionGuid);

            availabilitySet.name = targetAvailabilitySet.ToString();
            availabilitySet.location = "[resourceGroup().location]";

            // todo Add Properties - PlatformUpdateDomainCount
            // todo Add Properties - PlatformFaultDomainCount

            if (targetAvailabilitySet.IsManagedDisks)
            {
                // https://docs.microsoft.com/en-us/azure/virtual-machines/windows/using-managed-disks-template-deployments
                // see "Create managed availability sets with VMs using managed disks"

                availabilitySet.apiVersion = "2017-03-30";

                Dictionary<string, string> availabilitySetSku = new Dictionary<string, string>();
                availabilitySet.sku = availabilitySetSku;
                availabilitySetSku.Add("name", "Aligned");
            }

            this.AddResource(availabilitySet);

            LogProvider.WriteLog("BuildAvailabilitySetObject", "End");

            return availabilitySet;
        }

        private async Task BuildPublicIPAddressObject(Azure.MigrationTarget.PublicIp publicIp)
        {
            LogProvider.WriteLog("BuildPublicIPAddressObject", "Start " + ArmConst.ProviderLoadBalancers + publicIp.ToString());

            PublicIPAddress publicipaddress = new PublicIPAddress(this.ExecutionGuid);
            publicipaddress.name = publicIp.ToString();
            publicipaddress.location = "[resourceGroup().location]";

            PublicIPAddress_Properties publicipaddress_properties = new PublicIPAddress_Properties();
            publicipaddress.properties = publicipaddress_properties;

            if (publicIp.DomainNameLabel != String.Empty)
            {
                Hashtable dnssettings = new Hashtable();
                dnssettings.Add("domainNameLabel", publicIp.DomainNameLabel);
                publicipaddress_properties.dnsSettings = dnssettings;
            }

            this.AddResource(publicipaddress);

            LogProvider.WriteLog("BuildPublicIPAddressObject", "End " + ArmConst.ProviderLoadBalancers + publicIp.ToString());
        }

        private async Task BuildLoadBalancerObject(Azure.MigrationTarget.LoadBalancer loadBalancer)
        {
            LogProvider.WriteLog("BuildLoadBalancerObject", "Start " + ArmConst.ProviderLoadBalancers + loadBalancer.ToString());

            List<string> dependson = new List<string>();
            LoadBalancer loadbalancer = new LoadBalancer(this.ExecutionGuid);
            loadbalancer.name = loadBalancer.ToString();
            loadbalancer.location = "[resourceGroup().location]";
            loadbalancer.dependsOn = dependson;

            LoadBalancer_Properties loadbalancer_properties = new LoadBalancer_Properties();
            loadbalancer.properties = loadbalancer_properties;


            List<FrontendIPConfiguration> frontendipconfigurations = new List<FrontendIPConfiguration>();
            loadbalancer_properties.frontendIPConfigurations = frontendipconfigurations;

            foreach (Azure.MigrationTarget.FrontEndIpConfiguration targetFrontEndIpConfiguration in loadBalancer.FrontEndIpConfigurations)
            {
                FrontendIPConfiguration frontendipconfiguration = new FrontendIPConfiguration();
                frontendipconfiguration.name = targetFrontEndIpConfiguration.Name;
                frontendipconfigurations.Add(frontendipconfiguration);

                FrontendIPConfiguration_Properties frontendipconfiguration_properties = new FrontendIPConfiguration_Properties();
                frontendipconfiguration.properties = frontendipconfiguration_properties;

                if (loadBalancer.LoadBalancerType == MigrationTarget.LoadBalancerType.Internal)
                {
                    frontendipconfiguration_properties.privateIPAllocationMethod = targetFrontEndIpConfiguration.TargetPrivateIPAllocationMethod;
                    frontendipconfiguration_properties.privateIPAddress = targetFrontEndIpConfiguration.TargetPrivateIpAddress;

                    if (targetFrontEndIpConfiguration.TargetVirtualNetwork != null && targetFrontEndIpConfiguration.TargetVirtualNetwork.GetType() == typeof(Azure.MigrationTarget.VirtualNetwork))
                        dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderVirtualNetwork + targetFrontEndIpConfiguration.TargetVirtualNetwork.ToString() + "')]");

                    Reference subnet_ref = new Reference();
                    frontendipconfiguration_properties.subnet = subnet_ref;

                    if (targetFrontEndIpConfiguration.TargetSubnet != null)
                    {
                        subnet_ref.id = targetFrontEndIpConfiguration.TargetSubnet.TargetId;
                    }
                }
                else
                {
                    if (targetFrontEndIpConfiguration.PublicIp != null)
                    {
                        await BuildPublicIPAddressObject(targetFrontEndIpConfiguration.PublicIp);

                        Reference publicipaddress_ref = new Reference();
                        publicipaddress_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderPublicIpAddress + targetFrontEndIpConfiguration.PublicIp.ToString() + "')]";
                        frontendipconfiguration_properties.publicIPAddress = publicipaddress_ref;

                        dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderPublicIpAddress + targetFrontEndIpConfiguration.PublicIp.ToString() + "')]");
                    }
                }
            }

            List<Hashtable> backendaddresspools = new List<Hashtable>();
            loadbalancer_properties.backendAddressPools = backendaddresspools;

            foreach (Azure.MigrationTarget.BackEndAddressPool targetBackEndAddressPool in loadBalancer.BackEndAddressPools)
            {
                Hashtable backendaddresspool = new Hashtable();
                backendaddresspool.Add("name", targetBackEndAddressPool.Name);
                backendaddresspools.Add(backendaddresspool);
            }

            List<InboundNatRule> inboundnatrules = new List<InboundNatRule>();
            List<LoadBalancingRule> loadbalancingrules = new List<LoadBalancingRule>();
            List<Probe> probes = new List<Probe>();

            loadbalancer_properties.inboundNatRules = inboundnatrules;
            loadbalancer_properties.loadBalancingRules = loadbalancingrules;
            loadbalancer_properties.probes = probes;

            // Add Inbound Nat Rules
            foreach (Azure.MigrationTarget.InboundNatRule inboundNatRule in loadBalancer.InboundNatRules)
            {
                InboundNatRule_Properties inboundnatrule_properties = new InboundNatRule_Properties();
                inboundnatrule_properties.frontendPort = inboundNatRule.FrontEndPort;
                inboundnatrule_properties.backendPort = inboundNatRule.BackEndPort;
                inboundnatrule_properties.protocol = inboundNatRule.Protocol;

                if (inboundNatRule.FrontEndIpConfiguration != null)
                {
                    Reference frontendIPConfiguration = new Reference();
                    frontendIPConfiguration.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + loadbalancer.name + "/frontendIPConfigurations/default')]";
                    inboundnatrule_properties.frontendIPConfiguration = frontendIPConfiguration;
                }

                InboundNatRule inboundnatrule = new InboundNatRule();
                inboundnatrule.name = inboundNatRule.Name;
                inboundnatrule.properties = inboundnatrule_properties;

                loadbalancer_properties.inboundNatRules.Add(inboundnatrule);
            }

            foreach (Azure.MigrationTarget.Probe targetProbe in loadBalancer.Probes)
            {
                Probe_Properties probe_properties = new Probe_Properties();
                probe_properties.port = targetProbe.Port;
                probe_properties.protocol = targetProbe.Protocol;
                probe_properties.intervalInSeconds = targetProbe.IntervalInSeconds;
                probe_properties.numberOfProbes = targetProbe.NumberOfProbes;

                if (targetProbe.RequestPath != null && targetProbe.RequestPath != String.Empty)
                    probe_properties.requestPath = targetProbe.RequestPath;

                Probe probe = new Probe();
                probe.name = targetProbe.Name;
                probe.properties = probe_properties;

                loadbalancer_properties.probes.Add(probe);
            }

            foreach (Azure.MigrationTarget.LoadBalancingRule targetLoadBalancingRule in loadBalancer.LoadBalancingRules)
            {
                Reference frontendipconfiguration_ref = new Reference();
                frontendipconfiguration_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + loadbalancer.name + "/frontendIPConfigurations/" + targetLoadBalancingRule.FrontEndIpConfiguration.Name + "')]";

                Reference backendaddresspool_ref = new Reference();
                backendaddresspool_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + loadbalancer.name + "/backendAddressPools/" + targetLoadBalancingRule.BackEndAddressPool.Name + "')]";

                Reference probe_ref = new Reference();
                probe_ref.id = "[concat(" + ArmConst.ResourceGroupId + ",'" + ArmConst.ProviderLoadBalancers + loadbalancer.name + "/probes/" + targetLoadBalancingRule.Probe.Name + "')]";

                LoadBalancingRule_Properties loadbalancingrule_properties = new LoadBalancingRule_Properties();
                loadbalancingrule_properties.frontendIPConfiguration = frontendipconfiguration_ref;
                loadbalancingrule_properties.backendAddressPool = backendaddresspool_ref;
                loadbalancingrule_properties.probe = probe_ref;
                loadbalancingrule_properties.frontendPort = targetLoadBalancingRule.FrontEndPort;
                loadbalancingrule_properties.backendPort = targetLoadBalancingRule.BackEndPort;
                loadbalancingrule_properties.protocol = targetLoadBalancingRule.Protocol;
                loadbalancingrule_properties.enableFloatingIP = targetLoadBalancingRule.EnableFloatingIP;
                loadbalancingrule_properties.idleTimeoutInMinutes = targetLoadBalancingRule.IdleTimeoutInMinutes;

                LoadBalancingRule loadbalancingrule = new LoadBalancingRule();
                loadbalancingrule.name = targetLoadBalancingRule.Name;
                loadbalancingrule.properties = loadbalancingrule_properties;

                loadbalancer_properties.loadBalancingRules.Add(loadbalancingrule);
            }

            this.AddResource(loadbalancer);

            LogProvider.WriteLog("BuildLoadBalancerObject", "End " + ArmConst.ProviderLoadBalancers + loadBalancer.ToString());
        }

        private async Task BuildVirtualNetworkObject(Azure.MigrationTarget.VirtualNetwork targetVirtualNetwork)
        {
            LogProvider.WriteLog("BuildVirtualNetworkObject", "Start Microsoft.Network/virtualNetworks/" + targetVirtualNetwork.ToString());

            List<string> dependson = new List<string>();

            AddressSpace addressspace = new AddressSpace();
            addressspace.addressPrefixes = targetVirtualNetwork.AddressPrefixes;

            VirtualNetwork_dhcpOptions dhcpoptions = new VirtualNetwork_dhcpOptions();
            dhcpoptions.dnsServers = targetVirtualNetwork.DnsServers;

            VirtualNetwork virtualnetwork = new VirtualNetwork(this.ExecutionGuid);
            virtualnetwork.name = targetVirtualNetwork.ToString();
            virtualnetwork.location = "[resourceGroup().location]";
            virtualnetwork.dependsOn = dependson;

            List<Subnet> subnets = new List<Subnet>();
            foreach (Azure.MigrationTarget.Subnet targetSubnet in targetVirtualNetwork.TargetSubnets)
            {
                Subnet_Properties properties = new Subnet_Properties();
                properties.addressPrefix = targetSubnet.AddressPrefix;

                Subnet subnet = new Subnet();
                subnet.name = targetSubnet.TargetName;
                subnet.properties = properties;

                subnets.Add(subnet);

                // add Network Security Group if exists
                if (targetSubnet.NetworkSecurityGroup != null)
                {
                    Core.ArmTemplate.NetworkSecurityGroup networksecuritygroup = await BuildNetworkSecurityGroup(targetSubnet.NetworkSecurityGroup);
                    // Add NSG reference to the subnet
                    Reference networksecuritygroup_ref = new Reference();
                    networksecuritygroup_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderNetworkSecurityGroups + networksecuritygroup.name + "')]";

                    properties.networkSecurityGroup = networksecuritygroup_ref;

                    // Add NSG dependsOn to the Virtual Network object
                    if (!virtualnetwork.dependsOn.Contains(networksecuritygroup_ref.id))
                    {
                        virtualnetwork.dependsOn.Add(networksecuritygroup_ref.id);
                    }
                }

                // add Route Table if exists
                if (targetSubnet.RouteTable != null)
                {
                    // Add Route Table reference to the subnet
                    Reference routetable_ref = new Reference();
                    routetable_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderRouteTables + targetSubnet.RouteTable.ToString() + "')]";

                    properties.routeTable = routetable_ref;

                    // Add Route Table dependsOn to the Virtual Network object
                    if (!virtualnetwork.dependsOn.Contains(routetable_ref.id))
                    {
                        virtualnetwork.dependsOn.Add(routetable_ref.id);
                    }
                }
            }

            VirtualNetwork_Properties virtualnetwork_properties = new VirtualNetwork_Properties();
            virtualnetwork_properties.addressSpace = addressspace;
            virtualnetwork_properties.subnets = subnets;
            virtualnetwork_properties.dhcpOptions = dhcpoptions;

            virtualnetwork.properties = virtualnetwork_properties;

            this.AddResource(virtualnetwork);

            await AddGatewaysToVirtualNetwork(targetVirtualNetwork, virtualnetwork);

            LogProvider.WriteLog("BuildVirtualNetworkObject", "End Microsoft.Network/virtualNetworks/" + targetVirtualNetwork.ToString());
        }

        private async Task AddGatewaysToVirtualNetwork(MigrationTarget.VirtualNetwork targetVirtualNetwork, VirtualNetwork templateVirtualNetwork)
        {
            if (targetVirtualNetwork.SourceVirtualNetwork.GetType() == typeof(Azure.Asm.VirtualNetwork))
            {
                Asm.VirtualNetwork asmVirtualNetwork = (Asm.VirtualNetwork)targetVirtualNetwork.SourceVirtualNetwork;

                // Process Virtual Network Gateway, if exists
                if ((asmVirtualNetwork.Gateway != null) && (asmVirtualNetwork.Gateway.IsProvisioned))
                {
                    // Gateway Public IP Address
                    PublicIPAddress_Properties publicipaddress_properties = new PublicIPAddress_Properties();
                    publicipaddress_properties.publicIPAllocationMethod = "Dynamic";

                    PublicIPAddress publicipaddress = new PublicIPAddress(this.ExecutionGuid);
                    publicipaddress.name = targetVirtualNetwork.TargetName + _settingsProvider.VirtualNetworkGatewaySuffix + _settingsProvider.PublicIPSuffix;
                    publicipaddress.location = "[resourceGroup().location]";
                    publicipaddress.properties = publicipaddress_properties;

                    this.AddResource(publicipaddress);

                    // Virtual Network Gateway
                    Reference subnet_ref = new Reference();
                    subnet_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderVirtualNetwork + templateVirtualNetwork.name + "/subnets/" + ArmConst.GatewaySubnetName + "')]";

                    Reference publicipaddress_ref = new Reference();
                    publicipaddress_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderPublicIpAddress + publicipaddress.name + "')]";

                    var dependson = new List<string>();
                    dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderVirtualNetwork + templateVirtualNetwork.name + "')]");
                    dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderPublicIpAddress + publicipaddress.name + "')]");

                    IpConfiguration_Properties ipconfiguration_properties = new IpConfiguration_Properties();
                    ipconfiguration_properties.privateIPAllocationMethod = "Dynamic";
                    ipconfiguration_properties.subnet = subnet_ref;
                    ipconfiguration_properties.publicIPAddress = publicipaddress_ref;

                    IpConfiguration virtualnetworkgateway_ipconfiguration = new IpConfiguration();
                    virtualnetworkgateway_ipconfiguration.name = "GatewayIPConfig";
                    virtualnetworkgateway_ipconfiguration.properties = ipconfiguration_properties;

                    VirtualNetworkGateway_Sku virtualnetworkgateway_sku = new VirtualNetworkGateway_Sku();
                    virtualnetworkgateway_sku.name = "Basic";
                    virtualnetworkgateway_sku.tier = "Basic";

                    List<IpConfiguration> virtualnetworkgateway_ipconfigurations = new List<IpConfiguration>();
                    virtualnetworkgateway_ipconfigurations.Add(virtualnetworkgateway_ipconfiguration);

                    VirtualNetworkGateway_Properties virtualnetworkgateway_properties = new VirtualNetworkGateway_Properties();
                    virtualnetworkgateway_properties.ipConfigurations = virtualnetworkgateway_ipconfigurations;
                    virtualnetworkgateway_properties.sku = virtualnetworkgateway_sku;

                    // If there is VPN Client configuration
                    if (asmVirtualNetwork.VPNClientAddressPrefixes.Count > 0)
                    {
                        AddressSpace vpnclientaddresspool = new AddressSpace();
                        vpnclientaddresspool.addressPrefixes = asmVirtualNetwork.VPNClientAddressPrefixes;

                        VPNClientConfiguration vpnclientconfiguration = new VPNClientConfiguration();
                        vpnclientconfiguration.vpnClientAddressPool = vpnclientaddresspool;

                        //Process vpnClientRootCertificates
                        List<VPNClientCertificate> vpnclientrootcertificates = new List<VPNClientCertificate>();
                        foreach (Asm.ClientRootCertificate certificate in asmVirtualNetwork.ClientRootCertificates)
                        {
                            VPNClientCertificate_Properties vpnclientcertificate_properties = new VPNClientCertificate_Properties();
                            vpnclientcertificate_properties.PublicCertData = certificate.PublicCertData;

                            VPNClientCertificate vpnclientcertificate = new VPNClientCertificate();
                            vpnclientcertificate.name = certificate.TargetSubject;
                            vpnclientcertificate.properties = vpnclientcertificate_properties;

                            vpnclientrootcertificates.Add(vpnclientcertificate);
                        }

                        vpnclientconfiguration.vpnClientRootCertificates = vpnclientrootcertificates;

                        virtualnetworkgateway_properties.vpnClientConfiguration = vpnclientconfiguration;
                    }

                    if (asmVirtualNetwork.LocalNetworkSites.Count > 0 && asmVirtualNetwork.LocalNetworkSites[0].ConnectionType == "Dedicated")
                    {
                        virtualnetworkgateway_properties.gatewayType = "ExpressRoute";
                        virtualnetworkgateway_properties.enableBgp = null;
                        virtualnetworkgateway_properties.vpnType = null;
                    }
                    else
                    {
                        virtualnetworkgateway_properties.gatewayType = "Vpn";
                        string vpnType = asmVirtualNetwork.Gateway.GatewayType;
                        if (vpnType == "StaticRouting")
                        {
                            vpnType = "PolicyBased";
                        }
                        else if (vpnType == "DynamicRouting")
                        {
                            vpnType = "RouteBased";
                        }
                        virtualnetworkgateway_properties.vpnType = vpnType;
                    }

                    VirtualNetworkGateway virtualnetworkgateway = new VirtualNetworkGateway(this.ExecutionGuid);
                    virtualnetworkgateway.location = "[resourceGroup().location]";
                    virtualnetworkgateway.name = targetVirtualNetwork.TargetName + _settingsProvider.VirtualNetworkGatewaySuffix;
                    virtualnetworkgateway.properties = virtualnetworkgateway_properties;
                    virtualnetworkgateway.dependsOn = dependson;

                    this.AddResource(virtualnetworkgateway);

                    if (!asmVirtualNetwork.HasGatewaySubnet)
                        this.AddAlert(AlertType.Error, "The Virtual Network '" + targetVirtualNetwork.TargetName + "' does not contain the necessary '" + ArmConst.GatewaySubnetName + "' subnet for deployment of the '" + virtualnetworkgateway.name + "' Gateway.", asmVirtualNetwork);

                    await AddLocalSiteToGateway(asmVirtualNetwork, templateVirtualNetwork, virtualnetworkgateway);
                }
            }
        }

        private async Task AddLocalSiteToGateway(Asm.VirtualNetwork asmVirtualNetwork, VirtualNetwork virtualnetwork, VirtualNetworkGateway virtualnetworkgateway)
        {
            // Local Network Gateways & Connections
            foreach (Asm.LocalNetworkSite asmLocalNetworkSite in asmVirtualNetwork.LocalNetworkSites)
            {
                GatewayConnection_Properties gatewayconnection_properties = new GatewayConnection_Properties();
                var dependson = new List<string>();

                if (asmLocalNetworkSite.ConnectionType == "IPsec")
                {
                    // Local Network Gateway
                    List<String> addressprefixes = asmLocalNetworkSite.AddressPrefixes;

                    AddressSpace localnetworkaddressspace = new AddressSpace();
                    localnetworkaddressspace.addressPrefixes = addressprefixes;

                    LocalNetworkGateway_Properties localnetworkgateway_properties = new LocalNetworkGateway_Properties();
                    localnetworkgateway_properties.localNetworkAddressSpace = localnetworkaddressspace;
                    localnetworkgateway_properties.gatewayIpAddress = asmLocalNetworkSite.VpnGatewayAddress;

                    LocalNetworkGateway localnetworkgateway = new LocalNetworkGateway(this.ExecutionGuid);
                    localnetworkgateway.name = asmLocalNetworkSite.Name + "-LocalGateway";
                    localnetworkgateway.name = localnetworkgateway.name.Replace(" ", String.Empty);

                    localnetworkgateway.location = "[resourceGroup().location]";
                    localnetworkgateway.properties = localnetworkgateway_properties;

                    this.AddResource(localnetworkgateway);

                    Reference localnetworkgateway_ref = new Reference();
                    localnetworkgateway_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLocalNetworkGateways + localnetworkgateway.name + "')]";
                    dependson.Add(localnetworkgateway_ref.id);

                    gatewayconnection_properties.connectionType = asmLocalNetworkSite.ConnectionType;
                    gatewayconnection_properties.localNetworkGateway2 = localnetworkgateway_ref;

                    string connectionShareKey = asmLocalNetworkSite.SharedKey;
                    if (connectionShareKey == String.Empty)
                    {
                        gatewayconnection_properties.sharedKey = "***SHARED KEY GOES HERE***";
                        this.AddAlert(AlertType.Error, $"Unable to retrieve shared key for VPN connection '{virtualnetworkgateway.name}'. Please edit the template to provide this value.", asmVirtualNetwork);
                    }
                    else
                    {
                        gatewayconnection_properties.sharedKey = connectionShareKey;
                    }
                }
                else if (asmLocalNetworkSite.ConnectionType == "Dedicated")
                {
                    gatewayconnection_properties.connectionType = "ExpressRoute";
                    gatewayconnection_properties.peer = new Reference() { id = "/subscriptions/***/resourceGroups/***" + ArmConst.ProviderExpressRouteCircuits + "***" }; // todo, this is incomplete
                    this.AddAlert(AlertType.Error, $"Gateway '{virtualnetworkgateway.name}' connects to ExpressRoute. MigAz is unable to migrate ExpressRoute circuits. Please create or convert the circuit yourself and update the circuit resource ID in the generated template.", asmVirtualNetwork);
                }

                // Connections
                Reference virtualnetworkgateway_ref = new Reference();
                virtualnetworkgateway_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderVirtualNetworkGateways + virtualnetworkgateway.name + "')]";

                dependson.Add(virtualnetworkgateway_ref.id);

                gatewayconnection_properties.virtualNetworkGateway1 = virtualnetworkgateway_ref;

                GatewayConnection gatewayconnection = new GatewayConnection(this.ExecutionGuid);
                gatewayconnection.name = virtualnetworkgateway.name + "-" + asmLocalNetworkSite.TargetName + "-connection"; // TODO, HardCoded
                gatewayconnection.location = "[resourceGroup().location]";
                gatewayconnection.properties = gatewayconnection_properties;
                gatewayconnection.dependsOn = dependson;

                this.AddResource(gatewayconnection);

            }
        }

        private async Task<NetworkSecurityGroup> BuildNetworkSecurityGroup(MigrationTarget.NetworkSecurityGroup targetNetworkSecurityGroup)
        {
            LogProvider.WriteLog("BuildNetworkSecurityGroup", "Start");

            NetworkSecurityGroup networksecuritygroup = new NetworkSecurityGroup(this.ExecutionGuid);
            networksecuritygroup.name = targetNetworkSecurityGroup.ToString();
            networksecuritygroup.location = "[resourceGroup().location]";

            NetworkSecurityGroup_Properties networksecuritygroup_properties = new NetworkSecurityGroup_Properties();
            networksecuritygroup_properties.securityRules = new List<SecurityRule>();

            // for each rule
            foreach (MigrationTarget.NetworkSecurityGroupRule targetNetworkSecurityGroupRule in targetNetworkSecurityGroup.Rules)
            {
                // if not system rule
                if (!targetNetworkSecurityGroupRule.IsSystemRule)
                {
                    SecurityRule_Properties securityrule_properties = new SecurityRule_Properties();
                    securityrule_properties.description = targetNetworkSecurityGroupRule.ToString();
                    securityrule_properties.direction = targetNetworkSecurityGroupRule.Direction;
                    securityrule_properties.priority = targetNetworkSecurityGroupRule.Priority;
                    securityrule_properties.access = targetNetworkSecurityGroupRule.Access;
                    securityrule_properties.sourceAddressPrefix = targetNetworkSecurityGroupRule.SourceAddressPrefix;
                    securityrule_properties.destinationAddressPrefix = targetNetworkSecurityGroupRule.DestinationAddressPrefix;
                    securityrule_properties.sourcePortRange = targetNetworkSecurityGroupRule.SourcePortRange;
                    securityrule_properties.destinationPortRange = targetNetworkSecurityGroupRule.DestinationPortRange;
                    securityrule_properties.protocol = targetNetworkSecurityGroupRule.Protocol;

                    SecurityRule securityrule = new SecurityRule();
                    securityrule.name = targetNetworkSecurityGroupRule.ToString();
                    securityrule.properties = securityrule_properties;

                    networksecuritygroup_properties.securityRules.Add(securityrule);
                }
            }

            networksecuritygroup.properties = networksecuritygroup_properties;

            this.AddResource(networksecuritygroup);

            LogProvider.WriteLog("BuildNetworkSecurityGroup", "End");

            return networksecuritygroup;
        }

        private async Task<RouteTable> BuildRouteTable(MigrationTarget.RouteTable routeTable)
        {
            LogProvider.WriteLog("BuildRouteTable", "Start");

            RouteTable routetable = new RouteTable(this.ExecutionGuid);
            routetable.name = routeTable.ToString();
            routetable.location = "[resourceGroup().location]";

            RouteTable_Properties routetable_properties = new RouteTable_Properties();
            routetable_properties.routes = new List<Route>();

            // for each route
            foreach (MigrationTarget.Route migrationRoute in routeTable.Routes)
            {
                Route_Properties route_properties = new Route_Properties();
                route_properties.addressPrefix = migrationRoute.AddressPrefix;
                route_properties.nextHopType = migrationRoute.NextHopType.ToString();

                if (route_properties.nextHopType == "VirtualAppliance")
                    route_properties.nextHopIpAddress = migrationRoute.NextHopIpAddress;

                Route route = new Route();
                route.name = migrationRoute.ToString();
                route.properties = route_properties;

                routetable_properties.routes.Add(route);
            }

            routetable.properties = routetable_properties;

            this.AddResource(routetable);

            LogProvider.WriteLog("BuildRouteTable", "End");

            return routetable;
        }

       
        private NetworkInterface BuildNetworkInterfaceObject(Azure.MigrationTarget.NetworkInterface targetNetworkInterface)
        {
            LogProvider.WriteLog("BuildNetworkInterfaceObject", "Start " + ArmConst.ProviderNetworkInterfaces + targetNetworkInterface.ToString());

            List<string> dependson = new List<string>();

            NetworkInterface networkInterface = new NetworkInterface(this.ExecutionGuid);
            networkInterface.name = targetNetworkInterface.ToString();
            networkInterface.location = "[resourceGroup().location]";

            List<IpConfiguration> ipConfigurations = new List<IpConfiguration>();
            foreach (Azure.MigrationTarget.NetworkInterfaceIpConfiguration ipConfiguration in targetNetworkInterface.TargetNetworkInterfaceIpConfigurations)
            {
                IpConfiguration ipconfiguration = new IpConfiguration();
                ipconfiguration.name = ipConfiguration.ToString();
                IpConfiguration_Properties ipconfiguration_properties = new IpConfiguration_Properties();
                ipconfiguration.properties = ipconfiguration_properties;
                Reference subnet_ref = new Reference();
                ipconfiguration_properties.subnet = subnet_ref;

                if (ipConfiguration.TargetSubnet != null)
                {
                    subnet_ref.id = ipConfiguration.TargetSubnet.TargetId;
                }

                ipconfiguration_properties.privateIPAllocationMethod = ipConfiguration.TargetPrivateIPAllocationMethod;
                if (String.Compare(ipConfiguration.TargetPrivateIPAllocationMethod, "Static") == 0)
                    ipconfiguration_properties.privateIPAddress = ipConfiguration.TargetPrivateIpAddress;

                if (ipConfiguration.TargetVirtualNetwork != null)
                {
                    if (ipConfiguration.TargetVirtualNetwork.GetType() == typeof(MigrationTarget.VirtualNetwork))
                    {
                        // only adding VNet DependsOn here because as it is a resource in the target migration (resource group)
                        MigrationTarget.VirtualNetwork targetVirtualNetwork = (MigrationTarget.VirtualNetwork)ipConfiguration.TargetVirtualNetwork;
                        dependson.Add(targetVirtualNetwork.TargetId);
                    }
                }

                if (targetNetworkInterface.BackEndAddressPool != null)
                {
                    if (_ExportArtifacts.ContainsLoadBalancer(targetNetworkInterface.BackEndAddressPool.LoadBalancer))
                    {
                        // If there is at least one endpoint add the reference to the LB backend pool
                        List<Reference> loadBalancerBackendAddressPools = new List<Reference>();
                        ipconfiguration_properties.loadBalancerBackendAddressPools = loadBalancerBackendAddressPools;

                        Reference loadBalancerBackendAddressPool = new Reference();
                        loadBalancerBackendAddressPool.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + targetNetworkInterface.BackEndAddressPool.LoadBalancer.Name + "/backendAddressPools/" + targetNetworkInterface.BackEndAddressPool.Name + "')]";

                        loadBalancerBackendAddressPools.Add(loadBalancerBackendAddressPool);

                        dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + targetNetworkInterface.BackEndAddressPool.LoadBalancer.Name + "')]");
                    }
                }

                // Adds the references to the inboud nat rules
                List<Reference> loadBalancerInboundNatRules = new List<Reference>();
                foreach (MigrationTarget.InboundNatRule inboundNatRule in targetNetworkInterface.InboundNatRules)
                {
                    if (_ExportArtifacts.ContainsLoadBalancer(inboundNatRule.LoadBalancer))
                    {
                        Reference loadBalancerInboundNatRule = new Reference();
                        loadBalancerInboundNatRule.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderLoadBalancers + inboundNatRule.LoadBalancer.Name + "/inboundNatRules/" + inboundNatRule.Name + "')]";

                        loadBalancerInboundNatRules.Add(loadBalancerInboundNatRule);
                    }
                }

                if (loadBalancerInboundNatRules.Count > 0)
                {
                    ipconfiguration_properties.loadBalancerInboundNatRules = loadBalancerInboundNatRules;
                }

                if (ipConfiguration.TargetPublicIp != null)
                {
                    Core.ArmTemplate.Reference publicIPAddressReference = new Core.ArmTemplate.Reference();
                    publicIPAddressReference.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderPublicIpAddress + ipConfiguration.TargetPublicIp.ToString() + "')]";
                    ipconfiguration_properties.publicIPAddress = publicIPAddressReference;

                    dependson.Add(publicIPAddressReference.id);
                }

                ipConfigurations.Add(ipconfiguration);
            }

            NetworkInterface_Properties networkinterface_properties = new NetworkInterface_Properties();
            networkinterface_properties.ipConfigurations = ipConfigurations;
            networkinterface_properties.enableIPForwarding = targetNetworkInterface.EnableIPForwarding;

            networkInterface.properties = networkinterface_properties;
            networkInterface.dependsOn = dependson;

            if (targetNetworkInterface.NetworkSecurityGroup != null)
            {
                // Add NSG reference to the network interface
                Reference networksecuritygroup_ref = new Reference();
                networksecuritygroup_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderNetworkSecurityGroups + targetNetworkInterface.NetworkSecurityGroup.ToString() + "')]";

                networkinterface_properties.NetworkSecurityGroup = networksecuritygroup_ref;

                networkInterface.dependsOn.Add(networksecuritygroup_ref.id);
            }

            this.AddResource(networkInterface);

            LogProvider.WriteLog("BuildNetworkInterfaceObject", "End " + ArmConst.ProviderNetworkInterfaces + targetNetworkInterface.ToString());

            return networkInterface;
        }

        private async Task BuildVirtualMachineObject(Azure.MigrationTarget.VirtualMachine targetVirtualMachine)
        {
            LogProvider.WriteLog("BuildVirtualMachineObject", "Start Microsoft.Compute/virtualMachines/" + targetVirtualMachine.ToString());

            VirtualMachine templateVirtualMachine = new VirtualMachine(this.ExecutionGuid);
            templateVirtualMachine.name = targetVirtualMachine.ToString();
            templateVirtualMachine.location = "[resourceGroup().location]";

            if (targetVirtualMachine.IsManagedDisks)
            {
                // using API Version "2016-04-30-preview" per current documentation at https://docs.microsoft.com/en-us/azure/storage/storage-using-managed-disks-template-deployments
                templateVirtualMachine.apiVersion = "2016-04-30-preview";
            }

            List<IStorageTarget> storageaccountdependencies = new List<IStorageTarget>();
            List<string> dependson = new List<string>();

            // process network interface
            List<NetworkProfile_NetworkInterface> networkinterfaces = new List<NetworkProfile_NetworkInterface>();

            foreach (MigrationTarget.NetworkInterface targetNetworkInterface in targetVirtualMachine.NetworkInterfaces)
            {
                NetworkProfile_NetworkInterface_Properties networkinterface_ref_properties = new NetworkProfile_NetworkInterface_Properties();
                networkinterface_ref_properties.primary = targetNetworkInterface.IsPrimary;

                NetworkProfile_NetworkInterface networkinterface_ref = new NetworkProfile_NetworkInterface();
                networkinterface_ref.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderNetworkInterfaces + targetNetworkInterface.TargetName + "')]";
                networkinterface_ref.properties = networkinterface_ref_properties;

                networkinterfaces.Add(networkinterface_ref);

                dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderNetworkInterfaces + targetNetworkInterface.TargetName + "')]");
            }

            HardwareProfile hardwareprofile = new HardwareProfile();
            hardwareprofile.vmSize = targetVirtualMachine.TargetSize.ToString();

            NetworkProfile networkprofile = new NetworkProfile();
            networkprofile.networkInterfaces = networkinterfaces;


            OsDisk osdisk = new OsDisk();
            osdisk.caching = targetVirtualMachine.OSVirtualHardDisk.HostCaching;

            ImageReference imagereference = new ImageReference();
            OsProfile osprofile = new OsProfile();

            // if the tool is configured to create new VMs with empty data disks
            if (_settingsProvider.BuildEmpty)
            {
                osdisk.name = targetVirtualMachine.OSVirtualHardDisk.ToString();
                osdisk.createOption = "FromImage";

                osprofile.computerName = targetVirtualMachine.ToString();
                osprofile.adminUsername = "[parameters('adminUsername')]";
                osprofile.adminPassword = "[parameters('adminPassword')]";

                if (!this.Parameters.ContainsKey("adminUsername"))
                {
                    Parameter parameter = new Parameter();
                    parameter.type = "string";
                    this.Parameters.Add("adminUsername", parameter);
                }

                if (!this.Parameters.ContainsKey("adminPassword"))
                {
                    Parameter parameter = new Parameter();
                    parameter.type = "securestring";
                    this.Parameters.Add("adminPassword", parameter);
                }

                if (targetVirtualMachine.OSVirtualHardDiskOS == "Windows")
                {
                    imagereference.publisher = "MicrosoftWindowsServer";
                    imagereference.offer = "WindowsServer";
                    imagereference.sku = "2016-Datacenter";
                    imagereference.version = "latest";
                }
                else if (targetVirtualMachine.OSVirtualHardDiskOS == "Linux")
                {
                    imagereference.publisher = "Canonical";
                    imagereference.offer = "UbuntuServer";
                    imagereference.sku = "16.04.0-LTS";
                    imagereference.version = "latest";
                }
                else
                {
                    imagereference.publisher = "<publisher>";
                    imagereference.offer = "<offer>";
                    imagereference.sku = "<sku>";
                    imagereference.version = "<version>";
                }
            }
            // if the tool is configured to attach copied disks
            else
            {
                osdisk.createOption = "Attach";
                osdisk.osType = targetVirtualMachine.OSVirtualHardDiskOS;

                this._CopyBlobDetails.Add(await BuildCopyBlob(targetVirtualMachine.OSVirtualHardDisk, _ExportArtifacts.ResourceGroup));

                if (targetVirtualMachine.OSVirtualHardDisk.IsUnmanagedDisk)
                {
                    osdisk.name = targetVirtualMachine.OSVirtualHardDisk.ToString();

                    Vhd vhd = new Vhd();
                    osdisk.vhd = vhd;
                    vhd.uri = targetVirtualMachine.OSVirtualHardDisk.TargetMediaLink;

                    if (targetVirtualMachine.OSVirtualHardDisk.TargetStorage != null)
                    {
                        if (!storageaccountdependencies.Contains(targetVirtualMachine.OSVirtualHardDisk.TargetStorage))
                            storageaccountdependencies.Add(targetVirtualMachine.OSVirtualHardDisk.TargetStorage);
                    }
                }
                else if (targetVirtualMachine.OSVirtualHardDisk.IsManagedDisk)
                {
                    ManagedDisk osManagedDisk = await BuildManagedDiskObject(targetVirtualMachine.OSVirtualHardDisk);

                    Reference managedDiskReference = new Reference();
                    managedDiskReference.id = targetVirtualMachine.OSVirtualHardDisk.ReferenceId;

                    osdisk.managedDisk = managedDiskReference;

                    dependson.Add(targetVirtualMachine.OSVirtualHardDisk.ReferenceId);
                }
            }

            // process data disks
            List<DataDisk> datadisks = new List<DataDisk>();
            foreach (MigrationTarget.Disk dataDisk in targetVirtualMachine.DataDisks)
            {
                if (dataDisk.TargetStorage != null)
                {
                    DataDisk datadisk = new DataDisk();
                    datadisk.name = dataDisk.ToString();
                    datadisk.caching = dataDisk.HostCaching;
                    datadisk.diskSizeGB = dataDisk.DiskSizeInGB;
                    if (dataDisk.Lun.HasValue)
                        datadisk.lun = dataDisk.Lun.Value;

                    // if the tool is configured to create new VMs with empty data disks
                    if (_settingsProvider.BuildEmpty)
                    {
                        datadisk.createOption = "Empty";
                    }
                    // if the tool is configured to attach copied disks
                    else
                    {
                        datadisk.createOption = "Attach";

                        this._CopyBlobDetails.Add(await BuildCopyBlob(dataDisk, _ExportArtifacts.ResourceGroup));
                    }

                    if (dataDisk.IsUnmanagedDisk)
                    {
                        Vhd vhd = new Vhd();
                        vhd.uri = dataDisk.TargetMediaLink;
                        datadisk.vhd = vhd;

                        if (dataDisk.TargetStorage != null)
                        {
                            if (!storageaccountdependencies.Contains(dataDisk.TargetStorage))
                                storageaccountdependencies.Add(dataDisk.TargetStorage);
                        }
                    }
                    else if (dataDisk.IsManagedDisk)
                    {
                        ManagedDisk datadiskManagedDisk = await BuildManagedDiskObject(dataDisk);

                        Reference managedDiskReference = new Reference();
                        managedDiskReference.id = dataDisk.ReferenceId;

                        datadisk.diskSizeGB = null;
                        datadisk.managedDisk = managedDiskReference;

                        dependson.Add(dataDisk.ReferenceId);
                    }

                    datadisks.Add(datadisk);
                }
            }

            StorageProfile storageprofile = new StorageProfile();
            if (_settingsProvider.BuildEmpty) { storageprofile.imageReference = imagereference; }
            storageprofile.osDisk = osdisk;
            storageprofile.dataDisks = datadisks;

            VirtualMachine_Properties virtualmachine_properties = new VirtualMachine_Properties();
            virtualmachine_properties.hardwareProfile = hardwareprofile;
            if (_settingsProvider.BuildEmpty) { virtualmachine_properties.osProfile = osprofile; }
            virtualmachine_properties.networkProfile = networkprofile;
            virtualmachine_properties.storageProfile = storageprofile;

            // process availability set
            if (targetVirtualMachine.TargetAvailabilitySet != null)
            {
                Reference availabilitySetReference = new Reference();
                virtualmachine_properties.availabilitySet = availabilitySetReference;
                availabilitySetReference.id = "[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderAvailabilitySets + targetVirtualMachine.TargetAvailabilitySet.ToString() + "')]";
                dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderAvailabilitySets + targetVirtualMachine.TargetAvailabilitySet.ToString() + "')]");
            }

            foreach (IStorageTarget storageaccountdependency in storageaccountdependencies)
            {
                if (storageaccountdependency.GetType() == typeof(Azure.MigrationTarget.StorageAccount)) // only add depends on if it is a Storage Account in the target template.  Otherwise, we'll get a "not in template" error for a resource that exists in another Resource Group.
                    dependson.Add("[concat(" + ArmConst.ResourceGroupId + ", '" + ArmConst.ProviderStorageAccounts + storageaccountdependency + "')]");
            }

            templateVirtualMachine.properties = virtualmachine_properties;
            templateVirtualMachine.dependsOn = dependson;
            templateVirtualMachine.resources = new List<ArmResource>();

            // Virtual Machine Plan Attributes (i.e. VM is an Azure Marketplace item that has a Marketplace plan associated
            templateVirtualMachine.plan = targetVirtualMachine.PlanAttributes;


            // Diagnostics Extension
            Extension extension_iaasdiagnostics = null;
            if (extension_iaasdiagnostics != null) { templateVirtualMachine.resources.Add(extension_iaasdiagnostics); }

            this.AddResource(templateVirtualMachine);

            LogProvider.WriteLog("BuildVirtualMachineObject", "End Microsoft.Compute/virtualMachines/" + targetVirtualMachine.ToString());
        }

        private async Task<ManagedDisk> BuildManagedDiskObject(Azure.MigrationTarget.Disk targetManagedDisk)
        {
            // https://docs.microsoft.com/en-us/azure/virtual-machines/windows/using-managed-disks-template-deployments
            // https://docs.microsoft.com/en-us/azure/virtual-machines/windows/template-description

            LogProvider.WriteLog("BuildManagedDiskObject", "Start Microsoft.Compute/disks/" + targetManagedDisk.ToString());

            ManagedDisk templateManagedDisk = new ManagedDisk(this.ExecutionGuid);
            templateManagedDisk.name = targetManagedDisk.ToString();
            templateManagedDisk.location = "[resourceGroup().location]";

            ManagedDisk_Properties templateManagedDiskProperties = new ManagedDisk_Properties();
            templateManagedDisk.properties = templateManagedDiskProperties;

            templateManagedDiskProperties.diskSizeGb = targetManagedDisk.DiskSizeInGB;
            templateManagedDiskProperties.accountType = targetManagedDisk.StorageAccountType.ToString();

            ManagedDiskCreationData_Properties templateManageDiskCreationDataProperties = new ManagedDiskCreationData_Properties();
            templateManagedDiskProperties.creationData = templateManageDiskCreationDataProperties;

            if (targetManagedDisk.SourceDisk != null && targetManagedDisk.SourceDisk.GetType() == typeof(Azure.Arm.ManagedDisk))
            {
                Azure.Arm.ManagedDisk armManagedDisk = (Azure.Arm.ManagedDisk)targetManagedDisk.SourceDisk;

                string managedDiskSourceUriParameterName = targetManagedDisk.ToString() + "_SourceUri";

                if (!this.Parameters.ContainsKey(managedDiskSourceUriParameterName))
                {
                    Parameter parameter = new Parameter();
                    parameter.type = "string";
                    parameter.value = "testing";

                    this.Parameters.Add(managedDiskSourceUriParameterName, parameter);
                }

                templateManageDiskCreationDataProperties.createOption = "Import";
                templateManageDiskCreationDataProperties.sourceUri = "[parameters('" + managedDiskSourceUriParameterName + "')]";
            }

            // sample json with encryption
            // https://raw.githubusercontent.com/Azure/azure-quickstart-templates/master/201-create-encrypted-managed-disk/CreateEncryptedManagedDisk-kek.json

            this.AddResource(templateManagedDisk);

            LogProvider.WriteLog("BuildManagedDiskObject", "End Microsoft.Compute/disks/" + targetManagedDisk.ToString());

            return templateManagedDisk;
        }

        private async Task<CopyBlobDetail> BuildCopyBlob(MigrationTarget.Disk disk, MigrationTarget.ResourceGroup resourceGroup)
        {
            if (disk.SourceDisk == null)
                return null;

            CopyBlobDetail copyblobdetail = new CopyBlobDetail();
            if (this.SourceSubscription != null)
                copyblobdetail.SourceEnvironment = this.SourceSubscription.AzureEnvironment.ToString();

            copyblobdetail.TargetResourceGroup = resourceGroup.ToString();
            copyblobdetail.TargetLocation = resourceGroup.TargetLocation.Name.ToString();

            if (disk.SourceDisk != null)
            {
                if (disk.SourceDisk.GetType() == typeof(Asm.Disk))
                {
                    Asm.Disk asmClassicDisk = (Asm.Disk)disk.SourceDisk;

                    copyblobdetail.SourceStorageAccount = asmClassicDisk.StorageAccountName;
                    copyblobdetail.SourceContainer = asmClassicDisk.StorageAccountContainer;
                    copyblobdetail.SourceBlob = asmClassicDisk.StorageAccountBlob;

                    if (asmClassicDisk.SourceStorageAccount != null && asmClassicDisk.SourceStorageAccount.Keys != null)
                        copyblobdetail.SourceKey = asmClassicDisk.SourceStorageAccount.Keys.Primary;
                }
                else if (disk.SourceDisk.GetType() == typeof(Arm.ClassicDisk))
                {
                    Arm.ClassicDisk armClassicDisk = (Arm.ClassicDisk)disk.SourceDisk;

                    copyblobdetail.SourceStorageAccount = armClassicDisk.StorageAccountName;
                    copyblobdetail.SourceContainer = armClassicDisk.StorageAccountContainer;
                    copyblobdetail.SourceBlob = armClassicDisk.StorageAccountBlob;

                    if (armClassicDisk.SourceStorageAccount != null && armClassicDisk.SourceStorageAccount.Keys != null)
                        copyblobdetail.SourceKey = armClassicDisk.SourceStorageAccount.Keys[0].Value;
                }
                else if (disk.SourceDisk.GetType() == typeof(Arm.ManagedDisk))
                {
                    Arm.ManagedDisk armManagedDisk = (Arm.ManagedDisk)disk.SourceDisk;

                    copyblobdetail.SourceAbsoluteUri = await armManagedDisk.GetSASUrlAsync(3600);
                }
            }

            copyblobdetail.TargetContainer = disk.TargetStorageAccountContainer;
            copyblobdetail.TargetBlob = disk.TargetStorageAccountBlob;

            if (disk.TargetStorage != null)
            {
                if (disk.TargetStorage.GetType() == typeof(Arm.StorageAccount))
                {
                    Arm.StorageAccount armStorageAccount = (Arm.StorageAccount)disk.TargetStorage;
                    copyblobdetail.TargetResourceGroup = armStorageAccount.ResourceGroup.Name;
                    copyblobdetail.TargetLocation = armStorageAccount.ResourceGroup.Location.Name.ToString();
                    copyblobdetail.TargetStorageAccount = disk.TargetStorage.ToString();
                }
                else if (disk.TargetStorage.GetType() == typeof(MigrationTarget.StorageAccount))
                {
                    copyblobdetail.TargetStorageAccount = disk.TargetStorage.ToString();
                    copyblobdetail.TargetStorageAccountType = disk.TargetStorage.StorageAccountType.ToString();
                }
                else if (disk.TargetStorage.GetType() == typeof(MigrationTarget.ManagedDiskStorage))
                {
                    MigrationTarget.ManagedDiskStorage managedDiskStorage = (MigrationTarget.ManagedDiskStorage)disk.TargetStorage;

                    // We are going to use a temporary storage account to stage the VHD file(s), which will then be deleted after Disk Creation
                    MigrationTarget.StorageAccount targetTemporaryStorageAccount = GetTempStorageAccountName(disk);
                    copyblobdetail.TargetStorageAccount = targetTemporaryStorageAccount.ToString();
                    copyblobdetail.TargetStorageAccountType = targetTemporaryStorageAccount.StorageAccountType.ToString();
                    copyblobdetail.TargetBlob = disk.TargetName + ".vhd";
                }
            }

            return copyblobdetail;
        }

        private MigrationTarget.StorageAccount GetTempStorageAccountName(MigrationTarget.Disk disk)
        {
            foreach (MigrationTarget.StorageAccount temporaryStorageAccount in this._TemporaryStorageAccounts)
            {
                if (temporaryStorageAccount.StorageAccountType == disk.StorageAccountType)
                {
                    return temporaryStorageAccount;
                }
            }

            MigrationTarget.StorageAccount newTemporaryStorageAccount = new MigrationTarget.StorageAccount(disk.StorageAccountType);
            _TemporaryStorageAccounts.Add(newTemporaryStorageAccount);

            return newTemporaryStorageAccount;
        }

        private void BuildStorageAccountObject(MigrationTarget.StorageAccount targetStorageAccount)
        {
            LogProvider.WriteLog("BuildStorageAccountObject", "Start Microsoft.Storage/storageAccounts/" + targetStorageAccount.ToString());

            StorageAccount_Properties storageaccount_properties = new StorageAccount_Properties();
            storageaccount_properties.accountType = targetStorageAccount.StorageAccountType.ToString();

            StorageAccount storageaccount = new StorageAccount(this.ExecutionGuid);
            storageaccount.name = targetStorageAccount.ToString();
            storageaccount.location = "[resourceGroup().location]";
            storageaccount.properties = storageaccount_properties;

            this.AddResource(storageaccount);

            LogProvider.WriteLog("BuildStorageAccountObject", "End");
        }

        private bool HasBlobCopyDetails
        {
            get
            {
                return _CopyBlobDetails.Count > 0;
            }
        }



        public async Task SerializeStreams()
        {
            LogProvider.WriteLog("SerializeStreams", "Start");

            TemplateStreams.Clear();

            await SerializeDeploymentInstructions();

            if (HasBlobCopyDetails)
            {
                await SerializeBlobCopyDetails(); // Serialize blob copy details
                await SerializeBlobCopyPowerShell();
            }

            await SerializeExportTemplate();
            await SerializeParameterTemplate();

            StatusProvider.UpdateStatus("Ready");

            LogProvider.WriteLog("SerializeStreams", "End");
        }

        private async Task SerializeBlobCopyDetails()
        {
            LogProvider.WriteLog("SerializeBlobCopyDetails", "Start");

            ASCIIEncoding asciiEncoding = new ASCIIEncoding();

            // Only generate copyblobdetails.json if it contains disks that are being copied
            if (_CopyBlobDetails.Count > 0)
            {
                StatusProvider.UpdateStatus("BUSY:  Generating copyblobdetails.json");
                LogProvider.WriteLog("SerializeStreams", "Start copyblobdetails.json stream");

                string jsontext = JsonConvert.SerializeObject(this._CopyBlobDetails, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                byte[] b = asciiEncoding.GetBytes(jsontext);
                MemoryStream copyBlobDetailStream = new MemoryStream();
                await copyBlobDetailStream.WriteAsync(b, 0, b.Length);
                TemplateStreams.Add("copyblobdetails.json", copyBlobDetailStream);

                LogProvider.WriteLog("SerializeStreams", "End copyblobdetails.json stream");
            }

            LogProvider.WriteLog("SerializeBlobCopyDetails", "End");
        }

        private async Task SerializeExportTemplate()
        {
            LogProvider.WriteLog("SerializeExportTemplate", "Start");

            StatusProvider.UpdateStatus("BUSY:  Generating export.json");

            String templateString = await GetTemplateString();
            ASCIIEncoding asciiEncoding = new ASCIIEncoding();
            byte[] a = asciiEncoding.GetBytes(templateString);
            MemoryStream templateStream = new MemoryStream();
            await templateStream.WriteAsync(a, 0, a.Length);
            TemplateStreams.Add("export.json", templateStream);

            LogProvider.WriteLog("SerializeExportTemplate", "End");
        }
        private async Task SerializeParameterTemplate()
        {
            LogProvider.WriteLog("SerializeParameterTemplate", "Start");

            if (this.Parameters.Count > 0)
            {
                StatusProvider.UpdateStatus("BUSY:  Generating parameters.json");

                String templateString = await GetParameterString();
                ASCIIEncoding asciiEncoding = new ASCIIEncoding();
                byte[] a = asciiEncoding.GetBytes(templateString);
                MemoryStream templateStream = new MemoryStream();
                await templateStream.WriteAsync(a, 0, a.Length);
                TemplateStreams.Add("parameters.json", templateStream);
            }

            LogProvider.WriteLog("SerializeParameterTemplate", "End");
        }

        private async Task SerializeBlobCopyPowerShell()
        {
            LogProvider.WriteLog("SerializeBlobCopyPowerShell", "Start");

            ASCIIEncoding asciiEncoding = new ASCIIEncoding();

            StatusProvider.UpdateStatus("BUSY:  Generating BlobCopy.ps1");
            LogProvider.WriteLog("SerializeStreams", "Start BlobCopy.ps1 stream");

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "MigAz.Azure.Generator.BlobCopy.ps1";
            string blobCopyPowerShell;

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                blobCopyPowerShell = reader.ReadToEnd();
            }

            byte[] c = asciiEncoding.GetBytes(blobCopyPowerShell);
            MemoryStream blobCopyPowerShellStream = new MemoryStream();
            await blobCopyPowerShellStream.WriteAsync(c, 0, c.Length);
            TemplateStreams.Add("BlobCopy.ps1", blobCopyPowerShellStream);

            LogProvider.WriteLog("SerializeBlobCopyPowerShell", "End");
        }

        private async Task SerializeDeploymentInstructions()
        {
            LogProvider.WriteLog("SerializeDeploymentInstructions", "Start");

            ASCIIEncoding asciiEncoding = new ASCIIEncoding();

            StatusProvider.UpdateStatus("BUSY:  Generating DeployInstructions.html");
            LogProvider.WriteLog("SerializeStreams", "Start DeployInstructions.html stream");

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "MigAz.Azure.Generator.DeployDocTemplate.html";
            string instructionContent;

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                instructionContent = reader.ReadToEnd();
            }

            string azureEnvironmentSwitch = String.Empty;
            string tenantSwitch = String.Empty;
            string subscriptionSwitch = String.Empty;

            if (this.TargetSubscription != null)
            {
                subscriptionSwitch = " -SubscriptionId '" + this.TargetSubscription.SubscriptionId + "'";

                if (this.TargetSubscription.AzureEnvironment != AzureEnvironment.AzureCloud)
                    azureEnvironmentSwitch = " -EnvironmentName " + this.TargetSubscription.AzureEnvironment.ToString();
                if (this.TargetSubscription.AzureEnvironment != AzureEnvironment.AzureCloud)
                    azureEnvironmentSwitch = " -EnvironmentName " + this.TargetSubscription.AzureEnvironment.ToString();

                if (this.TargetSubscription.AzureAdTenantId != Guid.Empty)
                    tenantSwitch = " -TenantId '" + this.TargetSubscription.AzureAdTenantId.ToString() + "'";
            }

            instructionContent = instructionContent.Replace("{migAzAzureEnvironmentSwitch}", azureEnvironmentSwitch);
            instructionContent = instructionContent.Replace("{tenantSwitch}", tenantSwitch);
            instructionContent = instructionContent.Replace("{subscriptionSwitch}", subscriptionSwitch);
            instructionContent = instructionContent.Replace("{templatePath}", GetTemplatePath());
            instructionContent = instructionContent.Replace("{blobDetailsPath}", GetCopyBlobDetailPath());
            instructionContent = instructionContent.Replace("{resourceGroupName}", this.TargetResourceGroupName);
            instructionContent = instructionContent.Replace("{location}", this.TargetResourceGroupLocation);
            instructionContent = instructionContent.Replace("{migAzPath}", AppDomain.CurrentDomain.BaseDirectory);
            instructionContent = instructionContent.Replace("{exportPath}", _OutputDirectory);
            instructionContent = instructionContent.Replace("{migAzMessages}", BuildMigAzMessages());

            byte[] c = asciiEncoding.GetBytes(instructionContent);
            MemoryStream instructionStream = new MemoryStream();
            await instructionStream.WriteAsync(c, 0, c.Length);
            TemplateStreams.Add("DeployInstructions.html", instructionStream);

            LogProvider.WriteLog("SerializeDeploymentInstructions", "End");
        }

        public string GetCopyBlobDetailPath()
        {
            return Path.Combine(this.OutputDirectory, "copyblobdetails.json");
        }


        public string GetTemplatePath()
        {
            return Path.Combine(this.OutputDirectory, "export.json");
        }

        public string GetInstructionPath()
        {
            return Path.Combine(this.OutputDirectory, "DeployInstructions.html");
        }

        private async Task<string> GetTemplateString()
        {
            Template template = new Template()
            {
                resources = this.Resources,
                parameters = this.Parameters
            };

            // save JSON template
            string jsontext = JsonConvert.SerializeObject(template, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            jsontext = jsontext.Replace("schemalink", "$schema");

            return jsontext;
        }

        private async Task<string> GetParameterString()
        {
            Template template = new Template()
            {
                parameters = this.Parameters
            };

            // save JSON template
            string jsontext = JsonConvert.SerializeObject(template, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            jsontext = jsontext.Replace("schemalink", "$schema");

            return jsontext;
        }


        public async Task<string> GetStorageAccountTemplateString()
        {
            List<ArmResource> storageAccountResources = new List<ArmResource>();
            foreach (ArmResource armResource in this.Resources)
            {
                if (armResource.GetType() == typeof(MigAz.Core.ArmTemplate.StorageAccount))
                {
                    storageAccountResources.Add(armResource);
                }
            }

            Template template = new Template()
            {
                resources = storageAccountResources,
                parameters = new Dictionary<string, Parameter>()
            };

            // save JSON template
            string jsontext = JsonConvert.SerializeObject(template, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            jsontext = jsontext.Replace("schemalink", "$schema");

            return jsontext;
        }

        public string TargetResourceGroupName
        {
            get
            {
                if (_ExportArtifacts != null && _ExportArtifacts.ResourceGroup != null && _ExportArtifacts.ResourceGroup.TargetLocation != null)
                    return _ExportArtifacts.ResourceGroup.ToString();
                else
                    return String.Empty;

            }
        }

        public string TargetResourceGroupLocation
        {
            get
            {
                if (_ExportArtifacts != null && _ExportArtifacts.ResourceGroup != null && _ExportArtifacts.ResourceGroup.TargetLocation != null)
                    return _ExportArtifacts.ResourceGroup.TargetLocation.Name;
                else
                    return String.Empty;
            }
        }

        protected void OnTemplateChanged()
        {
            // Call the base class event invocation method.
            EventHandler handler = AfterTemplateChanged;
            if (handler != null)
            {
                handler(this, null);
            }
        }

        ////The event-invoking method that derived classes can override.
        //protected virtual void OnTemplateChanged()
        //{
        //    // Make a temporary copy of the event to avoid possibility of
        //    // a race condition if the last subscriber unsubscribes
        //    // immediately after the null check and before the event is raised.
        //    EventHandler handler = AfterTemplateChanged;
        //    if (handler != null)
        //    {
        //        handler(this, null);
        //    }
        //}
    }
}