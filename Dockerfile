FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/CopilotStudioA2A/CopilotStudioA2A.csproj src/CopilotStudioA2A/
RUN dotnet restore src/CopilotStudioA2A/CopilotStudioA2A.csproj
COPY src/CopilotStudioA2A/ src/CopilotStudioA2A/
RUN dotnet publish src/CopilotStudioA2A/CopilotStudioA2A.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "CopilotStudioA2A.dll"]