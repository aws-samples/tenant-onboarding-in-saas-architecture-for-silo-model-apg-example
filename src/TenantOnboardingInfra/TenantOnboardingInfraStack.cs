
using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Amazon.CDK.AWS.Logs;
using Constructs;
using Cdklabs.CdkNag;

namespace TenantOnboardingInfra
{
    public class TenantOnboardingInfraStack : Stack
    {
        private string tableName = "TenantOnboarding";
        private string restApiName = "TenantOnboardingAPI";
        private string tenantOnboardingFuncName = "Tenant_Onboarding";
        private string infraProvisioningFuncName = "Infra_Provisioning";
        private string tenantOnboardingFuncHandler = "TenantOnboardingFunction::TenantOnboardingFunction.Function::FunctionHandler";
        private string infraProvisioningFuncHandler = "InfraProvisioningFunction::InfraProvisioningFunction.Function::FunctionHandler";
        private string s3ObjectName = "infra.yaml";

        internal TenantOnboardingInfraStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {

            // Enable adding suppressions to AwsSolutions-IAM4 to notify CDK-NAG that 
            // The S3 wildcard IAM policy is auto generated by CDK as part S3 deployment constructor
            // This need to be at stack level due to that custom resource is a random generated id, so cannot be fetch or supress by path directly
            // Example /TenantOnboardingInfraStack/Custom::CDKBucketDeployment8693BB64968944B69AAFB0CC9EB8756C/ServiceRole/DefaultPolicy/Resource
            NagSuppressions.AddStackSuppressions(
              this,
              new[]{
                new NagPackSuppression{Id = "AwsSolutions-IAM4", Reason = "This lambdaExecutionRole used recommended policies from https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html. For the administrator access policy, it is outline in README that user should restrict the access according to their business need."},
                new NagPackSuppression{ Id = "AwsSolutions-IAM5", Reason = "This wildcard permission on ['Action::s3:*'] comes from AWS CDK auto generated policy to enable auto deployment of S3 resources" },
              },
              true
            );

            // Create a DynamoDB Table
            // Note: RemovalPolicy.DESTROY will delete the DynamoDB table when you run cdk destroy command
            // Enable the DynamoDB stream
            Table tenantTable = new Table(this, tableName + "Table", new TableProps
            {
                // Use TenantName as primary key as it will be used as primary search
                PartitionKey = new Attribute { Name = "TenantName", Type = AttributeType.STRING },
                RemovalPolicy = RemovalPolicy.DESTROY,
                Encryption = TableEncryption.AWS_MANAGED,
                PointInTimeRecovery = true,
                // Need to pass both new and old image, otherwise during record remove, old image will not be passed
                Stream = StreamViewType.NEW_AND_OLD_IMAGES
            });

            // Create an S3 bucket
            // Not specify a bucket name to let CDK auto generated suffix to append to resource name to make unique bucket name
            var bucket = new Bucket(this, "tenant-onboarding-infra-bucket-tw", new BucketProps
            {
                Encryption = BucketEncryption.S3_MANAGED,
                RemovalPolicy = RemovalPolicy.DESTROY,
                AutoDeleteObjects = true,
                EnforceSSL = true,
                PublicReadAccess = false,
                BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,            // Block all public access by default (only ACL or IAM)
                ServerAccessLogsPrefix = "TenantOnboardingBucketAccessLog"  // Enable access log AwsSolutions-S1
            });

            // Upload the objects to S3 bucket
            new BucketDeployment(this, "CFNTemplate", new BucketDeploymentProps
            {
                Sources = new[] { Source.Asset("./template") },
                DestinationBucket = bucket,
            });

            // Create tenantOnboardingFunctionExecutionRole Lambda execution role
            Role tenantOnboardingFunctionExecutionRole = new Role(this, tenantOnboardingFuncName + "-execution-role", new RoleProps
            {
                AssumedBy = new ServicePrincipal("lambda.amazonaws.com")
            });

            // Add required lambda policies
            // These polices according to documentation https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html are recommended to use over manually add policy. As Lambda will need to access random cloudwatch/XRAY endpoint base on input requirement
            tenantOnboardingFunctionExecutionRole.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSLambdaBasicExecutionRole"));

            // Addd READ/WRITE access to DynamoDB table
            tenantTable.GrantReadWriteData(tenantOnboardingFunctionExecutionRole);

            // Enable adding suppressions to AwsSolutions-IAM4 to notify CDK-NAG that 
            // This role used required AWS Managed Lambda policies. These polices according to documentation https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html are recommended to use over manually add policy. As Lambda will need to access random cloudwatch/XRAY endpoint base on input requirement
            // The wildcard is coming from AWS Managed Lambda policy due to how Lambda dynamically create cloudwatch/xray setup on start up
            NagSuppressions.AddResourceSuppressions(
              tenantOnboardingFunctionExecutionRole, new[]{
                new NagPackSuppression{ Id= "AwsSolutions-IAM4", Reason= "This lambdaExecutionRole used recommended policies from https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html." },
                new NagPackSuppression{ Id= "AwsSolutions-IAM5", Reason= "This wildcard permission comes from AWS Managed Lambda policies for cloudwatch/xray, which is required as lambda cannot control cloudwatch log group name in advance and require wildcard permission to create on fly so cannot be replaced" }
              },
              true
            );

            // Create a Lambda function - Tenant_Onboarding function
            var tenantOnboardingFunction = CreateLambdaFunction(tenantOnboardingFunctionExecutionRole, tenantOnboardingFuncHandler, "src/TenantOnboardingFunction", tenantOnboardingFuncName, "TenantOnboardingFunction");
            tenantOnboardingFunction.AddEnvironment("TABLE_NAME", tenantTable.TableName);

            var apiLogGroup = new LogGroup(this, "ApiLogGroup");

            // Create and configure API Gateway
            var apiGatewayIntegrationRole = new Role(this, "ApiGatewayIntegrationRole", new RoleProps()
            {
                AssumedBy = new ServicePrincipal("apigateway.amazonaws.com"),
            });
            var apiGateway = new RestApi(this, restApiName, new RestApiProps()
            {
                RestApiName = restApiName,
                DeployOptions = new StageOptions
                { // Default logging for all API endpoint dervied from this REST API
                    LoggingLevel = MethodLoggingLevel.ERROR,
                    AccessLogDestination = new LogGroupLogDestination(apiLogGroup),  // Enable Access Logging per AwsSolutions-APIG1
                    AccessLogFormat = AccessLogFormat.JsonWithStandardFields(new JsonWithStandardFieldProps
                    {
                        Caller = false,
                        HttpMethod = true,
                        Ip = true,
                        Protocol = true,
                        RequestTime = true,
                        ResourcePath = true,
                        ResponseLength = true,
                        Status = true,
                        User = true
                    })
                }
            });

            // Tenant is the base resource for tenant management
            var tenantResource = apiGateway.Root.AddResource("tenant");
            tenantResource.AddMethod("POST", new LambdaIntegration(tenantOnboardingFunction));

            // /tenant/{tenantId} will be used for deletion
            var specificTenantResource = tenantResource.AddResource("{tenantName}");
            specificTenantResource.AddMethod("DELETE", new LambdaIntegration(tenantOnboardingFunction));

            // Enable supression for input validation as it will be performed by backend logic (as certain field is optional and in certain format)
            // Enable supression for using managing log group, as the managed policy in this case is need for AWS API Gateway to create/log logs into Cloudwatch Logs and is preffered method according to AWS Doc
            // Enable supression for not enabling WAFv2, as the setup will be vary base on each custom use case, so keep the setup generic for now, a note is added to notify user to review the setting and adjusted before moving to production
            NagSuppressions.AddResourceSuppressions(
              apiGateway, new[]{
                new NagPackSuppression{ Id= "AwsSolutions-APIG2", Reason= "Backend integration Lambda will perform necessary validation on input parameters according to business logic." },
                new NagPackSuppression{ Id= "AwsSolutions-APIG3", Reason= "Theese API are not associated with AWS WAFv2 web ACL to keep the setup generic for now, a note is added to notify user to review the setting and adjusted before moving to production" },
                new NagPackSuppression{ Id= "AwsSolutions-APIG4", Reason= "These API are not configured with authorizer to keep the sample simple and generic. A note is documented in README to notify user to integrate with their business security practices before moving to production."},
                new NagPackSuppression{ Id= "AwsSolutions-COG4", Reason= "These API are not configured with cognito authorizer to keep the sample simple and generic. A note is documented in README to notify user to integrate with their business security practices before moving to production."},
                new NagPackSuppression{ Id= "AwsSolutions-IAM4", Reason= "This is generated AWS IAM role for managing log from CDK/API Gateway constructor. The managed policy in question is AmazonAPIGatewayPushToCloudWatchLogs and is the preffered way to setup according to AWS doc https://docs.aws.amazon.com/apigateway/latest/developerguide/set-up-logging.html"}
              },
              true
            );

            tenantOnboardingFunction.GrantInvoke(apiGatewayIntegrationRole);


            // Cloudformation Stack Role
            // A role used by cloudformation to provision necessary setup
            // It will be consumable by CloudFormation as a Service
            Role infraCloudFormationRole = new Role(this, "infra-cloudformation-role", new RoleProps
            {
                AssumedBy = new ServicePrincipal("cloudformation.amazonaws.com")
            });

            // Grant S3 read access for CloudFormation Template file
            bucket.GrantRead(infraCloudFormationRole);

            // Below provide necessary peviliges for tenant CloudFormation setup, if the customer tenant CloudFormation changed, please adjust with necessary policy change to ensure stack can be created
            // The reason to not lock down on specific resource is to accomdate resource naming schema in cloudformation, but it is possible to align all naming to lock down previliges to particular naming convention for enhanced security
            // Provide necessary preilives for KMS interaction (as part of tenant CloudFormation setup)
            // From https://docs.aws.amazon.com/kms/latest/developerguide/deleting-keys.html
            // This need to be * on resources as the KMS Key/ARN is random generated
            infraCloudFormationRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "kms:*"
                },
                Resources = new[] { "*" }
            }));

            // Provide all cloudformation API access to ensure cluster creation success
            infraCloudFormationRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "cloudformation:*"
                },
                Resources = new[] { $"arn:aws:cloudformation:{this.Region}:{this.Account}:stack/tenantcluster-*" }
            }));

            // For SNS topic creation/deletion
            // https://docs.aws.amazon.com/sns/latest/dg/sns-using-identity-based-policies.html
            infraCloudFormationRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "sns:*"
                },
                Resources = new[] { $"arn:aws:sns:{this.Region}:{this.Account}:tenantcluster-*" }
            }));

            // Provide all for Alarm
            infraCloudFormationRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "cloudwatch:*"
                },
                Resources = new[] { $"arn:aws:cloudwatch:{this.Region}:{this.Account}:alarm:tenantcluster-*" }
            }));


            // For SQS
            infraCloudFormationRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "sqs:*"
                },
                Resources = new[] { $"arn:aws:sqs:{this.Region}:{this.Account}:tenantcluster-*" }
            }));

            // Create infraProvisioningFunctionExecutionRole Lambda execution role
            // The role is a different one then API handling lambda to ensure least required previlieges are assigned to each
            Role infraProvisioningFunctionExecutionRole = new Role(this, infraProvisioningFuncName + "-execution-role", new RoleProps
            {
                AssumedBy = new ServicePrincipal("lambda.amazonaws.com")
            });

            // Add required lambda policies
            // These polices according to documentation https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html are recommended to use over manually add policy. As Lambda will need to access random cloudwatch/XRAY endpoint base on input requirement
            infraProvisioningFunctionExecutionRole.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSLambdaBasicExecutionRole"));

            // Grant DynamoDB access
            tenantTable.GrantStreamRead(infraProvisioningFunctionExecutionRole);

            // Grant S3 read access for CloudFormation Template file
            bucket.GrantRead(infraProvisioningFunctionExecutionRole);

            // Allow the Lambda role to pass the infraCloudFormationRole role to the cloudformation service 
            // https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use_passrole.html
            infraCloudFormationRole.GrantPassRole(infraProvisioningFunctionExecutionRole);

            // Provide all cloudformation API access to ensure cluster creation success
            infraProvisioningFunctionExecutionRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = new[] {
                  "cloudformation:*"
                },
                Resources = new[] { $"arn:aws:cloudformation:{this.Region}:{this.Account}:stack/tenantcluster-*" }
            }));

            // Enable adding suppressions to AwsSolutions-IAM4 to notify CDK-NAG that 
            // This role used required AWS Managed Lambda policies. These polices according to documentation https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html are recommended to use over manually add policy. As Lambda will need to access random cloudwatch/XRAY endpoint base on input requirement
            // The wildcard is coming from AWS Managed Lambda policy due to how Lambda dynamically create cloudwatch/xray setup on start up
            // The Administrator previliege is already documented in README for user to adjust based on their business case
            NagSuppressions.AddResourceSuppressions(
              infraProvisioningFunctionExecutionRole, new[]{
                new NagPackSuppression{ Id= "AwsSolutions-IAM4", Reason= "This lambdaExecutionRole used recommended policies from https://docs.aws.amazon.com/lambda/latest/dg/lambda-intro-execution-role.html." },
                new NagPackSuppression{ Id= "AwsSolutions-IAM5", Reason= "This wildcard permission comes from AWS Managed Lambda policies for cloudwatch/xray, which is required as lambda cannot control cloudwatch log group name in advance and require wildcard permission to create on fly so cannot be replaced. The Cloudformation wildcard actions are for tenant stack creation, it is limited to tenant-cluster* stack resources to not disrupt other stacks." }
              },
              true
            );

            // Create a Lambda function - Infra_Provisioning function
            var infraProvisioningFunction = CreateLambdaFunction(infraProvisioningFunctionExecutionRole, infraProvisioningFuncHandler, "src/InfraProvisioningFunction", infraProvisioningFuncName, "InfraProvisioningFunction");
            infraProvisioningFunction.AddEnvironment("TEMPLATE_URL", bucket.UrlForObject(s3ObjectName));
            infraProvisioningFunction.AddEnvironment("CLOUDFORMATION_SERVICE_ROLE_ARN", infraCloudFormationRole.RoleArn);

            // Add an event source for AWS Lambda - Infra_Provisioning function
            EventSourceMapping infraProvisioningMapping = new EventSourceMapping(this, "InfraProvisioningMapping", new EventSourceMappingProps
            {
                Target = infraProvisioningFunction,
                BatchSize = 100,
                StartingPosition = StartingPosition.LATEST,
                EventSourceArn = tenantTable.TableStreamArn
            });
            infraProvisioningMapping.Node.AddDependency(tenantTable);
            infraProvisioningMapping.Node.AddDependency(infraProvisioningFunction);
        }

        // Method to create Lambda function
        private DockerImageFunction CreateLambdaFunction(Role lambdaExecutionRole, string handler, string asset, string functionName, string id)
        {
            DockerImageCode code = DockerImageCode.FromImageAsset(asset, new AssetImageCodeProps
            {
                Cmd = new string[] { handler }
            });
            DockerImageFunction dockerImageFunction = new DockerImageFunction(this, id, new DockerImageFunctionProps
            {
                FunctionName = functionName,
                Code = code,
                Role = lambdaExecutionRole,
                Timeout = Duration.Seconds(30),
                MemorySize = 256,
            });
            return dockerImageFunction;
        }
    }
}