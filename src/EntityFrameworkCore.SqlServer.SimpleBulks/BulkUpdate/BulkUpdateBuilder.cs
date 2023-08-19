﻿using EntityFrameworkCore.SqlServer.SimpleBulks.Extensions;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace EntityFrameworkCore.SqlServer.SimpleBulks.BulkUpdate
{
    public class BulkUpdateBuilder<T>
    {
        private IEnumerable<T> _data;
        private string _tableName;
        private IEnumerable<string> _idColumns;
        private IEnumerable<string> _columnNames;
        private IDictionary<string, string> _dbColumnMappings;
        private BulkUpdateOptions _options;
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;

        public BulkUpdateBuilder(SqlConnection connection)
        {
            _connection = connection;
        }

        public BulkUpdateBuilder(SqlTransaction transaction)
        {
            _transaction = transaction;
            _connection = transaction.Connection;
        }

        public BulkUpdateBuilder(SqlConnection connection, SqlTransaction transaction = null)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public BulkUpdateBuilder<T> WithData(IEnumerable<T> data)
        {
            _data = data;
            return this;
        }

        public BulkUpdateBuilder<T> ToTable(string tableName)
        {
            _tableName = tableName;
            return this;
        }

        public BulkUpdateBuilder<T> WithId(string idColumn)
        {
            _idColumns = new[] { idColumn };
            return this;
        }

        public BulkUpdateBuilder<T> WithId(IEnumerable<string> idColumns)
        {
            _idColumns = idColumns;
            return this;
        }

        public BulkUpdateBuilder<T> WithId(Expression<Func<T, object>> idSelector)
        {
            var idColumn = idSelector.Body.GetMemberName();
            _idColumns = string.IsNullOrEmpty(idColumn) ? idSelector.Body.GetMemberNames() : new List<string> { idColumn };
            return this;
        }

        public BulkUpdateBuilder<T> WithColumns(IEnumerable<string> columnNames)
        {
            _columnNames = columnNames;
            return this;
        }

        public BulkUpdateBuilder<T> WithColumns(Expression<Func<T, object>> columnNamesSelector)
        {
            _columnNames = columnNamesSelector.Body.GetMemberNames().ToArray();
            return this;
        }

        public BulkUpdateBuilder<T> WithDbColumnMappings(IDictionary<string, string> dbColumnMappings)
        {
            _dbColumnMappings = dbColumnMappings;
            return this;
        }

        public BulkUpdateBuilder<T> ConfigureBulkOptions(Action<BulkUpdateOptions> configureOptions)
        {
            _options = new BulkUpdateOptions();
            if (configureOptions != null)
            {
                configureOptions(_options);
            }
            return this;
        }

        private string GetDbColumnName(string columnName)
        {
            if (_dbColumnMappings == null)
            {
                return columnName;
            }

            return _dbColumnMappings.ContainsKey(columnName) ? _dbColumnMappings[columnName] : columnName;
        }

        public BulkUpdateResult Execute()
        {
            var temptableName = "#" + Guid.NewGuid();

            var propertyNamesIncludeId = _columnNames.Select(RemoveOperator).ToList();
            propertyNamesIncludeId.AddRange(_idColumns);

            var dataTable = _data.ToDataTable(propertyNamesIncludeId);
            var sqlCreateTemptable = dataTable.GenerateTableDefinition(temptableName);

            var joinCondition = string.Join(" and ", _idColumns.Select(x =>
            {
                string collation = dataTable.Columns[x].DataType == typeof(string) ?
                $" collate {Constants.Collation}" : string.Empty;
                return $"a.[{GetDbColumnName(x)}]{collation} = b.[{x}]{collation}";
            }));

            var updateStatementBuilder = new StringBuilder();
            updateStatementBuilder.AppendLine("update a set");
            updateStatementBuilder.AppendLine(string.Join("," + Environment.NewLine, _columnNames.Select(x => CreateSetStatement(x, "a", "b"))));
            updateStatementBuilder.AppendLine($"from {_tableName } a join [{ temptableName}] b on " + joinCondition);

            _connection.EnsureOpen();

            using (var createTemptableCommand = _connection.CreateTextCommand(_transaction, sqlCreateTemptable))
            {
                createTemptableCommand.ExecuteNonQuery();
            }

            dataTable.SqlBulkCopy(temptableName, null, _connection, _transaction, _options);

            using (var updateCommand = _connection.CreateTextCommand(_transaction, updateStatementBuilder.ToString()))
            {
                var affectedRows = updateCommand.ExecuteNonQuery();

                return new BulkUpdateResult
                {
                    AffectedRows = affectedRows
                };
            }
        }

        private string CreateSetStatement(string prop, string leftTable, string rightTable)
        {
            string sqlOperator = "=";
            string sqlProp = RemoveOperator(prop);

            if (prop.EndsWith("+="))
            {
                sqlOperator = "+=";
            }

            return $"{leftTable}.[{GetDbColumnName(sqlProp)}] {sqlOperator} {rightTable}.[{sqlProp}]";
        }

        private static string RemoveOperator(string prop)
        {
            var rs = prop.Replace("+=", "");
            return rs;
        }
    }
}
