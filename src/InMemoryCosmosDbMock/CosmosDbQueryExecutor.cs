// Executes queries on in-memory data

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TimAbell.MockableCosmos.Parsing;

namespace TimAbell.MockableCosmos;

public class CosmosDbQueryExecutor
{
	private readonly ILogger _logger;

	public CosmosDbQueryExecutor(ILogger logger = null)
	{
		_logger = logger;
	}

	public IEnumerable<JObject> Execute(ParsedQuery query, List<JObject> store)
	{
		if (_logger != null)
		{
			_logger.LogDebug("Executing query on {count} documents", store.Count);
			_logger.LogDebug("Query details - AST: {ast}", query.SprachedSqlAst != null ? "Present" : "null");

			if (query.WhereConditions != null)
			{
				_logger.LogDebug("WhereConditions count: {count}", query.WhereConditions.Count);
				foreach (var condition in query.WhereConditions)
				{
					_logger.LogDebug("Condition: {property} {operator} {value}",
						condition.PropertyPath, condition.Operator, condition.Value);
				}
			}
			else
			{
				_logger.LogDebug("No WhereConditions present in ParsedQuery");
			}
		}

		var filtered = store.AsQueryable();

		// Apply WHERE if specified
		if (query.SprachedSqlAst != null && query.SprachedSqlAst.Where != null)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying WHERE from AST");
			}

