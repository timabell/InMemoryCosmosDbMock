using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using InMemoryCosmosDbMock.Tests.Utilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TimAbell.MockableCosmos;
using TimAbell.MockableCosmos.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace InMemoryCosmosDbMock.Tests
{
	public class LoggingTests
	{
		private readonly ITestOutputHelper _output;

		public LoggingTests(ITestOutputHelper output)
		{
			_output = output;
		}

		[Fact]
		public async Task Can_Log_SQL_Parsing_And_Execution()
		{
			// Arrange
			var logger = new TestLogger(_output);
			var cosmosDb = new CosmosInMemoryCosmosDb(logger);

			// Create a container and add some test data
			await cosmosDb.AddContainerAsync("TestContainer");

			var alice = new JObject
			{
				["id"] = "1",
				["Name"] = "Alice",
				["Age"] = 30
			};

			var bob = new JObject
			{
				["id"] = "2",
				["Name"] = "Bob",
				["Age"] = 25
			};

			await cosmosDb.AddItemAsync("TestContainer", alice);
			await cosmosDb.AddItemAsync("TestContainer", bob);

			// Act - this will generate detailed logs about the parsing and execution
			var result = cosmosDb.QueryAsync("TestContainer", "SELECT * FROM c WHERE c.Name = 'Alice'").Result;

			// Assert
			result.Should().ContainSingle();

			// The test logger will have output all the debug information to the test console
		}
	}
}
