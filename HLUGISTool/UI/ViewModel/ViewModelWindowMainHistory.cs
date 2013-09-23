﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using HLU.Data.Model;

namespace HLU.UI.ViewModel
{
    class ViewModelWindowMainHistory
    {
        ViewModelWindowMain _viewModelMain;

        public ViewModelWindowMainHistory(ViewModelWindowMain viewModelMain)
        {
            _viewModelMain = viewModelMain;
        }

        /// <summary>
        /// Writes history records to DB.
        /// </summary>
        /// <param name="fixedValues">Dictionary of insert values that will be equal for all new rows.</param>
        /// <param name="newHistoryRecords">DataTable containing the new history rows to be inserted.</param>
        /// <param name="operation">One of the members of the Operations enum describing the operation 
        /// that created the new history records.</param>
        internal void HistoryWrite(Dictionary<int, string> fixedValues,
            DataTable newHistoryRecords, ViewModelWindowMain.Operations operation)
        {
            if ((newHistoryRecords == null) || (newHistoryRecords.Rows.Count == 0)) return;

            bool startTransaction = _viewModelMain.DataBase.Transaction == null;

            if (startTransaction) _viewModelMain.DataBase.BeginTransaction(true, IsolationLevel.ReadCommitted);

            try
            {
                // create dictionary of insert values that will be equal for all new rows
                Dictionary<int, object> fixedValueDict = new Dictionary<int, object>();
                if (fixedValues != null)
                {
                    foreach (KeyValuePair<int, string> kv in fixedValues)
                        fixedValueDict.Add(kv.Key, kv.Value);
                }
                if (!fixedValueDict.ContainsKey(_viewModelMain.HluDataset.history.modified_user_idColumn.Ordinal))
                    fixedValueDict.Add(_viewModelMain.HluDataset.history.modified_user_idColumn.Ordinal, _viewModelMain.UserID);
                if (!fixedValueDict.ContainsKey(_viewModelMain.HluDataset.history.modified_dateColumn.Ordinal))
                    fixedValueDict.Add(_viewModelMain.HluDataset.history.modified_dateColumn.Ordinal, DateTime.Today);
                if (!fixedValueDict.ContainsKey(_viewModelMain.HluDataset.history.modified_reasonColumn.Ordinal))
                    fixedValueDict.Add(_viewModelMain.HluDataset.history.modified_reasonColumn.Ordinal, _viewModelMain.Reason);
                if (!fixedValueDict.ContainsKey(_viewModelMain.HluDataset.history.modified_processColumn.Ordinal))
                    fixedValueDict.Add(_viewModelMain.HluDataset.history.modified_processColumn.Ordinal, _viewModelMain.Process);
                if (!fixedValueDict.ContainsKey(_viewModelMain.HluDataset.history.modified_operationColumn.Ordinal))
                {
                    string operationCode = ViewModelWindowMainHelpers.GetOperationsCode(_viewModelMain.HluDataset, operation);
                    if (!String.IsNullOrEmpty(operationCode))
                        fixedValueDict.Add(_viewModelMain.HluDataset.history.modified_operationColumn.Ordinal,
                            operationCode);
                    else
                        throw new Exception(String.Format("Missing or indeterminate operation code for {0}.",
                            operation.ToString()));
                }

                // rename the generically named geom1 and geom2 fields according to layer type
                HistoryRenameGeometryPropertyColumns(_viewModelMain.HluDataset.history.modified_lengthColumn.ColumnName,
                    _viewModelMain.HluDataset.history.modified_areaColumn.ColumnName, ref newHistoryRecords);

                // get an array of column ordinals in the new history table as they match columns 
                // in DB history table and are not among the fixed values (fixedValueDic)
                // a new history column named "<columnName>" will match both DB history columns named 
                // "<columnName>" *and* "modified_<columnName>" as long as there is no column named 
                // "modified_<columnName>" in the new history table
                int[] newHistoryColumns = (from t in _viewModelMain.HluDataset.history.Columns.Cast<DataColumn>()
                                           let gisCols = (from g in newHistoryRecords.Columns.Cast<DataColumn>()
                                                          select g.ColumnName).ToArray()
                                           let gisColsMod = (from g in newHistoryRecords.Columns.Cast<DataColumn>()
                                                             select !gisCols.Contains("modified_" + g.ColumnName) ?
                                                             "modified_" + g.ColumnName : String.Empty).ToArray()
                                           select new
                                           {
                                               gisPos = !fixedValueDict.ContainsKey(t.Ordinal) ?
                                                        Array.IndexOf(gisCols, t.ColumnName) : -1,
                                               gisModPos = !fixedValueDict.ContainsKey(t.Ordinal) ?
                                                        Array.IndexOf(gisColsMod, t.ColumnName) : -1
                                           })
                                           .Select(a => a.gisPos != -1 ? a.gisPos : a.gisModPos).ToArray();

                // get the next available history_id
                object objHistId = _viewModelMain.DataBase.ExecuteScalar(String.Format("SELECT MAX({0}) + 1 FROM {1}",
                    _viewModelMain.DataBase.QuoteIdentifier(_viewModelMain.HluDataset.history.history_idColumn.ColumnName),
                    _viewModelMain.DataBase.QualifyTableName(_viewModelMain.HluDataset.history.TableName)),
                    _viewModelMain.DataBase.Connection.ConnectionTimeout, CommandType.Text);
                int historyId = objHistId != DBNull.Value && objHistId != null ? (int)objHistId : 1;

                // append new history rows to a history table
                HluDataSet.historyDataTable historyTable = new HluDataSet.historyDataTable();
                foreach (DataRow r in newHistoryRecords.Rows)
                {
                    HluDataSet.historyRow newRow = historyTable.NewhistoryRow();
                    for (int i = 0; i < historyTable.Columns.Count; i++)
                    {
                        if (i == _viewModelMain.HluDataset.history.history_idColumn.Ordinal)
                        {
                            newRow[i] = historyId++;
                        }
                        else if (newHistoryColumns[i] != -1)
                        {
                            if (!r.IsNull(newHistoryColumns[i]))
                                newRow[i] = r[newHistoryColumns[i]];
                        }
                        else
                        {
                            object val;
                            if (fixedValueDict.TryGetValue(i, out val))
                                newRow[i] = val;
                        }
                    }
                    historyTable.AddhistoryRow(newRow);
                }

                // insert new rows
                if (_viewModelMain.HluTableAdapterManager.historyTableAdapter.Update(historyTable) == -1)
                    throw new Exception("Failed to update history table.");

                if (startTransaction) _viewModelMain.DataBase.CommitTransaction();
            }
            catch
            {
                if (startTransaction) _viewModelMain.DataBase.RollbackTransaction();
                throw;
            }
        }