			filtered = filtered.Where(e => ApplyWhere(e, query.SprachedSqlAst.Where.Condition));
		}
		else if (query.WhereConditions != null && query.WhereConditions.Count > 0)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying WHERE from WhereConditions");
			}

			// Remove the statement body from the lambda expression
			filtered = filtered.Where(e => EvaluateWhereConditions(e, query.WhereConditions));

			// Log outside the expression tree if needed
			if (_logger != null && filtered.Any())
			{
				var firstItem = filtered.First();
				_logger.LogDebug("Evaluated document {id} against WHERE conditions: {result}",
					firstItem["id"], EvaluateWhereConditions(firstItem, query.WhereConditions));
			}
		}
		else
		{
			if (_logger != null)
			{
				_logger.LogDebug("No WHERE conditions to apply");
			}
		}

		// Apply ORDER BY if specified
		if (query.SprachedSqlAst != null && query.SprachedSqlAst.OrderBy != null && query.SprachedSqlAst.OrderBy.Items.Count > 0)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying ORDER BY from AST");
			}

			foreach (var orderByItem in query.SprachedSqlAst.OrderBy.Items)
			{
				if (_logger != null)
				{
					_logger.LogDebug("ORDER BY {property} {direction}",
						orderByItem.PropertyPath, orderByItem.Descending ? "DESC" : "ASC");
				}

				if (orderByItem.Descending)
				{
					filtered = filtered.OrderByDescending(e => GetPropertyValue(e, orderByItem.PropertyPath));
				}
				else
				{
					filtered = filtered.OrderBy(e => GetPropertyValue(e, orderByItem.PropertyPath));
				}
			}
		}
		else if (query.OrderBy != null && query.OrderBy.Count > 0)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying ORDER BY from ParsedQuery.OrderBy");
			}

			foreach (var orderBy in query.OrderBy)
			{
				if (_logger != null)
				{
					_logger.LogDebug("ORDER BY {property} {direction}", orderBy.PropertyPath, orderBy.Direction);
				}

				if (orderBy.Direction == SortDirection.Descending)
				{
					filtered = filtered.OrderByDescending(e => GetPropertyValue(e, orderBy.PropertyPath));
				}
				else
				{
					filtered = filtered.OrderBy(e => GetPropertyValue(e, orderBy.PropertyPath));
				}
			}
		}

		var results = filtered.ToList();
		if (_logger != null)
		{
			_logger.LogDebug("After filtering and ordering, got {count} results", results.Count);
		}

		// Apply LIMIT if specified
		if (query.SprachedSqlAst != null && query.SprachedSqlAst.Limit != null)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying LIMIT {limit} from AST", query.SprachedSqlAst.Limit.Value);
			}

			results = results.Take(query.SprachedSqlAst.Limit.Value).ToList();
		}
		else if (query.Limit > 0)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying LIMIT {limit} from ParsedQuery.Limit", query.Limit);
			}

			results = results.Take(query.Limit).ToList();
		}

		if (_logger != null)
		{
			_logger.LogDebug("After limit, final results count: {count}", results.Count);
		}

		// Apply SELECT projection if not SELECT *
		if (query.SprachedSqlAst != null && query.SprachedSqlAst.Select != null && !query.SprachedSqlAst.Select.IsSelectAll)
		{
			var properties = GetSelectedProperties(query.SprachedSqlAst.Select);
			if (_logger != null)
			{
				_logger.LogDebug("Applying projection from AST for properties: {properties}", string.Join(", ", properties));
			}

			return ApplyProjection(results, properties);
		}
		else if (query.PropertyPaths != null && query.PropertyPaths.Count > 0 && !query.PropertyPaths.Contains("*"))
		{
			if (_logger != null)
			{
				_logger.LogDebug("Applying projection for properties: {properties}", string.Join(", ", query.PropertyPaths));
			}

			return ApplyProjection(results, query.PropertyPaths);
		}

		return results;
	}

	// Helper method to evaluate WHERE conditions with detailed logging
	private bool EvaluateWhereConditions(JObject document, List<WhereCondition> conditions)
	{
		if (conditions == null || conditions.Count == 0)
		{
			return true;
		}

		foreach (var condition in conditions)
		{
			var propertyValue = GetPropertyByPath(document, condition.PropertyPath);
			if (_logger != null)
			{
				_logger.LogDebug("Checking {property} {operator} {value}",
					condition.PropertyPath, condition.Operator, condition.Value);
				_logger.LogDebug("Document property value: {value} (Type: {type})",
					propertyValue?.ToString() ?? "null", propertyValue?.Type.ToString() ?? "null");
			}

			var matches = EvaluateCondition(propertyValue, condition.Operator, condition.Value);
			if (_logger != null)
			{
				_logger.LogDebug("Condition result: {result}", matches);
			}

			if (!matches)
			{
				return false;
			}
		}

		return true;
	}

	private bool EvaluateCondition(JToken propertyValue, ComparisonOperator op, JToken conditionValue)
	{
		// Handle null property values
		if (propertyValue == null)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Property value is null, condition fails");
			}

			return false;
		}

		// Convert the condition value to a comparable format
		object conditionObj = null;
		if (conditionValue != null)
		{
			// Extract primitive value from JToken
			switch (conditionValue.Type)
			{
				case JTokenType.String:
					conditionObj = conditionValue.Value<string>();
					break;
				case JTokenType.Integer:
					conditionObj = conditionValue.Value<int>();
					break;
				case JTokenType.Float:
					conditionObj = conditionValue.Value<double>();
					break;
				case JTokenType.Boolean:
					conditionObj = conditionValue.Value<bool>();
					break;
				default:
					conditionObj = conditionValue.ToString();
					break;
			}

			if (_logger != null)
			{
				_logger.LogDebug("Extracted condition value: {value} (Type: {type})",
					conditionObj, conditionObj?.GetType().Name ?? "null");
				_logger.LogDebug("Property value for comparison: {value} (Type: {type})",
					propertyValue.ToString(), propertyValue.Type.ToString());
			}
		}

		if (_logger != null)
		{
			_logger.LogDebug("Comparing using operator: {operator}", op);
			_logger.LogDebug("Condition value after extraction: {value} (Type: {type})",
				conditionObj, conditionObj?.GetType().Name ?? "null");
		}

		// Handle different operators
		switch (op)
		{
			case ComparisonOperator.Equals:
				if (propertyValue.Type == JTokenType.String && conditionObj is string stringValue)
				{
					string propStringValue = propertyValue.Value<string>();
					var result = string.Equals(propStringValue, stringValue, StringComparison.OrdinalIgnoreCase);
					if (_logger != null)
					{
						_logger.LogDebug("String equality check: '{property}' = '{value}' => {result}",
							propStringValue, stringValue, result);
					}

					return result;
				}

				var equality = JToken.DeepEquals(propertyValue, JToken.FromObject(conditionObj));
				if (_logger != null)
				{
					_logger.LogDebug("Deep equality check: '{property}' = '{value}' => {result}",
						propertyValue.ToString(), conditionObj?.ToString() ?? "null", equality);
				}

				return equality;

			case ComparisonOperator.NotEquals:
				return !JToken.DeepEquals(propertyValue, JToken.FromObject(conditionObj));

			case ComparisonOperator.GreaterThan:
				return CompareValues(propertyValue, conditionObj) > 0;

			case ComparisonOperator.GreaterThanOrEqual:
				return CompareValues(propertyValue, conditionObj) >= 0;

			case ComparisonOperator.LessThan:
				return CompareValues(propertyValue, conditionObj) < 0;

			case ComparisonOperator.LessThanOrEqual:
				return CompareValues(propertyValue, conditionObj) <= 0;

			case ComparisonOperator.StringContains:
				if (propertyValue.Type == JTokenType.String && conditionObj is string containsValue)
				{
					return propertyValue.Value<string>().IndexOf(containsValue, StringComparison.OrdinalIgnoreCase) >= 0;
				}

				return false;

			case ComparisonOperator.StringStartsWith:
				if (propertyValue.Type == JTokenType.String && conditionObj is string startsWithValue)
				{
					return propertyValue.Value<string>().StartsWith(startsWithValue, StringComparison.OrdinalIgnoreCase);
				}

				return false;

			case ComparisonOperator.IsDefined:
				return propertyValue != null;

			case ComparisonOperator.ArrayContains:
				if (propertyValue is JArray array)
				{
					// Extract the value to search for
					object searchValue = conditionObj;

					// If the search value is null, we can't meaningfully search for it
					if (searchValue == null)
					{
						return false;
					}

					// Convert the search value to string for comparison
					string searchString = searchValue.ToString();

					// Check each element in the array
					foreach (var element in array)
					{
						if (element != null && element.ToString() == searchString)
						{
							return true;
						}
					}

					return false;
				}

				// If propValue is not an array, return false
				return false;

			default:
				if (_logger != null)
				{
					_logger.LogWarning("Unsupported operator: {operator}", op);
				}

				return false;
		}
	}

	private int CompareValues(JToken token, object value)
	{
		if (token == null || value == null)
		{
			return 0;
		}

		if (token.Type == JTokenType.Integer && value is int intValue)
		{
			return token.Value<int>().CompareTo(intValue);
		}
		else if (token.Type == JTokenType.Float && value is double doubleValue)
		{
			return token.Value<double>().CompareTo(doubleValue);
		}
		else if (token.Type == JTokenType.String && value is string stringValue)
		{
			return string.Compare(token.Value<string>(), stringValue, StringComparison.OrdinalIgnoreCase);
		}
		// Add more comparisons as needed

		return 0;
	}

	private IEnumerable<string> GetSelectedProperties(SelectClause selectClause)
	{
		return selectClause.Items
			.OfType<PropertySelectItem>()
			.Select(item => item.PropertyPath)
			.ToList();
	}

	private IEnumerable<JObject> ApplyProjection(IEnumerable<JObject> results, IEnumerable<string> properties)
	{
		var projectedResults = new List<JObject>();
		var propertyPaths = properties.ToList();

		foreach (var item in results)
		{
			var projectedItem = new JObject();

			// Always include the 'id' field if it exists in the original document
			if (item["id"] != null)
			{
				projectedItem["id"] = item["id"];
			}

			foreach (var path in propertyPaths)
			{
				// Remove the FROM alias (like 'c.') if present at the beginning of the path
				string processedPath = path;
				if (path.Contains('.') && (path.StartsWith("c.") || path.StartsWith("r.")))
				{
					// Skip the alias (e.g., "c.") part
					processedPath = path.Substring(path.IndexOf('.') + 1);
				}

				var propValue = GetPropertyByPath(item, path);
				if (propValue != null)
				{
					SetPropertyByPath(projectedItem, processedPath, propValue);
				}
			}

			projectedResults.Add(projectedItem);
		}

		return projectedResults;
	}

	private object GetPropertyValue(JObject item, string propertyPath)
	{
		var token = GetPropertyByPath(item, propertyPath);
		object value = token?.Value<object>();

		if (_logger != null)
		{
			_logger.LogDebug("GetPropertyValue for path '{path}' returned: {value} (Type: {type})",
				propertyPath, value?.ToString() ?? "null", value?.GetType().Name ?? "null");
		}

		return value;
	}

	private JToken GetPropertyByPath(JObject item, string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}

		// Special case for * to return the entire object
		if (path == "*")
		{
			return item;
		}

		var parts = path.Split('.');
		JToken current = item;

		// Skip the first part if it's the FROM alias
		int startIndex = 0;
		if (parts.Length > 1 && (parts[0] == "c" || parts[0] == "r")) // Common FROM aliases are 'c' and 'r'
		{
			startIndex = 1;
			if (_logger != null)
			{
				_logger.LogDebug("Skipping FROM alias '{alias}' in property path", parts[0]);
			}
		}

		// Navigate through the path parts
		for (int i = startIndex; i < parts.Length; i++)
		{
			if (current == null)
			{
				return null;
			}

			if (current is JObject obj)
			{
				current = obj[parts[i]];
			}
			else
			{
				// Can't navigate further
				return null;
			}
		}

		return current;
	}

	private void SetPropertyByPath(JObject item, string path, JToken value)
	{
		var parts = path.Split('.');
		var current = item;

		// Navigate to the last parent in the path
		for (int i = 0; i < parts.Length - 1; i++)
		{
			var part = parts[i];
			if (current[part] == null || !(current[part] is JObject))
			{
				current[part] = new JObject();
			}

			current = (JObject)current[part];
		}

		// Set the value on the last part
		current[parts[parts.Length - 1]] = value;
	}

	private bool ApplyWhere(JObject item, Expression condition)
	{
		if (condition == null) return true;

		var result = EvaluateExpression(item, condition);

		// Convert the result to a boolean if it's not already
		bool boolResult;
		if (result is bool b)
		{
			boolResult = b;
		}
		else if (result != null)
		{
			boolResult = Convert.ToBoolean(result);
		}
		else
		{
			// Null evaluates to false
			boolResult = false;
		}

		if (_logger != null)
		{
			_logger.LogDebug("ApplyWhere result: {result} for item with id: {id}", boolResult, item["id"]);
		}

		return boolResult;
	}

	private bool ApplyLegacyWhereConditions(JObject item, List<WhereCondition> whereConditions)
	{
		// All conditions must be true (AND semantics)
		foreach (var condition in whereConditions)
		{
			var propValue = GetPropertyValue(item, condition.PropertyPath);

			// For string comparisons, we might need to handle the stored JToken format 
			if (!CompareCondition(propValue, condition.Operator, condition.Value))
			{
				return false;
			}
		}

		return true;
	}

	private bool CompareCondition(object propValue, ComparisonOperator operatorEnum, JToken conditionValue)
	{
		// Handle null values
		if (propValue == null)
		{
			// Special case for IS_NULL check (represented as Equals with null value)
			if (operatorEnum == ComparisonOperator.Equals && conditionValue.Type == JTokenType.Null)
			{
				return true;
			}

			// For IsDefined operator, propValue being null means the property is not defined
			if (operatorEnum == ComparisonOperator.IsDefined)
			{
				return false;
			}

			return false;
		}

		// For IsDefined operator, if we get here, the property exists
		if (operatorEnum == ComparisonOperator.IsDefined)
		{
			return true;
		}

		// For ArrayContains operator
		if (operatorEnum == ComparisonOperator.ArrayContains)
		{
			if (propValue is JArray array)
			{
				// Extract the value to search for
				object searchValue = conditionValue.ToObject<object>();

				// If the search value is null, we can't meaningfully search for it
				if (searchValue == null)
				{
					return false;
				}

				// Convert the search value to string for comparison
				string searchString = searchValue.ToString();

				// Check each element in the array
				foreach (var element in array)
				{
					if (element != null && element.ToString() == searchString)
					{
						return true;
					}
				}

				return false;
			}

			// If propValue is not an array, return false
			return false;
		}

		// Extract the value from JToken if needed
		object value = conditionValue.Type == JTokenType.String
			? conditionValue.Value<string>()
			: conditionValue.ToObject<object>();

		// For string comparisons where we're comparing different types
		if (propValue is string propString && value is string valueString)
		{
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return string.Equals(propString, valueString, StringComparison.OrdinalIgnoreCase);
				case ComparisonOperator.NotEquals:
					return !string.Equals(propString, valueString, StringComparison.OrdinalIgnoreCase);
				case ComparisonOperator.StringContains:
					return propString.IndexOf(valueString, StringComparison.OrdinalIgnoreCase) >= 0;
				case ComparisonOperator.StringStartsWith:
					return propString.StartsWith(valueString, StringComparison.OrdinalIgnoreCase);
				default:
					throw new NotImplementedException($"String operator {operatorEnum} not implemented");
			}
		}

		// For numeric comparisons
		if (propValue is IComparable comparable && value != null)
		{
			// Convert value to the same type as propValue if possible
			if (propValue is int)
			{
				value = Convert.ToInt32(value);
			}
			else if (propValue is double)
			{
				value = Convert.ToDouble(value);
			}
			else if (propValue is decimal)
			{
				value = Convert.ToDecimal(value);
			}
			else if (propValue is long)
			{
				value = Convert.ToInt64(value);
			}

			int comparison = comparable.CompareTo(value);
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return comparison == 0;
				case ComparisonOperator.NotEquals:
					return comparison != 0;
				case ComparisonOperator.GreaterThan:
					return comparison > 0;
				case ComparisonOperator.GreaterThanOrEqual:
					return comparison >= 0;
				case ComparisonOperator.LessThan:
					return comparison < 0;
				case ComparisonOperator.LessThanOrEqual:
					return comparison <= 0;
				default:
					throw new NotImplementedException($"Operator {operatorEnum} not implemented for numeric comparison");
			}
		}

		// For boolean values
		if (propValue is bool propBool && value is bool valueBool)
		{
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return propBool == valueBool;
				case ComparisonOperator.NotEquals:
					return propBool != valueBool;
				default:
					throw new NotImplementedException($"Operator {operatorEnum} not implemented for boolean comparison");
			}
		}

		return false;
	}

	private bool CompareCondition(JToken propertyValue, JToken conditionValue, ComparisonOperator operatorEnum)
	{
		// Extract primitive values for comparison
		object propValue = propertyValue?.Value<object>();
		object conditionObj = null;
		if (conditionValue != null)
		{
			// Extract primitive value from JToken
			switch (conditionValue.Type)
			{
				case JTokenType.String:
					conditionObj = conditionValue.Value<string>();
					break;
				case JTokenType.Integer:
					conditionObj = conditionValue.Value<int>();
					break;
				case JTokenType.Float:
					conditionObj = conditionValue.Value<double>();
					break;
				case JTokenType.Boolean:
					conditionObj = conditionValue.Value<bool>();
					break;
				default:
					conditionObj = conditionValue.ToString();
					break;
			}

			if (_logger != null)
			{
				_logger.LogDebug("Extracted condition value: {value} (Type: {type})",
					conditionObj, conditionObj?.GetType().Name ?? "null");
				_logger.LogDebug("Property value for comparison: {value} (Type: {type})",
					propertyValue.ToString(), propertyValue.Type.ToString());
			}
		}

		return CompareValues(propValue, conditionObj, operatorEnum);
	}

	private bool CompareValues(object propValue, object value, ComparisonOperator operatorEnum)
	{
		if (_logger != null)
		{
			_logger.LogDebug("Comparing values: '{left}' ({leftType}) {op} '{right}' ({rightType})",
				propValue?.ToString() ?? "null",
				propValue?.GetType().Name ?? "null",
				operatorEnum.ToString(),
				value?.ToString() ?? "null",
				value?.GetType().Name ?? "null");
		}

		// For null values
		if (propValue == null)
		{
			if (operatorEnum == ComparisonOperator.Equals)
			{
				return value == null;
			}
			else if (operatorEnum == ComparisonOperator.NotEquals)
			{
				return value != null;
			}

			// All other comparisons with null return false
			return false;
		}

		if (value == null)
		{
			if (operatorEnum == ComparisonOperator.NotEquals)
			{
				return true;
			}

			// All other comparisons with null return false
			return false;
		}

		// For string values
		if (propValue is string strPropValue && value is string strValue)
		{
			int comparison = string.Compare(strPropValue, strValue, StringComparison.OrdinalIgnoreCase);
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return comparison == 0;
				case ComparisonOperator.NotEquals:
					return comparison != 0;
				case ComparisonOperator.GreaterThan:
					return comparison > 0;
				case ComparisonOperator.GreaterThanOrEqual:
					return comparison >= 0;
				case ComparisonOperator.LessThan:
					return comparison < 0;
				case ComparisonOperator.LessThanOrEqual:
					return comparison <= 0;
				default:
					throw new NotImplementedException($"Operator {operatorEnum} not implemented for string comparison");
			}
		}

		// For numeric comparisons
		if (propValue is IComparable comparable && value != null)
		{
			// Convert value to the same type as propValue if possible
			if (propValue is int)
			{
				value = Convert.ToInt32(value);
			}
			else if (propValue is double)
			{
				value = Convert.ToDouble(value);
			}
			else if (propValue is decimal)
			{
				value = Convert.ToDecimal(value);
			}
			else if (propValue is long)
			{
				value = Convert.ToInt64(value);
			}

			int comparison = comparable.CompareTo(value);
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return comparison == 0;
				case ComparisonOperator.NotEquals:
					return comparison != 0;
				case ComparisonOperator.GreaterThan:
					return comparison > 0;
				case ComparisonOperator.GreaterThanOrEqual:
					return comparison >= 0;
				case ComparisonOperator.LessThan:
					return comparison < 0;
				case ComparisonOperator.LessThanOrEqual:
					return comparison <= 0;
				default:
					throw new NotImplementedException($"Operator {operatorEnum} not implemented for numeric comparison");
			}
		}

		// For boolean values
		if (propValue is bool boolPropValue && value is bool boolValue)
		{
			switch (operatorEnum)
			{
				case ComparisonOperator.Equals:
					return boolPropValue == boolValue;
				case ComparisonOperator.NotEquals:
					return boolPropValue != boolValue;
				default:
					throw new NotImplementedException($"Operator {operatorEnum} not implemented for boolean comparison");
			}
		}

		// Default case: convert to string and compare
		return CompareStringValues(propValue.ToString(), value.ToString(), operatorEnum);
	}

	private bool CompareStringValues(string left, string right, ComparisonOperator operatorEnum)
	{
		int comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
		switch (operatorEnum)
		{
			case ComparisonOperator.Equals:
				return comparison == 0;
			case ComparisonOperator.NotEquals:
				return comparison != 0;
			case ComparisonOperator.GreaterThan:
				return comparison > 0;
			case ComparisonOperator.GreaterThanOrEqual:
				return comparison >= 0;
			case ComparisonOperator.LessThan:
				return comparison < 0;
			case ComparisonOperator.LessThanOrEqual:
				return comparison <= 0;
			default:
				throw new NotImplementedException($"Operator {operatorEnum} not implemented for string comparison");
		}
	}

	private bool EvaluateExpression(JObject item, Expression expression)
	{
		if (_logger != null)
		{
			_logger.LogDebug("Evaluating expression of type: {type}", expression.GetType().Name);
		}

		if (expression is BinaryExpression binary)
		{
			var leftValue = EvaluateValue(item, binary.Left);
			var rightValue = EvaluateValue(item, binary.Right);

			if (_logger != null)
			{
				_logger.LogDebug("Comparing values: '{left}' ({leftType}) {op} '{right}' ({rightType})",
					leftValue, leftValue?.GetType().Name,
					binary.Operator,
					rightValue, rightValue?.GetType().Name);
			}

			switch (binary.Operator)
			{
				case BinaryOperator.Equal:
					// Handle string comparison for JValue objects
					if (leftValue is JValue leftJValue && leftJValue.Type == JTokenType.String)
					{
						string leftStr = leftJValue.Value<string>();
						string rightStr = rightValue is JValue rightJValue && rightJValue.Type == JTokenType.String
							? rightJValue.Value<string>()
							: rightValue as string;

						if (rightStr != null)
						{
							if (_logger != null)
							{
								_logger.LogDebug("JValue string comparison: '{left}' = '{right}'", leftStr, rightStr);
							}
							return string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
						}
					}

					// Handle numeric comparisons
					else if (leftValue is JValue leftNumJValue &&
							 (leftNumJValue.Type == JTokenType.Integer || leftNumJValue.Type == JTokenType.Float))
					{
						// Try to get numeric values for comparison
						if (leftNumJValue.Type == JTokenType.Integer)
						{
							var leftInt = leftNumJValue.Value<int>();
							if (rightValue is int rightInt)
							{
								if (_logger != null)
								{
									_logger.LogDebug("JValue integer comparison: {left} = {right}", leftInt, rightInt);
								}
								return leftInt == rightInt;
							}
							else if (rightValue is double rightDouble)
							{
								return leftInt == rightDouble;
							}
							else if (rightValue is JValue rightJValue1 && rightJValue1.Type == JTokenType.Integer)
							{
								var rightInt1 = rightJValue1.Value<int>();
								return leftInt == rightInt1;
							}
							else if (rightValue is JValue rightJValue2 && rightJValue2.Type == JTokenType.Float)
							{
								var rightDouble1 = rightJValue2.Value<double>();
								return leftInt == rightDouble1;
							}
							else if (int.TryParse(rightValue?.ToString(), out int parsedInt))
							{
								return leftInt == parsedInt;
							}
						}
						else if (leftNumJValue.Type == JTokenType.Float)
						{
							var leftDouble = leftNumJValue.Value<double>();
							if (rightValue is double rightDouble2)
							{
								if (_logger != null)
								{
									_logger.LogDebug("JValue float comparison: {left} = {right}", leftDouble, rightDouble2);
								}
								return leftDouble == rightDouble2;
							}
							else if (rightValue is int rightInt2)
							{
								return leftDouble == rightInt2;
							}
							else if (rightValue is JValue rightJValue3 && rightJValue3.Type == JTokenType.Float)
							{
								var rightDouble3 = rightJValue3.Value<double>();
								return leftDouble == rightDouble3;
							}
							else if (rightValue is JValue rightJValue4 && rightJValue4.Type == JTokenType.Integer)
							{
								var rightInt3 = rightJValue4.Value<int>();
								return leftDouble == rightInt3;
							}
							else if (double.TryParse(rightValue?.ToString(), out double parsedDouble))
							{
								return leftDouble == parsedDouble;
							}
						}
					}
					// Handle boolean comparisons
					else if (leftValue is JValue leftBoolJValue && leftBoolJValue.Type == JTokenType.Boolean)
					{
						var leftBool = leftBoolJValue.Value<bool>();
						if (_logger != null)
						{
							_logger.LogDebug("JValue boolean comparison: {left} = {right}", leftBool, rightValue);
						}

						// Check if rightValue is a direct boolean
						if (rightValue is bool rightBoolValue)
						{
							return leftBool == rightBoolValue;
						}
						// Check if rightValue is a JValue Boolean
						else if (rightValue is JValue rightBoolJValue && rightBoolJValue.Type == JTokenType.Boolean)
						{
							var rightBool = rightBoolJValue.Value<bool>();
							return leftBool == rightBool;
						}
						// Try to parse as boolean string
						else if (bool.TryParse(rightValue?.ToString(), out bool parsedBool))
						{
							return leftBool == parsedBool;
						}
					}

					return Equals(leftValue, rightValue);

				case BinaryOperator.NotEqual:
					// Handle string comparison for JValue objects
					if (leftValue is JValue leftJValueNE && leftJValueNE.Type == JTokenType.String)
					{
						string leftStr = leftJValueNE.Value<string>();
						string rightStr = rightValue is JValue rightJValueNE && rightJValueNE.Type == JTokenType.String
							? rightJValueNE.Value<string>()
							: rightValue as string;

						if (rightStr != null)
						{
							if (_logger != null)
							{
								_logger.LogDebug("JValue string not-equals comparison: '{left}' != '{right}'", leftStr, rightStr);
							}

							return !string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
						}
					}

					return !Equals(leftValue, rightValue);

				case BinaryOperator.GreaterThan:
					return CompareValues(leftValue, rightValue) > 0;

				case BinaryOperator.LessThan:
					return CompareValues(leftValue, rightValue) < 0;

				case BinaryOperator.GreaterThanOrEqual:
					return CompareValues(leftValue, rightValue) >= 0;

				case BinaryOperator.LessThanOrEqual:
					return CompareValues(leftValue, rightValue) <= 0;

				case BinaryOperator.And:
					return EvaluateExpression(item, binary.Left) && EvaluateExpression(item, binary.Right);

				case BinaryOperator.Or:
					return EvaluateExpression(item, binary.Left) || EvaluateExpression(item, binary.Right);

				default:
					throw new NotImplementedException($"Operator {binary.Operator} not implemented");
			}
		}

		if (expression is PropertyExpression prop)
		{
			// For boolean property expressions, simply check if the property exists and is true
			var value = GetPropertyValue(item, prop.PropertyPath);
			if (value is JValue jValue)
			{
				value = jValue.Value;
				if (_logger != null)
				{
					_logger.LogDebug("Converted JValue to underlying value: {value} (Type: {type})",
						value?.ToString() ?? "null", value?.GetType().Name ?? "null");
				}
			}
			if (value is bool boolValue)
			{
				return boolValue;
			}

			return value != null;
		}

		if (expression is FunctionCallExpression func)
		{
			return EvaluateFunction(item, func);
		}
		else if (expression is UnaryExpression unary)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Evaluating unary expression: {op}", unary.Operator);
			}

			object operandValue = EvaluateExpression(item, unary.Operand);

			switch (unary.Operator)
			{
				case UnaryOperator.Not:
					// Only handle boolean values directly
					if (operandValue is bool boolVal)
					{
						bool result = !boolVal;
						if (_logger != null)
						{
							_logger.LogDebug("NOT operator on boolean value {value} returned {result}", boolVal, result);
							_logger.LogDebug("NOT operator: operand value {value} is of type {type}", operandValue, operandValue?.GetType().Name);
						}
						return result;
					}

					// If it's not a boolean, throw an exception for clarity
					throw new InvalidOperationException($"NOT operator can only be applied to boolean values, but got {operandValue?.GetType().Name ?? "null"}");

				default:
					throw new NotImplementedException($"Unary operator {unary.Operator} not implemented");
			}
		}

		throw new NotImplementedException($"Expression type {expression.GetType().Name} not implemented");
	}

	private object EvaluateValue(JObject item, Expression expression)
	{
		if (_logger != null)
		{
			_logger.LogDebug("Evaluating expression of type: {type}", expression.GetType().Name);
		}

		if (expression is ConstantExpression constant)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Constant value: '{value}' (Type: {type})", constant.Value?.ToString() ?? "null", constant.Value?.GetType().Name ?? "null");
			}

			return constant.Value;
		}

		if (expression is PropertyExpression prop)
		{
			var propValue = GetPropertyValue(item, prop.PropertyPath);
			if (propValue is JValue jValue)
			{
				propValue = jValue.Value;
				if (_logger != null)
				{
					_logger.LogDebug("Converted JValue to underlying value: {value} (Type: {type})",
						propValue?.ToString() ?? "null", propValue?.GetType().Name ?? "null");
				}
			}
			if (_logger != null)
			{
				_logger.LogDebug("Property '{path}' value: '{value}' (Type: {type})",
					prop.PropertyPath, propValue?.ToString() ?? "null", propValue?.GetType().Name ?? "null");
			}

			return propValue;
		}

		if (expression is FunctionCallExpression func)
		{
			if (_logger != null)
			{
				_logger.LogDebug("Evaluating function: {name}", func.Name);
			}

			return EvaluateFunction(item, func);
		}

		if (expression is BinaryExpression binary)
		{
			// For binary expressions in a value context, we evaluate them as boolean
			bool result = EvaluateExpression(item, binary);
			if (_logger != null)
			{
				_logger.LogDebug("Binary expression evaluated to: {result}", result);
			}
			return result;
		}

		throw new NotImplementedException($"Value expression type {expression.GetType().Name} not implemented");
	}

	private bool EvaluateFunction(JObject item, FunctionCallExpression function)
	{
		if (_logger != null)
		{
			_logger.LogDebug("Evaluating function: {name} with {count} arguments", function.Name, function.Arguments.Count);
		}

		switch (function.Name.ToUpperInvariant())
		{
			case "CONTAINS":
				if (function.Arguments.Count != 2)
				{
					throw new ArgumentException("CONTAINS function requires exactly 2 arguments");
				}

				var containsPropertyValue = EvaluateValue(item, function.Arguments[0])?.ToString();
				var containsSearchValue = EvaluateValue(item, function.Arguments[1])?.ToString();

				if (containsPropertyValue == null || containsSearchValue == null)
				{
					return false;
				}

				return containsPropertyValue.Contains(containsSearchValue);

			case "STARTSWITH":
				if (function.Arguments.Count != 2)
				{
					throw new ArgumentException("STARTSWITH function requires exactly 2 arguments");
				}

				var startsWithPropertyValue = EvaluateValue(item, function.Arguments[0])?.ToString();
				var startsWithSearchValue = EvaluateValue(item, function.Arguments[1])?.ToString();

				if (startsWithPropertyValue == null || startsWithSearchValue == null)
				{
					return false;
				}

				return startsWithPropertyValue.StartsWith(startsWithSearchValue);

			case "ARRAY_CONTAINS":
				if (function.Arguments.Count != 2)
				{
					throw new ArgumentException("ARRAY_CONTAINS function requires exactly 2 arguments");
				}

				var arrayValue = EvaluateValue(item, function.Arguments[0]);
				var searchValue = EvaluateValue(item, function.Arguments[1]);

				if (arrayValue == null || searchValue == null)
				{
					return false;
				}

				if (_logger != null)
				{
					_logger.LogDebug("ARRAY_CONTAINS: Checking if array {array} contains value {value}",
						arrayValue, searchValue);
				}

				// Handle JArray type
				if (arrayValue is JArray jArray)
				{
					// Convert search value to string for comparison if it's not null
					var searchValueString = searchValue?.ToString();

					foreach (var element in jArray)
					{
						if (element.ToString().Equals(searchValueString, StringComparison.OrdinalIgnoreCase))
						{
							if (_logger != null)
							{
								_logger.LogDebug("ARRAY_CONTAINS: Found match for {value} in array", searchValue);
							}
							return true;
						}
					}

					if (_logger != null)
					{
						_logger.LogDebug("ARRAY_CONTAINS: No match found for {value} in array", searchValue);
					}
					return false;
				}

				// Handle regular array types
				if (arrayValue is Array array)
				{
					foreach (var element in array)
					{
						if (element.Equals(searchValue))
						{
							if (_logger != null)
							{
								_logger.LogDebug("ARRAY_CONTAINS: Found match for {value} in array", searchValue);
							}
							return true;
						}
					}

					if (_logger != null)
					{
						_logger.LogDebug("ARRAY_CONTAINS: No match found for {value} in array", searchValue);
					}
					return false;
				}

				// If it's neither a JArray nor a regular array, return false
				if (_logger != null)
				{
					_logger.LogDebug("ARRAY_CONTAINS: Value is not an array: {value} (Type: {type})",
						arrayValue, arrayValue.GetType().Name);
				}
				return false;

			case "IS_NULL":
				if (function.Arguments.Count != 1)
				{
					throw new ArgumentException("IS_NULL function requires exactly 1 argument");
				}

				if (function.Arguments[0] is PropertyExpression propExpr)
				{
					var token = GetPropertyByPath(item, propExpr.PropertyPath);
					bool isNull = token == null || token.Type == JTokenType.Null;

					if (_logger != null)
					{
						_logger.LogDebug("IS_NULL: Property '{path}' is {result}", propExpr.PropertyPath, isNull ? "null" : "not null");
					}

					return isNull;
				}

				var value = EvaluateValue(item, function.Arguments[0]);
				bool result = value == null;

				if (_logger != null)
				{
					_logger.LogDebug("IS_NULL: Value is {result}", result ? "null" : "not null");
				}

				return result;

			case "IS_DEFINED":
				if (function.Arguments.Count != 1)
				{
					throw new ArgumentException("IS_DEFINED function requires exactly 1 argument");
				}

				if (function.Arguments[0] is PropertyExpression propDefined)
				{
					var token = GetPropertyByPath(item, propDefined.PropertyPath);
					bool isDefined = token != null;

					if (_logger != null)
					{
						_logger.LogDebug("IS_DEFINED: Property '{path}' is {result}", propDefined.PropertyPath, isDefined ? "defined" : "not defined");
					}

					return isDefined;
				}

				throw new ArgumentException("IS_DEFINED function requires a property expression as its argument");

			default:
				throw new NotImplementedException($"Function {function.Name} not implemented");
		}
	}

	private int CompareValues(object left, object right)
	{
		if (left == null && right == null)
		{
			return 0;
		}

		if (left == null)
		{
			return -1;
		}

		if (right == null)
		{
			return 1;
		}

		if (left is IComparable comparable)
		{
			// Convert value to the same type as propValue if possible
			if (left is int)
			{
				right = Convert.ToInt32(right);
			}
			else if (left is double)
			{
				right = Convert.ToDouble(right);
			}
			else if (left is decimal)
			{
				right = Convert.ToDecimal(right);
			}
			else if (left is long)
			{
				right = Convert.ToInt64(right);
			}

			int comparison = comparable.CompareTo(right);
			return comparison;
		}

		// Default to string comparison
		return left.ToString().CompareTo(right.ToString());
	}
}
