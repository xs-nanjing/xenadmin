/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using XenAdmin.Core;
using System.IO;
using XenAdmin.Network;
using System.Threading;
using XenAPI;
using Microsoft.Reporting.WinForms;
using System.Collections.Generic;
using XenAdmin.XenSearch;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace XenAdmin.Actions
{
    public class ExportStaticReportAction : AsyncAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly string _filename;
        private readonly int _fileType;
        private Exception _exception = null;
        private static MetricUpdater MetricUpdater;
        private List<HostInfo> m_Hosts;
        List<SRInfo> m_SRs;
        List<NetworkInfo> m_Networks;
        List<VMInfo> m_VMs;
        List<PGPUInfo> m_PGUPs;
        long itemCount = 0;
        long itemIndex = 0;
        long baseIndex = 90;
        enum FILE_TYPE_INDEX { XLS = 1, CSV = 2 };
        /// <summary> 
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="filename"></param>
        public ExportStaticReportAction(IXenConnection connection, string filename, int fileType)
            : base(connection, string.Format(Messages.ACTION_EXPORT_RESOURCE_LIST_FROM_X, Helpers.GetName(connection)),
            Messages.ACTION_EXPORT_DESCRIPTION_PREPARING)
        {
            Pool = Helpers.GetPool(connection);
            _filename = filename;
            _fileType = fileType;
            MetricUpdater = new MetricUpdater();
            MetricUpdater.SetXenObjects(connection.Cache.Hosts);
            MetricUpdater.SetXenObjects(connection.Cache.VMs);
            MetricUpdater.SetXenObjects(connection.Cache.PIFs);
            MetricUpdater.SetXenObjects(connection.Cache.VBDs);
            MetricUpdater.SetXenObjects(connection.Cache.VIFs);
            MetricUpdater.UpdateMetricsOnce();
            itemCount = connection.Cache.Hosts.Length + connection.Cache.Networks.Length + connection.Cache.SRs.Length + connection.Cache.VMs.Length;
        }

        protected override void Run()
        {
            SafeToExit = false;
            Description = Messages.ACTION_EXPORT_DESCRIPTION_IN_PROGRESS;
            RelatedTask = XenAPI.Task.create(Session,
                string.Format(Messages.ACTION_EXPORT_POOL_RESOURCE_LIST_TASK_NAME, this.Connection.Cache.Pools[0].Name),
                string.Format(Messages.ACTION_EXPORT_POOL_RESOURCE_LIST_TASK_DESCRIPTION, this.Connection.Cache.Pools[0].Name, _filename));

            if (Cancelling)
                throw new CancelledException();

            UriBuilder uriBuilder = new UriBuilder(this.Session.Url);
            uriBuilder.Path = "export";
            uriBuilder.Query = string.Format("session_id={0}&uuid={1}&task_id={2}",
                Uri.EscapeDataString(this.Session.uuid),
                Uri.EscapeDataString(this.Connection.Cache.Pools[0].uuid),
                Uri.EscapeDataString(this.RelatedTask.opaque_ref));

            log.DebugFormat("Exporting resource list report from {1} to {2}", this.Connection.Cache.Pools[0].Name, uriBuilder.ToString(), _filename);
 
            try
            {
                DoExport();
            }
            catch (Exception e)
            {
                if (XenAPI.Task.get_status(this.Session, this.RelatedTask.opaque_ref) == XenAPI.task_status_type.pending
                    && XenAPI.Task.get_progress(this.Session, this.RelatedTask.opaque_ref) == 0)
                {
                    XenAPI.Task.destroy(this.Session, this.RelatedTask.opaque_ref);
                }
                // Test for null: don't overwrite a previous exception
                if (_exception == null)
                    _exception = e;
            }

            PercentComplete = 100;
            if (Cancelling || _exception is CancelledException)
            {
                log.InfoFormat("Export of Pool {0} cancelled", this.Connection.Cache.Pools[0].Name);
                this.Description = Messages.ACTION_EXPORT_DESCRIPTION_CANCELLED;

                log.DebugFormat("Deleting {0}", _filename);
                File.Delete(_filename);
                throw new CancelledException();
            }
            else if (_exception != null)
            {
                log.Warn(string.Format("Export of Pool {0} failed", this.Connection.Cache.Pools[0].Name), _exception);
                log.DebugFormat("Progress of the action until exception: {0}", PercentComplete);
                
                this.Description = Messages.ACTION_EXPORT_DESCRIPTION_FAILED;
                throw new Exception(Description);
            }
            else
            {
                log.InfoFormat("Export of Pool {0} successful", this.Connection.Cache.Pools[0].Name);
                this.Description = Messages.ACTION_EXPORT_DESCRIPTION_SUCCESSFUL;
            }
        }
        private void progressPoll()
        {
            try
            {
                PollToCompletion(0, 50);
            }
            catch (Failure e)
            {
                // Don't overwrite a previous exception unless we're sure that the one that
                // we have here is going to be more useful than the client one.  Sometimes,
                // the server exception will be "failed to write", which is just in
                // response to us closing the stream when we run out of disk space or whatever
                // on the client side.  Other times, it's the server that's got the useful
                // error message.
                if (_exception == null || e.ErrorDescription[0] == Failure.VDI_IN_USE)
                    _exception = e;
            }
            catch (Exception e)
            {
                // Test for null: don't overwrite a previous exception
                if (_exception == null)
                    _exception = e;
            }
        }
        public class HostInfo
        {
            public HostInfo(string hostName, string hostAddress, string hostUUID, string hostCpuUsage,
                string hostRole, string hostnetworkUsage, string hostMemUsage, string hostUptime)
            {
                _name = hostName;
                _address = hostAddress;
                _uuid = hostUUID;
                _cpuUsage = hostCpuUsage;
                _role = hostRole;
                _networkUsage = hostnetworkUsage;
                _memUsage = hostMemUsage;
                _uptime = hostUptime;
            }
            public virtual string Address
            {
                get { return _address; }
            }
            public virtual string Name
            {
                get { return _name; }
            }
            public virtual string UUID
            {
                get { return _uuid; }
            }
            public virtual string CpuUsage
            {
                get { return _cpuUsage; }
            }
            public virtual string Role
            {
                get { return _role; }
            }
            public virtual string NetworkUsage
            {
                get { return _networkUsage; }
            }
            public virtual string MemUsage
            {
                get { return _memUsage; }
            }
            public virtual string Uptime
            {
                get { return _uptime; }
            }
            private string _name;
            private string _role;
            private string _address;
            private string _uuid;
            private string _cpuUsage;
            private string _networkUsage;
            private string _memUsage;
            private string _uptime;
        }

        public class SRInfo
        {
            public SRInfo(string name, string SRUuid, string SRType,
                string SRSize, string SRRemark, string SRDescription)
            {
                _name = name;
                _uuid = SRUuid;
                _type = SRType;
                _size = SRSize;
                _remark = SRRemark;
                _description = SRDescription;
            }
            public virtual string Name
            {
                get { return _name; }
            }
            public virtual string UUID
            {
                get { return _uuid; }
            }
            public virtual string Type
            {
                get { return _type; }
            }
            public virtual string Size
            {
                get { return _size; }
            }
            public virtual string Remark
            {
                get { return _remark; }
            }
            public virtual string Description
            {
                get { return _description; }
            }
            private string _name;
            private string _uuid;
            private string _type;
            private string _size;
            private string _remark;
            private string _description;
        }

        public class NetworkInfo
        {
            public NetworkInfo(string name, string networkVlanID, string networkLinkStatus,
                string NetworkMac, string NetworkMtu)
            {
                _name = name;
                _vlanID = networkVlanID;
                _linkStatus = networkLinkStatus;
                _mac = NetworkMac;
                _mtu = NetworkMtu;
            }
            public virtual string Name
            {
                get { return _name; }
            }
            public virtual string VlanID
            {
                get { return _vlanID; }
            }
            public virtual string LinkStatus
            {
                get { return _linkStatus; }
            }
            public virtual string MAC
            {
                get { return _mac; }
            }
            public virtual string MTU
            {
                get { return _mtu; }
            }
            private string _name;
            private string _vlanID;
            private string _linkStatus;
            private string _mac;
            private string _mtu;
        }

        public class VMInfo
        {
            public VMInfo(string Name, string VMuuid, string VMvCpuNum, string VMmemSize, string VMsrInfo,
                string VMnicNum, string VMip, string VMmac, string VMosInfo,
                string VMpowerStatus, string VMuptime, string VMhostInfo, string VMTemplateName)
            {
                _name = Name;
                _uuid = VMuuid;
                _vCpuNum = VMvCpuNum;
                _memSize = VMmemSize;
                _srInfo = VMsrInfo;
                _nicNum = VMnicNum;
                _ip = VMip;
                _mac = VMmac;
                _osInfo = VMosInfo;
                _powerStatus = VMpowerStatus;
                _uptime = VMuptime;
                _hostInfo = VMhostInfo;
                _templateName = VMTemplateName;
            }
            public virtual string Name
            {
                get { return _name; }
            }
            public virtual string UUID
            {
                get { return _uuid; }
            }
            public virtual string VCpuNum
            {
                get { return _vCpuNum; }
            }
            public virtual string MemSize
            {
                get { return _memSize; }
            }
            public virtual string SRInfo
            {
                get { return _srInfo; }
            }
            public virtual string NicNum
            {
                get { return _nicNum; }
            }
            public virtual string IP
            {
                get { return _ip; }
            }
            public virtual string MAC
            {
                get { return _mac; }
            }
            public virtual string OSInfo
            {
                get { return _osInfo; }
            }
            public virtual string PowerStatus
            {
                get { return _powerStatus; }
            }
            public virtual string Uptime
            {
                get { return _uptime; }
            }
            public virtual string HostInfo
            {
                get { return _hostInfo; }
            }
            public virtual string TemplateName
            {
                get { return _templateName; }
            }
            private string _name;
            private string _uuid;
            private string _vCpuNum;
            private string _memSize;
            private string _srInfo;
            private string _nicNum;
            private string _ip;
            private string _mac;
            private string _osInfo;
            private string _powerStatus;
            private string _uptime;
            private string _hostInfo;
            private string _templateName;
        }

        public class PGPUInfo
        {
            public PGPUInfo(string name, string PGPUuuid, string PGUPHost,
                string BusAddress, string utilization, string memoryUtilization, string Temperature, string PowerStatus)
            {
                _name = name;
                _uuid = PGPUuuid;
                _host = PGUPHost;
                _busAddress = BusAddress;
                _utilization = utilization;
                _MemUtilization = memoryUtilization;
                _temperature = Temperature;
                _powerStatus = PowerStatus;
            }
            public virtual string Name
            {
                get { return _name; }
            }
            public virtual string UUID
            {
                get { return _uuid; }
            }
            public virtual string Host
            {
                get { return _host; }
            }
            public virtual string BusAddress
            {
                get { return _busAddress; }
            }
            public virtual string Utilization
            {
                get { return _utilization; }
            }
            public virtual string MemoryUtilization
            {
                get { return _MemUtilization; }
            }
            public virtual string Temperature
            {
                get { return _temperature; }
            }
            public virtual string PowerStatus
            {
                get { return _powerStatus; }
            }
            private string _name;
            private string _uuid;
            private string _host;
            private string _busAddress;
            private string _utilization;
            private string _MemUtilization;
            private string _temperature;
            private string _powerStatus;
        }

        private string hostCpuUsageString(Host host)
        {
            double sum = 0;
            if (host.host_CPUs == null)
                return Messages.HYPHEN;
            int total = host.host_CPUs.Count;
            for (int i = 0; i < total; i++)
            {
                sum += MetricUpdater.GetValue(host, String.Format("cpu{0}", i.ToString()));
            }
            if (total == 0 || Double.IsNaN(sum))
                return Messages.HYPHEN;
            if (total == 1)
                return String.Format(Messages.QUERY_PERCENT_OF_CPU, (sum * 100).ToString("0."));
            return String.Format(Messages.QUERY_PERCENT_OF_CPUS, ((sum * 100) / total).ToString("0."), total);
        }

        private string hostMemoryUsageString(Host host)
        {
            double free = MetricUpdater.GetValue(host, "memory_free_kib");
            double total = MetricUpdater.GetValue(host, "memory_total_kib");

            if (total == 0 || Double.IsNaN(total) || Double.IsNaN(free))
                return Messages.HYPHEN;

            return String.Format(Messages.QUERY_MEMORY_USAGE, ((total - free) / total * 100).ToString("0.") + "%", Util.MemorySizeString(total * Util.BINARY_KILO));
        }

        private string hostNetworkUsageString(Host host)
        {
            double sum = 0;
            double max = 0;
            int i = 0;
            foreach (PIF pif in host.Connection.ResolveAll(host.PIFs))
            {
                if (!pif.physical)
                    continue;

                double value = MetricUpdater.GetValue(host, String.Format("pif_{0}_rx", pif.device)) + MetricUpdater.GetValue(host, String.Format("vbd_{0}_tx", pif.device));
                sum += value;
                if (value > max)
                    max = value;
                i++;
            }
            if (Double.IsNaN(sum))
                return Messages.HYPHEN;
            return i == 0 ? Messages.HYPHEN : String.Format(Messages.QUERY_DATA_AVG_MAX, (sum / (Util.BINARY_KILO * i)).ToString("0."), (max / Util.BINARY_KILO).ToString("0."));
        }

        private string vmMemoryUsageString(VM vm)
        {
            double free = MetricUpdater.GetValue(vm, "memory_internal_free");
            double total = MetricUpdater.GetValue(vm, "memory");

            if (total == 0 || Double.IsNaN(total) || Double.IsNaN(free))
                return Messages.HYPHEN;

            return String.Format(Messages.QUERY_MEMORY_USAGE, ((total - free) / total * 100).ToString("0.") + "%", Util.MemorySizeString(total));
        }

        private string vmCpuUsageString(VM vm)
        {
            VM_metrics metrics = vm.Connection.Resolve(vm.metrics);
            if (metrics == null)
                return "";
            double sum = 0;
            int total = (int)metrics.VCPUs_number;
            for (int i = 0; i < total; i++)
            {
                sum += MetricUpdater.GetValue(vm, String.Format("cpu{0}", i.ToString()));
            }

            if (total == 0 || Double.IsNaN(sum))
                return Messages.HYPHEN;
            if (total == 1)
                return String.Format(Messages.QUERY_PERCENT_OF_CPU, (sum * 100).ToString("0."));
            return String.Format(Messages.QUERY_PERCENT_OF_CPUS, ((sum * 100) / total).ToString("0."), total);
        }

        private void ComposeParameters(ReportViewer viewer, IXenConnection connection)
        {
            string ParamLabelsStr;
            string ParamValuesStr;

            ParamLabelsStr = "LBL_POOLINFO|";
            ParamValuesStr = connection.Cache.Pools[0].Name + "|";
            ParamLabelsStr += "LBL_POOLUUID|";
            ParamValuesStr += "UUID:" + connection.Cache.Pools[0].uuid + "|";
            //Host Infor
            ParamLabelsStr += "LBL_SERVERS|";
            ParamValuesStr += Messages.SERVERS + "|";
            ParamLabelsStr += "LBL_HOSTNAME|";
            ParamValuesStr += Messages.NAME + "|";
            ParamLabelsStr += "LBL_ADDRESS|";
            ParamValuesStr += Messages.ADDRESS + "|";
            ParamLabelsStr += "LBL_POOLMASTER|";
            ParamValuesStr += Messages.POOL_MASTER + "|";
            ParamLabelsStr += "LBL_CPUUSAGE|";
            ParamValuesStr += Messages.OVERVIEW_CPU_USAGE + "|";
            ParamLabelsStr += "LBL_NETWORKUSAGE|";
            ParamValuesStr += Messages.OVERVIEW_NETWORK + Messages.OVERVIEW_UNITS + "|";
            ParamLabelsStr += "LBL_MEMORYUSAGE|";
            ParamValuesStr += Messages.OVERVIEW_MEMORY_USAGE + "|";
            ParamLabelsStr += "LBL_UPTIME|";
            ParamValuesStr += Messages.UPTIME + "|";

            //network Info
            ParamLabelsStr += "LBL_NETWORKS|";
            ParamValuesStr += Messages.NETWORKS + "|";
            ParamLabelsStr += "LBL_LINKSTATUS|";
            ParamValuesStr += Messages.LINK_STATUS + "|";
            ParamLabelsStr += "LBL_MAC|";
            ParamValuesStr += Messages.MAC + "|";

            //storage Info
            ParamLabelsStr += "LBL_STORAGE|";
            ParamValuesStr += Messages.DATATYPE_STORAGE + "|";
            ParamLabelsStr += "LBL_STORAGETYPE|";
            ParamValuesStr += Messages.STORAGE_TYPE + "|";
            ParamLabelsStr += "LBL_STORAGETYPE|";
            ParamValuesStr += Messages.STORAGE_TYPE + "|";
            ParamLabelsStr += "LBL_SIZE|";
            ParamValuesStr += Messages.SIZE + "|";
            ParamLabelsStr += "LBL_LOCATION|";
            ParamValuesStr += Messages.NEWSR_LOCATION + "|";
            ParamLabelsStr += "LBL_DESCRIPTION|";
            ParamValuesStr += Messages.DESCRIPTION + "|";

            //PGOU Info
            ParamLabelsStr += "LBL_GPU|";
            ParamValuesStr += Messages.GPU + "|";

            //VM Info
            ParamLabelsStr += "LBL_VMS|";
            ParamValuesStr += Messages.VMS + "|";
            ParamLabelsStr += "LBL_POWERSTATE|";
            ParamValuesStr += Messages.POWER_STATE + "|";
            ParamLabelsStr += "LBL_OPERATINGSYSTEM|";
            ParamValuesStr += Messages.OPERATING_SYSTEM + "|";
            ParamLabelsStr += "LBL_NIC|";
            ParamValuesStr += Messages.NIC + "|";
            ParamLabelsStr += "LBL_SERVER|";
            ParamValuesStr += Messages.SERVER + "|";
            ParamLabelsStr += "LBL_TEMPLATE|";
            ParamValuesStr += Messages.TEMPLATE + "|";
            ParamLabelsStr += "LBL_HOSTORPOOL|";
            ParamValuesStr += Messages.SERVER + "/" + Messages.POOL + "|";
            

            ReportParameter ParamLabels = new ReportParameter("ParamLabels", ParamLabelsStr);
            ReportParameter ParamValues = new ReportParameter("ParamValues", ParamValuesStr);
            viewer.LocalReport.SetParameters(new ReportParameter[] { ParamLabels, ParamValues });
        }

        private void ComposeHostData()
        {
            m_Hosts = new List<HostInfo>();
            foreach (Host host in Connection.Cache.Hosts)
            {
                if (Cancelling)
                    throw new CancelledException();
                string cpu_usage = hostCpuUsageString(host);
                string mem_usgae = hostMemoryUsageString(host);
                string network_usgae = hostNetworkUsageString(host);
                HostInfo buf = new HostInfo(host.name_label, host.address, host.uuid, cpu_usage, host.IsMaster() ? Messages.YES : Messages.NO, network_usgae, mem_usgae, Convert.ToString(host.Uptime));
                m_Hosts.Insert(0, buf);
                itemIndex++;
                PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
            }
        }
        private void ComposeNetworkData()
        {
            m_Networks = new List<NetworkInfo>();
            foreach (XenAPI.Network network in Connection.Cache.Networks)
            {
                if (Cancelling)
                    throw new CancelledException();
                if (network.other_config.ContainsKey("is_guest_installer_network"))
                {
                    if (network.other_config["is_guest_installer_network"].ToLower() == "true")
                    {
                        itemIndex++;
                        PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
                        continue;
                    }
                }
                List<PIF> pifs = network.Connection.ResolveAll(network.PIFs);
                NetworkInfo buf;
                if (pifs.Count != 0)
                   buf = new NetworkInfo(network.Name, Helpers.VlanString(pifs[0]), network.LinkStatusString, pifs[0].MAC, network.MTU.ToString());
                else
                    buf = new NetworkInfo(network.Name, Messages.HYPHEN, network.LinkStatusString, Messages.HYPHEN, network.MTU.ToString());
                m_Networks.Insert(0, buf);
                itemIndex++;
                PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
            }
        }
        private void ComposeSRData()
        {
            m_SRs = new List<SRInfo>();
            foreach (XenAPI.SR sr in Connection.Cache.SRs)
            {
                if (Cancelling)
                    throw new CancelledException();
                string srSizeString;
                if (sr.physical_size == 0)
                    srSizeString = Messages.HYPHEN;
                else
                    srSizeString = string.Format(Messages.SR_SIZE_USED,
                     (sr.physical_utilisation / sr.physical_size * 100).ToString("0.") + "%",
                     Util.DiskSizeString(sr.physical_size),
                     Util.DiskSizeString(sr.virtual_allocation));
                string locationStr = Messages.HYPHEN;
                foreach (XenRef<PBD> pbdRef in sr.PBDs)
                {
                    PBD pbd = Connection.Resolve(pbdRef);

                    if (pbd.device_config.ContainsKey("location"))
                    {
                        if (locationStr == Messages.HYPHEN)
                            locationStr = "location:" + pbd.device_config["location"] + ";";
                        else
                            locationStr += "location:" + pbd.device_config["location"] + ";";
                    }
                    if (pbd.device_config.ContainsKey("device"))
                    {
                        if (locationStr == Messages.HYPHEN)
                            locationStr = "device:" + pbd.device_config["device"] + ";";
                        else
                            locationStr += "device:" + pbd.device_config["device"] + ";";
                    }
                    if (pbd.device_config.ContainsKey("SCSIid"))
                    {
                        if (locationStr == Messages.HYPHEN)
                            locationStr = "SCSIid:" + pbd.device_config["SCSIid"] + ";";
                        else
                            locationStr += "SCSIid:" + pbd.device_config["SCSIid"] + ";";
                    }
                    if (pbd.device_config.ContainsKey("targetIQN"))
                    {
                        if (locationStr == Messages.HYPHEN)
                            locationStr = "targetIQN:" + pbd.device_config["targetIQN"] + ";";
                        else
                            locationStr += "targetIQN:" + pbd.device_config["targetIQN"] + ";";
                    }
                }
                SRInfo buf = new SRInfo(sr.Name, sr.uuid, sr.type, srSizeString, locationStr, sr.Description);
                m_SRs.Insert(0, buf);
                itemIndex++;
                PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
            }
        }
        private void ComposeVMData()
        {
            m_VMs = new List<VMInfo>();
            foreach (XenAPI.VM vm in Connection.Cache.VMs)
            {
                if (Cancelling)
                    throw new CancelledException();
                if (vm.is_a_snapshot || vm.is_a_template || vm.is_control_domain || vm.is_snapshot_from_vmpp)
                {
                    itemIndex++;
                    PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
                    continue;
                }

                string OSinfo = Messages.HYPHEN;
                string srInfo = "";
                string MacInfo = "";
                ComparableList<ComparableAddress> addresses = new ComparableList<ComparableAddress>();
                if (vm.guest_metrics != null && !string.IsNullOrEmpty(vm.guest_metrics.opaque_ref) && !(vm.guest_metrics.opaque_ref.ToLower().Contains("null")))
                {
                    VM_guest_metrics metrics = VM_guest_metrics.get_record(Connection.Session, vm.guest_metrics);
                    OSinfo = metrics.os_version["name"];

                    List<VIF> vifs = vm.Connection.ResolveAll(vm.VIFs);

                    foreach (VIF vif in vifs)
                    {
                        MacInfo += vif.MAC + " ";
                        // PR-1373 - VM_guest_metrics.networks is a dictionary of IP addresses in the format:
                        // [["0/ip", <IPv4 address>], ["0/ipv6/0", <IPv6 address>], ["0/ipv6/1", <IPv6 address>]]
                        foreach (var network in metrics.networks.Where(n => n.Key.StartsWith(String.Format("{0}/ip", vif.device))))
                        {
                            ComparableAddress ipAddress;
                            if (!ComparableAddress.TryParse(network.Value, false, true, out ipAddress))
                                continue;

                            addresses.Add(ipAddress);
                        }
                    }
                }
                string host_name;
                if (MacInfo.Length == 0)
                    MacInfo = Messages.HYPHEN;
                foreach (XenRef<VBD> vbdRef in vm.VBDs)
                {
                    var vbd = vm.Connection.Resolve(vbdRef);
                    if (vbd != null && !vbd.IsCDROM && !vbd.IsFloppyDrive && vbd.bootable)
                    {
                        VDI vdi = vm.Connection.Resolve(vbd.VDI);
                        srInfo += vdi.name_label + ":" + vbd.Name + ":" + vdi.SizeText + ";";
                    }
                }
                if (srInfo.Length == 0)
                    srInfo = Messages.HYPHEN;
                
                if (vm.resident_on != null && !string.IsNullOrEmpty(vm.resident_on.opaque_ref) && !(vm.resident_on.opaque_ref.ToLower().Contains("null")))
                {
                    host_name = vm.Connection.Resolve(vm.resident_on).Name;
                }
                else //if there is no host, use pool replace the host
                {
                    host_name = Connection.Cache.Pools[0].Name;
                }

                string default_template_name = Messages.HYPHEN;
                if(vm.other_config.ContainsKey("base_template_name"))
                    default_template_name = vm.other_config["base_template_name"];
                
                VMInfo buf = new VMInfo(vm.Name, vm.uuid, vmCpuUsageString(vm), vmMemoryUsageString(vm),
                    srInfo, Convert.ToString(vm.VIFs.Count), Convert.ToString(addresses), MacInfo, OSinfo, Convert.ToString(vm.power_state),
                    Convert.ToString(vm.RunningTime), host_name, default_template_name);
                m_VMs.Insert(0, buf);
                itemIndex++;
                PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
            }
        }

        

        private void ComposeGPUData()
        {
            m_PGUPs = new List<PGPUInfo>();
            foreach (XenAPI.PGPU pGpu in Connection.Cache.PGPUs)
            {
                Host host= Connection.Resolve(pGpu.host);
                if (host == null)
                    return;
                PCI pci = Connection.Resolve(pGpu.PCI);
                string pci_id = pci.pci_id.Replace(@":", "/");
                double temperature = MetricUpdater.GetValue(host, String.Format("gpu_temperature_{0}", pci_id));
                double powerStatus = MetricUpdater.GetValue(host, String.Format("power_usage_{0}", pci_id));
                double utilisation_computer = MetricUpdater.GetValue(host, String.Format("utilisation_computer_{0}", pci_id));
                double utilisation_memory_io = MetricUpdater.GetValue(host, String.Format("utilisation_memory_io_{0}", pci_id));
                PGPUInfo buf = new PGPUInfo(pGpu.Name, pGpu.uuid, host.Name, pci.pci_id, utilisation_computer.ToString(),
                    utilisation_memory_io.ToString(), temperature.ToString(), powerStatus.ToString());
                m_PGUPs.Insert(0, buf);
            }
        }

        public override void RecomputeCanCancel()
        {
            CanCancel = true;
        }

        private void export2XLS()
        {
            Warning[] warnings;
            string[] streamIds;
            string mimeType = string.Empty;
            string encoding = string.Empty;
            string extension = string.Empty;
            FileStream fs = null;
            ReportViewer viewer = new ReportViewer();
            viewer.ProcessingMode = ProcessingMode.Local;
            viewer.LocalReport.ReportPath = "resource_tatistic_report.rdlc";

            ReportDataSource rds1 = new ReportDataSource("Report_HostInfo", m_Hosts);
            ReportDataSource rds2 = new ReportDataSource("Report_NetworkInfo", m_Networks);
            ReportDataSource rds3 = new ReportDataSource("Report_SRInfo", m_SRs);
            ReportDataSource rds4 = new ReportDataSource("Report_VMInfo", m_VMs);
            ReportDataSource rds5 = new ReportDataSource("Report_PGPUInfo", m_PGUPs);
            viewer.LocalReport.DataSources.Add(rds1);
            viewer.LocalReport.DataSources.Add(rds2);
            viewer.LocalReport.DataSources.Add(rds3);
            viewer.LocalReport.DataSources.Add(rds4);
            viewer.LocalReport.DataSources.Add(rds5);
            ComposeParameters(viewer, Connection);
            byte[] bytes = viewer.LocalReport.Render("Excel", null, out mimeType, out encoding, out extension, out streamIds, out warnings);

            try
            {
                fs = new FileStream(_filename, FileMode.Create);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                log.Debug(ex, ex);
            }
            finally
            {
                PercentComplete = 100;
                if (fs != null)
                    fs.Close();
                try
                {
                    Process xlProcess = Process.Start(_filename);
                    /*ProcessStartInfo startInfo = new ProcessStartInfo(excel, _filename);
                    startInfo.FileName = "EXCEL.EXE";
                    startInfo.Arguments = _filename;
                    Process.Start(startInfo);*/
                }
                catch (Exception ex)
                {
                    log.Debug(ex, ex);
                }
            }
        }

        private void ComposeCSVRow(ref FileStream fs, ref List<string> items)
        {
            StringBuilder builder = new StringBuilder();
            bool firstColumn = true;
            byte[] info;
            foreach (string value in items)
            {
                // Add separator if this isn't the first value
                if (!firstColumn)
                    builder.Append(',');
                if (value.IndexOfAny(new char[] { '"', ',' }) != -1)
                    builder.AppendFormat("\"{0}\"", value.Replace("\"", "\"\""));
                else
                    builder.Append(value);
                firstColumn = false;
            }
            info = new UTF8Encoding(true).GetBytes(builder.ToString() + "\n");
            fs.Write(info, 0, info.Length);
            items.Clear();
        }

        private void HostInfoCSVMaker(ref FileStream fs)
        {
            List<string> items = new List<string>();
            items.Add("\n");
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.SERVER);
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.NAME);
            items.Add("UUID");
            items.Add(Messages.POOL_MASTER);
            items.Add(Messages.ADDRESS);
            items.Add(Messages.OVERVIEW_CPU_USAGE);
            items.Add(Messages.OVERVIEW_MEMORY_USAGE);
            items.Add(Messages.OVERVIEW_NETWORK + Messages.OVERVIEW_UNITS);
            items.Add(Messages.UPTIME);
            ComposeCSVRow(ref fs, ref items);

            foreach (HostInfo host in m_Hosts)
            {
                items.Add(host.Name);
                items.Add(host.UUID);
                items.Add(host.Role);
                items.Add(host.Address);
                items.Add(host.CpuUsage);
                items.Add(host.MemUsage);
                items.Add(host.NetworkUsage);
                items.Add(host.Uptime);
                ComposeCSVRow(ref fs, ref items);
            }
        }

        private void NetworkInfoCSVMaker(ref FileStream fs)
        {
            List<string> items = new List<string>();
            items.Add("\n");
            ComposeCSVRow(ref fs, ref items);
            items.Add(Messages.NETWORKS);
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.NAME);
            items.Add(Messages.LINK_STATUS);
            items.Add(Messages.MAC);
            items.Add("MTU");
            items.Add("VLAN");
            ComposeCSVRow(ref fs, ref items);

            foreach (NetworkInfo network in m_Networks)
            {
                items.Add(network.Name);
                items.Add(network.LinkStatus);
                items.Add(network.MAC);
                items.Add(network.MTU);
                items.Add(network.VlanID);
                ComposeCSVRow(ref fs, ref items);
            }
        }

        private void SRInfoCSVMaker(ref FileStream fs)
        {
            List<string> items = new List<string>();
            items.Add("\n");
            ComposeCSVRow(ref fs, ref items);
            items.Add(Messages.DATATYPE_STORAGE);
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.NAME);
            items.Add("UUID");
            items.Add(Messages.STORAGE_TYPE);
            items.Add(Messages.SIZE);
            items.Add(Messages.NEWSR_LOCATION);
            items.Add(Messages.DESCRIPTION);
            ComposeCSVRow(ref fs, ref items);

            foreach (SRInfo sr in m_SRs)
            {
                items.Add(sr.Name);
                items.Add(sr.UUID);
                items.Add(sr.Type);
                items.Add(sr.Size);
                items.Add(sr.Remark);
                items.Add(sr.Description);
                ComposeCSVRow(ref fs, ref items);
            }
        }

        private void PGPUInfoCSVMaker(ref FileStream fs)
        {
            List<string> items = new List<string>();
            items.Add("\n");
            ComposeCSVRow(ref fs, ref items);
            items.Add(Messages.GPU);
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.NAME);
            items.Add("UUID");
            items.Add(Messages.BUS_PATH);
            items.Add(Messages.SERVER);
            items.Add(Messages.OVERVIEW_MEMORY_USAGE);
            items.Add(Messages.POWER_STATE);
            items.Add("Temperature");
            items.Add("Utilization");
            ComposeCSVRow(ref fs, ref items);

            foreach (PGPUInfo pGpu in m_PGUPs)
            {
                items.Add(pGpu.Name);
                items.Add(pGpu.UUID);
                items.Add(pGpu.BusAddress);
                items.Add(pGpu.Host);
                items.Add(pGpu.MemoryUtilization);
                items.Add(pGpu.PowerStatus);
                items.Add(pGpu.Temperature);
                items.Add(pGpu.Utilization);
                ComposeCSVRow(ref fs, ref items);
            }
        }

        private void VMInfoCSVMaker(ref FileStream fs)
        {
            List<string> items = new List<string>();
            items.Add("\n");
            ComposeCSVRow(ref fs, ref items);
            items.Add(Messages.VMS);
            ComposeCSVRow(ref fs, ref items);

            items.Add(Messages.NAME);
            items.Add("UUID");
            items.Add(Messages.SERVER + "/" + Messages.POOL);
            items.Add(Messages.ADDRESS);
            items.Add(Messages.MAC);
            items.Add(Messages.NIC);
            items.Add(Messages.OVERVIEW_MEMORY_USAGE);
            items.Add(Messages.OPERATING_SYSTEM);
            items.Add(Messages.POWER_STATE);
            items.Add(Messages.STORAGE_DISK);
            items.Add(Messages.TEMPLATE);
            items.Add(Messages.OVERVIEW_CPU_USAGE);
            items.Add(Messages.UPTIME);
            ComposeCSVRow(ref fs, ref items);

            foreach (VMInfo vm in m_VMs)
            {
                items.Add(vm.Name);
                items.Add(vm.UUID);
                items.Add(vm.HostInfo);
                items.Add(vm.IP);
                items.Add(vm.MAC);
                items.Add(vm.NicNum);
                items.Add(vm.MemSize);
                items.Add(vm.OSInfo);
                items.Add(vm.PowerStatus);
                items.Add(vm.SRInfo);
                items.Add(vm.TemplateName);
                items.Add(vm.VCpuNum);
                items.Add(vm.Uptime);
                ComposeCSVRow(ref fs, ref items);
            }
        }

        private void export2CSV()
        {
            FileStream fs = null;
            try
            {
                List<string> items = new List<string>();

                fs = new FileStream(_filename, FileMode.Create);
                //pool information part
                items.Add(Messages.POOL + ":" + Connection.Cache.Pools[0].Name);
                ComposeCSVRow(ref fs, ref items);
                items.Add("UUID:" + Connection.Cache.Pools[0].uuid);
                ComposeCSVRow(ref fs, ref items);
                //server information
                HostInfoCSVMaker(ref fs);
                NetworkInfoCSVMaker(ref fs);
                SRInfoCSVMaker(ref fs);
                PGPUInfoCSVMaker(ref fs);
                VMInfoCSVMaker(ref fs);
            }
            catch (Exception ex)
            {
                log.Debug(ex, ex);
            }
            finally
            {
                PercentComplete = 100;
                if (fs != null)
                    fs.Close();
            }
        }

        private void DoExport()
        {
            CanCancel = true;
            PercentComplete = 0;
            ComposeHostData();
            ComposeNetworkData();
            ComposeSRData();
            ComposeVMData();
            ComposeGPUData();

            if (_fileType == Convert.ToInt32(FILE_TYPE_INDEX.XLS))
            {
                export2XLS();
            }
            else
            {
                export2CSV();
            }
        }
    }
}
