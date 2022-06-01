using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TenantOnboardingFunction;

public class Function
{
    private static string _tenantClusterPrefix = "tenantcluster-";

    private static String _tenantNameField = "Name";
    private static String _tenantDescriptionField = "Description";

    // Use regex to check to ensure description only contain alpha numeric and dash
    // To fulfill cloudformation and most AWS resource naming convention
    private Regex _regex = new Regex(@"^[a-zA-Z0-9\s\-]*$");

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {

        if (request.HttpMethod == "POST" && request.Resource == "/tenant")
        {
            return await ProvisionRequest(request.Body, context);
        }
        else if (request.HttpMethod == "DELETE" && request.Resource == "/tenant/{tenantName}")
        {
            return await DeletionRequest(request.PathParameters, context);
        }
        else
        {
            // No implementation HTTP method, return invalid request
            return _createAPIGatewayProxyResponse((int)HttpStatusCode.BadRequest, "Invalid request");
        }

        async Task<APIGatewayProxyResponse> ProvisionRequest(string requestBody, ILambdaContext context)
        {

            // Default set to error state, and will be update to success code/state if success through
            // Set default to error
            int returnStatusCode = (int)HttpStatusCode.InternalServerError;
            // Use generic error to not expose detail server error to sender
            string returnMessage = "Internal Server error";

            // Create a new JSON to handle the body (as the response is application/json)
            string description = "";
            string tenantName = "";

            try
            {
                // If parse fail, will trigger catch
                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip
                };

                JsonDocument jsonDocument = JsonDocument.Parse(requestBody, options);

                // Check tenantName if it is parsable and if it is validated
                if (String.IsNullOrWhiteSpace(jsonDocument.RootElement.GetProperty(_tenantNameField).GetString()))
                {
                    returnMessage = "Invalid input on tenantName as it is not parsable or null";
                    // Set the bad request status
                    returnStatusCode = (int)HttpStatusCode.BadRequest;
                }
                else
                {
                    // Set tenant name
                    tenantName = jsonDocument.RootElement.GetProperty(_tenantNameField).GetString()!;
                    // Check against regex
                    returnMessage = _validateTenantName(tenantName);
                }

                // If tenant name is validated, check description
                if (String.IsNullOrEmpty(returnMessage))
                {
                    // Check tenantName if it is parsable and if it is validated
                    if (jsonDocument.RootElement.GetProperty(_tenantDescriptionField).GetString() == null)
                    {
                        // Description can be empty, but not null, so error out
                        returnMessage = "Invalid input on tenantName as it is not parsable or null";

                        // Set the bad request status
                        returnStatusCode = (int)HttpStatusCode.BadRequest;
                    }
                    else
                    {
                        // Set the description
                        description = jsonDocument.RootElement.GetProperty(_tenantDescriptionField).GetString()!;
                    }
                }
            }
            catch (Exception exception)
            {
                // Add a message to notify invalid input
                returnMessage = "Invalid input on tenantName and/or description";
                // Set the bad request status
                returnStatusCode = (int)HttpStatusCode.BadRequest;

                context.Logger.LogLine($"Error parsing input: {exception.Message}");
            }

            // If there is error message, it indicate parsing error, so will log the error and skip the provision (which will result internal response code return as it is setup previously)
            if (!String.IsNullOrEmpty(returnMessage))
            {
                context.Logger.LogLine($"Error parsing input where name and/or description are invalid with request body: {requestBody}");
            }
            else
            {
                // Force the tenant name to lower case to ensure tenant name is not abused with internal cluster
                tenantName = _tenantClusterPrefix + tenantName.ToLower().Trim();

                AmazonDynamoDBClient client = new AmazonDynamoDBClient();

                // Use DynamoDB expression to prevent same tenant name being recorded
                // As the tenant name is used for cloudformation name, it must be unique
                try
                {
                    var request = new PutItemRequest
                    {
                        TableName = Environment.GetEnvironmentVariable("TABLE_NAME"),
                        Item = new Dictionary<string, AttributeValue>()
                    {
                            { "TenantId", new AttributeValue(Guid.NewGuid().ToString())},
                            { "Description", new AttributeValue (description)},
                            { "TenantName", new AttributeValue (tenantName)},
                    },
                        ExpressionAttributeNames = new Dictionary<string, string>{
                        {
                            "#tenantname",
                            "TenantName"
                        }
                    },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(){
                        {
                            ":tenantname",
                            new AttributeValue {S = tenantName}
                        }
                    },
                        // The condition is to only insert if the tenant name does not present previously
                        ConditionExpression = "#tenantname <> :tenantname"
                    };

                    await client.PutItemAsync(request);

                    // Set the success message and status code
                    returnMessage = "A new tenant added - " + DateTime.Now;
                    returnStatusCode = (int)HttpStatusCode.OK;
                }
                catch (ConditionalCheckFailedException exception)
                {
                    returnMessage = "The tenant name is already exist in system";
                    returnStatusCode = (int)HttpStatusCode.BadRequest;

                    context.Logger.LogLine("Provision reject due to already existing tenant: " + exception.Message);
                }
                // Catch all other exception
                catch (Exception exception)
                {
                    returnMessage = "Internal provision error";
                    returnStatusCode = (int)HttpStatusCode.InternalServerError;

                    context.Logger.LogLine("Provision failure due to error: " + exception.Message);
                }
            }

            return _createAPIGatewayProxyResponse(returnStatusCode, returnMessage);
        }