        /// <summary>
        /// Renames the two geometry property columns returned from GIS to match the geometry type of the GIS layer.
        /// </summary>
        /// <param name="newGeom1ColumnName">Name of geometry property column 1.</param>
        /// <param name="newGeom2ColumnName">Name of geometry property column 2.</param>
        /// <param name="table">DataTable holding the new history records returned from GIS.</param>
        internal void HistoryRenameGeometryPropertyColumns(string newGeom1ColumnName,
            string newGeom2ColumnName, ref DataTable table)
        {
            if (table == null) return;
            switch (_viewModelMain.GisLayerType)
            {
                case ViewModelWindowMain.GeometryTypes.Point:
                    break;
                case ViewModelWindowMain.GeometryTypes.Line:
                    if (table.Columns.Contains(ViewModelWindowMain.HistoryGeometry1ColumnName))
                        table.Columns[ViewModelWindowMain.HistoryGeometry1ColumnName].ColumnName = newGeom1ColumnName;
                    break;
                case ViewModelWindowMain.GeometryTypes.Polygon:
                    if (table.Columns.Contains(ViewModelWindowMain.HistoryGeometry1ColumnName))
                        table.Columns[ViewModelWindowMain.HistoryGeometry1ColumnName].ColumnName = newGeom1ColumnName;
                    if (table.Columns.Contains(ViewModelWindowMain.HistoryGeometry2ColumnName))
                        table.Columns[ViewModelWindowMain.HistoryGeometry2ColumnName].ColumnName = newGeom2ColumnName;
                    break;
            }
        }
    }
}