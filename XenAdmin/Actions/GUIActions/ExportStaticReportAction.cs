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

namespace XenAdmin.Actions
{
    public class ExportStaticReportAction : AsyncAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly string _filename;
        private Exception _exception = null;
        private static MetricUpdater MetricUpdater;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="filename"></param>
        public ExportStaticReportAction(IXenConnection connection, string filename)
            : base(connection, string.Format(Messages.ACTION_EXPORT_STATISTIC_FROM_X, Helpers.GetName(connection)),
            Messages.ACTION_EXPORT_DESCRIPTION_PREPARING)
        {
            Pool = Helpers.GetPool(connection);
            _filename = filename;
            MetricUpdater = new MetricUpdater();
            MetricUpdater.SetXenObjects(connection.Cache.Hosts);
            MetricUpdater.SetXenObjects(connection.Cache.VMs);
            MetricUpdater.SetXenObjects(connection.Cache.PIFs);
            MetricUpdater.SetXenObjects(connection.Cache.VBDs);
            MetricUpdater.SetXenObjects(connection.Cache.VIFs);
            MetricUpdater.UpdateMetricsOnce();

            m_Hosts = new List<HostInfo>();
            m_SRs = new List<SRInfo>();
            m_Networks = new List<NetworkInfo>();
            m_VMs = new List<VMInfo>();
            itemCount = connection.Cache.Hosts.Length + connection.Cache.Networks.Length + connection.Cache.SRs.Length + connection.Cache.VMs.Length;
        }

        protected override void Run()
        {
            SafeToExit = false;
            Description = Messages.ACTION_EXPORT_DESCRIPTION_IN_PROGRESS;
            RelatedTask = XenAPI.Task.create(Session,
                string.Format(Messages.ACTION_EXPORT_POOL_RESOURCE_STATISTIC_TASK_NAME, this.Connection.Cache.Pools[0].Name),
                string.Format(Messages.ACTION_EXPORT_POOL_RESOURCE_STATISTIC_TASK_DESCRIPTION, this.Connection.Cache.Pools[0].Name, _filename));

            if (Cancelling)
                throw new CancelledException();

            UriBuilder uriBuilder = new UriBuilder(this.Session.Url);
            uriBuilder.Path = "export";
            uriBuilder.Query = string.Format("session_id={0}&uuid={1}&task_id={2}",
                Uri.EscapeDataString(this.Session.uuid),
                Uri.EscapeDataString(this.Connection.Cache.Pools[0].uuid),
                Uri.EscapeDataString(this.RelatedTask.opaque_ref));

            log.DebugFormat("Exporting Statistic report from {1} to {2}", this.Connection.Cache.Pools[0].Name, uriBuilder.ToString(), _filename);
 
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
                var fi = new FileInfo(_filename);
                log.DebugFormat("Progress of the action until exception: {0}", PercentComplete);
                log.DebugFormat("Size file exported until exception: {0}", fi.Length);
                
                log.DebugFormat("Deleting {0}", _filename);
                File.Delete(_filename);
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
                string SRSize, string SRRemark)
            {
                _name = name;
                _uuid = SRUuid;
                _type = SRType;
                _size = SRSize;
                _remark = SRRemark;
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
            private string _name;
            private string _uuid;
            private string _type;
            private string _size;
            private string _remark;
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
                string VMpowerStatus, string VMuptime, string VMhostInfo)
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

            return String.Format(Messages.QUERY_MEMORY_USAGE, Util.MemorySizeStringWithoutUnits((total - free) * Util.BINARY_KILO), Util.MemorySizeString(total * Util.BINARY_KILO));
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

            return String.Format(Messages.QUERY_MEMORY_USAGE, Util.MemorySizeStringWithoutUnits((total - (free * Util.BINARY_KILO))), Util.MemorySizeString(total));
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

        private string vmIPAddresses(VM vm)
        {
            VM_guest_metrics metrics = vm.Connection.Resolve(vm.guest_metrics);
            if (metrics == null)
                return Messages.HYPHEN;

            List<string> addresses = new List<string>(metrics.networks.Values);

            if (addresses.Count > 0)
                return String.Join(", ", addresses.ToArray());
            else
                return Messages.HYPHEN;
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

            ReportParameter ParamLabels = new ReportParameter("ParamLabels", ParamLabelsStr);
            ReportParameter ParamValues = new ReportParameter("ParamValues", ParamValuesStr);
            viewer.LocalReport.SetParameters(new ReportParameter[] { ParamLabels, ParamValues });
        }

