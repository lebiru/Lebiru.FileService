FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Temporarily switch to root for installing curl and ping
USER root
RUN apt-get update && apt-get install -y curl iputils-ping && rm -rf /var/lib/apt/lists/*

# Switch back to the default user for security
USER app

# Set memory limits for the container
CMD ["--memory=2g"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Install git for the build process
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*
COPY ["Lebiru.FileService.csproj", "."]
RUN dotnet restore "./Lebiru.FileService.csproj"
COPY . .
WORKDIR "/src/."
# Use MSBuild property to skip Git-dependent targets
RUN dotnet build "./Lebiru.FileService.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:SkipGitVersion=true

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Lebiru.FileService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Lebiru.FileService.dll"]