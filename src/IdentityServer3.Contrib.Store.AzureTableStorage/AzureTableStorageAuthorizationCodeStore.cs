﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IdentityServer3.Core.Models;
using IdentityServer3.Core.Services;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace IdentityServer3.Contrib.Store.AzureTableStorage
{
    /// <summary>
    /// An Azure Table Storage backed authrozation code store for Identity Server 3
    /// </summary>
    public class AzureTableStorageAuthorizationCodeStore : BaseTokenStore<AuthorizationCode>, IAuthorizationCodeStore
    {
        private readonly Lazy<CloudTable> _table;

        /// <summary>
        /// Creates a new instance of the Azure Table Storage authorization code store
        /// </summary>
        /// <param name="clientStore">Needed because we don't serialize the whole AuthroizationCode. It is looked up by id from the store.</param>
        /// <param name="scopeStore">Needed because we don't serialize the whole AuthorizationCode. It is looked up by id from the store.</param>
        /// <param name="connectionString">The connection string for connecting to Azure Table Storage.</param>
        /// <param name="tableName">Optional table name. Defaults to RefreshTokens</param>
        public AzureTableStorageAuthorizationCodeStore(IClientStore clientStore, IScopeStore scopeStore, string connectionString, string tableName = "AuthorizationCodes") : base(clientStore, scopeStore)
        {
            _table = new Lazy<CloudTable>(() =>
            {
                var account = CloudStorageAccount.Parse(connectionString);
                var client = account.CreateCloudTableClient();
                var table = client.GetTableReference(tableName);

                table.CreateIfNotExists();
                return table;
            });
        }

        /// <summary>
        /// Saves the authorization code with its given key
        /// </summary>
        /// <param name="key">The key for the authorization code</param>
        /// <param name="value">The authorization code to serialize and store</param>
        public async Task StoreAsync(string key, AuthorizationCode value)
        {
            var entity = new TokenTableEntity
            {
                PartitionKey = key.GetParitionKey(),
                RowKey = key,
                ClientId = value.ClientId,
                Json = ToJson(value),
                SubjectId = value.SubjectId
            };
            var op = TableOperation.InsertOrReplace(entity);
            await _table.Value.ExecuteAsync(op);
        }

        /// <summary>
        /// Retrieves the authorization code using its key 
        /// </summary>
        /// <param name="key">The key for the authorization code</param>
        /// <returns>A Tasks with the authorization code</returns>
        public async Task<AuthorizationCode> GetAsync(string key)
        {
            var op = TableOperation.Retrieve<TokenTableEntity>(key.GetParitionKey(), key);
            var result = await _table.Value.ExecuteAsync(op);
            var tokenEntity = result.Result as TokenTableEntity;
            return tokenEntity != null ? FromJson(tokenEntity.Json) : null;
        }

        /// <summary>
        /// Removes the authorization code from the store with a given key
        /// </summary>
        /// <param name="key">The key of the authorization code</param>
        public async Task RemoveAsync(string key)
        {
            var entity = new TokenTableEntity
            {
                PartitionKey = key.GetParitionKey(),
                RowKey = key,
                ETag = "*"
            };
            var op = TableOperation.Delete(entity);
            await _table.Value.ExecuteAsync(op);
        }

        /// <summary>
        /// Retrieves all the authorization codes for a given subject
        /// </summary>
        /// <param name="subject">The subject to filter by.</param>
        /// <returns></returns>
        public async Task<IEnumerable<ITokenMetadata>> GetAllAsync(string subject)
        {
            var query =
                new TableQuery<TokenTableEntity>().Where(TableQuery.GenerateFilterCondition("SubjectId",
                    QueryComparisons.Equal, subject));
            var list = new List<TokenTableEntity>();
            TableContinuationToken continuationToken = null;
            do
            {
                var result = await _table.Value.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = result.ContinuationToken;
                list.AddRange(result.Results);
            } while (continuationToken != null);
            return list.Select(tte => FromJson(tte.Json));
        }

        /// <summary>
        /// Removes the authorization code for a given subject and client.
        /// </summary>
        /// <param name="subject">The subject to filter by.</param>
        /// <param name="client">The client to filter by.</param>
        /// <returns></returns>
        public async Task RevokeAsync(string subject, string client)
        {
            var query = new TableQuery<TokenTableEntity>().Where(
                TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("SubjectId", QueryComparisons.Equal, subject),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("ClientId", QueryComparisons.Equal, client)));
            var list = new List<TokenTableEntity>();
            TableContinuationToken continuationToken = null;
            do
            {
                var result = await _table.Value.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = result.ContinuationToken;
                list.AddRange(result.Results);
            } while (continuationToken != null);
            var entityDeletionTasks = list.Select(entity =>
            {
                var op = TableOperation.Delete(entity);
                return _table.Value.ExecuteAsync(op);
            });

            await Task.WhenAll(entityDeletionTasks);
        }
    }
}