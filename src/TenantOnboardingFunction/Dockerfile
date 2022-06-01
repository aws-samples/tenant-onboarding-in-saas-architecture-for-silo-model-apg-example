FROM --platform=linux/amd64 public.ecr.aws/lambda/dotnet:6 as base

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:6.0 as build
WORKDIR /src  
COPY ["TenantOnboardingFunction.csproj", "base/"]  
RUN dotnet restore "base/TenantOnboardingFunction.csproj"  
 
WORKDIR "/src"  
COPY . .  
RUN dotnet build "TenantOnboardingFunction.csproj" --configuration Release --output /app/build  
 
FROM build AS publish  
RUN dotnet publish "TenantOnboardingFunction.csproj" --no-restore --configuration Release --output /app/publish

FROM base AS final
WORKDIR /var/task  
COPY --from=publish /app/publish .  
