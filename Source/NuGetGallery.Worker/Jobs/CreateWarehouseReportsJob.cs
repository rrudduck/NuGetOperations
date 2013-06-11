﻿using System;
using System.ComponentModel.Composition;
using System.Data.SqlClient;
using NuGetGallery.Operations;

namespace NuGetGallery.Worker.Jobs
{
    //[Export(typeof(WorkerJob))]
    public class CreateWarehouseReportsJob : WorkerJob
    {
        public override TimeSpan Period
        {
            get
            {
                return TimeSpan.FromHours(12);
            }
        }

        public override void RunOnce()
        {
            Logger.Info("Starting create warehouse reports task.");
            new CreateWarehouseReportsTask
            {
                ConnectionString = new SqlConnectionStringBuilder(Settings.WarehouseConnectionString),
                ReportStorage = Settings.MainStorage,
                WhatIf = Settings.WhatIf
            }.Execute();
            Logger.Info("Finished create warehouse reports task.");
        }
    }
}