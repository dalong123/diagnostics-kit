﻿/**
 *  Part of the Diagnostics Kit
 *
 *  Copyright (C) 2016  Sebastian Solnica
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.

 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 */

using Dapper;
using LowLevelDesign.Diagnostics.Commons.Models;
using LowLevelDesign.Diagnostics.LogStore.Commons.Models;
using LowLevelDesign.Diagnostics.LogStore.MySql;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace LowLevelDesign.Diagnostics.LogStore.Tests
{
    public class MySqlLogStoreFixture : IDisposable
    {
        public void Dispose()
        {
            using (var conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["mysqlconn"].ConnectionString))
            {
                conn.Open();

                var paths = new[] { "###rather_not_existing_application_path###", "###rather_not_existing_application_path2###",
                    "###rather_not_existing_application_path3###" };
                var tableNames = new List<String>(paths.Length + paths.Length);

                foreach (var path in paths)
                {
                    var hash = BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", String.Empty);
                    tableNames.Add(MySqlLogStore.AppLogTablePrefix + hash);

                    conn.Execute("delete from appstat where ApplicationHash = @hash", new { hash });
                }

                // we will drop all the newly created tables in the db
                foreach (var tbl in conn.Query<String>("select table_name from information_schema.tables where table_name in @tableNames", new { tableNames }))
                {
                    conn.Execute("drop table if exists " + tbl);
                }
            }
        }
    }

    public class MySqlLogStoreTests : IDisposable, IClassFixture<MySqlLogStoreFixture>
    {
        [Fact]
        public async Task TestAddLogRecord()
        {
            var utcnow = DateTime.UtcNow.Date;
            var mysqlLogStore = new MySqlLogStore(() => utcnow);
            const string appPath = "###rather_not_existing_application_path###";
            var hash = BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(appPath))).Replace("-", String.Empty);

            var logrec = new LogRecord {
                LoggerName = "TestLogger",
                ApplicationPath = appPath,
                LogLevel = LogRecord.ELogLevel.Error,
                TimeUtc = DateTime.UtcNow,
                ProcessId = 123,
                ThreadId = 456,
                Server = "TestServer",
                Identity = "TestIdentity",
                CorrelationId = Guid.NewGuid().ToString(),
                Message = "Test log message to store in the log",
                ExceptionMessage = "Test exception log message",
                ExceptionType = "TestException",
                ExceptionAdditionalInfo = "Additinal info for the test exception",
                AdditionalFields = new Dictionary<String, Object>
                {
                    { "Host", "testhost.com" },
                    { "LoggedUser", "testloggeduser" },
                    { "HttpStatusCode", "200.1" },
                    { "Url", "http://testhost.com" },
                    { "Referer", "http://prevtesthost.com" },
                    { "ClientIP", null },
                    { "RequestData", "test test test" },
                    { "ResponseData", null },
                    { "ServiceName", "TestService" },
                    { "ServiceDisplayName", "Test service generating logs" },
                    { "NotExisting", null }
                },
                PerformanceData = new Dictionary<String, float>
                {
                    { "CPU", 2.0f },
                    { "Memory", 20000000f }
                }
            };

            // add log
            await mysqlLogStore.AddLogRecordAsync(logrec);

            using (var conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["mysqlconn"].ConnectionString))
            {
                conn.Open();

                // check if app tables were created
                var tables = conn.Query<String>("select table_name from information_schema.tables where table_schema = @db", new { db = conn.Database }).ToArray();

                Assert.Contains(MySqlLogStore.AppLogTablePrefix + hash, tables, StringComparer.OrdinalIgnoreCase);

                // check partitions
                var expectedPartitionNames = new[] { String.Format("{0}{1:yyyyMMdd}", Partition.PartitionPrefix, utcnow.Date.AddDays(1)),
                    String.Format("{0}{1:yyyyMMdd}", Partition.PartitionPrefix, utcnow.Date.AddDays(2)) };
                foreach (var prefix in new[] { MySqlLogStore.AppLogTablePrefix })
                {
                    var partitions = conn.Query<String>("select partition_name From information_schema.partitions where table_name = @tableName and table_schema = @db order by partition_name",
                        new { tableName = prefix + hash, db = conn.Database });
                    Assert.Equal(expectedPartitionNames, partitions);
                }

                // check logs content
                var dbLogRecs = conn.Query<DbAppLogRecord>("select * from " + MySqlLogStore.AppLogTablePrefix + hash).ToArray();

                Assert.True(dbLogRecs.Length == 1);
                var dbLogRec = dbLogRecs[0];

                Assert.Equal(logrec.LoggerName, dbLogRec.LoggerName);
                Assert.Equal(logrec.ApplicationPath, dbLogRec.ApplicationPath);
                Assert.Equal(logrec.LogLevel, dbLogRec.LogLevel);
                Assert.Equal(logrec.TimeUtc.ToShortDateString(), dbLogRec.TimeUtc.ToShortDateString());
                Assert.Equal(logrec.ProcessId, dbLogRec.ProcessId);
                Assert.Equal(logrec.ThreadId, dbLogRec.ThreadId);
                Assert.Equal(logrec.Server, dbLogRec.Server);
                Assert.Equal(logrec.Identity, dbLogRec.Identity);
                Assert.Equal(logrec.CorrelationId, dbLogRec.CorrelationId);
                Assert.Equal(logrec.Message, dbLogRec.Message);
                Assert.Equal(logrec.ExceptionMessage, dbLogRec.ExceptionMessage);
                Assert.Equal(logrec.ExceptionType, dbLogRec.ExceptionType);
                Assert.Equal(logrec.ExceptionAdditionalInfo, dbLogRec.ExceptionAdditionalInfo);
                Assert.Equal((String)logrec.AdditionalFields["Host"], dbLogRec.Host);
                Assert.Equal((String)logrec.AdditionalFields["LoggedUser"], dbLogRec.LoggedUser);
                Assert.Equal((String)logrec.AdditionalFields["HttpStatusCode"], dbLogRec.HttpStatusCode);
                Assert.Equal((String)logrec.AdditionalFields["Url"], dbLogRec.Url);
                Assert.Equal((String)logrec.AdditionalFields["Referer"], dbLogRec.Referer);
                Assert.Equal((String)logrec.AdditionalFields["ClientIP"], dbLogRec.ClientIP);
                Assert.Equal((String)logrec.AdditionalFields["RequestData"], dbLogRec.RequestData);
                Assert.Equal((String)logrec.AdditionalFields["ResponseData"], dbLogRec.ResponseData);
                Assert.Equal((String)logrec.AdditionalFields["ServiceName"], dbLogRec.ServiceName);
                Assert.Equal((String)logrec.AdditionalFields["ServiceDisplayName"], dbLogRec.ServiceDisplayName);

                var dbPerfLogs = JsonConvert.DeserializeObject<IDictionary<String, float>>(dbLogRec.PerfData);
                Assert.True(dbPerfLogs.Count == 2);

                float r;
                Assert.True(dbPerfLogs.TryGetValue("CPU", out r));
                Assert.Equal(r, logrec.PerformanceData["CPU"]);

                Assert.True(dbPerfLogs.TryGetValue("Memory", out r));
                Assert.Equal(r, logrec.PerformanceData["Memory"]);

            }
        }

        [Fact]
        public async Task ApplicationStatusTest()
        {

            var utcnow = DateTime.UtcNow.Date;
            var mysqlLogStore = new MySqlLogStore(() => utcnow);
            const string appPath = "###rather_not_existing_application_path###";
            var hash = BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(appPath))).Replace("-", String.Empty);
            var startDate = DateTime.UtcNow.AddMinutes(-1);

            var expectedAppStatus = new LastApplicationStatus {
                ApplicationPath = appPath,
                Server = "SRV1",
                LastUpdateTimeUtc = DateTime.UtcNow
            };
            await mysqlLogStore.UpdateApplicationStatusAsync(expectedAppStatus);

            var actualAppStatus = (await mysqlLogStore.GetApplicationStatusesAsync(startDate)).FirstOrDefault(
                st => string.Equals(st.ApplicationPath, expectedAppStatus.ApplicationPath, StringComparison.Ordinal));
            Assert.NotNull(actualAppStatus);
            Assert.Equal(expectedAppStatus.ApplicationPath, actualAppStatus.ApplicationPath);
            Assert.Equal(expectedAppStatus.Cpu, 0);
            Assert.Equal(expectedAppStatus.Memory, 0);
            Assert.Null(actualAppStatus.LastErrorTimeUtc);
            Assert.Null(actualAppStatus.LastPerformanceDataUpdateTimeUtc);

            expectedAppStatus.LastErrorType = "TestException";
            expectedAppStatus.LastErrorTimeUtc = DateTime.UtcNow;
            await mysqlLogStore.UpdateApplicationStatusAsync(expectedAppStatus);

            actualAppStatus = (await mysqlLogStore.GetApplicationStatusesAsync(startDate)).FirstOrDefault(
                st => string.Equals(st.ApplicationPath, expectedAppStatus.ApplicationPath, StringComparison.Ordinal));
            Assert.NotNull(actualAppStatus);
            Assert.Equal(expectedAppStatus.ApplicationPath, actualAppStatus.ApplicationPath);
            Assert.Equal(expectedAppStatus.Cpu, 0);
            Assert.Equal(expectedAppStatus.Memory, 0);
            Assert.Equal(expectedAppStatus.LastErrorType, actualAppStatus.LastErrorType);
            Assert.NotNull(actualAppStatus.LastErrorTimeUtc);
            Assert.Null(actualAppStatus.LastPerformanceDataUpdateTimeUtc);

            expectedAppStatus.Cpu = 10f;
            expectedAppStatus.Memory = 1000f;
            expectedAppStatus.LastPerformanceDataUpdateTimeUtc = DateTime.UtcNow;
            await mysqlLogStore.UpdateApplicationStatusAsync(expectedAppStatus);

            actualAppStatus = (await mysqlLogStore.GetApplicationStatusesAsync(startDate)).FirstOrDefault(
                st => string.Equals(st.ApplicationPath, expectedAppStatus.ApplicationPath, StringComparison.Ordinal));
            Assert.NotNull(actualAppStatus);
            Assert.Equal(expectedAppStatus.ApplicationPath, actualAppStatus.ApplicationPath);
            Assert.Equal(expectedAppStatus.LastErrorType, actualAppStatus.LastErrorType);
            Assert.NotNull(actualAppStatus.LastErrorTimeUtc);
            Assert.Equal(expectedAppStatus.Cpu, 10f);
            Assert.Equal(expectedAppStatus.Memory, 1000f);
            Assert.NotNull(actualAppStatus.LastPerformanceDataUpdateTimeUtc);
        }

        [Fact]
        public async Task LogFilteringTest()
        {
            // add a test log record
            var utcnow = DateTime.UtcNow.Date;
            var mysqlLogStore = new MySqlLogStore(() => utcnow);
            const string appPath = "###rather_not_existing_application_path2###";

            var logrec = new LogRecord {
                LoggerName = "TestLogger",
                ApplicationPath = appPath,
                LogLevel = LogRecord.ELogLevel.Error,
                TimeUtc = DateTime.UtcNow,
                ProcessId = 123,
                ThreadId = 456,
                Server = "TestServer",
                Identity = "TestIdentity",
                CorrelationId = Guid.NewGuid().ToString(),
                Message = "Test log message to store in the log",
                ExceptionMessage = "Test exception log message",
                ExceptionType = "TestException",
                ExceptionAdditionalInfo = "Additinal info for the test exception",
                AdditionalFields = new Dictionary<String, Object>
                {
                    { "Host", "testhost.com" },
                    { "LoggedUser", "testloggeduser" },
                    { "HttpStatusCode", "200.1" },
                    { "Url", "http://testhost.com" },
                    { "Referer", "http://prevtesthost.com" },
                    { "ClientIP", null },
                    { "RequestData", "test test test" },
                    { "ResponseData", null },
                    { "ServiceName", "TestService" },
                    { "ServiceDisplayName", "Test service generating logs" },
                    { "NotExisting", null }
                },
                PerformanceData = new Dictionary<String, float>
                {
                    { "CPU", 2.0f },
                    { "Memory", 20000000f }
                }
            };

            // add log
            await mysqlLogStore.AddLogRecordAsync(logrec);

            var searchResults = await mysqlLogStore.FilterLogsAsync(new LogSearchCriteria {
                FromUtc = DateTime.UtcNow.AddMinutes(-10),
                ToUtc = DateTime.UtcNow.AddMinutes(10),
                ApplicationPath = appPath,
                Levels = new[] { LogRecord.ELogLevel.Error, LogRecord.ELogLevel.Info },
                Limit = 10,
                Offset = 0,
                Server = "TestServer",
                Keywords = new KeywordsParsed() { Url = "http://testhost.com" }
            });

            Assert.NotNull(searchResults.FoundItems);
            var foundItems = searchResults.FoundItems.ToArray();
            Assert.True(foundItems.Length == 1);
            var logrec2 = foundItems[0];
            Assert.Equal(logrec.LoggerName, logrec2.LoggerName);
            Assert.Equal(logrec.ApplicationPath, logrec2.ApplicationPath);
            Assert.Equal(logrec.LogLevel, logrec2.LogLevel);
            Assert.Equal(logrec.TimeUtc.ToShortDateString(), logrec2.TimeUtc.ToShortDateString());
            Assert.Equal(logrec.ProcessId, logrec2.ProcessId);
            Assert.Equal(logrec.ThreadId, logrec2.ThreadId);
            Assert.Equal(logrec.Server, logrec2.Server);
            Assert.Equal(logrec.Identity, logrec2.Identity);
            Assert.Equal(logrec.CorrelationId, logrec2.CorrelationId);
            Assert.Equal(logrec.Message, logrec2.Message);
            Assert.Equal(logrec.ExceptionMessage, logrec2.ExceptionMessage);
            Assert.Equal(logrec.ExceptionType, logrec2.ExceptionType);
            Assert.Equal(logrec.ExceptionAdditionalInfo, logrec2.ExceptionAdditionalInfo);
            Assert.Equal(logrec.AdditionalFields["Host"], logrec2.AdditionalFields["Host"]);
            Assert.Equal(logrec.AdditionalFields["LoggedUser"], logrec2.AdditionalFields["LoggedUser"]);
            Assert.Equal(logrec.AdditionalFields["HttpStatusCode"], logrec2.AdditionalFields["HttpStatusCode"]);
            Assert.Equal(logrec.AdditionalFields["Url"], logrec2.AdditionalFields["Url"]);
            Assert.Equal(logrec.AdditionalFields["Referer"], logrec2.AdditionalFields["Referer"]);
            Assert.Equal(logrec.AdditionalFields["RequestData"], logrec2.AdditionalFields["RequestData"]);
            Assert.Equal(logrec.AdditionalFields["ServiceName"], logrec2.AdditionalFields["ServiceName"]);
            Assert.Equal(logrec.AdditionalFields["ServiceDisplayName"], logrec2.AdditionalFields["ServiceDisplayName"]);
        }

        [Fact]
        public void PartitionTests()
        {
            var today = DateTime.UtcNow.Date;
            var todayPartition = Partition.ForDay(today);
            Assert.Equal(String.Format("{0}{1:yyyyMMdd}", Partition.PartitionPrefix, today.AddDays(1)), todayPartition.Name);
            Assert.Equal(todayPartition, Partition.ForDay(today));
            Assert.True(todayPartition.CompareTo(Partition.ForDay(today)) == 0);

            var tomorrowPartition = Partition.ForDay(today.AddDays(1));
            Assert.True(tomorrowPartition.CompareTo(todayPartition) > 0);

            var yesterdayPartition = Partition.ForDay(today.AddDays(-1));
            Assert.True(yesterdayPartition.CompareTo(todayPartition) < 0);
        }

        [Fact]
        public async Task MaintainPartitionsTest()
        {
            var utcnow = DateTime.UtcNow.Date;
            var mysqlLogStore = new MySqlLogStore(() => utcnow);
            const string appPath = "###rather_not_existing_application_path3###";
            var hash = BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(appPath))).Replace("-", String.Empty);


            using (var conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["mysqlconn"].ConnectionString))
            {
                conn.Open();

                // timespan = 2 days - one partition to remove, one to create
                var partitionDefs = new StringBuilder();
                partitionDefs.Append(GetPartitionDefinition(utcnow.AddDays(-4))).Append(',');
                partitionDefs.Append(GetPartitionDefinition(utcnow.AddDays(-3))).Append(',');
                partitionDefs.Append(GetPartitionDefinition(utcnow.AddDays(-2))).Append(',');
                partitionDefs.Append(GetPartitionDefinition(utcnow.AddDays(-1))).Append(',');
                partitionDefs.Append(GetPartitionDefinition(utcnow));

                conn.Execute(String.Format("create table {0} (Id int unsigned auto_increment not null, TimeUtc datetime not null, " +
                    "primary key (Id, TimeUtc)) partition by range columns (TimeUtc) ({1})", MySqlLogStore.AppLogTablePrefix + hash,
                    partitionDefs));

                await mysqlLogStore.MaintainAsync(TimeSpan.FromDays(0));

                // no partitions should be removed
                foreach (var tbl in new[] { MySqlLogStore.AppLogTablePrefix })
                {

                    var partitions = conn.Query<String>("select partition_name from information_schema.partitions where table_name = @Table and table_schema = @Database",
                        new { conn.Database, Table = tbl + hash });

                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-4)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-3)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-2)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-1)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(1)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                }

                await mysqlLogStore.MaintainAsync(TimeSpan.FromDays(3));

                foreach (var tbl in new[] { MySqlLogStore.AppLogTablePrefix })
                {

                    var partitions = conn.Query<String>("select partition_name from information_schema.partitions where table_name = @Table and table_schema = @Database",
                        new { conn.Database, Table = tbl + hash });

                    Assert.DoesNotContain(Partition.ForDay(utcnow.AddDays(-4)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-3)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-2)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(-1)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(1)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                }

                // drop current and future partition and check if it will be recreated
                conn.Execute(String.Format("alter table {0} drop partition {1}", MySqlLogStore.AppLogTablePrefix + hash, Partition.ForDay(utcnow.AddDays(1)).Name));
                conn.Execute(String.Format("alter table {0} drop partition {1}", MySqlLogStore.AppLogTablePrefix + hash, Partition.ForDay(utcnow).Name));
                await mysqlLogStore.MaintainAsync(TimeSpan.FromDays(3));
                foreach (var tbl in new[] { MySqlLogStore.AppLogTablePrefix })
                {

                    var partitions = conn.Query<String>("select partition_name from information_schema.partitions where table_name = @Table and table_schema = @Database",
                        new { conn.Database, Table = tbl + hash });

                    Assert.Contains(Partition.ForDay(utcnow).Name, partitions, StringComparer.OrdinalIgnoreCase);
                    Assert.Contains(Partition.ForDay(utcnow.AddDays(1)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                }

                // check if configuration per table is working
                mysqlLogStore.MaintainAsync(TimeSpan.FromDays(3), new Dictionary<String, TimeSpan> { { appPath, TimeSpan.FromDays(2) } }).Wait();
                foreach (var tbl in new[] { MySqlLogStore.AppLogTablePrefix })
                {

                    var partitions = conn.Query<String>("select partition_name from information_schema.partitions where table_name = @Table and table_schema = @Database",
                        new { conn.Database, Table = tbl + hash });

                    Assert.DoesNotContain(Partition.ForDay(utcnow.AddDays(-3)).Name, partitions, StringComparer.OrdinalIgnoreCase);
                }

                await Assert.ThrowsAsync<ArgumentException>(async () => await mysqlLogStore.MaintainAsync(TimeSpan.FromDays(-2)));
                await Assert.ThrowsAsync<ArgumentException>(async () => await mysqlLogStore.MaintainAsync(TimeSpan.FromDays(2),
                    new Dictionary<String, TimeSpan> { { appPath, TimeSpan.FromDays(-1) } }));
            }
        }

        private static String GetPartitionDefinition(DateTime dt)
        {
            return String.Format("PARTITION {0} VALUES LESS THAN ('{1:yyyy-MM-dd HH:mm}')", Partition.ForDay(dt).Name, dt.Date.AddDays(1));
        }

        public void Dispose()
        {
        }
    }
}
