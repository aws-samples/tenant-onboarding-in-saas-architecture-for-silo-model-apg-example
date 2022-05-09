using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TenantOnboardingFunction;

public class Function
{
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
            JObject errorBody = new JObject();
            errorBody.Add("message", "Invalid request");
            // No implementation HTTP method, return invalid request
            return new APIGatewayProxyResponse()
            {
                StatusCode = (int)HttpStatusCode.BadRequest,
                Body = errorBody.ToString(),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }

        async Task<APIGatewayProxyResponse> ProvisionRequest(string requestBody, ILambdaContext context)
        {
            // Create a new JSON to handle the body (as the response is application/json)
            JObject responseBody = new JObject();

            string description = "";
            string name = "";

            try
            {
                // If parse fail, will trigger catch
                JObject body = JObject.Parse(requestBody);

                JToken descriptionToken = body["Description"];
                JToken nameToken = body["Name"];

                // Check for valid input, if not valid (name and description not present, and/or name is empty), return bad request code
                if (descriptionToken == null || nameToken == null || nameToken.ToString().Trim() == String.Empty)
                {
                    // Create a new JSON to handle the body (as the response is application/json)
                    responseBody.Add("message", "Invalid input");

                }
                else
                {
                    description = descriptionToken.ToString();
                    // Force the tenant name to lower case for case insenstivie tenant name (avoid different tenant name case to be present as different tenant)
                    name = nameToken.ToString().ToLower().Trim();
                }

            }
            catch (Exception exception)
            {
                // Add a message to notify invalid input
                responseBody.Add("message", "Invalid input on name and/or description");
                context.Logger.LogLine($"Error parsing input: {exception.Message}");
            }

            // If there is message, it indicate parsing error, so return that error
            if (responseBody["message"] != null)
            {
                context.Logger.LogLine($"Error parsing input where name and/or description are invalid with request body: {requestBody}");

                return new APIGatewayProxyResponse()
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = responseBody.ToString(),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }

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
                            { "TenantName", new AttributeValue (name)},
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
                            new AttributeValue {S = name}
                        }
                    },
                    // The condition is to only insert if the tenant name does not present previously
                    ConditionExpression = "#tenantname <> :tenantname"
                };

                await client.PutItemAsync(request);
            }
            catch (ConditionalCheckFailedException exception)
            {
                responseBody.Add("message", "The tenant name is already exist in system");

                context.Logger.LogLine("Provision reject due to already existing tenant: " + exception.Message);

                return new APIGatewayProxyResponse()
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = responseBody.ToString(),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }
            // Catch all other exception
            catch (Exception exception)
            {
                responseBody.Add("message", "Internal provision error");

                context.Logger.LogLine("Provision failure due to error: " + exception.Message);

                return new APIGatewayProxyResponse()
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Body = responseBody.ToString(),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }

            responseBody.Add("message", "A new tenant added - " + DateTime.Now);

            return new APIGatewayProxyResponse()
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = responseBody.ToString(),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }

        async Task<APIGatewayProxyResponse> DeletionRequest(IDictionary<string, string> pathParameters, ILambdaContext context)
        {
            // Create a new JSON to handle the body (as the response is application/json)
            JObject responseBody = new JObject();
            string tenantName = pathParameters["tenantName"];

            if (tenantName == null || tenantName.Trim() == String.Empty)
            {
                responseBody.Add("message", "The input tenant name is invalid");
                return new APIGatewayProxyResponse()
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = responseBody.ToString(),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }

            // Force the tenant name to lower case to align with provision case
            tenantName = tenantName.ToLower().Trim();

            AmazonDynamoDBClient client = new AmazonDynamoDBClient();

            // Use DynamoDB expression to prevent same tenant name being recorded
            // As the tenant name is used for cloudformation name, it must be unique
            try
            {
                var request = new DeleteItemRequest
                {
                    TableName = Environment.GetEnvironmentVariable("TABLE_NAME"),
                    Key = new Dictionary<string, AttributeValue>()
                    {
                            { "TenantName", new AttributeValue {S = tenantName}},
                    }
                };

                await client.DeleteItemAsync(request);
            }
            // Catch all other exception
            catch (Exception exception)
            {
                responseBody.Add("message", "Internal deleteion error");

                context.Logger.LogLine("Deletion failure due to error: " + exception.Message);

                return new APIGatewayProxyResponse()
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Body = responseBody.ToString(),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }

            responseBody.Add("message", "Tenant destroyed - " + DateTime.Now);

            return new APIGatewayProxyResponse()
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = responseBody.ToString(),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
    }

}
