﻿/* Copyright (c) Citrix Systems Inc. 
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
using System.Collections.Generic;
using XenAdmin.Network;
using System.Windows.Forms;
using System.IO;

namespace XenAdmin.Commands
{
    /// <summary>
    /// export statistic report for the pool.
    /// </summary>
    internal class ExportPooResourceStatisticlCommand : Command
    {
        public ExportPooResourceStatisticlCommand()
        {
        }

        public ExportPooResourceStatisticlCommand(IMainWindow mainWindow, IEnumerable<SelectedItem> selection)
            : base(mainWindow, selection)
        {
        }

        public ExportPooResourceStatisticlCommand(IMainWindow mainWindow, SelectedItem selection)
            : base(mainWindow, selection)
        {
        }
        
        protected override void ExecuteCore(SelectedItemCollection selection)
        {
            foreach (SelectedItem item in selection)
            {
                if (CanExecute(item))
                {
                    Execute(item.Connection);
                }
            }
        }

        protected override bool CanExecuteCore(SelectedItemCollection selection)
        {
            foreach (SelectedItem item in selection)
            {
                if (CanExecute(item))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanExecute(SelectedItem selection)
        {
            return selection.Connection != null && selection.Connection.IsConnected && selection.PoolAncestor != null;
        }

        public override string MenuText
        {
            get
            {
                return Messages.MAINWINDOW_EXPORT_POOL_RESOURCE_STATISTIC;
            }
        }

        public override string ContextMenuText
        {
            get
            {
                return Messages.MAINWINDOW_EXPORT_POOL_RESOURCE_STATISTIC;
            }
        }

        private bool Execute(IXenConnection connection)
        {
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = " xls files(*.xls)|*.xls";
            saveFileDialog1.FilterIndex = 1;
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                string localFilePath = saveFileDialog1.FileName.ToString();
                try
                {
                    File.Delete(localFilePath);
                    new XenAdmin.Actions.ExportStaticReportAction(connection, localFilePath).RunAsync();
                }
                catch (Exception exp)
                {
                    MessageBox.Show("导出文件失败, " + exp.Message);
                }
            }
            return true;
        }
    }
}