        private void ComposeHostData()
        {
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
                NetworkInfo buf = new NetworkInfo(network.Name, Helpers.VlanString(pifs[0]), network.LinkStatusString, pifs[0].MAC, network.MTU.ToString());
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
                SRInfo buf = new SRInfo(sr.Name, sr.uuid, sr.type, sr.SizeString, sr.name_description);
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
                if (vm.guest_metrics != null && !string.IsNullOrEmpty(vm.guest_metrics.opaque_ref) && !(vm.guest_metrics.opaque_ref.ToLower().Contains("null")))
                {
                    VM_guest_metrics gms = VM_guest_metrics.get_record(Connection.Session, vm.guest_metrics);
                    OSinfo = gms.os_version["name"];
                }
                foreach (XenRef<VBD> vbdRef in vm.VBDs)
                {
                    string device = VBD.get_device(Connection.Session, vbdRef);
                    if (device == "")
                    {
                        device = "unknown";
                    }
                    XenRef<VDI> vdiRef = VBD.get_VDI(Connection.Session, vbdRef);
                    if (vdiRef != null && !string.IsNullOrEmpty(vdiRef.opaque_ref) && !(vdiRef.opaque_ref.ToLower().Contains("null")))
                    {
                        VDI lVdi = VDI.get_record(Connection.Session, vdiRef);
                        srInfo += device + ":" + lVdi.name_label + ":" + lVdi.SizeText + "\n";
                    }
                }
                if (srInfo.Length == 0)
                    srInfo = Messages.HYPHEN;
                List<VIF> vifs = vm.Connection.ResolveAll(vm.VIFs);
                string MacInfo = "";
                foreach (VIF vif in vifs)
                {
                    MacInfo += vif.MAC + " ";
                }
                if (MacInfo.Length == 0)
                    MacInfo = Messages.HYPHEN;
                string host_name;
                if (vm.resident_on != null && !string.IsNullOrEmpty(vm.resident_on.opaque_ref) && !(vm.resident_on.opaque_ref.ToLower().Contains("null")))
                {
                    host_name = Host.get_name_label(Connection.Session, vm.resident_on);
                }
                else //if there is no host, use pool replace the host
                {
                    host_name = Connection.Cache.Pools[0].Name;
                }

                VMInfo buf = new VMInfo(vm.Name, vm.uuid, vmCpuUsageString(vm), vmMemoryUsageString(vm),
                    srInfo, Convert.ToString(vm.VIFs.Count), vmIPAddresses(vm), MacInfo, OSinfo, Convert.ToString(vm.power_state),
                    Convert.ToString(vm.RunningTime), host_name);
                m_VMs.Insert(0, buf);
                itemIndex++;
                PercentComplete = Convert.ToInt32(itemIndex * baseIndex / itemCount);
            }
        }

        private List<HostInfo> m_Hosts;
        List<SRInfo> m_SRs;
        List<NetworkInfo> m_Networks;
        List<VMInfo> m_VMs;
        long itemCount = 0;
        long itemIndex = 0;
        long baseIndex = 90;

        private void DoExport()
        {
            Warning[] warnings;
            string[] streamIds;
            string mimeType = string.Empty;
            string encoding = string.Empty;
            string extension = string.Empty;
            CanCancel = true;
            if (Cancelling)
                throw new CancelledException();
            // Setup the report viewer object and get the array of bytes
            ReportViewer viewer = new ReportViewer();
            viewer.ProcessingMode = ProcessingMode.Local;
            viewer.LocalReport.ReportPath = "C:\\Users\\chengz\\Documents\\Visual Studio 2005\\Projects\\Report\\Report\\Report1.rdlc";
            PercentComplete = 0;
            ComposeHostData();
            ComposeNetworkData();
            ComposeSRData();
            ComposeVMData();
            ReportDataSource rds1 = new ReportDataSource("Report_HostInfo", m_Hosts);
            ReportDataSource rds2 = new ReportDataSource("Report_NetworkInfo", m_Networks);
            ReportDataSource rds3 = new ReportDataSource("Report_SRInfo", m_SRs);
            ReportDataSource rds4 = new ReportDataSource("Report_VMInfo", m_VMs);
            viewer.LocalReport.DataSources.Add(rds1);
            viewer.LocalReport.DataSources.Add(rds2);
            viewer.LocalReport.DataSources.Add(rds3);
            viewer.LocalReport.DataSources.Add(rds4);
            ComposeParameters(viewer, Connection);
            byte[] bytes = viewer.LocalReport.Render("Excel", null, out mimeType, out encoding, out extension, out streamIds, out warnings);

            FileStream fs = null;
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
            }
        }
    }
}
