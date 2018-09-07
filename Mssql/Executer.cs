﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;

namespace System.Data.SqlClient {
	public partial class Executer : IDisposable {

		public bool IsTracePerformance { get; set; } = string.Compare(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", true) == 0;
		public ILogger Log { get; set; }
		public ConnectionPool MasterPool { get; } = new ConnectionPool();
		public List<ConnectionPool> SlavePools { get; } = new List<ConnectionPool>();
		private Random slaveRandom = new Random();

		void LoggerException(ConnectionPool pool, SqlCommand cmd, Exception e, DateTime dt, string logtxt) {
			var logPool = this.SlavePools.Count == 0 ? "" : (pool == this.MasterPool ? "【主库】" : $"【从库{this.SlavePools.IndexOf(pool)}】");
			if (IsTracePerformance) {
				TimeSpan ts = DateTime.Now.Subtract(dt);
				if (e == null && ts.TotalMilliseconds > 100)
					Log.LogWarning($"{logPool}执行SQL语句耗时过长{ts.TotalMilliseconds}ms\r\n{cmd.CommandText}\r\n{logtxt}");
			}

			if (e == null) return;
			string log = $"{logPool}数据库出错（执行SQL）〓〓〓〓〓〓〓〓〓〓〓〓〓〓〓\r\n{cmd.CommandText}\r\n";
			foreach (SqlParameter parm in cmd.Parameters)
				log += parm.ParameterName.PadRight(20, ' ') + " = " + (parm.Value ?? "NULL") + "\r\n";

			log += e.Message;
			Log.LogError(log);

			RollbackTransaction();
			cmd.Parameters.Clear();
			throw e;
		}

		/// <summary>
		/// 若使用【读写分离】，查询【从库】条件cmdText.StartsWith("SELECT ")，否则查询【主库】
		/// </summary>
		/// <param name="readerHander"></param>
		/// <param name="cmdType"></param>
		/// <param name="cmdText"></param>
		/// <param name="cmdParms"></param>
		public void ExecuteReader(Action<SqlDataReader> readerHander, CommandType cmdType, string cmdText, params SqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			SqlCommand cmd = new SqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			ConnectionPool pool = this.MasterPool;
			//读写分离规则，暂时定为：所有查询的同步方法会读主库，所有查询的异步方法会读从库
			//if (this.SlavePools.Count > 0 && this.CurrentThreadTransaction == null) pool = this.SlavePools.Count == 1 ? this.SlavePools[0] : this.SlavePools[slaveRandom.Next(this.SlavePools.Count)];
			if (this.SlavePools.Count > 0 && cmdText.StartsWith("SELECT ", StringComparison.CurrentCultureIgnoreCase)) pool = this.SlavePools.Count == 1 ? this.SlavePools[0] : this.SlavePools[slaveRandom.Next(this.SlavePools.Count)];

			var pc = PrepareCommand(pool, cmd, cmdType, cmdText, cmdParms, ref logtxt);
			if (IsTracePerformance) logtxt += $"PrepareCommand: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			Exception ex = null;
			try {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				if (cmd.Connection.State == ConnectionState.Closed) cmd.Connection.Open();
				if (IsTracePerformance) {
					logtxt += $"Open: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
					logtxt_dt = DateTime.Now;
				}
				using (SqlDataReader dr = cmd.ExecuteReader()) {
					if (IsTracePerformance) logtxt += $"ExecuteReader: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
					while (true) {
						if (IsTracePerformance) logtxt_dt = DateTime.Now;
						bool isread = dr.Read();
						if (IsTracePerformance) logtxt += $"	dr.Read: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
						if (isread == false) break;

						if (readerHander != null) {
							object[] values = null;
							if (IsTracePerformance) {
								logtxt_dt = DateTime.Now;
								values = new object[dr.FieldCount];
								dr.GetValues(values);
								logtxt += $"	dr.GetValues: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
								logtxt_dt = DateTime.Now;
							}
							readerHander(dr);
							if (IsTracePerformance) logtxt += $"	readerHander: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms ({string.Join(",", values)})\r\n";
						}
					}
					if (IsTracePerformance) logtxt_dt = DateTime.Now;
					dr.Close();
				}
				if (IsTracePerformance) logtxt += $"ExecuteReader_dispose: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				pool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(pool, cmd, ex, dt, logtxt);
		}
		/// <summary>
		/// 若使用【读写分离】，查询【从库】条件cmdText.StartsWith("SELECT ")，否则查询【主库】
		/// </summary>
		/// <param name="cmdType"></param>
		/// <param name="cmdText"></param>
		/// <param name="cmdParms"></param>
		/// <returns></returns>
		public object[][] ExecuteArray(CommandType cmdType, string cmdText, params SqlParameter[] cmdParms) {
			List<object[]> ret = new List<object[]>();
			ExecuteReader(dr => {
				object[] values = new object[dr.FieldCount];
				dr.GetValues(values);
				ret.Add(values);
			}, cmdType, cmdText, cmdParms);
			return ret.ToArray();
		}
		/// <summary>
		/// 在【主库】执行
		/// </summary>
		/// <param name="cmdType"></param>
		/// <param name="cmdText"></param>
		/// <param name="cmdParms"></param>
		/// <returns></returns>
		public int ExecuteNonQuery(CommandType cmdType, string cmdText, params SqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			SqlCommand cmd = new SqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			var pc = PrepareCommand(this.MasterPool, cmd, cmdType, cmdText, cmdParms, ref logtxt);
			int val = 0;
			Exception ex = null;
			try {
				if (cmd.Connection.State == ConnectionState.Closed) cmd.Connection.Open();
				val = cmd.ExecuteNonQuery();
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				this.MasterPool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(this.MasterPool, cmd, ex, dt, logtxt);
			cmd.Parameters.Clear();
			return val;
		}
		/// <summary>
		/// 在【主库】执行
		/// </summary>
		/// <param name="cmdType"></param>
		/// <param name="cmdText"></param>
		/// <param name="cmdParms"></param>
		/// <returns></returns>
		public object ExecuteScalar(CommandType cmdType, string cmdText, params SqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			SqlCommand cmd = new SqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			var pc = PrepareCommand(this.MasterPool, cmd, cmdType, cmdText, cmdParms, ref logtxt);
			object val = null;
			Exception ex = null;
			try {
				if (cmd.Connection.State == ConnectionState.Closed) cmd.Connection.Open();
				val = cmd.ExecuteScalar();
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				this.MasterPool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(this.MasterPool, cmd, ex, dt, logtxt);
			cmd.Parameters.Clear();
			return val;
		}

		private PrepareCommandReturnInfo PrepareCommand(ConnectionPool pool, SqlCommand cmd, CommandType cmdType, string cmdText, SqlParameter[] cmdParms, ref string logtxt) {
			DateTime dt = DateTime.Now;
			cmd.CommandType = cmdType;
			cmd.CommandText = cmdText;

			if (cmdParms != null) {
				foreach (SqlParameter parm in cmdParms) {
					if (parm == null) continue;
					if (parm.Value == null) parm.Value = DBNull.Value;
					cmd.Parameters.Add(parm);
				}
			}

			Connection2 conn = null;
			SqlTransaction tran = CurrentThreadTransaction;
			if (IsTracePerformance) logtxt += $"	PrepareCommand_part1: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms cmdParms: {cmdParms.Length}\r\n";

			if (tran == null) {
				if (IsTracePerformance) dt = DateTime.Now;
				conn = pool.GetConnection();
				cmd.Connection = conn.SqlConnection;
				if (IsTracePerformance) logtxt += $"	PrepareCommand_tran==null: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			} else {
				if (IsTracePerformance) dt = DateTime.Now;
				cmd.Connection = tran.Connection;
				cmd.Transaction = tran;
				if (IsTracePerformance) logtxt += $"	PrepareCommand_tran!=null: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			}

			if (IsTracePerformance) dt = DateTime.Now;
			AutoCommitTransaction();
			if (IsTracePerformance) logtxt += $"	AutoCommitTransaction: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";

			return new PrepareCommandReturnInfo { Conn = conn, Tran = tran };
		}

		class PrepareCommandReturnInfo {
			public Connection2 Conn;
			public SqlTransaction Tran;
		}
	}
}
