﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace System.Data.SqlClient {
	partial class Executer {

		class Transaction2 {
			internal Connection2 Conn;
			internal SqlTransaction Transaction;
			internal DateTime RunTime;
			internal TimeSpan Timeout;

			public Transaction2(Connection2 conn, SqlTransaction tran, TimeSpan timeout) {
				Conn = conn;
				Transaction = tran;
				RunTime = DateTime.Now;
				Timeout = timeout;
			}
		}

		private Dictionary<int, Transaction2> _trans = new Dictionary<int, Transaction2>();
		private object _trans_lock = new object();

		public SqlTransaction CurrentThreadTransaction => _trans.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var conn) && conn.Transaction?.Connection != null ? conn.Transaction : null;

		private Dictionary<int, List<string>> _preRemoveKeys = new Dictionary<int, List<string>>();
		private object _preRemoveKeys_lock = new object();
		public string[] PreRemove(params string[] key) {
			var tid = Thread.CurrentThread.ManagedThreadId;
			List<string> keys = null;
			if (key == null || key.Any() == false) return _preRemoveKeys.TryGetValue(tid, out keys) ? keys.ToArray() : new string[0];
			Log.LogDebug($"线程{tid}事务预删除Redis {JsonConvert.SerializeObject(key)}");
			if (_preRemoveKeys.TryGetValue(tid, out keys) == false)
				lock (_preRemoveKeys_lock)
					if (_preRemoveKeys.TryGetValue(tid, out keys) == false) {
						_preRemoveKeys.Add(tid, keys = new List<string>(key));
						return key;
					}
			keys.AddRange(key);
			return keys.ToArray();
		}

		/// <summary>
		/// 启动事务
		/// </summary>
		public void BeginTransaction(TimeSpan timeout) {
			int tid = Thread.CurrentThread.ManagedThreadId;
			var conn = MasterPool.GetConnection();
			Transaction2 tran = null;

			try {
				if (conn.SqlConnection.State == ConnectionState.Closed) conn.SqlConnection.Open();
				tran = new Transaction2(conn, conn.SqlConnection.BeginTransaction(), timeout);
			} catch (Exception ex) {
				Log.LogError($"数据库出错（开启事务）{ex.Message} \r\n{ex.StackTrace}");
				throw ex;
			}
			if (_trans.ContainsKey(tid)) CommitTransaction();

			lock (_trans_lock)
				_trans.Add(tid, tran);
		}

		/// <summary>
		/// 自动提交事务
		/// </summary>
		private void AutoCommitTransaction() {
			if (_trans.Count > 0) {
				Transaction2[] trans = null;
				lock (_trans_lock)
					trans = _trans.Values.Where(st2 => DateTime.Now.Subtract(st2.RunTime) > st2.Timeout).ToArray();
				foreach (Transaction2 tran in trans) CommitTransaction(true, tran);
			}
		}
		private void CommitTransaction(bool isCommit, Transaction2 tran) {
			if (tran == null || tran.Transaction == null || tran.Transaction.Connection == null) return;

			if (_trans.ContainsKey(tran.Conn.ThreadId))
				lock (_trans_lock)
					if (_trans.ContainsKey(tran.Conn.ThreadId))
						_trans.Remove(tran.Conn.ThreadId);

			var removeKeys = PreRemove();
			if (_preRemoveKeys.ContainsKey(tran.Conn.ThreadId))
				lock (_preRemoveKeys_lock)
					if (_preRemoveKeys.ContainsKey(tran.Conn.ThreadId))
						_preRemoveKeys.Remove(tran.Conn.ThreadId);

			var f001 = isCommit ? "提交" : "回滚";
			try {
				Log.LogDebug($"线程{tran.Conn.ThreadId}事务{f001}，批量删除Redis {Newtonsoft.Json.JsonConvert.SerializeObject(removeKeys)}");
				CacheRemove(removeKeys);
				if (isCommit) tran.Transaction.Commit();
				else tran.Transaction.Rollback();
			} catch (Exception ex) {
				Log.LogError($"数据库出错（{f001}事务）：{ex.Message} {ex.StackTrace}");
			} finally {
				MasterPool.ReleaseConnection(tran.Conn);
			}
		}
		private void CommitTransaction(bool isCommit) {
			if (_trans.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var tran)) CommitTransaction(isCommit, tran);
		}
		/// <summary>
		/// 提交事务
		/// </summary>
		public void CommitTransaction() => CommitTransaction(true);
		/// <summary>
		/// 回滚事务
		/// </summary>
		public void RollbackTransaction() => CommitTransaction(false);

		public void Dispose() {
			Transaction2[] trans = null;
			lock (_trans_lock)
				trans = _trans.Values.ToArray();
			foreach (Transaction2 tran in trans) CommitTransaction(false, tran);
		}

		/// <summary>
		/// 开启事务（不支持异步），60秒未执行完将自动提交
		/// </summary>
		/// <param name="handler">事务体 () => {}</param>
		public void Transaction(Action handler) {
			Transaction(handler, TimeSpan.FromSeconds(60));
		}
		/// <summary>
		/// 开启事务（不支持异步）
		/// </summary>
		/// <param name="handler">事务体 () => {}</param>
		/// <param name="timeout">超时，未执行完将自动提交</param>
		public void Transaction(Action handler, TimeSpan timeout) {
			try {
				BeginTransaction(timeout);
				handler();
				CommitTransaction();
			} catch (Exception ex) {
				RollbackTransaction();
				throw ex;
			}
		}
	}
}
