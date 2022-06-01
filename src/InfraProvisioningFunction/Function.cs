using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.Lambda;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using System.Threading.Tasks;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace InfraProvisioningFunction;

public class Function
{
    public async Task<string> FunctionHandler(DynamoDBEvent dynamoEvent, ILambdaContext context)
    {
        // Cannot execute as the template url is null, error out
        // ?? is the null-coalescing operator
        // https://docs.microsoft.com/en-in/dotnet/csharp/language-reference/operators/null-coalescing-operator
        string templateURL = System.Environment.GetEnvironmentVariable("TEMPLATE_URL") ?? throw new ArgumentException("TEMPLATE_URL is not setup, fail the execution");
        string cloudformationServiceRoleARN = System.Environment.GetEnvironmentVariable("CLOUDFORMATION_SERVICE_ROLE_ARN") ?? throw new ArgumentException("CLOUDFORMATION_SERVICE_ROLE_ARN is not setup, fail the execution");

        context.Logger.LogLine($"Beginning to process {dynamoEvent.Records.Count} records...");
        AmazonCloudFormationClient client = new AmazonCloudFormationClient();

        foreach (var record in dynamoEvent.Records)
        {
            context.Logger.LogLine($"Event ID: {record.EventID}");
            context.Logger.LogLine($"Event Name: {record.EventName}");
            if (record.EventName.Value.Contains("INSERT"))
            {
                // Need to notify compiler that tenantID/tenantName (by adding ? in the declaration) is not possible to be null
                // The API TryGetValue may return null only if false, which will be catched by below if statement
                // So it is safe to ignore
                Amazon.DynamoDBv2.Model.AttributeValue? tenantID = new Amazon.DynamoDBv2.Model.AttributeValue();
                Amazon.DynamoDBv2.Model.AttributeValue? tenantName = new Amazon.DynamoDBv2.Model.AttributeValue();
                if (record.Dynamodb.NewImage.TryGetValue("TenantId", out tenantID) &&
                    record.Dynamodb.NewImage.TryGetValue("TenantName", out tenantName))
                {
                    context.Logger.LogLine($"Creation command received for TenantId: {tenantID.S}, TenantName: {tenantName.S}");

                    await CreateCfnStack(client, tenantName.S);
                }
                else
                {
                    throw new ArgumentException("Fail to analyze the DynamDB Stream NewImage TenantId/TenantName, fail the creation execution");
                }
            }
            else if (record.EventName.Value.Contains("REMOVE"))
            {
                // Need to notify compiler that tenantID/tenantName (by adding ? in the declaration) is not possible to be null
                // The API TryGetValue may return null only if false, which will be catched by below if statement
                // So it is safe to ignore
                Amazon.DynamoDBv2.Model.AttributeValue? tenantID = new Amazon.DynamoDBv2.Model.AttributeValue();
                Amazon.DynamoDBv2.Model.AttributeValue? tenantName = new Amazon.DynamoDBv2.Model.AttributeValue();
                // Need to use OldImage, as there is no new modification but a removal (no NewImage)
                if (record.Dynamodb.OldImage.TryGetValue("TenantId", out tenantID) &&
                    record.Dynamodb.OldImage.TryGetValue("TenantName", out tenantName))
                {

                    context.Logger.LogLine($"Deletion command received for TenantId: {tenantID.S}, TenantName: {tenantName.S}");
                    await DeleteCfnStack(client, tenantName.S);
                }
                else
                {
                    throw new ArgumentException("Fail to analyze the DynamDB Stream OldImage TenantId/TenantName, fail the deletion execution");
                }

            }
        }

        async Task<CreateStackResponse> CreateCfnStack(AmazonCloudFormationClient client, string tenantName)
        {

            CreateStackRequest stackRequest = new CreateStackRequest()
            {
                StackName = tenantName,
                DisableRollback = true,
                TemplateURL = templateURL,
                RoleARN = cloudformationServiceRoleARN,
                Parameters = new System.Collections.Generic.List<Parameter>
                {
                    new Parameter()
                    {
                         ParameterKey="TenantName",
                         ParameterValue= tenantName
                    }
                }

            };
            try
            {
                return await client.CreateStackAsync(stackRequest);
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Source of error " + e.Message);
                throw new ArgumentException("CloudFormation stack creation fail due to " + e.Message);
            }
        }


        async Task<DeleteStackResponse> DeleteCfnStack(AmazonCloudFormationClient client, string tenantName)
        {

            DeleteStackRequest stackRequest = new DeleteStackRequest()
            {
                StackName = tenantName
            };
            try
            {
                return await client.DeleteStackAsync(stackRequest);
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Source of error " + e.Message);
                throw new ArgumentException("CloudFormation stack creation fail due to " + e.Message);
            }
        }

        context.Logger.LogLine("Stream processing complete.");
        return "Complete Stack Operation";
    }

}
