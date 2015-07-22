﻿using Dapper;
using LowLevelDesign.Diagnostics.Commons.Models;
using LowLevelDesign.Diagnostics.Commons;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LowLevelDesign.Diagnostics.LogStore.MySql
{
    internal class LogTable
    {
        private static readonly Object lck = new Object();
        private static readonly ISet<String> availableTables = new HashSet<String>();

        private readonly Func<DateTime> currentUtcDateRetriever;

        static LogTable()
        {
            // query the mysql database and retrieve all the log tables
            using (var conn = new MySqlConnection(MySqlLogStoreConfiguration.ConnectionString)) {
                conn.Open();
                availableTables = new HashSet<String>(conn.Query<String>("select table_name from information_schema.tables where table_schema = @Database", conn),
                                                        StringComparer.OrdinalIgnoreCase);

                if (!availableTables.Contains("appstat")) {
                    // we need to create a table which will store application statuses for the grid
                    conn.Execute("create table if not exists appstat (ApplicationHash char(32), Server varchar(200) not null, ApplicationPath varchar(2000) not null, Cpu float, Memory float null, " +
                        "LastErrorType varchar(100), LastErrorTimeUtc datetime, LastUpdateTimeUtc datetime null, primary key (ApplicationHash, Server))");
                }
            }
        }

        public static bool IsLogTableAvailable(String tableName)
        {
            return availableTables.Contains(tableName);
        }

        public LogTable(Func<DateTime> currentUtcDateRetriever)
        {
            this.currentUtcDateRetriever = currentUtcDateRetriever;
        }

        public async Task<UInt32> HandleAppLog(MySqlConnection conn, MySqlTransaction tran, String tableName, LogRecord logrec)
        {
            if (!availableTables.Contains(tableName)) {
                lock (lck) {
                    if (!availableTables.Contains(tableName)) {
                        var currentUtcDate = currentUtcDateRetriever().Date;
                        conn.Execute("create table if not exists " + tableName + " (Id int unsigned auto_increment not null,LoggerName varchar(200) not null" +
                            ",LogLevel smallint not null ,TimeUtc datetime not null ,Message varchar(7000) null ,ExceptionType varchar(100) null" +
                            ",ExceptionMessage varchar(2000) null ,ExceptionAdditionalInfo text null ,CorrelationId varchar(100) null" +
                            ",Server varchar(200) null ,ApplicationPath varchar(2000) null ,ProcessId int null ,ThreadId int null" +
                            ",Identity varchar(200) null ,Host varchar(100) null ,LoggedUser varchar(200) null ,HttpStatusCode varchar(15) character set ascii null" +
                            ",Url varchar(2000) null ,Referer varchar(2000) null ,ClientIP varchar(50) character set ascii null ,RequestData text null" +
                            ",ResponseData text null,ServiceName varchar(100) null ,ServiceDisplayName varchar(200) null, PerfData varchar(3000) null" +
                            ",PRIMARY KEY (TimeUtc, Server, Id), KEY(Id)) COLLATE='utf8_general_ci' PARTITION BY RANGE COLUMNS(TimeUtc)" + 
                            String.Format("({0},{1})",
                            GetPartitionDefinition(currentUtcDate), GetPartitionDefinition(currentUtcDate.AddDays(1))),
                            transaction: tran);
                        availableTables.Add(tableName);
                    }
                }
            }
            // we need to make sure that the additional fields collection is initialized
            if (logrec.AdditionalFields == null) {
                logrec.AdditionalFields = new Dictionary<String, Object>();
            }

            // save log in the table
            Object v;
            return (UInt32)(await conn.QueryAsync<UInt64>("insert into " + tableName + "(LoggerName ,LogLevel ,TimeUtc ,Message ,ExceptionType " +
                    ",ExceptionMessage ,ExceptionAdditionalInfo ,CorrelationId ,Server ,ApplicationPath ,ProcessId ,ThreadId ,Identity ,Host " +
                    ",LoggedUser ,HttpStatusCode ,Url ,Referer ,ClientIP ,RequestData ,ResponseData ,ServiceName ,ServiceDisplayName, PerfData) values (" +
                    "@LoggerName ,@LogLevel ,@TimeUtc ,@Message ,@ExceptionType ,@ExceptionMessage ,@ExceptionAdditionalInfo ,@CorrelationId " +
                    ",@Server ,@ApplicationPath ,@ProcessId ,@ThreadId ,@Identity ,@Host ,@LoggedUser ,@HttpStatusCode ,@Url ,@Referer ,@ClientIP " +
                    ",@RequestData ,@ResponseData ,@ServiceName ,@ServiceDisplayName, @PerfData); select LAST_INSERT_ID()", new DbAppLogRecord {
                        LoggerName = logrec.LoggerName,
                        LogLevel = logrec.LogLevel,
                        TimeUtc = logrec.TimeUtc,
                        Message = logrec.Message,
                        ExceptionType = logrec.ExceptionType,
                        ExceptionMessage = logrec.ExceptionMessage,
                        ExceptionAdditionalInfo = logrec.ExceptionAdditionalInfo,
                        CorrelationId = logrec.CorrelationId,
                        Server = logrec.Server,
                        ApplicationPath = logrec.ApplicationPath,
                        ProcessId = logrec.ProcessId,
                        ThreadId = logrec.ThreadId,
                        Identity = logrec.Identity,
                        Host = logrec.AdditionalFields.TryGetValue("Host", out v) ? ((String)v).ShortenIfNecessary(100) : null,
                        LoggedUser = logrec.AdditionalFields.TryGetValue("LoggedUser", out v) ? ((String)v).ShortenIfNecessary(200) : null,
                        HttpStatusCode = logrec.AdditionalFields.TryGetValue("HttpStatusCode", out v) ? ((String)v).ShortenIfNecessary(15) : null,
                        Url = logrec.AdditionalFields.TryGetValue("Url", out v) ? ((String)v).ShortenIfNecessary(2000) : null,
                        Referer = logrec.AdditionalFields.TryGetValue("Referer", out v) ? ((String)v).ShortenIfNecessary(2000) : null,
                        ClientIP = logrec.AdditionalFields.TryGetValue("ClientIP", out v) ? ((String)v).ShortenIfNecessary(50) : null,
                        RequestData = logrec.AdditionalFields.TryGetValue("RequestData", out v) ? ((String)v).ShortenIfNecessary(2000) : null,
                        ResponseData = logrec.AdditionalFields.TryGetValue("ResponseData", out v) ? ((String)v).ShortenIfNecessary(2000) : null,
                        ServiceName = logrec.AdditionalFields.TryGetValue("ServiceName", out v) ? ((String)v).ShortenIfNecessary(100) : null,
                        ServiceDisplayName = logrec.AdditionalFields.TryGetValue("ServiceDisplayName", out v) ? ((String)v).ShortenIfNecessary(200) : null,
                        PerfData = logrec.PerformanceData != null && logrec.PerformanceData.Count > 0 ? JsonConvert.SerializeObject(
                            logrec.PerformanceData).ShortenIfNecessary(3000) : null
                    }, tran)).Single();
        }

        public async Task UpdateApplicationStatus(MySqlConnection conn, MySqlTransaction tran, String apphash, LastApplicationStatus status)
        {
            var sql = new StringBuilder("update appstat set ApplicationPath = ApplicationPath");
            if (status.LastUpdateTimeUtc.HasValue) {
                sql.Append(",LastUpdateTimeUtc = @LastUpdateTimeUtc, Cpu = @Cpu, Memory = @Memory");
            }
            if (status.LastErrorTimeUtc.HasValue) {
                sql.Append(",LastErrorTimeUtc = @LastErrorTimeUtc, LastErrorType = @LastErrorType");
            }
            sql.Append(" where ApplicationHash = @ApplicationHash and Server = @Server");

            var model = new {
                ApplicationHash = apphash,
                status.ApplicationPath,
                status.Server,
                status.Cpu,
                status.Memory,
                status.LastUpdateTimeUtc,
                status.LastErrorType,
                status.LastErrorTimeUtc
            };
            if (await conn.ExecuteAsync(sql.ToString(), model, tran) == 0) {
                // we need to insert a record
                await conn.ExecuteAsync("insert ignore into appstat (ApplicationHash, ApplicationPath, Server, Cpu, Memory, LastUpdateTimeUtc, LastErrorType, LastErrorTimeUtc)" +
                    " values (@ApplicationHash, @ApplicationPath, @Server, @Cpu, @Memory, @LastUpdateTimeUtc, @LastErrorType, @LastErrorTimeUtc)", model, tran);
            }
        }

        private static String GetPartitionDefinition(DateTime dt)
        {
            return String.Format("PARTITION {0} VALUES LESS THAN ('{1:yyyy-MM-dd HH:mm}')", Partition.ForDay(dt).Name, dt.Date.AddDays(1));
        }


        public async Task ManageTablePartitions(MySqlConnection conn, String tableName, TimeSpan keepTime, IEnumerable<Partition> partitions)
        {
            DateTime today = currentUtcDateRetriever(), tomorrow = today.AddDays(1);
            // if zero timespan is passed it means that no partition should be removed
            var oldestPartition = Partition.ForDay(keepTime == TimeSpan.Zero ? DateTime.MinValue : today.Subtract(keepTime));
            var currentPartition = Partition.ForDay(today);
            var futurePartition = Partition.ForDay(tomorrow);

            bool isCurrentPartitionCreated = false, isFuturePartitionCreated = false;
            var partitionsToDrop = new List<String>();
            foreach (var partition in partitions) {
                if (oldestPartition.CompareTo(partition) > 0) {
                    partitionsToDrop.Add(partition.Name);
                } else if (currentPartition.Equals(partition)) {
                    isCurrentPartitionCreated = true;
                } else if (futurePartition.Equals(partition)) {
                    isFuturePartitionCreated = true;
                }
            }

            if (!isCurrentPartitionCreated) {
                await conn.ExecuteAsync(String.Format("alter table {0} add partition (partition {1} values less than ('{2:yyyy-MM-dd HH:mm}'))",
                    tableName, currentPartition.Name, today.AddDays(1)));
            }
            if (!isFuturePartitionCreated) {
                await conn.ExecuteAsync(String.Format("alter table {0} add partition (partition {1} values less than ('{2:yyyy-MM-dd HH:mm}'))",
                    tableName, futurePartition.Name, today.AddDays(2)));
            }
            // remove older partitions
            foreach (var p in partitionsToDrop) {
                await conn.ExecuteAsync(String.Format("alter table {0} drop partition {1}", tableName, p));
            }
        }
    }
}