        async Task<APIGatewayProxyResponse> DeletionRequest(IDictionary<string, string> pathParameters, ILambdaContext context)
        {
            // Default set to error state, and will be update to success code/state if success through
            // Set default to error
            int returnStatusCode = (int)HttpStatusCode.InternalServerError;
            // Use generic error to not expose detail server error to sender
            string returnMessage = "Internal Server error";

            try
            {
                // Create a new JSON to handle the body (as the response is application/json)
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream);

                string tenantName = pathParameters["tenantName"];

                string validateMessage = _validateTenantName(tenantName);

                // If there is validation error (validation message is not empty) on tenant name, proceed to deletion
                if (! String.IsNullOrWhiteSpace(validateMessage))
                {
                    // Set the error message to validate message (as it contains which part of tenant name is not valid)
                    returnMessage = validateMessage;
                    // Set to bad request input
                    returnStatusCode = (int)HttpStatusCode.BadRequest;
                }
                // If validate message is empty, proceed the deletion logic
                else
                {
                    // Force the tenant name to lower case to align with provision case
                    tenantName = _tenantClusterPrefix + tenantName.ToLower().Trim();

                    AmazonDynamoDBClient client = new AmazonDynamoDBClient();

                    // Use DynamoDB expression to prevent same tenant name being recorded
                    // As the tenant name is used for cloudformation name, it must be unique

                    var request = new DeleteItemRequest
                    {
                        TableName = Environment.GetEnvironmentVariable("TABLE_NAME"),
                        Key = new Dictionary<string, AttributeValue>()
                    {
                            { "TenantName", new AttributeValue {S = tenantName}},
                    }
                    };

                    await client.DeleteItemAsync(request);

                    // Set the success message and status code
                    returnMessage = "Tenant destroyed - " + DateTime.Now;
                    returnStatusCode = (int)HttpStatusCode.OK;
                }
            }
            // Catch all other exception
            catch (Exception exception)
            {
                // The return code and message are already error by default, so only need to log message
                context.Logger.LogLine("Deletion failure due to error: " + exception.Message);
            }

            return _createAPIGatewayProxyResponse(returnStatusCode, returnMessage);
        }
    }

    ///
    /// <summary>
    ///     The method create APIGatewayProxyResponse with JSON response body based on input message and status code
    /// </summary>
    /// <param name="statusCode">
    ///     int object to return the HTTP response code to be used in return APIGatewayProxyResponse
    /// </param>
    /// <param name="message">
    ///     string object to be used in JSON body to be used in return APIGatewayProxyResponse
    /// </param>
    /// <returns>
    ///     APIGatewayProxyResponse according to input parameters
    /// </returns>
    private APIGatewayProxyResponse _createAPIGatewayProxyResponse(int statusCode, string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.Flush();

        return new APIGatewayProxyResponse()
        {
            StatusCode = statusCode,
            Body = Encoding.UTF8.GetString(stream.ToArray()),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    ///
    /// <summary>
    ///     The method will validate the input name with regex/length/characters.
    /// </summary>
    /// <param name="tenantName">
    ///     Tenant Name string to be validated
    /// </param>
    /// <returns>
    ///     A string object. If no error will be empty, otherwise contain the reason for the validation error
    /// </returns>
    private string _validateTenantName(String tenantName)
    {
        if (String.IsNullOrWhiteSpace(tenantName))
        {
            return "Invalid input";

        }
        else if (tenantName.Length > 30)
        {
            return "Invalid input as tenant name length need to be less than 30.";
        }
        else if (!_regex.IsMatch(tenantName))
        {
            return "Invalid input as tenant name must be alpha numeric and dash only.";
        }

        return "";
    }
}
