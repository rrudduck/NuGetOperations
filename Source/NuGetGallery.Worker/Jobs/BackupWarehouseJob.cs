﻿using System;
using System.ComponentModel.Composition;
using System.Data.SqlClient;
using NuGetGallery.Operations;

namespace NuGetGallery.Worker.Jobs
{
    //[Export(typeof(WorkerJob))]
    public class BackupWarehouseJob : WorkerJob
    {
        public override TimeSpan Period
        {
            get
            {
                return TimeSpan.FromDays(1);
            }
        }

        public override TimeSpan Offset
        {
            get
            {
                return TimeSpan.FromMinutes(30);
            }
        }

        public override void RunOnce()
        {
            ExecuteTask(new BackupWarehouseTask
            {
                ConnectionString = new SqlConnectionStringBuilder(Settings.WarehouseConnectionString),
                WhatIf = Settings.WhatIf,
                IfOlderThan = 25,
            });
        }
    }
}