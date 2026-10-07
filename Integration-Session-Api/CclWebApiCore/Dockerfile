# syntax=docker/dockerfile:1
#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["CclWebApiCore/CclWebApiCore.csproj", "CclWebApiCore/"]
COPY ["CclCrmProxyCore/CclCrmProxyCore.csproj", "CclCrmProxyCore/"]
# NuGet.config is supplied as a build secret (from Key Vault by build-push-composite-action@v4.0),
# not copied into a layer — restore needs the private CCL feed for Shared.HealthCheck.
RUN --mount=type=secret,id=nugetconfig,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet restore "./CclWebApiCore/CclWebApiCore.csproj"
COPY . .
WORKDIR "/src/CclWebApiCore"
RUN dotnet build "./CclWebApiCore.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./CclWebApiCore.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CclWebApiCore.dll"]