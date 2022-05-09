using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using System.Threading.Tasks;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace InfraProvisioningFunction;

public class Function
{
    private string templateURL;
    public async Task<string> FunctionHandler(DynamoDBEvent dynamoEvent, ILambdaContext context)
    {
        templateURL = System.Environment.GetEnvironmentVariable("TEMPLATE_URL");
        context.Logger.LogLine($"Beginning to process {dynamoEvent.Records.Count} records...");
        AmazonCloudFormationClient client = new AmazonCloudFormationClient();

        foreach (var record in dynamoEvent.Records)
        {
            context.Logger.LogLine($"Event ID: {record.EventID}");
            context.Logger.LogLine($"Event Name: {record.EventName}");
            if (record.EventName.Value.Contains("INSERT"))
            {
                Amazon.DynamoDBv2.Model.AttributeValue tenantID = new Amazon.DynamoDBv2.Model.AttributeValue();
                Amazon.DynamoDBv2.Model.AttributeValue tenantName = new Amazon.DynamoDBv2.Model.AttributeValue();
                record.Dynamodb.NewImage.TryGetValue("TenantId", out tenantID);
                record.Dynamodb.NewImage.TryGetValue("TenantName", out tenantName);
                context.Logger.LogLine($"CREATION COMMAND RECEIVED FOR TENANTID: {tenantID.S}, TENANTNAME: {tenantName.S}");

                await CreateCfnStack(client, tenantName.S);
            }else if(record.EventName.Value.Contains("REMOVE")) {
                Amazon.DynamoDBv2.Model.AttributeValue tenantID = new Amazon.DynamoDBv2.Model.AttributeValue();
                Amazon.DynamoDBv2.Model.AttributeValue tenantName = new Amazon.DynamoDBv2.Model.AttributeValue();
                // Need to use OldImage, as there is no new modification but a removal (no NewImage)
                record.Dynamodb.OldImage.TryGetValue("TenantId", out tenantID);
                record.Dynamodb.OldImage.TryGetValue("TenantName", out tenantName);

                context.Logger.LogLine($"DELETION COMMAND RECEIVED FOR TENANTID: {tenantID.S}, TENANTNAME: {tenantName.S}");
                await DeleteCfnStack(client, tenantName.S);
            }
        }

        async Task<CreateStackResponse> CreateCfnStack(AmazonCloudFormationClient client, string tenantName)
        {

            CreateStackRequest stackRequest = new CreateStackRequest()
            {
                StackName = tenantName,
                DisableRollback = true,
                TemplateURL = templateURL,
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
                await client.CreateStackAsync(stackRequest);
            }
            catch (Exception e) {
                context.Logger.LogLine("I am here source of error " + e.Message);
            }
            return null;
        }


        async Task<CreateStackResponse> DeleteCfnStack(AmazonCloudFormationClient client, string tenantName)
        {

            DeleteStackRequest stackRequest = new DeleteStackRequest()
            {
                StackName = tenantName
            };
            try
            {
                await client.DeleteStackAsync(stackRequest);
            }
            catch (Exception e) {
                context.Logger.LogLine("I am here source of error " + e.Message);
            }
            return null;
        }

        context.Logger.LogLine("Stream processing complete.");
        return templateURL;
    }

}
