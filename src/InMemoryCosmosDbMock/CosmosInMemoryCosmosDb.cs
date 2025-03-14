using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TimAbell.MockableCosmos.Parsing;

namespace TimAbell.MockableCosmos;

public class CosmosInMemoryCosmosDb : ICosmosDb
{
	private readonly Dictionary<string, CosmosDbContainer> _containers = new();
	private readonly CosmosDbSqlQueryParser _queryParser;
	private readonly ILogger _logger;
	private readonly CosmosDbQueryExecutor _queryExecutor;

	public CosmosInMemoryCosmosDb()
	{
		_queryParser = new CosmosDbSqlQueryParser();
		_queryExecutor = new CosmosDbQueryExecutor();
	}

	public CosmosInMemoryCosmosDb(ILogger logger)
	{
		_logger = logger;
		_queryParser = new CosmosDbSqlQueryParser(_logger);
		_queryExecutor = new CosmosDbQueryExecutor(logger);
	}

	public Task AddContainerAsync(string containerName)
	{
		if (!_containers.ContainsKey(containerName))
			_containers[containerName] = new CosmosDbContainer(_logger);
		return Task.CompletedTask;
	}

	public Task AddItemAsync(string containerName, object entity)
	{
		if (!_containers.ContainsKey(containerName))
			throw new InvalidOperationException($"Container '{containerName}' does not exist.");

		return _containers[containerName].AddAsync(entity);
	}

	public Task<IEnumerable<JObject>> QueryAsync(string containerName, string sql)
	{
		_logger?.LogDebug("Executing query '{sql}' on container '{containerName}'", sql, containerName);

		try
		{
			// Get the container
			if (!_containers.TryGetValue(containerName, out var container))
			{
				_logger?.LogWarning("Container '{containerName}' not found", containerName);
				throw new InvalidOperationException($"Container '{containerName}' not found");
			}

			// Parse the query
			_logger?.LogDebug("Parsing query");
			var parsedQuery = _queryParser.Parse(sql);
			_logger?.LogDebug("Query parsed successfully. WhereConditions: {count}",
				parsedQuery.WhereConditions != null ? parsedQuery.WhereConditions.Count.ToString() : "null");

			// Execute the query
			_logger?.LogDebug("Executing query against in-memory store");
			var results = _queryExecutor.Execute(parsedQuery, container.Documents);
			_logger?.LogDebug("Query execution complete. Results count: {count}", results.Count());

			return Task.FromResult<IEnumerable<JObject>>(results);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Error executing query: {message}", ex.Message);
			if (ex.InnerException != null)
			{
				_logger?.LogError(ex, "Inner exception: {message}", ex.InnerException.Message);
			}

			throw;
		}
	}

	public Task<(IEnumerable<JObject> Results, string ContinuationToken)> QueryWithPaginationAsync(string containerName, string sql, int maxItemCount, string continuationToken = null)
	{
		if (!_containers.ContainsKey(containerName))
			throw new InvalidOperationException($"Container '{containerName}' does not exist.");

		return _containers[containerName].QueryWithPaginationAsync(sql, maxItemCount, continuationToken);
	}
}
