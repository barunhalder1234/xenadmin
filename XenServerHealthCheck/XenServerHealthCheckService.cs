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
using System.ServiceProcess;
using XenAdmin.Network;
using XenAPI;
using System.Threading;
using XenAdmin;

namespace XenServerHealthCheck
{
    public partial class XenServerHealthCheckService : ServiceBase
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private CancellationTokenSource cts = new CancellationTokenSource();

        public XenServerHealthCheckService()
        {
            InitializeComponent();
            AutoLog = false;
            if (!System.Diagnostics.EventLog.SourceExists("XenServerHealthCheck"))
            {
                System.Diagnostics.EventLog.CreateEventSource(
                    "XenServerHealthCheck", "XenServerHealthCheckLog");
            }

            XenAdminConfigManager.Provider = new XenServerHealthCheckConfigProvider();
        }

        private static void initConfig()
        {
            if (string.IsNullOrEmpty(Properties.Settings.Default.UUID))
            {
                Properties.Settings.Default.UUID = System.Guid.NewGuid().ToString();
                Properties.Settings.Default.Save();
            }

            log.InfoFormat("XenServer Health Check Service {0} starting...", Properties.Settings.Default.UUID);
        }

        protected override void OnStart(string[] args)
        {
            // Set up a timer to trigger the uploading service.
            try
            {
                initConfig();
                CredentialReceiver.instance.Init();
                ServerListHelper.instance.Init();
                System.Timers.Timer timer = new System.Timers.Timer();
                timer.Interval = Registry.HealthCheckTimeInterval * 60000;
                log.InfoFormat("XenServer Health Check Service will be scheduled every {0} milliseconds", timer.Interval);
                timer.Elapsed += new System.Timers.ElapsedEventHandler(this.OnTimer);
                timer.Start();
            }
            catch (Exception exp)
            {
                EventLog.WriteEntry(exp.Message, System.Diagnostics.EventLogEntryType.FailureAudit);
                Stop();
            }
        }

        protected override void OnStop()
        {
            log.Info("XenServer Health Check Service stopping...");
            CredentialReceiver.instance.UnInit();
            cts.Cancel();
            bool canStop;
            do
            {
                canStop = true;
                List<ServerInfo> servers = ServerListHelper.instance.GetServerList();
                foreach (ServerInfo server in servers)
                {
                    if (server.task != null && !(server.task.IsCompleted || server.task.IsCanceled || server.task.IsFaulted))
                        canStop = false;
                }
                if(canStop == false)
                    Thread.Sleep(1000);
            } while (canStop == false);
        }

        public void OnTimer(object sender, System.Timers.ElapsedEventArgs args)
        {
            log.Info("XenServer Health Check Service start to refresh uploading tasks");
            
            //We need to check if CIS can be accessed in current enviroment

            List<ServerInfo> servers = ServerListHelper.instance.GetServerList();
            foreach (ServerInfo server in servers)
            {
                if (server.task != null && (!server.task.IsCompleted || !server.task.IsCanceled || !server.task.IsFaulted))
                {
                    continue;
                }

                XenConnection connectionInfo = new XenConnection();
                connectionInfo.Hostname = server.HostName;
                connectionInfo.Username = server.UserName;
                connectionInfo.Password = server.Password;
                log.InfoFormat("Check server {0} with user {1}", connectionInfo.Hostname, connectionInfo.Username);
                Session session = new Session(server.HostName, 80);
                session.APIVersion = API_Version.LATEST;
                try
                {
                    session.login_with_password(server.UserName, server.Password);
                    connectionInfo.LoadCache(session);
                    if (RequestUploadTask.Request(connectionInfo, session) || RequestUploadTask.OnDemandRequest(connectionInfo, session))
                    {
                        // Create a task to collect server status report and upload to CIS server
                        log.InfoFormat("Start to upload server status report for XenServer {0}", connectionInfo.Hostname);

                        XenServerHealthCheckBundleUpload upload = new XenServerHealthCheckBundleUpload(connectionInfo);
                        Action uploadAction = delegate()
                        {
                            upload.runUpload(cts.Token);
                        };
                        System.Threading.Tasks.Task task = new System.Threading.Tasks.Task(uploadAction);
                        task.Start();

                        server.task = task;
                        ServerListHelper.instance.UpdateServerInfo(server);
                    }
                    session.logout();
                    session = null;
                }
                catch (Exception exn)
                {
                    if (session != null)
                        session.logout();
                    log.Error(exn, exn);
                }
            }
        }
    }
}
